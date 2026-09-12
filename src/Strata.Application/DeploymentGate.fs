namespace Strata.Application

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Semantic.Wire
open Strata.Analysis.Graph
open Strata.Analysis.ProposedChange

/// A deterministic pre-deployment gate.
///
/// Authority for: whether a proposed change may proceed.
///
/// This is the capability `HY-STRATA-2026-6F14` claims stands on its own. Its
/// defining property is not cleverness — it is that the SAME input yields the
/// SAME verdict every run, with an exit code, so it can block a pipeline with
/// no human or agent in the loop. A probabilistic answer cannot do that however
/// good it is.
///
/// Two design rules, both from the notebook:
///
///   - §130: when blocking, say what would make the change permissible. A gate
///     that only refuses gets routed around (`RK-016`, §136).
///   - §144.11 / `P-009`: "no dependency found" is NOT clearance. Absence of
///     evidence inside a bounded scope is not evidence of absence, so a clean
///     result under an incomplete scope cannot return `Allow`.
module DeploymentGate =

    /// What the gate decided.
    ///
    /// Three outcomes, not two. Collapsing `RequiresApproval` into either
    /// neighbour is the failure mode: fold it into `Allow` and destructive
    /// changes slip through; fold it into `Block` and the gate cries wolf until
    /// someone disables it.
    type Verdict =
        /// Provably safe within a scope that supports the claim.
        | Allow
        /// A human must decide. Strata has evidence but not authority.
        | RequiresApproval
        /// Known breakage. Proceeding loses something.
        | Block

    [<RequireQualifiedAccess>]
    module Verdict =

        let tag (v: Verdict) =
            match v with
            | Allow -> "allow"
            | RequiresApproval -> "requires-approval"
            | Block -> "block"

        /// Process exit code, so CI can gate on it.
        let exitCode (v: Verdict) =
            match v with
            | Allow -> 0
            | RequiresApproval -> 2
            | Block -> 1

        /// The most restrictive of two verdicts. A migration is as safe as its
        /// least safe statement.
        let combine (a: Verdict) (b: Verdict) =
            match a, b with
            | Block, _
            | _, Block -> Block
            | RequiresApproval, _
            | _, RequiresApproval -> RequiresApproval
            | Allow, Allow -> Allow

    /// One change, judged.
    type Finding =
        { Change: Change
          Verdict: Verdict
          /// What was detected, in one line.
          Detected: string
          /// Why it matters.
          Rationale: string
          /// Sources that would break, where known.
          AffectedSources: string list
          /// §130: what would make this permissible.
          NextSafeMove: string }

    /// The gate's answer for a whole migration.
    type GateResult =
        { Verdict: Verdict
          Findings: Finding list
          Scope: Scope }

    /// Judge one change against the graph.
    /// A grant's object, for a finding a human reads before approving.
    ///
    /// The kind is stated because it changes the decision. "grants USAGE on
    /// schema app" is a different question from "grants USAGE on app" — one
    /// opens a namespace, the other a sequence — and an approver cannot tell
    /// them apart from a bare name.
    let private grantTargetDisplay (target: GrantTarget) =
        match target with
        | GrantTarget.Relation name -> QualifiedName.display name
        | GrantTarget.Schema schema -> sprintf "schema %s" schema.Text
        | GrantTarget.Routine (name, arguments) ->
            sprintf "routine %s(%s)" (QualifiedName.display name) (String.concat ", " arguments)

    let private judge (graph: SemanticGraph) (scope: Scope) (change: Change) : Finding =
        // An absence claim is only trustworthy if the scope supports one.
        // This single check is what stops the gate approving a drop because it
        // looked in an empty corpus.
        let scopeSupportsAbsence = Scope.supportsAbsenceClaim scope

        let cleanResultVerdict =
            if scopeSupportsAbsence then Allow else RequiresApproval

        let cleanResultRationale =
            if scopeSupportsAbsence then
                "No dependency found, and the analysed scope supports that conclusion."
            else
                "No dependency found, but the analysed scope does NOT support concluding that none exists. This is not clearance."

        let cleanResultNextMove =
            if scopeSupportsAbsence then "Proceed."
            else "Widen the analysed scope (index the SQL corpus, inspect the live database) and re-run, or approve explicitly."

        match change with
        | AddColumn (table, column) ->
            { Change = change
              Verdict = Allow
              Detected = sprintf "adds column %s.%s" (QualifiedName.display table) column.Display
              // Additive changes cannot break an existing reader, so this is safe
              // regardless of scope — the one case where an incomplete scope does
              // not matter.
              Rationale = "Additive. No existing reader can depend on a column that does not yet exist."
              AffectedSources = []
              NextSafeMove = "Proceed." }

        | CreateTable table ->
            { Change = change
              Verdict = Allow
              Detected = sprintf "creates %s" (QualifiedName.display table)
              Rationale = "Additive. Creates a new object."
              AffectedSources = []
              NextSafeMove = "Proceed." }

        // A rename preserves the data and breaks every reader of the OLD name,
        // so the gate weighs dependents on the name being RETIRED, not the new
        // one — nothing can depend on a name that does not exist yet.
        | RenameTable (from, to') ->
            let dependents =
                graph.Dependencies
                |> List.filter (fun d -> QualifiedName.display d.Target = QualifiedName.display from)
                |> List.map (fun d -> d.SourceId)
                |> List.distinct

            { Change = change
              Verdict = if List.isEmpty dependents && scopeSupportsAbsence then Allow else Block
              Detected =
                sprintf
                    "renames %s to %s, which %d source(s) still reference by the old name"
                    (QualifiedName.display from)
                    (QualifiedName.display to')
                    (List.length dependents)
              Rationale =
                if List.isEmpty dependents then cleanResultRationale
                else "The data survives, but every reader of the old name breaks at once."
              AffectedSources = dependents
              NextSafeMove =
                if List.isEmpty dependents then cleanResultNextMove
                else "Update the listed sources to the new name, re-run this gate, then re-plan the rename." }

        | RenameColumn (table, from, to') ->
            let dependents =
                graph.ColumnDependencies
                |> List.filter (fun d ->
                    QualifiedName.display d.Table = QualifiedName.display table
                    && Identifier.sameName d.Column from)
                |> List.map (fun d -> d.SourceId)
                |> List.distinct

            { Change = change
              Verdict = if List.isEmpty dependents && scopeSupportsAbsence then Allow else Block
              Detected =
                sprintf
                    "renames %s.%s to %s, which %d source(s) still reference by the old name"
                    (QualifiedName.display table)
                    from.Display
                    to'.Display
                    (List.length dependents)
              Rationale =
                if List.isEmpty dependents then cleanResultRationale
                else "The data survives, but every reader of the old column name breaks at once."
              AffectedSources = dependents
              NextSafeMove =
                if List.isEmpty dependents then cleanResultNextMove
                else "Update the listed sources to the new name, re-run this gate, then re-plan the rename." }

        | CreateIndex (table, index) ->
            { Change = change
              Verdict = Allow
              Detected = sprintf "creates index %s on %s" index.Display (QualifiedName.display table)
              Rationale =
                // Additive to readers. Building it takes a lock, which is an
                // operational cost rather than a correctness one, and saying so
                // beats implying the operation is free.
                "Additive. No query can depend on an index that does not exist; building it takes a lock."
              AffectedSources = []
              NextSafeMove = "Proceed." }

        | DropIndex (table, index) ->
            // Dropping an index breaks nothing and reports nothing: every query
            // keeps working and gets slower. There is no dependency to find, so
            // the honest verdict is that a human decides.
            { Change = change
              Verdict = RequiresApproval
              Detected = sprintf "drops index %s on %s" index.Display (QualifiedName.display table)
              Rationale =
                "No query breaks, and none reports anything — they get slower. Strata cannot tell which rely on it."
              AffectedSources = []
              NextSafeMove = "Confirm no query depends on this index for its plan, then approve explicitly." }

        | InsertRow (table, key) ->
            { Change = change
              Verdict = Allow
              Detected = sprintf "inserts row %s into %s" key (QualifiedName.display table)
              Rationale =
                // Genuinely additive, unlike a new trigger. Nothing can already
                // reference a lookup row that does not exist; the rows that
                // will reference it come later, and need it to be there first.
                "Additive. No row can already reference a lookup row that does not exist."
              AffectedSources = []
              NextSafeMove = "Proceed." }

        | UpdateRow (table, key) ->
            // A reference row is read, not written, by the code that uses it:
            // everything that joins to this table sees the new value at once.
            // So the readers are the question here, where for a trigger it was
            // the writers.
            let readers = SemanticGraph.readersOf table graph

            { Change = change
              Verdict = if List.isEmpty readers && scopeSupportsAbsence then Allow else RequiresApproval
              Detected =
                sprintf
                    "changes row %s of %s, which %d source(s) read"
                    key
                    (QualifiedName.display table)
                    (List.length readers)
              Rationale =
                if List.isEmpty readers then cleanResultRationale
                else
                    "A reference row is read by everything that joins to it. The new value reaches \
                     every listed source at once, and nothing errors — a label, a rate or a flag simply \
                     starts meaning something else."
              AffectedSources = readers
              NextSafeMove =
                if List.isEmpty readers then cleanResultNextMove
                else "Confirm the listed sources are correct with the new value, then approve." }

        | CreateTrigger (table, trigger) ->
            // The writers, not the readers. A trigger changes what a write
            // does; a SELECT never fires one.
            let writers = SemanticGraph.writersOf table graph

            { Change = change
              Verdict = if List.isEmpty writers && scopeSupportsAbsence then Allow else RequiresApproval
              Detected =
                sprintf
                    "creates trigger %s on %s, which %d source(s) write to"
                    trigger.Display
                    (QualifiedName.display table)
                    (List.length writers)
              Rationale =
                if List.isEmpty writers then cleanResultRationale
                else
                    "The trigger is new; the behaviour it changes is not. Every listed write gets it at once, \
                     and a trigger can raise, rewrite the row, or cascade."
              AffectedSources = writers
              NextSafeMove =
                if List.isEmpty writers then cleanResultNextMove
                else "Confirm each listed write still behaves correctly with the trigger firing, then approve." }

        | DropTrigger (table, trigger) ->
            let writers = SemanticGraph.writersOf table graph

            { Change = change
              // The writers are the whole question. A trigger fires only when
              // something writes the table, so a table nothing writes carries
              // an inert trigger and removing it removes nothing. Where there
              // ARE writers, no analysis can help: a trigger's effect is
              // invisible to the SQL that provokes it, so Strata could never
              // find the code that depended on it.
              Verdict = if List.isEmpty writers && scopeSupportsAbsence then Allow else RequiresApproval
              Detected =
                sprintf
                    "drops trigger %s on %s, which %d source(s) write to"
                    trigger.Display
                    (QualifiedName.display table)
                    (List.length writers)
              Rationale =
                if List.isEmpty writers then cleanResultRationale
                else
                    "Every write that relied on its effect — an audit row, a maintained timestamp — \
                     silently stops getting it, and nothing errors. Strata cannot tell which relied on it."
              AffectedSources = writers
              NextSafeMove =
                if List.isEmpty writers then cleanResultNextMove
                else "Confirm nothing depends on this trigger's effect, then approve explicitly." }

        | ReplaceTrigger (table, trigger) ->
            let writers = SemanticGraph.writersOf table graph

            { Change = change
              // Same reasoning as the drop: a trigger on a table nothing writes
              // never fires, so redefining it changes nothing observable.
              Verdict = if List.isEmpty writers && scopeSupportsAbsence then Allow else RequiresApproval
              Detected =
                sprintf
                    "redefines trigger %s on %s, which %d source(s) write to"
                    trigger.Display
                    (QualifiedName.display table)
                    (List.length writers)
              Rationale =
                if List.isEmpty writers then cleanResultRationale
                else
                    "Both a drop and a create at once: the old effect stops and a new one starts, \
                     and every write to the table gets the change with no error either way."
              AffectedSources = writers
              NextSafeMove =
                if List.isEmpty writers then cleanResultNextMove
                else "Review the new definition against what the old one did, then approve explicitly." }

        | CreateSchema schema ->
            { Change = change
              Verdict = Allow
              Detected = sprintf "creates schema %s" schema.Display
              Rationale = "Additive. An empty namespace; nothing can already depend on it."
              AffectedSources = []
              NextSafeMove = "Proceed." }

        | GrantPrivileges (target, grantee, privileges) ->
            // Nothing breaks, so nothing here is looking for breakage. What it
            // weighs is who can now reach the data — and PUBLIC is a different
            // question from a named role, because PUBLIC includes every role
            // that exists now and every one created later.
            let toPublic = grantee.ToUpperInvariant() = "PUBLIC"

            { Change = change
              Verdict = if toPublic then RequiresApproval else Allow
              Detected =
                sprintf
                    "grants %s on %s to %s"
                    (String.concat ", " privileges)
                    (grantTargetDisplay target)
                    grantee
              Rationale =
                if toPublic then
                    "PUBLIC is not a role, it is every role — including ones created after this runs. \
                     Nothing breaks; the data simply becomes reachable by anyone who can connect."
                else
                    "Additive. Widens who can reach the object; breaks nothing that works today."
              AffectedSources = []
              NextSafeMove = if toPublic then "Confirm the data is meant to be readable by any role, then approve." else "Proceed." }

        | RevokePrivileges (target, grantee, privileges) ->
            // There is nothing to find, and that is the finding. The corpus
            // records SQL, not the role that runs it, so Strata cannot say
            // which code runs as this grantee. An empty AffectedSources here
            // would read as "nothing depends on it", which is not the claim.
            { Change = change
              Verdict = RequiresApproval
              Detected =
                sprintf
                    "revokes %s on %s from %s"
                    (String.concat ", " privileges)
                    (grantTargetDisplay target)
                    grantee
              Rationale =
                "Whatever runs as this grantee starts failing on its next statement, with no warning before \
                 it does. Strata cannot say what that is: the corpus records SQL, not the role that runs it."
              AffectedSources = []
              NextSafeMove = "Confirm nothing runs as this grantee against this object, then approve explicitly." }

        | CreateSequence name ->
            { Change = change
              Verdict = Allow
              Detected = sprintf "creates sequence %s" (QualifiedName.display name)
              Rationale = "Additive. Nothing can already draw from a sequence that does not exist."
              AffectedSources = []
              NextSafeMove = "Proceed." }

        | AlterSequence name ->
            // Nothing to look up, and that is the finding. A sequence is named
            // in a column DEFAULT, which the catalog reports as rendered text
            // rather than as a dependency, so Strata cannot enumerate who draws
            // from it. Saying so beats a clean-looking empty list.
            { Change = change
              Verdict = RequiresApproval
              Detected = sprintf "changes the increment, bounds, cache or cycle of %s" (QualifiedName.display name)
              Rationale =
                "The numbers it hands out change and nothing errors. Strata cannot list what draws from it: \
                 a sequence is named inside a column default, which the catalog reports as text rather than \
                 as a dependency."
              AffectedSources = []
              NextSafeMove = "Confirm nothing depends on the current increment or bounds, then approve explicitly." }

        | DropSequence name ->
            { Change = change
              Verdict = RequiresApproval
              Detected = sprintf "drops sequence %s" (QualifiedName.display name)
              Rationale =
                "A column default naming it keeps its text, so every insert relying on that default starts \
                 failing. Strata cannot list which defaults name it, for the same reason it cannot list a \
                 sequence's readers."
              AffectedSources = []
              NextSafeMove = "Confirm no column default draws from it, then approve explicitly." }

        | CreateView view ->
            { Change = change
              Verdict = Allow
              Detected = sprintf "creates view %s" (QualifiedName.display view)
              Rationale = "Additive. Creates a new object."
              AffectedSources = []
              NextSafeMove = "Proceed." }

        | ReplaceView view ->
            let dependents =
                graph.Dependencies
                |> List.filter (fun d -> QualifiedName.display d.Target = QualifiedName.display view)
                |> List.map (fun d -> d.SourceId)
                |> List.distinct

            { Change = change
              Verdict = if List.isEmpty dependents && scopeSupportsAbsence then Allow else RequiresApproval
              Detected =
                sprintf "redefines view %s, which %d source(s) depend on" (QualifiedName.display view) (List.length dependents)
              Rationale =
                // CREATE OR REPLACE VIEW fails outright if the column list
                // changes. The dangerous case is the one that SUCCEEDS: a
                // narrowed filter or altered join changes what every reader
                // gets, and nothing errors.
                if List.isEmpty dependents then cleanResultRationale
                else "Redefining a view changes what every reader of it receives, and a successful replace reports nothing."
              AffectedSources = dependents
              NextSafeMove =
                if List.isEmpty dependents then cleanResultNextMove
                else "Confirm the new definition returns what the listed sources expect, then approve explicitly." }

        | ReplaceRoutine routine ->
            let dependents =
                graph.Dependencies
                |> List.filter (fun d -> QualifiedName.display d.Target = QualifiedName.display routine)
                |> List.map (fun d -> d.SourceId)
                |> List.distinct

            { Change = change
              Verdict = if List.isEmpty dependents && scopeSupportsAbsence then Allow else RequiresApproval
              Detected =
                sprintf
                    "redefines routine %s, which %d source(s) depend on"
                    (QualifiedName.display routine)
                    (List.length dependents)
              Rationale =
                if List.isEmpty dependents then cleanResultRationale
                else "Every caller gets the new behaviour immediately, and a body change that compiles reports nothing."
              AffectedSources = dependents
              NextSafeMove =
                if List.isEmpty dependents then cleanResultNextMove
                else "Confirm the new body behaves as the listed callers expect, then approve explicitly." }

        | CreateRoutine routine ->
            { Change = change
              Verdict = Allow
              Detected = sprintf "creates routine %s" (QualifiedName.display routine)
              Rationale =
                // The CREATION is additive. Whether the routine's BODY is sound
                // is a different question, answered by `strata validate`, and
                // conflating the two would have this gate imply a check it did
                // not perform.
                "Additive. Creates a new object. Its body is not validated here."
              AffectedSources = []
              NextSafeMove = "Proceed." }

        | DropConstraint (table, constraintName, kind) ->
            // Nothing to find in the corpus, and that is the point. Every query
            // keeps working and keeps returning rows; what goes is the
            // database's refusal to accept the data the constraint excluded,
            // and the first anyone hears of it is a row that should not exist.
            { Change = change
              Verdict = RequiresApproval
              Detected =
                sprintf
                    "drops the %s constraint %s from %s"
                    (ConstraintKind.tag kind)
                    constraintName.Display
                    (QualifiedName.display table)
              Rationale =
                "No query breaks and none errors. The guarantee goes: the database stops refusing \
                 the data this constraint excluded, and nothing reports the first row that slips through."
              AffectedSources = []
              NextSafeMove =
                "Confirm nothing relies on this constraint holding — including code that omits a check \
                 because the database made it — then approve explicitly." }

        | AddConstraint (table, constraintName, _, _) ->
            { Change = change
              Verdict = RequiresApproval
              Detected =
                sprintf
                    "adds %s to %s"
                    (match constraintName with
                     | Some name -> sprintf "constraint %s" name.Display
                     // The file did not name it; the server will.
                     | None -> "an unnamed constraint")
                    (QualifiedName.display table)
              // Breaks no reader, but can fail outright against rows that already
              // violate it — which Strata cannot check without querying data.
              Rationale =
                "Breaks no reader, but may fail against existing rows. Strata does not inspect data and cannot confirm the table already satisfies it."
              AffectedSources = []
              NextSafeMove =
                sprintf
                    "Verify no existing row violates the constraint (e.g. run the equivalent SELECT against %s), then approve."
                    (QualifiedName.display table) }

        | DropColumn (table, column) ->
            let readers = SemanticGraph.columnReadersOf table column graph
            let writers = SemanticGraph.columnWritersOf table column graph
            let explicit = readers |> List.filter (snd >> not) |> List.map fst
            let wildcard = readers |> List.filter snd |> List.map fst
            let affected = List.distinct (explicit @ wildcard @ writers)

            if List.isEmpty affected then
                { Change = change
                  Verdict = cleanResultVerdict
                  Detected =
                    sprintf "drops column %s.%s" (QualifiedName.display table) column.Display
                  Rationale = cleanResultRationale
                  AffectedSources = []
                  NextSafeMove = cleanResultNextMove }
            else
                { Change = change
                  Verdict = Block
                  Detected =
                    sprintf
                        "drops column %s.%s, which %d source(s) depend on"
                        (QualifiedName.display table)
                        column.Display
                        (List.length affected)
                  Rationale =
                    let parts =
                        [ if not (List.isEmpty explicit) then
                              yield sprintf "%d source(s) name the column explicitly" (List.length explicit)
                          if not (List.isEmpty wildcard) then
                              // The case a text search misses entirely.
                              yield
                                  sprintf
                                      "%d source(s) read it via SELECT * and would silently change shape"
                                      (List.length wildcard)
                          if not (List.isEmpty writers) then
                              yield sprintf "%d source(s) write it" (List.length writers) ]

                    System.String.Join("; ", parts) + "."
                  AffectedSources = affected
                  NextSafeMove =
                    "Migrate or retire the listed sources, re-run this gate, then re-plan the drop." }

        | AlterColumnType (table, column, _) ->
            let readers = SemanticGraph.columnReadersOf table column graph |> List.map fst
            let writers = SemanticGraph.columnWritersOf table column graph
            let affected = List.distinct (readers @ writers)

            if List.isEmpty affected then
                { Change = change
                  Verdict = cleanResultVerdict
                  Detected =
                    sprintf "changes the type of %s.%s" (QualifiedName.display table) column.Display
                  Rationale = cleanResultRationale
                  AffectedSources = []
                  NextSafeMove = cleanResultNextMove }
            else
                { Change = change
                  // Not Block: a type change may be widening and entirely safe.
                  // Strata knows WHO is affected, not whether the new type is
                  // compatible — that is a human judgement, and pretending
                  // otherwise would be the false-confidence failure §144.2 warns
                  // about.
                  Verdict = RequiresApproval
                  Detected =
                    sprintf
                        "changes the type of %s.%s, which %d source(s) use"
                        (QualifiedName.display table)
                        column.Display
                        (List.length affected)
                  Rationale =
                    "A type change does not break readers if it widens, and does if it narrows. Strata identifies the affected sources but cannot judge type compatibility."
                  AffectedSources = affected
                  NextSafeMove =
                    "Confirm the new type accepts every value the listed sources produce and consume, then approve." }

        | DropTable table ->
            let readers = SemanticGraph.readersOf table graph
            let writers = SemanticGraph.writersOf table graph
            let relationships =
                SemanticGraph.relationshipsFor table graph
                |> List.map (fun r ->
                    sprintf "%s -> %s" (QualifiedName.display r.FromTable) (QualifiedName.display r.ToTable))

            let affected = List.distinct (readers @ writers)

            if List.isEmpty affected && List.isEmpty relationships then
                { Change = change
                  Verdict = cleanResultVerdict
                  Detected = sprintf "drops table %s" (QualifiedName.display table)
                  Rationale = cleanResultRationale
                  AffectedSources = []
                  NextSafeMove = cleanResultNextMove }
            else
                { Change = change
                  Verdict = Block
                  Detected =
                    sprintf
                        "drops table %s, which %d source(s) and %d relationship(s) depend on"
                        (QualifiedName.display table)
                        (List.length affected)
                        (List.length relationships)
                  Rationale = "Dropping a table removes every row and breaks every dependent."
                  AffectedSources = affected @ relationships
                  NextSafeMove =
                    "Migrate or retire the listed dependents, re-run this gate, then re-plan the drop." }

        | TruncateTable table ->
            { Change = change
              Verdict = Block
              Detected = sprintf "truncates %s" (QualifiedName.display table)
              // No predicate exists to narrow it; this is unconditionally every
              // row, which is why §11.3 lists it separately from DELETE.
              Rationale = "TRUNCATE removes every row unconditionally and is not recoverable without a backup."
              AffectedSources = []
              NextSafeMove =
                "Confirm a current backup exists and approve explicitly, or replace with a bounded DELETE." }

        | UnclassifiedChange detail ->
            { Change = change
              // ER-008: Strata not knowing what a statement does is not the same
              // as the statement being harmless.
              Verdict = RequiresApproval
              Detected = sprintf "proposes a change Strata does not model: %s" detail
              Rationale = "Strata cannot assess this statement's consequences. That is not the same as it being safe."
              AffectedSources = []
              NextSafeMove = "Review this statement manually and approve explicitly." }

    /// Run the gate over every proposed change.
    let run (graph: SemanticGraph) (scope: Scope) (changes: Change list) : GateResult =
        let findings = changes |> List.map (judge graph scope)

        { Verdict =
            match findings with
            // A migration proposing nothing Strata recognises is not approved by
            // default.
            | [] -> RequiresApproval
            | _ -> findings |> List.map (fun f -> f.Verdict) |> List.reduce Verdict.combine
          Findings = findings
          Scope = scope }

    let private finding (f: Finding) =
        JObject [ "change", JString(Change.tag f.Change)
                  "target",
                  (match Change.target f.Change with
                   | Some t -> JString(QualifiedName.display t)
                   | None -> JNull)
                  "verdict", JString(Verdict.tag f.Verdict)
                  "detected", JString f.Detected
                  "rationale", JString f.Rationale
                  "affectedSources", JArray(f.AffectedSources |> List.sort |> List.map JString)
                  "nextSafeMove", JString f.NextSafeMove ]

    let toJson (result: GateResult) =
        JObject [ "verdict", JString(Verdict.tag result.Verdict)
                  "exitCode", JInt(Verdict.exitCode result.Verdict)
                  "findings", JArray(result.Findings |> List.map finding)
                  "scope", scope result.Scope ]
        |> Json.render

    /// Human-readable gate output.
    let toText (result: GateResult) =
        let lines = ResizeArray<string>()
        lines.Add(sprintf "VERDICT: %s" ((Verdict.tag result.Verdict).ToUpperInvariant()))
        lines.Add ""

        for f in result.Findings do
            lines.Add(sprintf "  [%s] %s" ((Verdict.tag f.Verdict).ToUpperInvariant()) f.Detected)
            lines.Add(sprintf "         why:  %s" f.Rationale)

            if not (List.isEmpty f.AffectedSources) then
                for s in List.sort f.AffectedSources do
                    lines.Add(sprintf "         - %s" s)

            lines.Add(sprintf "         next: %s" f.NextSafeMove)
            lines.Add ""

        if not (Scope.supportsAbsenceClaim result.Scope) then
            lines.Add "NOTE: the analysed scope does not support absence claims. A clean result here"
            lines.Add "      is a bounded result, not clearance."

        System.String.Join("\n", lines)
