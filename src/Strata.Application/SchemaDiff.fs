namespace Strata.Application

open System
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
        /// Strata holds both sides but cannot compare them faithfully, so it
        /// reports that rather than implying they match. This is the reason
        /// that must exist for the diff to be honest: without it, a property
        /// nobody compared renders identically to one that is equal.
        | NotCompared

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
            | NotCompared -> "not-compared"

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
    let private emit
        (declarations: (QualifiedName * string) list)
        (desired: Table list)
        (change: Change)
        : string option =
        let desiredTable name = desired |> List.tryFind (fun t -> sameName t.Name name)

        let declaredText name =
            declarations
            |> List.tryPick (fun (declared, text) -> if sameName declared name then Some text else None)

        match change with
        // Most unclassified changes cannot be written — they describe a
        // difference rather than name an object. A declared view or routine is
        // the exception: the change names it, and the file holds exactly the
        // DDL that creates it. Matching on the message is unpleasant, but the
        // alternative is inventing CreateView and CreateFunction cases that the
        // gate would then judge by rules written for tables.
        | UnclassifiedChange _ -> None

        // A view or routine is created by executing the file that declares it.
        // There is nothing to reconstruct: the definition text is not
        // recoverable from the parse tree, so a project without the file cannot
        // create one, and says so by emitting nothing.
        | CreateView name
        | CreateRoutine name -> declaredText name

        // The declaring file says CREATE VIEW; replacing needs CREATE OR
        // REPLACE VIEW. Rewriting only the leading keyword keeps the author's
        // body byte-for-byte, which is the whole reason the file is used.
        | ReplaceView name ->
            declaredText name
            |> Option.bind (fun text ->
                let trimmed = text.TrimStart()

                if trimmed.StartsWith("CREATE VIEW", StringComparison.OrdinalIgnoreCase) then
                    Some("CREATE OR REPLACE VIEW" + trimmed.Substring("CREATE VIEW".Length))
                else
                    // A materialized view cannot be replaced in place, and
                    // anything else is a shape this did not expect. Emitting
                    // nothing stops the apply rather than guessing.
                    None)

        // The declaring file holds exactly the DDL the author wrote, defaults
        // and check expressions included. Reconstruction below is the fallback
        // for a snapshot built without files, and it still refuses whatever it
        // cannot render faithfully.
        | CreateTable name when (declaredText name).IsSome -> declaredText name

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
        // Views and routines reference tables and columns, so they come after
        // every table change that might create what they read.
        | CreateView _ -> 4
        | ReplaceView _ -> 4
        | CreateRoutine _ -> 4
        | TruncateTable _ -> 5
        | DropColumn _ -> 6
        | DropTable _ -> 7
        | UnclassifiedChange _ -> 8

    /// Constraint and default differences for a table present on both sides.
    ///
    /// These were not compared at all until `EV-STRATA-2026-D3A8`, which found
    /// `plan` reporting "already matches desired state" for a table whose
    /// foreign key, check constraint and column default all differed. The
    /// model carried every one of those facts on both sides; nothing looked at
    /// them. A difference nobody compared rendered identically to no
    /// difference, which is the exact collapse `ER-008` exists to forbid.
    ///
    /// What can be compared faithfully is compared. What cannot is reported as
    /// `NotCompared` — never passed over.
    let private constraintChanges (desired: Table) (actual: Table) =
        let columnList (columns: Identifier list) =
            columns |> List.map (fun c -> Identifier.folded c) |> String.concat ","

        let named (name: Identifier) = Identifier.folded name

        // Constraints are matched by NAME, because that is the identity the
        // catalog and the file agree on. A constraint present on one side only
        // is a real difference whatever its definition says.
        let compareSet kind (desiredNames: (string * string) list) (actualNames: (string * string) list) =
            let added =
                desiredNames
                |> List.filter (fun (n, _) -> not (actualNames |> List.exists (fun (m, _) -> m = n)))
                |> List.map (fun (n, _) ->
                    Ok(AddConstraint(desired.Name, Identifier.unquoted n)))

            let removed =
                actualNames
                |> List.filter (fun (n, _) -> not (desiredNames |> List.exists (fun (m, _) -> m = n)))
                |> List.map (fun (n, _) ->
                    // The change vocabulary has no DropConstraint case.
                    // Unclassified is judged as potentially destructive, which
                    // is the right default for removing a constraint.
                    Ok(
                        UnclassifiedChange(
                            sprintf
                                "%s: %s '%s' exists in the database and not in desired state"
                                (QualifiedName.display desired.Name)
                                kind
                                n)))

            // Same name on both sides, different membership.
            let redefined =
                desiredNames
                |> List.choose (fun (n, dcols) ->
                    actualNames
                    |> List.tryFind (fun (m, _) -> m = n)
                    |> Option.bind (fun (_, acols) ->
                        if dcols <> acols then
                            Some(
                                Ok(
                                    UnclassifiedChange(
                                        sprintf
                                            "%s: %s '%s' covers (%s) in desired state and (%s) in the database"
                                            (QualifiedName.display desired.Name)
                                            kind
                                            n
                                            dcols
                                            acols)))
                        else
                            None))

            added @ removed @ redefined

        let primaryKey =
            match desired.PrimaryKey, actual.PrimaryKey with
            | Some d, Some a when columnList d.Columns <> columnList a.Columns ->
                [ Ok(
                    UnclassifiedChange(
                        sprintf
                            "%s: primary key covers (%s) in desired state and (%s) in the database"
                            (QualifiedName.display desired.Name)
                            (columnList d.Columns)
                            (columnList a.Columns))) ]
            | Some d, None -> [ Ok(AddConstraint(desired.Name, d.ConstraintName)) ]
            | None, Some a ->
                [ Ok(
                    UnclassifiedChange(
                        sprintf
                            "%s: primary key '%s' exists in the database and not in desired state"
                            (QualifiedName.display desired.Name)
                            a.ConstraintName.Text)) ]
            | Some _, Some _
            | None, None -> []

        let uniques =
            compareSet
                "unique constraint"
                (desired.UniqueConstraints |> List.map (fun u -> named u.ConstraintName, columnList u.Columns))
                (actual.UniqueConstraints |> List.map (fun u -> named u.ConstraintName, columnList u.Columns))

        let foreignKeys =
            let describe (f: ForeignKey) =
                sprintf
                    "%s -> %s(%s)"
                    (columnList f.Columns)
                    (QualifiedName.display f.ReferencedTable)
                    (columnList f.ReferencedColumns)

            compareSet
                "foreign key"
                (desired.ForeignKeys |> List.map (fun f -> named f.ConstraintName, describe f))
                (actual.ForeignKeys |> List.map (fun f -> named f.ConstraintName, describe f))

        // A check's PRESENCE is comparable by name. Its EXPRESSION is not: the
        // declared side does not carry one, and the catalog reports its own
        // normalised rendering. Presence is therefore compared and equality of
        // expression is explicitly not claimed, below.
        let checks =
            compareSet
                "check constraint"
                (desired.CheckConstraints |> List.map (fun c -> named c.ConstraintName, ""))
                (actual.CheckConstraints |> List.map (fun c -> named c.ConstraintName, ""))

        let defaults =
            desired.Columns
            |> List.choose (fun d ->
                actual.Columns
                |> List.tryFind (fun a -> Identifier.sameName a.Name d.Name)
                |> Option.bind (fun a ->
                    if d.HasDefault <> a.HasDefault then
                        Some(
                            Ok(
                                UnclassifiedChange(
                                    sprintf
                                        "%s.%s: default %s in desired state and %s in the database"
                                        (QualifiedName.display desired.Name)
                                        d.Name.Text
                                        (if d.HasDefault then "present" else "absent")
                                        (if a.HasDefault then "present" else "absent"))))
                    else
                        None))

        // Indexes are never compared: CREATE INDEX is a separate statement and
        // the declared side therefore always reports none. Comparing them would
        // propose dropping every index in the database; ignoring them silently
        // is the other half of the same mistake, so they are disclosed.
        //
        // Only SECONDARY indexes are reported. PostgreSQL creates an index to
        // back each primary key and unique constraint, naming it after the
        // constraint, and those ARE compared above — listing them here would
        // report an uncompared difference for every table with a key.
        let uncomparedIndexes =
            let constraintNames =
                (match actual.PrimaryKey with Some pk -> [ named pk.ConstraintName ] | None -> [])
                @ (actual.UniqueConstraints |> List.map (fun u -> named u.ConstraintName))

            actual.Indexes
            |> List.filter (fun i -> not (constraintNames |> List.contains (named i.Name)))

        // Everything above establishes PRESENCE. Two expressions Strata cannot
        // read might still differ, and saying so is the difference between a
        // bounded result and a false clean.
        let notCompared =
            let sharedChecks =
                desired.CheckConstraints
                |> List.filter (fun d ->
                    actual.CheckConstraints |> List.exists (fun a -> named a.ConstraintName = named d.ConstraintName))

            let sharedDefaults =
                desired.Columns
                |> List.filter (fun d ->
                    d.HasDefault
                    && actual.Columns
                       |> List.exists (fun a -> Identifier.sameName a.Name d.Name && a.HasDefault))

            [ if not (List.isEmpty sharedChecks) then
                  { Object = desired.Name
                    Reason = NotCompared
                    Detail =
                      sprintf
                          "%d check constraint(s) exist on both sides; their expressions were NOT compared, so they may differ"
                          (List.length sharedChecks) }
              if not (List.isEmpty sharedDefaults) then
                  { Object = desired.Name
                    Reason = NotCompared
                    Detail =
                      sprintf
                          "%d column default(s) exist on both sides; their expressions were NOT compared, so they may differ"
                          (List.length sharedDefaults) }
              if not (List.isEmpty uncomparedIndexes) then
                  { Object = desired.Name
                    Reason = NotModelled
                    Detail =
                      sprintf
                          "%d index(es) exist in the database; CREATE INDEX is not read as desired state, so indexes were NOT compared"
                          (List.length uncomparedIndexes) } ]

        primaryKey @ uniques @ foreignKeys @ checks @ defaults, notCompared

    /// Views and routines, compared by PRESENCE only.
    ///
    /// Their definitions are deliberately not compared. A declared view's
    /// definition text is not recoverable from the parse tree, and PostgreSQL
    /// rewrites what it stores — `SELECT 1 AS x` comes back schema-qualified
    /// and reformatted — so even a perfect deparse would differ from
    /// `pg_get_viewdef` on a view nobody changed. A routine body has the same
    /// problem. Comparing text would report churn on every run.
    ///
    /// A declared view also carries an EMPTY column list, which means "not
    /// knowable from a file", not "no columns". Nothing here may read it as a
    /// fact, which is why only presence is compared.
    ///
    /// Presence alone is still worth having: before this, a view in the
    /// database was suppressed as not-modelled and a view in a file failed to
    /// load, so a project could not express that a view should exist at all.
    let private otherObjectChanges
        (allowDrops: bool)
        (managedSchemas: string list)
        (desiredComplete: bool)
        /// Declared view definitions as the SERVER renders them, keyed by
        /// display name. Produced by executing the declared DDL in a
        /// rolled-back transaction, which is the only way to compare a view
        /// faithfully. A view missing from this list could not be normalised —
        /// no privilege, a read-only target, DDL the server rejected — and is
        /// disclosed rather than assumed equal.
        (normalisedViews: (string * string) list)
        (desired: SchemaObject list)
        (actual: SchemaObject list)
        =
        // Routines are identified by name AND argument types: PostgreSQL
        // allows overloads, so f(int) and f(text) are different objects and
        // matching on name alone would read one as a redefinition of the other.
        let identity (o: SchemaObject) =
            match o with
            | TableObject t -> "table:" + QualifiedName.display t.Name
            | ViewObject v -> "view:" + QualifiedName.display v.Name
            | RoutineObject r ->
                sprintf "routine:%s(%s)" (QualifiedName.display r.Name) (String.concat "," r.ArgumentTypes)

        let nonTable (objects: SchemaObject list) =
            objects
            |> List.filter (fun o ->
                match o with
                | TableObject _ -> false
                | ViewObject _
                | RoutineObject _ -> true)

        let desiredOther = nonTable desired
        let actualOther = nonTable actual

        let kindOf (o: SchemaObject) =
            match o with
            | ViewObject v -> if v.IsMaterialized then "materialized view" else "view"
            | RoutineObject r -> (match r.Kind with Procedure -> "procedure" | Function -> "function")
            | TableObject _ -> "table"

        let created =
            desiredOther
            |> List.filter (fun d -> not (actualOther |> List.exists (fun a -> identity a = identity d)))
            |> List.map (fun d ->
                match d with
                | ViewObject v -> Ok(CreateView v.Name)
                | RoutineObject r -> Ok(CreateRoutine r.Name)
                | TableObject t -> Ok(CreateTable t.Name))

        let removed =
            actualOther
            |> List.filter (fun a -> not (desiredOther |> List.exists (fun d -> identity d = identity a)))
            |> List.map (fun a ->
                let name = SchemaObject.name a

                if not (isManaged managedSchemas name) then
                    Microsoft.FSharp.Core.Error
                        { Object = name
                          Reason = OutsideManagedSchemas
                          Detail = sprintf "%s exists in the database and not in desired state, but its schema is not managed" (kindOf a) }
                elif SchemaObject.scope a = ExtensionOwned then
                    Microsoft.FSharp.Core.Error
                        { Object = name
                          Reason = ExtensionOwnedObject
                          Detail = sprintf "%s is owned by an extension" (kindOf a) }
                elif not desiredComplete then
                    Microsoft.FSharp.Core.Error
                        { Object = name
                          Reason = DesiredStateIncomplete
                          Detail =
                            sprintf
                                "%s is absent from desired state, but desired state did not load completely"
                                (kindOf a) }
                elif not allowDrops then
                    Microsoft.FSharp.Core.Error
                        { Object = name
                          Reason = DropsNotEnabled
                          Detail = sprintf "%s would be dropped; pass --allow-drops to propose removals" (kindOf a) }
                else
                    Ok(
                        UnclassifiedChange(
                            sprintf
                                "%s %s exists in the database and not in desired state"
                                (kindOf a)
                                (QualifiedName.display name))))

        let onBothSides =
            desiredOther
            |> List.choose (fun d ->
                actualOther
                |> List.tryFind (fun a -> identity a = identity d)
                |> Option.map (fun a -> d, a))

        // A view whose declared DDL the server normalised can be compared
        // exactly: both sides now carry PostgreSQL's own rendering.
        let redefinitions =
            onBothSides
            |> List.choose (fun (d, a) ->
                match d, a with
                | ViewObject dv, ViewObject av when not dv.IsMaterialized ->
                    normalisedViews
                    |> List.tryPick (fun (name, definition) ->
                        if name = QualifiedName.display dv.Name then Some definition else None)
                    |> Option.bind (fun declaredDefinition ->
                        if declaredDefinition.Trim() <> av.Definition.Trim() then
                            Some(Ok(ReplaceView dv.Name))
                        else
                            None)
                | _ -> None)

        // Everything on both sides that could NOT be compared. A view that
        // normalised and matched produces nothing here — silence is correct
        // once the comparison actually happened.
        let notCompared =
            onBothSides
            |> List.choose (fun (d, _) ->
                let comparedExactly =
                    match d with
                    | ViewObject dv when not dv.IsMaterialized ->
                        normalisedViews |> List.exists (fun (name, _) -> name = QualifiedName.display dv.Name)
                    | _ -> false

                if comparedExactly then None
                else
                    Some
                        { Object = SchemaObject.name d
                          Reason = NotCompared
                          Detail =
                            sprintf
                                "%s exists on both sides; its definition was NOT compared, so the bodies may differ"
                                (kindOf d) })

        created @ removed @ redefinitions, notCompared

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
        (declarations: (QualifiedName * string) list)
        (normalisedViews: (string * string) list)
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

        let shared =
            desiredTables
            |> List.choose (fun d ->
                actualTables
                |> List.tryFind (fun a -> sameName a.Name d.Name)
                |> Option.map (fun a -> d, a))

        let columnResults =
            shared
            |> List.collect (fun (d, a) -> columnChanges allowDrops managedSchemas desiredComplete d a)

        let constraintResults =
            shared |> List.map (fun (d, a) -> constraintChanges d a)

        let constraintChangeResults = constraintResults |> List.collect fst
        let notCompared = constraintResults |> List.collect snd

        let otherChangeResults, otherNotCompared =
            otherObjectChanges
                allowDrops
                managedSchemas
                desiredComplete
                normalisedViews
                desired.Objects
                actual.Objects

        let unmodelled = otherNotCompared

        let all =
            creations
            @ removals allowDrops managedSchemas desiredComplete desiredTables actualTables
            @ columnResults
            @ constraintChangeResults
            @ otherChangeResults

        let changes =
            all
            |> List.choose (function Ok change -> Some change | Microsoft.FSharp.Core.Error _ -> None)
            |> List.sortBy orderKey

        { Changes = changes
          Statements = changes |> List.map (fun c -> { Change = c; Sql = emit declarations desiredTables c })
          Suppressed =
            (all |> List.choose (function Microsoft.FSharp.Core.Error s -> Some s | Ok _ -> None))
            @ notCompared
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
        | CreateView view -> sprintf "create-view        %s" (QualifiedName.display view)
        | ReplaceView view -> sprintf "replace-view       %s" (QualifiedName.display view)
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
