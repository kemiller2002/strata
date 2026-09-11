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
        /// The difference is real and Strata would act on it, but removals were
        /// not enabled for this run.
        | DropsNotEnabled

    [<RequireQualifiedAccess>]
    module SuppressionReason =

        /// Wire tag, hand-written per Boundary Preservation.
        let tag (reason: SuppressionReason) =
            match reason with
            | OutsideManagedSchemas -> "outside-managed-schemas"
            | DesiredStateIncomplete -> "desired-state-incomplete"
            | ExtensionOwnedObject -> "extension-owned"
            | NotModelled -> "not-modelled"
            | DropsNotEnabled -> "drops-not-enabled"

    type Suppression =
        { Object: QualifiedName
          Reason: SuppressionReason
          Detail: string }

    /// A change together with the DDL that would effect it.
    type PlannedStatement =
        { Change: Change
          /// `None` is NOT "nothing to do". It is "Strata classified this
          /// difference and cannot write DDL for it safely", which must stop an
          /// apply rather than be silently skipped — executing the rest would
          /// leave the database in a state matching neither side.
          Sql: string option }

    type DiffResult =
        { /// Differences Strata proposes acting on. These go to the gate.
          Changes: Change list
          /// The same changes, with the DDL that would effect each.
          Statements: PlannedStatement list
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
        (allowDrops: bool)
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
            elif not allowDrops then
                Microsoft.FSharp.Core.Error
                    { Object = a.Name
                      Reason = DropsNotEnabled
                      Detail = "would be dropped; pass --allow-drops to propose removals" }
            else
                Ok(DropTable a.Name))

    /// Column-level differences for a table present on both sides.
    let private columnChanges
        (allowDrops: bool)
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
                elif not allowDrops then
                    Microsoft.FSharp.Core.Error
                        { Object = desired.Name
                          Reason = DropsNotEnabled
                          Detail =
                            sprintf "column '%s' would be dropped; pass --allow-drops to propose removals" a.Name.Text }
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
    // ---- DDL emission -------------------------------------------------------

    /// Render an identifier for execution.
    ///
    /// Always quoted. A name from the catalog is already case-folded, so
    /// quoting it changes nothing; a name that was quoted in the file keeps the
    /// case it needs. Emitting unquoted would silently fold a mixed-case
    /// identifier into a different object.
    let private quote (identifier: Identifier) =
        "\"" + identifier.Text.Replace("\"", "\"\"") + "\""

    let private quoteName (name: QualifiedName) =
        match name.Schema with
        | Some schema -> sprintf "%s.%s" (quote schema) (quote name.Name)
        | None -> quote name.Name

    /// A column as it appears in DDL.
    ///
    /// The default EXPRESSION is not carried by the semantic model — only the
    /// fact that one exists — so a column with a default cannot be emitted
    /// without inventing its value. Callers handle that by refusing to emit,
    /// not by dropping the default.
    let private columnDdl (column: Column) =
        sprintf
            "%s %s%s"
            (quote column.Name)
            (QualifiedName.display column.Type.TypeName)
            (if column.Type.IsNullable then "" else " NOT NULL")

    /// DDL for one change, or `None` when Strata cannot write it faithfully.
    ///
    /// Every `None` here is a deliberate refusal, and each is a gap in the
    /// semantic model rather than an oversight: the model carries that a
    /// default or a check constraint EXISTS but not its expression, because
    /// the catalog reports those already normalised and storing a
    /// half-understood expression would be worse than storing none. Emitting a
    /// table without its checks would create an object that differs from what
    /// the project declared while reporting success.
    let private emit (desired: Table list) (change: Change) : string option =
        let desiredTable name = desired |> List.tryFind (fun t -> sameName t.Name name)

        match change with
        | UnclassifiedChange _ -> None

        | CreateTable name ->
            match desiredTable name with
            | None -> None
            | Some table when not (List.isEmpty table.CheckConstraints) -> None
            | Some table when table.Columns |> List.exists (fun c -> c.HasDefault) -> None
            | Some table ->
                let columns = table.Columns |> List.map columnDdl

                let primaryKey =
                    match table.PrimaryKey with
                    | Some pk ->
                        [ sprintf
                            "CONSTRAINT %s PRIMARY KEY (%s)"
                            (quote pk.ConstraintName)
                            (pk.Columns |> List.map quote |> String.concat ", ") ]
                    | None -> []

                let uniques =
                    table.UniqueConstraints
                    |> List.map (fun u ->
                        sprintf
                            "CONSTRAINT %s UNIQUE (%s)"
                            (quote u.ConstraintName)
                            (u.Columns |> List.map quote |> String.concat ", "))

                let foreignKeys =
                    table.ForeignKeys
                    |> List.map (fun f ->
                        sprintf
                            "CONSTRAINT %s FOREIGN KEY (%s) REFERENCES %s (%s)"
                            (quote f.ConstraintName)
                            (f.Columns |> List.map quote |> String.concat ", ")
                            (quoteName f.ReferencedTable)
                            (f.ReferencedColumns |> List.map quote |> String.concat ", "))

                Some(
                    sprintf
                        "CREATE TABLE %s (\n    %s\n)"
                        (quoteName name)
                        (columns @ primaryKey @ uniques @ foreignKeys |> String.concat ",\n    "))

        | AddColumn (table, column) ->
            desiredTable table
            |> Option.bind (fun t -> t.Columns |> List.tryFind (fun c -> Identifier.sameName c.Name column))
            // A column whose default expression Strata does not carry cannot be
            // added faithfully: emitting it without the default would populate
            // existing rows with NULL instead of the declared value.
            |> Option.filter (fun c -> not c.HasDefault)
            |> Option.map (fun c -> sprintf "ALTER TABLE %s ADD COLUMN %s" (quoteName table) (columnDdl c))

        | AlterColumnType (table, column, newType) ->
            Some(
                sprintf
                    "ALTER TABLE %s ALTER COLUMN %s TYPE %s"
                    (quoteName table)
                    (quote column)
                    newType)

        | DropColumn (table, column) ->
            Some(sprintf "ALTER TABLE %s DROP COLUMN %s" (quoteName table) (quote column))

        | DropTable table -> Some(sprintf "DROP TABLE %s" (quoteName table))

        | TruncateTable table -> Some(sprintf "TRUNCATE TABLE %s" (quoteName table))

        // The diff does not currently produce these, and emitting one would
        // need a definition it does not carry.
        | AddConstraint _ -> None

    /// Execution order.
    ///
    /// Creates before the things that reference them, drops after the things
    /// that depend on them, and columns dropped before their table so a
    /// statement never runs against an object the previous one removed.
    let private orderKey (change: Change) =
        match change with
        | CreateTable _ -> 0
        | AddColumn _ -> 1
        | AlterColumnType _ -> 2
        | AddConstraint _ -> 3
        | TruncateTable _ -> 4
        | DropColumn _ -> 5
        | DropTable _ -> 6
        | UnclassifiedChange _ -> 7

    /// Compare desired state against actual state.
    ///
    /// `allowDrops` defaults OFF at every call site, and deliberately. Managed
    /// schemas are inferred from the directory tree, so creating
    /// `schema/crm/` would otherwise be an implicit claim to own every object
    /// in `crm` and remove anything undeclared. Every comparable tool made the
    /// same choice: SSDT's DropObjectsNotInSource is false by default, sqldef
    /// disabled DROP by default in 2.0.0, migra requires --unsafe, and
    /// pg-schema-diff requires --allow-hazards. A removal that is not enabled
    /// is still REPORTED, as a suppression — the difference is real and the
    /// user needs to see it; what is withheld is the proposal, not the fact.
    let run
        (allowDrops: bool)
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
                | Some a -> columnChanges allowDrops managedSchemas desiredComplete d a
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
            @ removals allowDrops managedSchemas desiredComplete desiredTables actualTables
            @ columnResults

        let changes =
            all
            |> List.choose (function Ok change -> Some change | Microsoft.FSharp.Core.Error _ -> None)
            |> List.sortBy orderKey

        { Changes = changes
          Statements = changes |> List.map (fun c -> { Change = c; Sql = emit desiredTables c })
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
