namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Semantic.Wire
open Strata.Analysis.ProposedChange
open Strata.Application

/// Text and JSON for a person or a pipeline reading a plan.
///
/// The only part of the diff that knows about the deployment gate, and the
/// only part that formats for display. One exhaustive match over `Change`
/// (`describe`), so a new change kind fails to compile here until it has a
/// line a reader can understand.
module Render =

    /// A grant's object, for a human reading a plan.
    ///
    /// Says the KIND out loud. A plan line reading `grant USAGE on orders` does
    /// not tell a reader whether the schema or a table of that name is about to
    /// become reachable, and that is the one thing they need to know.
    let private describeGrantTarget (target: GrantTarget) =
        match target with
        | GrantTarget.Relation name -> QualifiedName.display name
        | GrantTarget.Schema schema -> sprintf "schema %s" schema.Text
        | GrantTarget.Routine (name, arguments) ->
            sprintf "routine %s(%s)" (QualifiedName.display name) (String.concat ", " arguments)
        | GrantTarget.RelationColumn (name, column) ->
            sprintf "%s(%s)" (QualifiedName.display name) column.Text

    /// One line describing a change, for a human reading a plan.
    let private describe (change: Change) =
        match change with
        | UnclassifiedChange detail -> sprintf "unclassified: %s" detail
        | CreateEnumType name -> sprintf "create-enum-type   %s" (QualifiedName.display name)
        | DropEnumType name -> sprintf "drop-enum-type     %s" (QualifiedName.display name)
        | AddEnumValue (name, value, after) ->
            sprintf
                "add-enum-value     %s '%s'%s"
                (QualifiedName.display name)
                value
                (match after with Some p -> sprintf " after '%s'" p | None -> " (first)")
        | CreateDomainType name -> sprintf "create-domain-type %s" (QualifiedName.display name)
        | DropDomainType name -> sprintf "drop-domain-type   %s" (QualifiedName.display name)
        | SetDomainDefault (name, expression, replacing) ->
            sprintf
                "set-domain-default %s %s%s"
                (QualifiedName.display name)
                expression
                (match replacing with Some previous -> sprintf " (was %s)" previous | None -> " (had none)")
        | DropDomainDefault name -> sprintf "drop-domain-default %s" (QualifiedName.display name)
        | SetDomainNotNull name -> sprintf "set-domain-not-null %s" (QualifiedName.display name)
        | DropDomainNotNull name -> sprintf "drop-domain-not-null %s" (QualifiedName.display name)
        | AddDomainConstraint (name, constraintName, definition) ->
            sprintf
                "add-domain-constraint %s %s %s"
                (QualifiedName.display name)
                (match constraintName with Some n -> n.Display | None -> "(unnamed)")
                definition
        | DropDomainConstraint (name, constraintName) ->
            sprintf
                "drop-domain-constraint %s %s"
                (QualifiedName.display name)
                (match constraintName with Some n -> n.Display | None -> "(unnamed)")
        | ValidateDomainConstraint (name, constraintName) ->
            sprintf "validate-domain-constraint %s %s" (QualifiedName.display name) constraintName.Display
        | DropColumn (table, column) ->
            sprintf "drop-column        %s.%s" (QualifiedName.display table) column.Text
        | AlterColumnType (table, column, newType) ->
            sprintf "alter-column-type  %s.%s -> %s" (QualifiedName.display table) column.Text newType
        | AddColumn (table, column) ->
            sprintf "add-column         %s.%s" (QualifiedName.display table) column.Text
        | AddConstraint (table, name, kind, _) ->
            sprintf
                "add-constraint     %s %s %s"
                (QualifiedName.display table)
                (ConstraintKind.tag kind)
                (match name with Some n -> n.Text | None -> "(unnamed in the declaring file)")
        | DropConstraint (table, name, kind) ->
            sprintf
                "drop-constraint    %s %s %s"
                (QualifiedName.display table)
                (ConstraintKind.tag kind)
                name.Text
        | DropTable table -> sprintf "drop-table         %s" (QualifiedName.display table)
        | CreateSchema schema -> sprintf "create-schema      %s" schema.Text
        | CreateSequence n -> sprintf "create-sequence    %s" (QualifiedName.display n)
        | CreateExtension extension -> sprintf "create-extension   %s" extension.Text
        | UpdateExtension (extension, version) ->
            sprintf "update-extension   %s -> %s" extension.Text version
        | SetExtensionSchema (extension, schema) ->
            sprintf "set-ext-schema     %s -> %s" extension.Text schema.Text
        | CreatePolicy (table, policy) ->
            sprintf "create-policy      %s on %s" policy.Text (QualifiedName.display table)
        | ReplacePolicy (table, policy) ->
            sprintf "replace-policy     %s on %s" policy.Text (QualifiedName.display table)
        | EnableRowLevelSecurity table ->
            sprintf "enable-rls         %s" (QualifiedName.display table)
        | DisableRowLevelSecurity table ->
            sprintf "disable-rls        %s" (QualifiedName.display table)
        | ForceRowLevelSecurity table ->
            sprintf "force-rls          %s" (QualifiedName.display table)
        | NoForceRowLevelSecurity table ->
            sprintf "no-force-rls       %s" (QualifiedName.display table)
        | GrantPrivileges (t, g, p) ->
            sprintf "grant              %s on %s to %s" (String.concat "," p) (describeGrantTarget t) g
        | RevokePrivileges (t, g, p) ->
            sprintf "revoke             %s on %s from %s" (String.concat "," p) (describeGrantTarget t) g
        | SetComment (t, _) -> sprintf "set-comment        %s" (CommentTarget.key t)
        | RemoveComment t -> sprintf "remove-comment     %s" (CommentTarget.key t)
        | DropSequence n -> sprintf "drop-sequence      %s" (QualifiedName.display n)
        | AlterSequence n -> sprintf "alter-sequence     %s" (QualifiedName.display n)
        | CreateTable table -> sprintf "create-table       %s" (QualifiedName.display table)
        | RenameTable (from, to') ->
            sprintf "rename-table       %s -> %s" (QualifiedName.display from) (QualifiedName.display to')
        | RenameColumn (table, from, to') ->
            sprintf "rename-column      %s.%s -> %s" (QualifiedName.display table) from.Text to'.Text
        | CreateIndex (table, index) ->
            sprintf "create-index       %s on %s" index.Text (QualifiedName.display table)
        | DropIndex (table, index) ->
            sprintf "drop-index         %s on %s" index.Text (QualifiedName.display table)
        | InsertRow (table, key) ->
            sprintf "insert-row         %s %s" (QualifiedName.display table) key
        | UpdateRow (table, key) ->
            sprintf "update-row         %s %s" (QualifiedName.display table) key
        | CreateTrigger (table, trigger) ->
            sprintf "create-trigger     %s on %s" trigger.Text (QualifiedName.display table)
        | DropTrigger (table, trigger) ->
            sprintf "drop-trigger       %s on %s" trigger.Text (QualifiedName.display table)
        | ReplaceTrigger (table, trigger) ->
            sprintf "replace-trigger    %s on %s" trigger.Text (QualifiedName.display table)
        | CreateView view -> sprintf "create-view        %s" (QualifiedName.display view)
        | ReplaceView view -> sprintf "replace-view       %s" (QualifiedName.display view)
        | ReplaceRoutine r -> sprintf "replace-routine    %s" (QualifiedName.display r)
        | CreateRoutine routine -> sprintf "create-routine     %s" (QualifiedName.display routine)
        | TruncateTable table -> sprintf "truncate-table     %s" (QualifiedName.display table)

    let private suppressionJson (s: Suppression) =
        JObject [ "object", JString(QualifiedName.display s.Object)
                  "reason", JString(SuppressionReason.tag s.Reason)
                  "detail", JString s.Detail ]

    /// A gate finding, rendered here rather than reused from `DeploymentGate`
    /// because its own renderer is private to that module.
    ///
    /// The reasons matter more in JSON than in text: a pipeline that gets a
    /// verdict with no explanation can only obey or override it, and neither
    /// is a decision anyone can review afterwards.
    let private findingJson (f: DeploymentGate.Finding) =
        JObject [ "verdict", JString(DeploymentGate.Verdict.tag f.Verdict)
                  "change", JString(describe f.Change)
                  "detected", JString f.Detected
                  "rationale", JString f.Rationale
                  "affectedSources", JArray(f.AffectedSources |> List.sort |> List.map JString)
                  "nextSafeMove", JString f.NextSafeMove ]

    let toJson (result: DiffResult) (gate: DeploymentGate.GateResult) =
        JObject [ "verdict", JString(DeploymentGate.Verdict.tag gate.Verdict)
                  "exitCode", JInt(DeploymentGate.Verdict.exitCode gate.Verdict)
                  "desiredStateComplete", JBool result.DesiredStateComplete
                  "changeCount", JInt(List.length result.Changes)
                  "changes", JArray(result.Changes |> List.map (fun c -> JString(describe c)))
                  "findings", JArray(gate.Findings |> List.map findingJson)
                  "suppressed", JArray(result.Suppressed |> List.map suppressionJson)
                  "scope", scope gate.Scope ]
        |> Json.render

    /// Human-readable dry run.
    let toText (result: DiffResult) (gate: DeploymentGate.GateResult) =
        let header =
            [ sprintf
                  "PLAN: %d change(s), %d suppressed  —  gate verdict %s"
                  (List.length result.Changes)
                  (List.length result.Suppressed)
                  ((DeploymentGate.Verdict.tag gate.Verdict).ToUpperInvariant())
              "" ]

        let changes =
            if List.isEmpty result.Changes then
                [ "  (no changes proposed)"; "" ]
            else
                (result.Changes |> List.map (fun change -> sprintf "  %s" (describe change))) @ [ "" ]

        let suppressed =
            if List.isEmpty result.Suppressed then
                []
            else
                [ "NOT PROPOSED — differences Strata saw and will not act on:"; "" ]
                @ (result.Suppressed
                   |> List.collect (fun s ->
                       [ sprintf "  %-28s [%s]" (QualifiedName.display s.Object) (SuppressionReason.tag s.Reason)
                         sprintf "      %s" s.Detail ]))
                @ [ "" ]

        let incomplete =
            if result.DesiredStateComplete then
                []
            else
                [ "WARNING: desired state did NOT load completely. No object was proposed for"
                  "         removal, because absence from a partial desired state is not evidence"
                  "         that anything should be dropped."
                  "" ]

        let findings =
            gate.Findings
            |> List.collect (fun f ->
                [ sprintf "  [%s] %s" ((DeploymentGate.Verdict.tag f.Verdict).ToUpperInvariant()) f.Detected
                  sprintf "         why:  %s" f.Rationale
                  sprintf "         next: %s" f.NextSafeMove
                  "" ])

        String.Join("\n", header @ changes @ suppressed @ incomplete @ findings)
