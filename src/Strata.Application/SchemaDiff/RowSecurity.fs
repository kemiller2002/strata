namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

module RowSecurity =

    /// What row-level security is doing that the project cannot see from its
    /// own files, for tables the project manages.
    ///
    /// Reported and never changed. This is the residue AFTER `comparePolicies`
    /// has done its work, and the two must not overlap or contradict: whatever
    /// the project declares is compared there, and whatever it does not declare
    /// is reported there for the tables it names. What is left here is the
    /// state no policy controls, and the tables the project names nothing about.
    ///
    /// ## Why the declared side has to be passed in
    ///
    /// It did not used to be, and the message said "Strata does not manage
    /// policies, so the N policies here were NOT compared" for every
    /// RLS-enabled table — including a table whose policy the project had just
    /// declared, whose expression had just been rendered by the server, and
    /// which `comparePolicies` had just compared on every field. The wording
    /// predates policy management and outlived it.
    ///
    /// Telling a reader a comparison did not happen when it did is the same
    /// class of defect as telling them one matched when it was never made
    /// (`ER-008`): both replace what Strata knows with a claim it did not
    /// earn. It is not the safer direction either — a reader who believes a
    /// policy is unmanaged goes looking for the drift by hand, or stops
    /// trusting the disclosures that are true.
    ///
    /// Only tables inside the managed schemas are reported. A project is not
    /// told about row-level security on schemas it does not manage, for the
    /// same reason it is not told about their columns.
    ///
    /// `None` means the state could not be read, which is disclosed rather
    /// than taken for "row-level security is off".
    let disclosures (inputs: Inputs) : Suppression list =
        let managedSchemas = inputs.ManagedSchemas
        let declaredPolicies = inputs.DeclaredPolicies
        let declaredSettings = inputs.DeclaredRowSecurity

        match inputs.ActualRowLevelSecurity with
        // The caller did not ask. Nothing was attempted, so there is nothing to
        // disclose — saying "could not be read" here would be a claim about the
        // database that nobody made.
        | None -> []
        | Some (Microsoft.FSharp.Core.Error message) ->
            [ { Object = QualifiedName.unqualified (Identifier.unquoted "(row level security)")
                Reason = NotCompared
                Detail =
                  sprintf
                      "the database's row-level security state could not be read (%s), so nothing is known about which rows any role can see"
                      message } ]
        | Some (Ok entries) ->
            entries
            |> List.filter (fun e -> DropSafety.isManaged managedSchemas e.Table)
            |> List.sortBy (fun e -> QualifiedName.display e.Table)
            |> List.choose (fun e ->
                let policies =
                    e.Policies
                    |> List.map (fun p ->
                        sprintf
                            "%s (%s%s for %s)"
                            p.Name.Text
                            (if p.IsPermissive then "" else "restrictive ")
                            (match p.Command with
                             | PolicyCommand.All -> "all commands"
                             | PolicyCommand.Select -> "select"
                             | PolicyCommand.Insert -> "insert"
                             | PolicyCommand.Update -> "update"
                             | PolicyCommand.Delete -> "delete")
                            (String.concat ", " p.Roles))
                    |> String.concat "; "

                let count = List.length e.Policies
                let plural = if count = 1 then "policy" else "policies"
                let restrict = if count = 1 then "restricts" else "restrict"
                let wereCompared = if count = 1 then "was" else "were"

                // What the project says about THIS table. A table it names in
                // either a policy file or a setting is a table `comparePolicies`
                // already reasons about, start to finish.
                let declaredHere =
                    declaredPolicies |> List.filter (fun (t, _) -> Names.same t e.Table) |> List.map snd

                let namedByProject =
                    not (List.isEmpty declaredHere)
                    || declaredSettings |> List.exists (fun (t, _) -> Names.same t e.Table)

                let ownerNote =
                    if e.Forced then
                        "FORCED, so it applies to the table's owner too"
                    else
                        "not forced, so the table's owner bypasses every policy"

                match e.Enabled, e.Policies with
                // The state that looks like nothing and denies everything.
                | true, [] when List.isEmpty declaredHere ->
                    Some
                        ("row-level security is ENABLED here and there are no policies, so every row is hidden"
                         + " from every role except the table's owner. The project declares no policy for this"
                         + " table, so nothing here will change that.")
                // Same state, but the plan fixes it. A difference Strata WILL
                // act on does not belong in the list of ones it will not.
                | true, [] -> None
                // The state that looks deliberate and does nothing.
                | false, _ :: _ ->
                    Some(
                        sprintf
                            "row-level security is DISABLED here, so the %d %s on this table %s NOTHING and every role sees every row: %s"
                            count
                            plural
                            restrict
                            policies
                    )
                | true, _ when namedByProject ->
                    // The project declares this table's row-level security, so
                    // `comparePolicies` compared what it declares and reported
                    // what it does not. Claiming anything about comparison here
                    // would contradict it. What is left is the one fact no
                    // policy controls.
                    Some(sprintf "row-level security is enabled and %s." ownerNote)
                | true, _ ->
                    Some(
                        sprintf
                            "row-level security is enabled and %s. The project declares nothing about this table's row-level security, so the %d %s here %s NOT compared and nothing changes them: %s"
                            ownerNote
                            count
                            plural
                            wereCompared
                            policies
                    )
                | false, [] ->
                    // Only reachable when `relforcerowsecurity` is set
                    // without `relrowsecurity`, which PostgreSQL allows and
                    // which does nothing at all.
                    Some "row-level security is FORCED here but not enabled, which has no effect."
                |> Option.map (fun detail ->
                    { Object = e.Table
                      Reason = NotModelled
                      Detail = detail }))

    /// Policies and row-level security a project declares, against what holds.
    ///
    /// ## An undeclared policy is never dropped
    ///
    /// Declaring one index or trigger on a table takes ownership of all of
    /// them, because the cost of removing one wrongly is a rebuild. Policies
    /// get the opposite rule, the one reference-data rows get: a policy the
    /// project does not declare is REPORTED and never dropped, `--allow-drops`
    /// included.
    ///
    /// The blast radius decides it. Dropping a policy does not break a query or
    /// lose a row — it makes rows that were hidden visible to whoever can read
    /// the table, silently, with nothing in the database recording that they
    /// used to be hidden. That is the least recoverable mistake in the
    /// vocabulary, and the one least likely to be noticed. Leaving an
    /// undeclared policy in place can also leave data exposed, but Strata says
    /// so rather than doing it.
    ///
    /// A policy the project DOES declare is another matter: the file names it,
    /// so a definition that differs is replaced.
    ///
    /// ## Expressions
    ///
    /// `Using` and `WithCheck` compare only where the server has rendered the
    /// declared side. A policy whose normalisation failed is disclosed as
    /// not-compared rather than assumed to match: a file's `tenant = 'x'` and
    /// the catalog's `(tenant = 'x'::text)` are the same policy, and nothing
    /// but PostgreSQL can say so.
    let comparePolicies (inputs: Inputs) : FamilyDiff =
        let managedSchemas = inputs.ManagedSchemas
        let declaredPolicies = inputs.DeclaredPolicies
        let declaredSettings = inputs.DeclaredRowSecurity

        match inputs.ActualRowLevelSecurity with
        | None
        | Some (Microsoft.FSharp.Core.Error _) ->
            // Nothing is known about what holds, so nothing is proposed. The
            // disclosure for this case is written by `disclosures`, which sees
            // the same value.
            FamilyDiff.ofDifferences []
        | Some (Ok deployed) ->

        let deployedFor table =
            deployed |> List.tryFind (fun e -> Names.same e.Table table)

        let declaredTables =
            (declaredPolicies |> List.map fst) @ (declaredSettings |> List.map fst)
            |> List.filter (DropSafety.isManaged managedSchemas)
            |> List.distinctBy QualifiedName.display

        let changes =
            declaredTables
            |> List.collect (fun table ->
                let held = deployedFor table
                let heldPolicies = held |> Option.map (fun e -> e.Policies) |> Option.defaultValue []
                let declaredHere = declaredPolicies |> List.filter (fun (t, _) -> Names.same t table) |> List.map snd

                let policyChanges =
                    declaredHere
                    |> List.collect (fun d ->
                        match heldPolicies |> List.tryFind (fun a -> Identifier.sameName a.Name d.Name) with
                        | None -> [ Ok(CreatePolicy(table, d.Name)) ]
                        | Some a ->
                            // `Some ""` is the placeholder for a clause that was
                            // written and NOT rendered by the server, and it is
                            // the third state — not "matches" and not "differs".
                            //
                            // It compared equal in a first version, which made a
                            // failed normalisation indistinguishable from a
                            // policy that matched: a live round-trip changed a
                            // policy's expression and Strata reported zero
                            // changes. Silently reporting a match is the exact
                            // failure this codebase keeps finding, and here it
                            // would leave the deployed policy admitting rows the
                            // file no longer admits.
                            let unrendered =
                                (d.Using = Some "") || (d.WithCheck = Some "")

                            // Everything that does not need the server.
                            let structureDiffers =
                                a.Command <> d.Command
                                || a.IsPermissive <> d.IsPermissive
                                || (a.Roles |> List.map (fun r -> r.ToLowerInvariant()) |> List.sort)
                                   <> (d.Roles |> List.map (fun r -> r.ToLowerInvariant()) |> List.sort)
                                // Whether a clause exists at all is comparable
                                // without rendering it: a policy that gained a
                                // WITH CHECK differs whatever the text says.
                                || Option.isSome d.Using <> Option.isSome a.Using
                                || Option.isSome d.WithCheck <> Option.isSome a.WithCheck

                            let expressionsDiffer =
                                not unrendered && (d.Using <> a.Using || d.WithCheck <> a.WithCheck)

                            if structureDiffers || expressionsDiffer then
                                [ Ok(ReplacePolicy(table, d.Name)) ]
                            elif unrendered then
                                // Structure matches and the expressions could
                                // not be compared. Disclosed, never assumed.
                                [ Microsoft.FSharp.Core.Error
                                      { Object = table
                                        Reason = NotCompared
                                        Detail =
                                          sprintf
                                              "policy %s matches on command, roles and permissiveness, but its USING/WITH CHECK expressions could not be rendered by the server, so they were NOT compared"
                                              d.Name.Text } ]
                            else
                                [])

                let settings = declaredSettings |> List.filter (fun (t, _) -> Names.same t table) |> List.map snd

                let enabled = held |> Option.map (fun e -> e.Enabled) |> Option.defaultValue false
                let forced = held |> Option.map (fun e -> e.Forced) |> Option.defaultValue false

                let settingChanges =
                    [ if List.contains RowSecuritySetting.Enable settings && not enabled then
                          Ok(EnableRowLevelSecurity table)
                      if List.contains RowSecuritySetting.Disable settings && enabled then
                          Ok(DisableRowLevelSecurity table)
                      if List.contains RowSecuritySetting.Force settings && not forced then
                          Ok(ForceRowLevelSecurity table)
                      if List.contains RowSecuritySetting.NoForce settings && forced then
                          Ok(NoForceRowLevelSecurity table) ]

                policyChanges @ settingChanges)

        // Policies on a declared table that the project does not name. Reported,
        // never dropped — see the note above.
        let undeclared =
            declaredTables
            |> List.choose (fun table ->
                let heldPolicies =
                    deployedFor table |> Option.map (fun e -> e.Policies) |> Option.defaultValue []

                let extra =
                    heldPolicies
                    |> List.filter (fun a ->
                        not (
                            declaredPolicies
                            |> List.exists (fun (t, d) -> Names.same t table && Identifier.sameName d.Name a.Name)
                        ))

                if List.isEmpty extra then
                    None
                else
                    Some
                        { Object = table
                          Reason = NotModelled
                          Detail =
                            sprintf
                                "this table carries %d policy/policies the project does not declare, and a policy is never dropped because removing one exposes rows silently: %s"
                                (List.length extra)
                                (extra |> List.map (fun p -> p.Name.Text) |> List.sort |> String.concat ", ") })

        { Differences = changes; Disclosures = undeclared }
