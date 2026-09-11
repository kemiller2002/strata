namespace Strata.Application

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Semantic.Wire
open Strata.Analysis.ProposedChange

/// Comparing desired state against actual state (PR-022, PR-023).
///
/// Authority for: what differs between two snapshots, and which of those
/// differences Strata is willing to propose acting on.
///
/// Those are two questions, not one, and keeping them apart is the whole
/// design. A diff that emits only `Changes` cannot distinguish "nothing
/// differs" from "plenty differs and I declined to say so", and a deployment
/// tool that cannot make that distinction is the one that drops your table.
///
/// ## Absence is not deletion
///
/// `NG-006` names a schema diff that assumes absence means delete as an
/// explicit NON-GOAL, and §1437 says it directly: "Do not delete unmanaged
/// objects simply because they are not in desired state."
///
/// A drop is proposed only when ALL of these hold:
///
///   1. the object's schema is listed in the project's `managedSchemas`;
///   2. the DESIRED snapshot is `Complete` for relations — a desired state
///      missing an object because its file failed to parse must never cause
///      that object to be dropped;
///   3. the object is not extension-owned (`RK-008`).
///
/// Every difference failing any of those is reported as a `Suppression`, which
/// names the object and why Strata will not act. Silence is not an option:
/// an unreported suppression is indistinguishable from no difference at all.
module SchemaDiff =

    /// Why a real difference will not be proposed as a change.
    type SuppressionReason =
        | OutsideManagedSchemas
        | DesiredStateIncomplete
        | ExtensionOwnedObject
        | NotModelled

    [<RequireQualifiedAccess>]
    module SuppressionReason =

        /// Wire tag, hand-written per Boundary Preservation.
        let tag (reason: SuppressionReason) =
            match reason with
            | OutsideManagedSchemas -> "outside-managed-schemas"
            | DesiredStateIncomplete -> "desired-state-incomplete"
            | ExtensionOwnedObject -> "extension-owned"
            | NotModelled -> "not-modelled"

    type Suppression =
        { Object: QualifiedName
          Reason: SuppressionReason
          Detail: string }

    type DiffResult =
        { /// Differences Strata proposes acting on. These go to the gate.
          Changes: Change list
          /// Differences Strata saw and will NOT act on, each with a reason.
          Suppressed: Suppression list
          /// True when the desired snapshot could support a deletion claim at
          /// all. A caller rendering a clean diff needs this: "no changes"
          /// means something different when desired state is partial.
          DesiredStateComplete: bool }

    let private tables (snapshot: SchemaSnapshot) =
        snapshot.Objects
        |> List.choose (fun o ->
            match o with
            | TableObject t -> Some t
            | ViewObject _
            | RoutineObject _ -> None)

    let private sameName (a: QualifiedName) (b: QualifiedName) =
        QualifiedName.display a = QualifiedName.display b

    /// Is this object's schema one the project is allowed to change?
    let private isManaged (managedSchemas: string list) (name: QualifiedName) =
        match name.Schema with
        | None -> false
        | Some schema ->
            managedSchemas
            |> List.exists (fun m -> Identifier.sameName (Identifier.unquoted m) schema)

    /// Objects present in the database and absent from desired state.
    ///
    /// This is the dangerous direction and the only one that can destroy data,
    /// so every guard lives here.
    let private removals
        (managedSchemas: string list)
        (desiredComplete: bool)
        (desired: Table list)
        (actual: Table list)
        =
        actual
        |> List.filter (fun a -> not (desired |> List.exists (fun d -> sameName d.Name a.Name)))
        |> List.map (fun a ->
            if not (isManaged managedSchemas a.Name) then
                Microsoft.FSharp.Core.Error
                    { Object = a.Name
                      Reason = OutsideManagedSchemas
                      Detail =
                        "present in the database and absent from desired state, but its schema is not managed by this project" }
            elif a.Scope = ExtensionOwned then
                Microsoft.FSharp.Core.Error
                    { Object = a.Name
                      Reason = ExtensionOwnedObject
                      Detail = "owned by an extension; absence from desired state does not make it removable" }
            elif not desiredComplete then
                Microsoft.FSharp.Core.Error
                    { Object = a.Name
                      Reason = DesiredStateIncomplete
                      Detail =
                        "absent from desired state, but desired state did not load completely, so its absence is not evidence it should be dropped" }
            else
                Ok(DropTable a.Name))

    /// Column-level differences for a table present on both sides.
    let private columnChanges
        (managedSchemas: string list)
        (desiredComplete: bool)
        (desired: Table)
        (actual: Table)
        =
        let managed = isManaged managedSchemas desired.Name

        let added =
            desired.Columns
            |> List.filter (fun d ->
                not (actual.Columns |> List.exists (fun a -> Identifier.sameName a.Name d.Name)))
            |> List.map (fun d -> Ok(AddColumn(desired.Name, d.Name)))

        let removed =
            actual.Columns
            |> List.filter (fun a ->
                not (desired.Columns |> List.exists (fun d -> Identifier.sameName d.Name a.Name)))
            |> List.map (fun a ->
                if not managed then
                    Microsoft.FSharp.Core.Error
                        { Object = desired.Name
                          Reason = OutsideManagedSchemas
                          Detail = sprintf "column '%s' is absent from desired state, but the schema is not managed" a.Name.Text }
                elif not desiredComplete then
                    Microsoft.FSharp.Core.Error
                        { Object = desired.Name
                          Reason = DesiredStateIncomplete
                          Detail =
                            sprintf
                                "column '%s' is absent from desired state, but desired state did not load completely"
                                a.Name.Text }
                else
                    Ok(DropColumn(desired.Name, a.Name)))

        let altered =
            desired.Columns
            |> List.choose (fun d ->
                actual.Columns
                |> List.tryFind (fun a -> Identifier.sameName a.Name d.Name)
                |> Option.bind (fun a ->
                    let desiredType = QualifiedName.display d.Type.TypeName
                    let actualType = QualifiedName.display a.Type.TypeName

                    if desiredType <> actualType then
                        Some(Ok(AlterColumnType(desired.Name, d.Name, desiredType)))
                    elif d.Type.IsNullable <> a.Type.IsNullable then
                        // The change vocabulary has no nullability case. Saying
                        // so is honest; inventing one that the gate would judge
                        // by the wrong rules is not.
                        Some(
                            Ok(
                                UnclassifiedChange(
                                    sprintf
                                        "%s.%s nullability differs: desired %b, actual %b"
                                        (QualifiedName.display desired.Name)
                                        d.Name.Text
                                        d.Type.IsNullable
                                        a.Type.IsNullable)))
                    else
                        None))

        added @ removed @ altered

    /// Compare desired state against actual state.
    let run
        (managedSchemas: string list)
        (desired: SchemaSnapshot)
        (actual: SchemaSnapshot)
        : DiffResult =

        // A drop is only ever as trustworthy as the desired state that implies
        // it, so this single flag gates every removal below.
        let desiredComplete =
            match Completeness.stateOf "relations" desired.Completeness with
            | Complete -> true
            | Partial _
            | Inaccessible _
            | NotRequested -> false

        let desiredTables = tables desired
        let actualTables = tables actual

        let creations =
            desiredTables
            |> List.filter (fun d -> not (actualTables |> List.exists (fun a -> sameName a.Name d.Name)))
            |> List.map (fun d -> Ok(CreateTable d.Name))

        let columnResults =
            desiredTables
            |> List.collect (fun d ->
                match actualTables |> List.tryFind (fun a -> sameName a.Name d.Name) with
                | Some a -> columnChanges managedSchemas desiredComplete d a
                | None -> [])

        // Objects the desired side could not model at all. Reported as
        // suppressions so a view file that failed to load does not read as
        // "this view should not exist".
        let unmodelled =
            actual.Objects
            |> List.choose (fun o ->
                match o with
                | TableObject _ -> None
                | ViewObject v ->
                    Some
                        { Object = v.Name
                          Reason = NotModelled
                          Detail = "views are not read as desired state yet, so no comparison was made" }
                | RoutineObject r ->
                    Some
                        { Object = r.Name
                          Reason = NotModelled
                          Detail = "routines are not read as desired state yet, so no comparison was made" })

        let all =
            creations
            @ removals managedSchemas desiredComplete desiredTables actualTables
            @ columnResults

        { Changes = all |> List.choose (function Ok change -> Some change | Microsoft.FSharp.Core.Error _ -> None)
          Suppressed =
            (all |> List.choose (function Microsoft.FSharp.Core.Error s -> Some s | Ok _ -> None))
            @ unmodelled
          DesiredStateComplete = desiredComplete }

    // ---- output -------------------------------------------------------------

    /// One line describing a change, for a human reading a plan.
    let private describe (change: Change) =
        match change with
        | UnclassifiedChange detail -> sprintf "unclassified: %s" detail
        | DropColumn (table, column) ->
            sprintf "drop-column        %s.%s" (QualifiedName.display table) column.Text
        | AlterColumnType (table, column, newType) ->
            sprintf "alter-column-type  %s.%s -> %s" (QualifiedName.display table) column.Text newType
        | AddColumn (table, column) ->
            sprintf "add-column         %s.%s" (QualifiedName.display table) column.Text
        | AddConstraint (table, name) ->
            sprintf "add-constraint     %s %s" (QualifiedName.display table) name.Text
        | DropTable table -> sprintf "drop-table         %s" (QualifiedName.display table)
        | CreateTable table -> sprintf "create-table       %s" (QualifiedName.display table)
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
        let lines = ResizeArray<string>()

        lines.Add(
            sprintf
                "PLAN: %d change(s), %d suppressed  —  gate verdict %s"
                (List.length result.Changes)
                (List.length result.Suppressed)
                ((DeploymentGate.Verdict.tag gate.Verdict).ToUpperInvariant()))

        lines.Add ""

        if List.isEmpty result.Changes then
            lines.Add "  (no changes proposed)"
            lines.Add ""
        else
            for change in result.Changes do
                lines.Add(sprintf "  %s" (describe change))
            lines.Add ""

        if not (List.isEmpty result.Suppressed) then
            lines.Add "NOT PROPOSED — differences Strata saw and will not act on:"
            lines.Add ""

            for s in result.Suppressed do
                lines.Add(sprintf "  %-28s [%s]" (QualifiedName.display s.Object) (SuppressionReason.tag s.Reason))
                lines.Add(sprintf "      %s" s.Detail)

            lines.Add ""

        if not result.DesiredStateComplete then
            lines.Add "WARNING: desired state did NOT load completely. No object was proposed for"
            lines.Add "         removal, because absence from a partial desired state is not evidence"
            lines.Add "         that anything should be dropped."
            lines.Add ""

        for f in gate.Findings do
            lines.Add(sprintf "  [%s] %s" ((DeploymentGate.Verdict.tag f.Verdict).ToUpperInvariant()) f.Detected)
            lines.Add(sprintf "         why:  %s" f.Rationale)
            lines.Add(sprintf "         next: %s" f.NextSafeMove)
            lines.Add ""

        System.String.Join("\n", lines)
