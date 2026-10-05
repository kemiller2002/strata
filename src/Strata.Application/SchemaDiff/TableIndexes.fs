namespace Strata.Application.SchemaDiff

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Analysis.ProposedChange

/// Secondary indexes and triggers on a table present on both sides.
///
/// Both follow the same ownership rule: a project that declares NONE of a
/// category anywhere is saying nothing about it, not that the table should
/// have none; one that declares any takes ownership of all of them. Without
/// that distinction this would propose dropping every index and trigger in
/// every existing database. `DropSafety.claimsCategory` decides it.
module TableIndexes =

    let private named (name: Identifier) = Identifier.folded name

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

                            if columnsOf d <> columnsOf a || d.IsUnique <> a.IsUnique then
                                Some(
                                    Ok(
                                        UnclassifiedChange(
                                            sprintf
                                                "%s: index '%s' covers (%s)%s in desired state and (%s)%s in the database"
                                                (QualifiedName.display desired.Name)
                                                d.Name.Text
                                                (columnsOf d)
                                                (if d.IsUnique then " unique" else "")
                                                (columnsOf a)
                                                (if a.IsUnique then " unique" else ""))))
                            else
                                None))

                created @ dropped @ redefined

        let uncompared = if indexesDeclared then [] else deployedIndexes

        changes,
        (if List.isEmpty uncompared then
             None
         else
             Some
                 { Object = desired.Name
                   Reason = NotModelled
                   Detail =
                     sprintf
                         "%d index(es) exist in the database and this project declares none, so indexes were NOT compared. Declaring any index file takes ownership of them."
                         (List.length uncompared) })

    /// Everything the model carries about a trigger, in one comparable string.
    /// A condition is compared by PRESENCE only, because its text is
    /// unavailable on both sides.
    let private triggerIdentity (t: Trigger) =
        String.concat
            "|"
            [ (match t.Timing with
               | TriggerTiming.Before -> "before"
               | TriggerTiming.After -> "after"
               | TriggerTiming.InsteadOf -> "instead")
              t.Events |> String.concat ","
              (match t.Level with
               | TriggerLevel.Row -> "row"
               | TriggerLevel.Statement -> "statement")
              t.UpdateColumns |> List.map named |> String.concat ","
              // The function is matched on its NAME only. A declared trigger
              // names `touch()` and the catalog reports `public.touch`, so an
              // unqualified declaration would never match a qualified
              // deployment however identical they are.
              named t.Function.Name
              t.Arguments |> String.concat ","
              (if t.HasCondition then "when" else "always") ]

    /// Trigger differences, the disclosure when triggers were not compared,
    /// and the disclosure for WHEN clauses that could not be.
    ///
    /// Note what is NOT here: an internal trigger. Every foreign key is
    /// implemented as a pair of them, and the catalog query filters them out by
    /// `tgisinternal` — without that filter a table with two foreign keys would
    /// show four undeclared triggers to drop.
    let triggers (policy: RemovalPolicy) (triggersDeclared: bool) (desired: Table) (actual: Table) =
        let changes =
            if not triggersDeclared then
                []
            else
                let created =
                    desired.Triggers
                    |> List.filter (fun d ->
                        not (actual.Triggers |> List.exists (fun a -> named a.Name = named d.Name)))
                    |> List.map (fun d -> Ok(CreateTrigger(desired.Name, d.Name)))

                let dropped =
                    actual.Triggers
                    |> List.filter (fun a ->
                        not (desired.Triggers |> List.exists (fun d -> named d.Name = named a.Name)))
                    |> List.map (fun a ->
                        DropSafety.removal
                            policy
                            desired.Name
                            [ DesiredStateLoaded(
                                  sprintf
                                      "trigger '%s' is absent from desired state, but desired state did not load completely"
                                      a.Name.Text
                              )
                              DropsEnabled(sprintf "trigger '%s' would be dropped; pass --allow-drops" a.Name.Text) ]
                            (DropTrigger(desired.Name, a.Name)))

                // Same name, different definition. Unlike an index, this IS
                // proposed as a change: a trigger is dropped and recreated from
                // the declaring file, which is exactly what the file asks for.
                let redefined =
                    desired.Triggers
                    |> List.choose (fun d ->
                        actual.Triggers
                        |> List.tryFind (fun a -> named a.Name = named d.Name)
                        |> Option.bind (fun a ->
                            if triggerIdentity d <> triggerIdentity a then
                                Some(Ok(ReplaceTrigger(desired.Name, d.Name)))
                            else
                                None))

                created @ dropped @ redefined

        let uncompared = if triggersDeclared then [] else actual.Triggers

        // A trigger with a WHEN clause on both sides, whose definitions
        // otherwise agree. Its condition may still differ and nothing here can
        // tell: `pg_get_expr` will not render `tgqual` at all.
        let uncomparedConditions =
            if not triggersDeclared then
                []
            else
                desired.Triggers
                |> List.filter (fun d ->
                    d.HasCondition
                    && actual.Triggers
                       |> List.exists (fun a ->
                           named a.Name = named d.Name
                           && a.HasCondition
                           && triggerIdentity a = triggerIdentity d))

        changes,
        (if List.isEmpty uncompared then
             None
         else
             Some
                 { Object = desired.Name
                   Reason = NotModelled
                   Detail =
                     sprintf
                         "%d trigger(s) exist in the database and this project declares none, so triggers were NOT compared. Declaring any trigger file takes ownership of them."
                         (List.length uncompared) }),
        (if List.isEmpty uncomparedConditions then
             None
         else
             Some
                 { Object = desired.Name
                   Reason = NotCompared
                   Detail =
                     sprintf
                         "%d trigger(s) carry a WHEN clause on both sides; the conditions were NOT compared, so they may differ"
                         (List.length uncomparedConditions) })
