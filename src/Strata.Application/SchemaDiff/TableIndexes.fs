namespace Strata.Application.SchemaDiff

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Analysis.ProposedChange

/// Secondary indexes on a table present on both sides.
///
/// A project that declares NO index anywhere is saying nothing about indexes,
/// not that the table should have none; one that declares any takes ownership
/// of all of them. Without that distinction this would propose dropping every
/// index in every existing database. `DropSafety.claimsCategory` decides it.
/// Triggers follow the same rule, in `TableTriggers`.
module TableIndexes =

    let private named (name: Identifier) = Identifier.folded name

    /// A declared table with its partial indexes' predicates as the server
    /// rendered them. A predicate the server did not render stays `None`, and
    /// is then compared by presence only and disclosed.
    let withRenderedPredicates (rendered: NormalisedIndex list) (table: Table) =
        let predicateOf (index: Index) =
            rendered
            |> List.tryPick (fun n ->
                if n.Table = QualifiedName.display table.Name && n.Index = index.Name.Text then Some n.Predicate
                else None)

        { table with
            Indexes = table.Indexes |> List.map (fun i -> { i with Predicate = predicateOf i |> Option.orElse i.Predicate }) }

    /// Whether both sides' predicates were READ, and so their content compared.
    let private predicatesCompared (d: Index) (a: Index) = d.Predicate.IsSome && a.Predicate.IsSome

    /// Index differences, and the disclosure when indexes were not compared.
    let indexes (policy: RemovalPolicy) (indexesDeclared: bool) (desired: Table) (actual: Table) =
        // PostgreSQL creates an index to back each primary key and unique
        // constraint, naming it after the constraint. Those are compared as
        // CONSTRAINTS, so they are excluded here — proposing to drop one would
        // be proposing to drop its constraint by a side door. Every catalog
        // constraint has a name, so `choose` drops nothing here; it is how the
        // shared optional type is read on the actual side.
        let constraintBackedNames =
            (match actual.PrimaryKey with
             | Some pk -> pk.ConstraintName |> Option.map named |> Option.toList
             | None -> [])
            @ (actual.UniqueConstraints |> List.choose (fun u -> u.ConstraintName |> Option.map named))

        let secondaryIndexes (t: Table) =
            t.Indexes |> List.filter (fun i -> not (constraintBackedNames |> List.contains (named i.Name)))

        let declaredIndexes = secondaryIndexes desired
        let deployedIndexes = secondaryIndexes actual

        let changes =
            if not indexesDeclared then
                []
            else
                let created =
                    declaredIndexes
                    |> List.filter (fun d -> not (deployedIndexes |> List.exists (fun a -> named a.Name = named d.Name)))
                    |> List.map (fun d -> Ok(CreateIndex(desired.Name, d.Name)))

                let dropped =
                    deployedIndexes
                    |> List.filter (fun a -> not (declaredIndexes |> List.exists (fun d -> named d.Name = named a.Name)))
                    |> List.map (fun a ->
                        DropSafety.removal
                            policy
                            desired.Name
                            [ DesiredStateLoaded(
                                  sprintf
                                      "index '%s' is absent from desired state, but desired state did not load completely"
                                      a.Name.Text
                              )
                              DropsEnabled(sprintf "index '%s' would be dropped; pass --allow-drops" a.Name.Text) ]
                            (DropIndex(desired.Name, a.Name)))

                // Same name, different columns or uniqueness. Reported rather
                // than silently rebuilt: dropping and recreating an index is a
                // different operation with a different cost.
                let redefined =
                    declaredIndexes
                    |> List.choose (fun d ->
                        deployedIndexes
                        |> List.tryFind (fun a -> named a.Name = named d.Name)
                        |> Option.bind (fun a ->
                            let columnsOf (i: Index) = i.Columns |> List.map named |> String.concat ","
                            let shapeOf (i: Index) = i.Unmodelled |> List.distinct |> List.sort

                            let describe (i: Index) =
                                (if List.isEmpty (shapeOf i) then "" else sprintf " with %s" (String.concat ", " (shapeOf i)))
                                + (if predicatesCompared d a then sprintf " where %s" i.Predicate.Value else "")

                            // Two predicates both READ are compared by their
                            // rendered content, not by both being there.
                            let predicateDiffers =
                                predicatesCompared d a && d.Predicate.Value.Trim() <> a.Predicate.Value.Trim()

                            if columnsOf d <> columnsOf a
                               || d.IsUnique <> a.IsUnique
                               || shapeOf d <> shapeOf a
                               || predicateDiffers then
                                Some(
                                    Ok(
                                        UnclassifiedChange(
                                            sprintf
                                                "%s: index '%s' covers (%s)%s%s in desired state and (%s)%s%s in the database"
                                                (QualifiedName.display desired.Name)
                                                d.Name.Text
                                                (columnsOf d)
                                                (if d.IsUnique then " unique" else "")
                                                (describe d)
                                                (columnsOf a)
                                                (if a.IsUnique then " unique" else "")
                                                (describe a))))
                            else
                                None))

                created @ dropped @ redefined

        let uncompared = if indexesDeclared then [] else deployedIndexes

        // The same KINDS of unmodelled content on both sides (a sort order and
        // a sort order): their content was not read, so say so. A predicate
        // both sides rendered WAS read, and compared above.
        let shapesNotCompared =
            if not indexesDeclared then
                []
            else
                declaredIndexes
                |> List.filter (fun d ->
                    deployedIndexes
                    |> List.exists (fun a ->
                        let shape (i: Index) = i.Unmodelled |> List.distinct |> List.sort
                        let read = if predicatesCompared d a then [ "a predicate" ] else []

                        named a.Name = named d.Name
                        && shape a = shape d
                        && not (List.isEmpty (List.except read (shape d)))))

        changes,
        [ if not (List.isEmpty uncompared) then
              { Object = desired.Name
                Reason = NotModelled
                Detail =
                  sprintf
                      "%d index(es) exist in the database and this project declares none, so indexes were NOT compared. Declaring any index file takes ownership of them."
                      (List.length uncompared) }
          if not (List.isEmpty shapesNotCompared) then
              { Object = desired.Name
                Reason = NotCompared
                Detail =
                  sprintf
                      "%d index(es) carry a predicate, sort order, expression or other content on both sides (%s); only its PRESENCE was compared, so the content may differ"
                      (List.length shapesNotCompared)
                      (shapesNotCompared |> List.map (fun i -> i.Name.Text) |> String.concat ", ") } ]
