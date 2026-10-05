namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

module Grants =

    /// Privileges, compared per object AND per grantee.
    ///
    /// ## Why the ownership rule is finer here than anywhere else
    ///
    /// Indexes and triggers use a coarse rule: declare one on a table and the
    /// project owns them all. That is right when the cost of a wrong removal is
    /// a rebuild. It is wrong for privileges, where a project managing
    /// `app_user` would silently revoke the replication role, the DBA's access
    /// and the monitoring user's along with it.
    ///
    /// So declaring a grant for a grantee on an object takes ownership of THAT
    /// GRANTEE's privileges on THAT object, and of nothing else. A grantee the
    /// project never names is left alone and disclosed — drift Strata can see
    /// and will not act on.
    ///
    /// `actual` is `None` when the ACLs could not be read, which is not the
    /// same as nobody holding anything: that case proposes nothing at all.
    let compare (policy: RemovalPolicy) (inputs: Inputs) : FamilyDiff =
        let declared = inputs.DeclaredGrants

        match inputs.ActualGrants with
        | None ->
            { Differences = []
              Disclosures =
                if List.isEmpty declared then
                    []
                else
                    [ { Object = QualifiedName.unqualified (Identifier.unquoted "(grants)")
                        Reason = NotCompared
                        Detail = "the database's privileges could not be read, so declared grants were NOT compared" } ] }
        | Some deployed ->

        // The kind is part of the key. A schema named `orders` and a table named
        // `orders` are two different things to grant on, and a key that could
        // not tell them apart would match a declared schema grant against a
        // deployed table grant and propose revoking privileges nobody declared.
        let key (g: Grant) = GrantTarget.key g.Target, g.Grantee.ToLowerInvariant()

        // What a declaration takes ownership of. Coarser than the key: a column
        // grant and a table grant on the same relation share a scope, so
        // declaring `GRANT SELECT (id) ON t TO r` claims r's table-wide
        // privileges on `t` too. Without that the table-wide SELECT would be
        // left in place as something the project never mentioned, and r would
        // go on reading every column — the column grant would add access and
        // narrow none, which is the opposite of what the file asked for.
        //
        // The grantee is still part of the scope, so this does not widen
        // ownership across grantees: the replication role is untouched.
        let scope (g: Grant) = GrantTarget.ownershipScope g.Target, g.Grantee.ToLowerInvariant()

        let deployedByKey = deployed |> List.map (fun g -> key g, g) |> Map.ofList
        let declaredByKey = declared |> List.map (fun g -> key g, g) |> Map.ofList
        let claimedScopes = declared |> List.map scope |> Set.ofList

        // Every key the project declares, plus every deployed key inside a scope
        // it claims. The second half is what catches a privilege held at a
        // different key in the same scope — the table-wide grant standing behind
        // a declared column grant — which iterating over `declared` alone never
        // reaches.
        let comparable =
            (declared |> List.map (fun g -> key g, g.Target))
            @ (deployed
               |> List.filter (fun a -> Set.contains (scope a) claimedScopes)
               |> List.map (fun g -> key g, g.Target))
            |> List.distinctBy fst

        let changes =
            comparable
            |> List.collect (fun (k, target) ->
                let grantee =
                    Map.tryFind k declaredByKey
                    |> Option.orElse (Map.tryFind k deployedByKey)
                    |> Option.map (fun g -> g.Grantee)
                    |> Option.defaultValue ""

                let wanted =
                    Map.tryFind k declaredByKey
                    |> Option.map (fun g -> g.Privileges)
                    |> Option.defaultValue []

                let held =
                    Map.tryFind k deployedByKey
                    |> Option.map (fun g -> g.Privileges)
                    |> Option.defaultValue []

                let d = {| Target = target; Grantee = grantee |}

                let missing = wanted |> List.filter (fun p -> not (List.contains p held))
                let extra = held |> List.filter (fun p -> not (List.contains p wanted))

                [ if not (List.isEmpty missing) then
                      Ok(GrantPrivileges(d.Target, d.Grantee, missing))
                  if not (List.isEmpty extra) then
                      DropSafety.removal
                          policy
                          (GrantTarget.name d.Target)
                          [ InManagedSchema(
                                DropSafety.isManagedGrant policy.ManagedSchemas d.Target,
                                sprintf "privileges held by %s lie outside the managed schemas" d.Grantee
                            )
                            DesiredStateLoaded(
                                sprintf
                                    "%s holds %s beyond what is declared, but desired state did not load completely"
                                    d.Grantee
                                    (String.concat ", " extra)
                            )
                            DropsEnabled(
                                sprintf
                                    "%s holds %s beyond what is declared; pass --allow-drops to revoke"
                                    d.Grantee
                                    (String.concat ", " extra)
                            ) ]
                          (RevokePrivileges(d.Target, d.Grantee, extra)) ])

        // Grantees the project never mentions. Reported, never revoked — this
        // is the whole point of the per-grantee rule, so it is stated in the
        // output rather than left to the reader to infer from silence.
        let unclaimed =
            deployed
            |> List.filter (fun a -> not (Set.contains (scope a) claimedScopes))
            |> List.groupBy (fun a -> GrantTarget.key a.Target)
            |> List.map (fun (_, gs) ->
                { Object = GrantTarget.name (List.head gs).Target
                  Reason = NotModelled
                  Detail =
                    sprintf
                        "%s holds privileges here and the project declares none for them, so they were NOT compared and nothing is revoked: %s"
                        (gs |> List.map (fun g -> g.Grantee) |> List.distinct |> List.sort |> String.concat ", ")
                        (gs
                         |> List.map (fun g -> sprintf "%s=%s" g.Grantee (String.concat "," g.Privileges))
                         |> String.concat "; ") })

        // Privileges held WITH GRANT OPTION.
        //
        // These are NOT a difference: the privilege itself matches what the
        // file declares, so nothing is proposed and the project converges. What
        // does not match is the power to pass the privilege on, which the model
        // does not hold — a file cannot ask for it (the loader refuses `WITH
        // GRANT OPTION`) and so the diff can never take it away.
        //
        // Reported for exactly that reason. "r holds SELECT" and "r holds
        // SELECT and can give it to anyone" are different states (ER-008), and
        // silently treating the second as the first would have Strata report a
        // converged project while a grantee widens access on its own.
        let grantable =
            deployed
            |> List.filter (fun a -> not (List.isEmpty a.Grantable))
            |> List.map (fun a ->
                { Object = GrantTarget.name a.Target
                  Reason = NotModelled
                  Detail =
                    sprintf
                        "%s holds %s WITH GRANT OPTION and can pass %s on to others; Strata does not model the grant option, so this was NOT compared and nothing removes it"
                        a.Grantee
                        (String.concat ", " a.Grantable)
                        (if List.length a.Grantable = 1 then "it" else "them") })

        { Differences = changes; Disclosures = unclaimed @ grantable }
