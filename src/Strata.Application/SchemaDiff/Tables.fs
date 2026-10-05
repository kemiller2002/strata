namespace Strata.Application.SchemaDiff

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Analysis.ProposedChange

/// How the desired tables pair with the deployed ones, once declared renames
/// are resolved. Computed once per run and shared by every table-level family
/// and by ordering, so they cannot disagree about which table is which.
type TableMatching =
    { Desired: Table list
      Actual: Table list
      /// `(previous name, new name)` for each declared rename that still applies.
      Renamed: (QualifiedName * QualifiedName) list
      /// In desired state and not in the database, renames excluded. Includes
      /// the tables withheld as cyclic: their depth is still computed.
      Created: Table list
      /// Display names of created tables whose foreign keys form a cycle among
      /// themselves, in name order.
      Cyclic: string list
      /// Present on both sides under the same name.
      Shared: (Table * Table) list }

/// Tables, their columns and declared renames.
module Tables =

    /// For each table being created, the OTHER tables being created that its
    /// foreign keys reference, by display name.
    ///
    /// Only tables being CREATED count. A reference to a table that already
    /// exists imposes no order, and neither does a self-reference: PostgreSQL
    /// accepts a foreign key to the table being defined. One graph, read by
    /// both cycle detection here and creation depth in `Ordering`.
    let createdParents (creating: Table list) : Map<string, string list> =
        let byName =
            creating |> List.map (fun t -> QualifiedName.display t.Name, t) |> Map.ofList

        byName
        |> Map.map (fun name t ->
            t.ForeignKeys
            |> List.map (fun f -> QualifiedName.display f.ReferencedTable)
            |> List.filter (fun p -> p <> name && Map.containsKey p byName)
            |> List.distinct)

    /// Tables being created whose foreign keys form a cycle among themselves.
    ///
    /// No order of plain CREATE TABLE statements satisfies one. Strata says so
    /// rather than emitting an order it knows cannot execute: the creations are
    /// withheld and the reason is reported, which is the same choice every
    /// other thing it cannot do faithfully gets.
    let private cyclicCreations (creating: Table list) =
        let parents = createdParents creating

        let parentsOf (name: string) =
            Map.tryFind name parents |> Option.defaultValue []

        let rec reaches (seen: Set<string>) (target: string) (name: string) =
            parentsOf name
            |> List.exists (fun p ->
                p = target || (not (Set.contains p seen) && reaches (Set.add p seen) target p))

        parents
        |> Map.toList
        |> List.map fst
        |> List.filter (fun name -> reaches (Set.singleton name) name name)

    let matching (inputs: Inputs) : TableMatching =
        let desiredTables = SnapshotObjects.tables inputs.Desired
        let actualTables = SnapshotObjects.tables inputs.Actual

        // An object whose file declares a previous name that EXISTS in the
        // database is a rename, not a create-plus-drop. Resolving it here keeps
        // both halves out of the plan: the create is replaced, and the drop is
        // suppressed because the old name is no longer treated as absent.
        let renamedTables =
            desiredTables
            |> List.choose (fun d ->
                inputs.Renames
                |> List.tryFind (fun r -> Names.same r.Object d.Name)
                |> Option.bind (fun r -> r.RenamedFrom)
                |> Option.bind (fun from ->
                    if
                        actualTables |> List.exists (fun a -> Names.same a.Name from)
                        && not (actualTables |> List.exists (fun a -> Names.same a.Name d.Name))
                    then
                        Some(from, d.Name)
                    else
                        // The old name is not in the database, or the new name
                        // already is. Either way the rename already happened or
                        // never applied, and the annotation is spent.
                        None))

        let created =
            desiredTables
            |> List.filter (fun d ->
                not (actualTables |> List.exists (fun a -> Names.same a.Name d.Name))
                && not (renamedTables |> List.exists (fun (_, to') -> Names.same to' d.Name)))

        { Desired = desiredTables
          Actual = actualTables
          Renamed = renamedTables
          Created = created
          // Two tables that reference each other cannot both be created first.
          // Withheld with a reason rather than emitted in an order that is
          // known not to execute.
          Cyclic = cyclicCreations created
          Shared =
            desiredTables
            |> List.choose (fun d ->
                actualTables
                |> List.tryFind (fun a -> Names.same a.Name d.Name)
                |> Option.map (fun a -> d, a)) }

    /// Column-level differences for a table present on both sides.
    let private columnChanges (policy: RemovalPolicy) (desired: Table) (actual: Table) =
        let managed = DropSafety.isManaged policy.ManagedSchemas desired.Name

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
                DropSafety.removal
                    policy
                    desired.Name
                    [ InManagedSchema(
                          managed,
                          sprintf "column '%s' is absent from desired state, but the schema is not managed" a.Name.Text
                      )
                      DesiredStateLoaded(
                          sprintf
                              "column '%s' is absent from desired state, but desired state did not load completely"
                              a.Name.Text
                      )
                      DropsEnabled(
                          sprintf "column '%s' would be dropped; pass --allow-drops to propose removals" a.Name.Text
                      ) ]
                    (DropColumn(desired.Name, a.Name)))

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

    /// Column renames, then the columns a shared or renamed table differs in.
    let private columnResults (policy: RemovalPolicy) (inputs: Inputs) (m: TableMatching) =
        // A table matched by its NEW name after a rename still needs its columns
        // compared, so renamed pairs join the shared set.
        let sharedIncludingRenamed =
            m.Shared
            @ (m.Renamed
               |> List.choose (fun (from, to') ->
                   match
                       m.Desired |> List.tryFind (fun d -> Names.same d.Name to'),
                       m.Actual |> List.tryFind (fun a -> Names.same a.Name from)
                   with
                   | Some d, Some a -> Some(d, a)
                   | _ -> None))

        let columnRenamesFor (d: Table) (a: Table) =
            inputs.Renames
            |> List.tryFind (fun r -> Names.same r.Object d.Name)
            |> Option.map (fun r ->
                r.Columns
                |> List.filter (fun (from, to') ->
                    // Only a rename whose OLD name is in the database and whose
                    // NEW name is not. Anything else is already applied.
                    a.Columns |> List.exists (fun c -> Identifier.sameName c.Name (Identifier.unquoted from))
                    && not (a.Columns |> List.exists (fun c -> Identifier.sameName c.Name (Identifier.unquoted to'))))
                |> List.map (fun (from, to') ->
                    // The table is named by its CURRENT name, not its desired
                    // one, for two reasons that point the same way: the corpus
                    // records dependencies against the name that exists today,
                    // so the gate can only find readers under it; and column
                    // renames are ordered BEFORE a table rename, so at
                    // execution time the table still answers to it.
                    Ok(RenameColumn(a.Name, Identifier.unquoted from, Identifier.unquoted to'))))
            |> Option.defaultValue []

        sharedIncludingRenamed
        |> List.collect (fun (d, a) ->
            let renamed = columnRenamesFor d a

            let renamedFrom =
                renamed
                |> List.choose (function
                    | Ok (RenameColumn (_, from, _)) -> Some from
                    | _ -> None)

            let renamedTo =
                renamed
                |> List.choose (function
                    | Ok (RenameColumn (_, _, to')) -> Some to'
                    | _ -> None)

            // A renamed column must not also appear as a drop of the old name
            // and an add of the new one, so both are hidden from the column
            // comparison.
            let actualMinus =
                { a with
                    Columns =
                        a.Columns
                        |> List.filter (fun c -> not (renamedFrom |> List.exists (Identifier.sameName c.Name))) }

            let desiredMinus =
                { d with
                    Columns =
                        d.Columns
                        |> List.filter (fun c -> not (renamedTo |> List.exists (Identifier.sameName c.Name))) }

            renamed @ columnChanges policy desiredMinus actualMinus)

    let compare (policy: RemovalPolicy) (inputs: Inputs) (m: TableMatching) : FamilyDiff =
        let isCyclic (t: Table) =
            m.Cyclic |> List.contains (QualifiedName.display t.Name)

        let creations =
            m.Created
            |> List.map (fun d ->
                if isCyclic d then
                    Result.Error
                        { Object = d.Name
                          Reason = NotModelled
                          Detail =
                            sprintf
                                "this table's foreign keys form a cycle with %s, and no order of plain CREATE TABLE statements satisfies one. Create them by hand, or declare one side's foreign key as a separate ALTER TABLE."
                                (m.Cyclic
                                 |> List.filter (fun n -> n <> QualifiedName.display d.Name)
                                 |> String.concat ", ") }
                else
                    Ok(CreateTable d.Name))

        let tableRenames = m.Renamed |> List.map (fun (from, to') -> Ok(RenameTable(from, to')))

        // A table being created needs its declared indexes and triggers created
        // too. Their comparison runs only for tables present on BOTH sides, so
        // without this a new table's indexes would need a second apply — the
        // first run created the table and reported the index as a change it had
        // not made. A table withheld as cyclic is not being created, so nothing
        // that depends on its existence may be proposed either.
        let newTables = m.Created |> List.filter (isCyclic >> not)

        let indexesForNewTables =
            newTables
            |> List.collect (fun d -> d.Indexes |> List.map (fun i -> Ok(CreateIndex(d.Name, i.Name))))

        let triggersForNewTables =
            newTables
            |> List.collect (fun d -> d.Triggers |> List.map (fun t -> Ok(CreateTrigger(d.Name, t.Name))))

        // Objects present in the database and absent from desired state. This
        // is the dangerous direction and the only one that can destroy data;
        // the guards are `DropSafety`'s. A table renamed away is not absent.
        let removals =
            m.Actual
            |> List.filter (fun a -> not (m.Renamed |> List.exists (fun (from, _) -> Names.same a.Name from)))
            |> List.filter (fun a -> not (m.Desired |> List.exists (fun d -> Names.same d.Name a.Name)))
            |> List.map (fun a ->
                DropSafety.objectRemoval
                    policy
                    a.Name
                    a.Scope
                    { OutsideManaged =
                        "present in the database and absent from desired state, but its schema is not managed by this project"
                      ExtensionOwned = "owned by an extension; absence from desired state does not make it removable"
                      Incomplete =
                        "absent from desired state, but desired state did not load completely, so its absence is not evidence it should be dropped"
                      DropsNotEnabled = "would be dropped; pass --allow-drops to propose removals" }
                    (DropTable a.Name))

        creations
        @ tableRenames
        @ indexesForNewTables
        @ triggersForNewTables
        @ removals
        @ columnResults policy inputs m
        |> FamilyDiff.ofDifferences
