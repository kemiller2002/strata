namespace Strata.Application

open Strata.Semantic.Identity
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

        | AddConstraint (table, constraintName) ->
            { Change = change
              Verdict = RequiresApproval
              Detected =
                sprintf "adds constraint %s to %s" constraintName.Display (QualifiedName.display table)
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
