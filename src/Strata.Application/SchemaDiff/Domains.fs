namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

module Domains =

    /// Domains, compared on everything PostgreSQL lets a domain carry.
    ///
    /// ## What can change and what cannot
    ///
    /// Measured against a live server: `ALTER DOMAIN` can set and drop a
    /// DEFAULT, set and drop NOT NULL, add and drop a CHECK, and validate one
    /// added `NOT VALID`. It cannot change the BASE TYPE — `ALTER DOMAIN ...
    /// TYPE` is a syntax error, not a privilege problem — and it cannot change
    /// the COLLATION. Those two are disclosed and never proposed, the same shape
    /// as an enum's value order, and for the same reason: a plan that cannot
    /// execute is worse than a report that says so.
    ///
    /// ## A base-type difference stops EVERY comparison on that domain
    ///
    /// Not only the base type. A domain's default and its check predicates are
    /// rendered THROUGH the base type — declared over `varchar(255)` a check
    /// reads back as `CHECK (((VALUE)::text ~ '@'::text))`, and over `text` as
    /// `CHECK ((VALUE ~ '@'::text))` — so while the base types differ the two
    /// sides can never agree on any of them.
    ///
    /// An earlier version proposed those changes anyway, on the reasoning that a
    /// domain should converge as far as it can. Measured against a live server,
    /// what it actually did was apply seven statements successfully and propose
    /// six of them again on the next run, forever: the re-added constraints came
    /// back rendered over the base type the DATABASE has. That is WI-0054's
    /// churn with a different object, and the only honest answer is that nothing
    /// about this domain was compared.
    ///
    /// ## Nothing here is compared without the server
    ///
    /// A parsed `CREATE DOMAIN` carries no default expression and no check
    /// predicates: neither survives the parse tree, exactly as for a table's
    /// check constraint. So a domain the resolver could not render is not
    /// compared at all — it is disclosed. Diffing the catalog's
    /// `CHECK ((VALUE ~ '@'::text))` against the empty string a bare parse
    /// produces would propose dropping and re-adding every constraint on every
    /// run, forever.
    let compare (policy: RemovalPolicy) (inputs: Inputs) : FamilyDiff =
        let normalisedDomains = inputs.NormalisedDomains
        let declared = SnapshotObjects.domains inputs.Desired
        let deployed = SnapshotObjects.domains inputs.Actual

        /// A declared domain with the server's rendering of its default, its
        /// base type and its check predicates put back on it.
        ///
        /// `None` when the declaration was never rendered, and `None` again when
        /// the server reported a different NUMBER of constraints than the file
        /// wrote. The second should not happen — the shadow builds exactly the
        /// declaration — and if it ever does, pairing the two lists positionally
        /// would attach one constraint's predicate to another constraint's name.
        let rendered (d: DomainType) =
            normalisedDomains
            |> List.tryFind (fun n -> n.Domain = QualifiedName.display d.Name)
            |> Option.bind (fun n ->
                if List.length n.Constraints <> List.length d.Constraints then
                    None
                else
                    Some
                        { d with
                            BaseType = n.BaseType
                            Collation = n.Collation
                            NotNull = n.NotNull
                            Default = n.Default
                            Constraints =
                              List.map2
                                  (fun (c: DomainConstraint) (definition, validated) ->
                                      { c with Definition = definition; IsValidated = validated })
                                  d.Constraints
                                  n.Constraints })

        let unrendered (d: DomainType) =
            Microsoft.FSharp.Core.Error
                { Object = d.Name
                  Reason = NotCompared
                  Detail =
                    "this domain's declaration could not be rendered by the server, so its base type, default and check constraints were not compared. A CREATE DOMAIN carries no predicate text through the parser; only the server can say what it means." }

        let created =
            declared
            |> List.filter (fun d -> not (deployed |> List.exists (fun a -> Names.same a.Name d.Name)))
            // A domain may be declared OVER another one the same plan creates,
            // so the base it stands on has to be created first. Depth in that
            // graph, not name order: `List.sortBy` is stable, so domains at the
            // same depth keep the order they were declared in.
            |> fun creating ->
                let byName =
                    creating |> List.map (fun d -> QualifiedName.display d.Name, d) |> Map.ofList

                let rec depth (seen: Set<string>) (name: string) =
                    if Set.contains name seen then
                        0
                    else
                        match Map.tryFind name byName with
                        | None -> 0
                        | Some d ->
                            let baseName =
                                rendered d
                                |> Option.map (fun r -> r.BaseType)
                                |> Option.defaultValue d.BaseType

                            if baseName <> name && Map.containsKey baseName byName then
                                1 + depth (Set.add name seen) baseName
                            else
                                0

                creating |> List.sortBy (fun d -> depth Set.empty (QualifiedName.display d.Name))
            |> List.map (fun d -> Ok(CreateDomainType d.Name))

        /// Constraint differences for one domain, matched the way table
        /// constraints are: a NAMED declaration claims the deployed constraint
        /// of that name, and an UNNAMED one claims any deployed constraint with
        /// the same predicate. A file that wrote a bare `CHECK (...)` asked for
        /// a constraint that does this and said nothing about what it is called,
        /// and PostgreSQL's `<domain>_check1` is not a name any file can predict.
        let constraintDifferences (name: QualifiedName) (declaredOnes: DomainConstraint list) (deployedOnes: DomainConstraint list) =
            let named =
                declaredOnes |> List.choose (fun c -> c.Name |> Option.map (fun n -> Identifier.folded n, c))

            let claimed =
                named
                |> List.choose (fun (n, _) ->
                    deployedOnes
                    |> List.tryFind (fun a -> a.Name |> Option.exists (fun m -> Identifier.folded m = n)))

            // Each unnamed declaration claims the FIRST deployed constraint with
            // its predicate: two declarations that say the same thing need two
            // deployed constraints, not one counted twice.
            let unnamedAdded, leftover =
                declaredOnes
                |> List.filter (fun c -> Option.isNone c.Name)
                |> List.fold
                    (fun (added, pool) (c: DomainConstraint) ->
                        match Lists.removeFirst (fun (a: DomainConstraint) -> a.Definition = c.Definition) pool with
                        | Some (_, rest) -> added, rest
                        | None -> added @ [ c ], pool)
                    ([], deployedOnes |> List.filter (fun a -> not (List.contains a claimed)))

            let namedChanges =
                named
                |> List.collect (fun (n, c) ->
                    match
                        deployedOnes
                        |> List.tryFind (fun a -> a.Name |> Option.exists (fun m -> Identifier.folded m = n))
                    with
                    | None -> [ Ok(AddDomainConstraint(name, c.Name, c.Definition)) ]
                    | Some a when a.Definition <> c.Definition ->
                        // The name is taken, so the add would fail on it: the
                        // drop has to run first, and `compare` returns
                        // drops ahead of adds for exactly this.
                        [ Ok(DropDomainConstraint(name, a.Name)); Ok(AddDomainConstraint(name, c.Name, c.Definition)) ]
                    | Some a when not a.IsValidated ->
                        // Same predicate, but the values already stored were
                        // never checked against it. A file that declares the
                        // constraint plainly is asking for them to be.
                        [ Ok(ValidateDomainConstraint(name, a.Name |> Option.defaultValue (Identifier.unquoted n))) ]
                    | Some _ -> [])

            let unnamed =
                unnamedAdded |> List.map (fun c -> Ok(AddDomainConstraint(name, None, c.Definition)))

            let removed =
                leftover
                |> List.map (fun a ->
                    // Gated on completeness alone, NOT on `--allow-drops`. That
                    // is the behaviour this had before the decomposition and it
                    // is preserved exactly; whether it is intended is an open
                    // question recorded against STRATA-QUAL-001 (strata#14).
                    DropSafety.removal
                        policy
                        name
                        [ DesiredStateLoaded(
                              sprintf
                                  "%s is on this domain and not in the project, but desired state did not load completely"
                                  (a.Name |> Option.map (fun n -> n.Display) |> Option.defaultValue "a constraint")
                          ) ]
                        (DropDomainConstraint(name, a.Name)))

            // Drops first: a redefined constraint keeps its name, and the add
            // would collide with the one still holding it.
            (removed @ (namedChanges |> List.filter (function Ok (DropDomainConstraint _) -> true | _ -> false)))
            @ (namedChanges |> List.filter (function Ok (DropDomainConstraint _) -> false | _ -> true))
            @ unnamed

        let altered =
            declared
            |> List.collect (fun d ->
                match deployed |> List.tryFind (fun a -> Names.same a.Name d.Name) with
                | None -> []
                | Some a ->
                    match rendered d with
                    | None -> [ unrendered d ]
                    | Some d ->
                        let immovable =
                            [ if d.BaseType <> a.BaseType then
                                  yield
                                      sprintf
                                          "the project declares this domain over %s and the database has it over %s"
                                          d.BaseType
                                          a.BaseType
                              if d.Collation <> a.Collation then
                                  yield
                                      sprintf
                                          "the project declares collation %s and the database has %s"
                                          (d.Collation |> Option.defaultValue "the base type's")
                                          (a.Collation |> Option.defaultValue "the base type's") ]

                        if not (List.isEmpty immovable) then
                            [ Microsoft.FSharp.Core.Error
                                  { Object = d.Name
                                    Reason = NotCompared
                                    Detail =
                                      sprintf
                                          "%s. PostgreSQL has no ALTER DOMAIN ... TYPE and cannot change a domain's collation either, so this cannot converge without recreating the domain and every column declared with it. Nothing else about this domain was compared: its default and its check predicates are rendered through the base type, so while the base types differ the two sides can never agree on them." 
                                          (String.concat "; " immovable) } ]
                        else

                        let nullability =
                            if d.NotNull && not a.NotNull then [ Ok(SetDomainNotNull d.Name) ]
                            elif not d.NotNull && a.NotNull then [ Ok(DropDomainNotNull d.Name) ]
                            else []

                        let defaults =
                            match d.Default, a.Default with
                            | Some declaredDefault, deployedDefault when Some declaredDefault <> deployedDefault ->
                                [ Ok(SetDomainDefault(d.Name, declaredDefault, deployedDefault)) ]
                            | None, Some _ -> [ Ok(DropDomainDefault d.Name) ]
                            | _ -> []

                        nullability
                        @ defaults
                        @ constraintDifferences d.Name d.Constraints a.Constraints)

        let dropped =
            deployed
            |> List.filter (fun a -> not (declared |> List.exists (fun d -> Names.same d.Name a.Name)))
            |> List.map (fun a ->
                DropSafety.objectRemoval
                    policy
                    a.Name
                    a.Scope
                    { OutsideManaged = "a domain outside the project's managed schemas"
                      ExtensionOwned = "an extension owns this domain"
                      Incomplete = "absent from desired state, but desired state did not load completely"
                      DropsNotEnabled = "this domain would be dropped; pass --allow-drops" }
                    (DropDomainType a.Name))

        created @ altered @ dropped |> FamilyDiff.ofDifferences
