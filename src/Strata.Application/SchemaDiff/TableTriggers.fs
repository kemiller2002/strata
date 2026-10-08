namespace Strata.Application.SchemaDiff

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Analysis.ProposedChange

/// Triggers on a table present on both sides.
///
/// The ownership rule indexes follow: a project that declares NONE anywhere is
/// saying nothing about them; one that declares any takes ownership of all of
/// them on the tables it declares. Split from `TableIndexes`, which it shared
/// a file with, so each stays inside its line budget.
module TableTriggers =

    let private named (name: Identifier) = Identifier.folded name

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
