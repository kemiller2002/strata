namespace Strata.Analysis

open Strata.Semantic.Identity
open Strata.Semantic.Resolution
open Strata.Analysis.StatementReferences

/// Lexical scope resolution over an extraction result.
///
/// Authority for: whether a name in SQL refers to a database object at all.
///
/// Implements DF-STRATA-2026-9B2E. The load-bearing case from
/// EV-STRATA-2026-B9C4:
///
///     WITH orders AS (SELECT 1 AS id) SELECT id FROM orders
///
/// A naive extractor reports a reference to the real `orders` table. It is a
/// false edge: the statement never touches that table. This module removes such
/// names from the object-reference set before anything downstream can turn them
/// into a dependency.
module ScopeResolution =

    /// A name bound by the statement itself rather than by the database.
    type LocalBinding =
        | CteBinding of name: Identifier * level: int
        | AliasBinding of alias: Identifier * target: QualifiedName * level: int
        | TempRelationBinding of name: Identifier * level: int

    [<RequireQualifiedAccess>]
    module LocalBinding =

        let boundName (b: LocalBinding) =
            match b with
            | CteBinding (n, _) -> n
            | AliasBinding (a, _, _) -> a
            | TempRelationBinding (n, _) -> n

        let level (b: LocalBinding) =
            match b with
            | CteBinding (_, l) -> l
            | AliasBinding (_, _, l) -> l
            | TempRelationBinding (_, l) -> l

    /// The set of names a statement binds locally.
    type ScopeChain = { Bindings: LocalBinding list }

    [<RequireQualifiedAccess>]
    module ScopeChain =

        /// Build the scope chain from an extraction.
        ///
        /// A CTE is visible to the query level that defines it and to levels
        /// nested inside it, so a binding at level L is in scope for any
        /// reference at level >= L. This is an approximation of PostgreSQL's
        /// visibility rules, deliberately biased toward treating a name as
        /// locally bound: doing so removes a candidate edge, whereas the
        /// opposite error invents one (RK-001).
        let ofExtraction (extraction: StatementExtraction) : ScopeChain =
            let bindings =
                extraction.Relations
                |> List.collect (fun mention ->
                    let fromRole =
                        match mention.Role with
                        | CommonTableExpressionDefinition ->
                            [ CteBinding(mention.Name.Name, mention.QueryLevel) ]
                        | TemporaryRelationDefinition ->
                            [ TempRelationBinding(mention.Name.Name, mention.QueryLevel) ]
                        | RelationReference -> []

                    let fromAlias =
                        match mention.Alias with
                        | Some alias when mention.Role = RelationReference ->
                            [ AliasBinding(alias, mention.Name, mention.QueryLevel) ]
                        | Some _
                        | None -> []

                    fromRole @ fromAlias)

            { Bindings = bindings }

        /// Find a binding visible to a reference at `level`.
        let tryResolveLocally (name: Identifier) (level: int) (chain: ScopeChain) =
            chain.Bindings
            |> List.filter (fun b ->
                Identifier.sameName (LocalBinding.boundName b) name
                && LocalBinding.level b <= level)
            // Innermost visible binding wins.
            |> List.sortByDescending LocalBinding.level
            |> List.tryHead

    /// What a relation mention turned out to be.
    type RelationOutcome =
        /// Refers to a database object. Only these may become dependency edges,
        /// and only when their `Resolution` is `Resolved`.
        | DatabaseObject of Resolution<QualifiedName>
        /// Refers to a name the statement bound itself. NOT a database object.
        | LocallyBound of LocalBinding
        /// Defines a local binding rather than referring to anything.
        | BindingSite of LocalBinding

    /// Resolve every relation mention against the scope chain.
    ///
    /// `searchPath` is the effective search_path. When it is empty an
    /// unqualified name cannot be resolved to a schema, and the result is
    /// `PartiallyResolved ... SchemaNotQualified` — never a guess at `public`
    /// (RK-006).
    let resolveRelations (searchPath: Identifier list) (extraction: StatementExtraction) =
        let chain = ScopeChain.ofExtraction extraction

        extraction.Relations
        |> List.map (fun mention ->
            match mention.Role with
            | CommonTableExpressionDefinition ->
                mention, BindingSite(CteBinding(mention.Name.Name, mention.QueryLevel))
            | TemporaryRelationDefinition ->
                mention, BindingSite(TempRelationBinding(mention.Name.Name, mention.QueryLevel))
            | RelationReference ->
                // A schema-qualified name can never be shadowed by a CTE or alias,
                // so local lookup only applies to unqualified names.
                if QualifiedName.isSchemaQualified mention.Name then
                    mention, DatabaseObject(Resolved mention.Name)
                else
                    match ScopeChain.tryResolveLocally mention.Name.Name mention.QueryLevel chain with
                    | Some binding -> mention, LocallyBound binding
                    | None ->
                        match searchPath with
                        | [] ->
                            mention,
                            DatabaseObject(PartiallyResolved(mention.Name, SchemaNotQualified))
                        | [ single ] ->
                            mention,
                            DatabaseObject(Resolved(QualifiedName.qualified single mention.Name.Name))
                        | many ->
                            // More than one schema could supply this name. Without a
                            // catalog snapshot Strata cannot tell which; it must not pick.
                            let candidates =
                                many |> List.map (fun s -> QualifiedName.qualified s mention.Name.Name)

                            mention, DatabaseObject(Ambiguous(MultipleCandidates candidates)))

    /// The relation names that may legitimately become dependency edges.
    ///
    /// This is the invariant gate for DF-STRATA-2026-9B2E. A CTE name, an alias,
    /// a temp-relation name, and any non-`Resolved` reference are all excluded.
    let dependencyEdgeCandidates (searchPath: Identifier list) (extraction: StatementExtraction) =
        resolveRelations searchPath extraction
        |> List.choose (fun (_, outcome) ->
            match outcome with
            | DatabaseObject resolution when Resolution.isDependencyEdgeSafe resolution ->
                Resolution.tryValue resolution
            | DatabaseObject _
            | LocallyBound _
            | BindingSite _ -> None)

    /// Resolve a column mention.
    ///
    /// `SELECT *` yields `WildcardNotExpanded` rather than "no columns"
    /// (RK-002). An unqualified column in a multi-relation statement yields
    /// `ColumnNotAttributable` with the candidate relations, never a guess.
    let resolveColumns (extraction: StatementExtraction) =
        let chain = ScopeChain.ofExtraction extraction

        let candidateRelations =
            extraction.Relations
            |> List.filter (fun m -> m.Role = RelationReference)
            |> List.map (fun m -> m.Name)

        extraction.Columns
        |> List.map (fun mention ->
            if mention.IsWildcard then
                mention, Unresolved WildcardNotExpanded
            else
                match mention.Column with
                | None -> mention, Unresolved(AnalyzabilityLimit "column reference carried no name")
                | Some column ->
                    match mention.Qualifier with
                    | Some qualifier ->
                        match ScopeChain.tryResolveLocally qualifier mention.QueryLevel chain with
                        | Some (AliasBinding (_, target, _)) ->
                            mention, Resolved(target, column)
                        | Some (CteBinding _)
                        | Some (TempRelationBinding _) ->
                            // Qualified by a locally bound name: a real reference,
                            // but not to a database object.
                            mention,
                            Unsupported(ConstructNotModelled "column of a locally bound relation")
                        | None ->
                            // Qualifier is a bare relation name.
                            mention,
                            Resolved(QualifiedName.unqualified qualifier, column)
                    | None ->
                        match candidateRelations with
                        | [ single ] -> mention, Resolved(single, column)
                        | [] -> mention, Unresolved(AnalyzabilityLimit "no candidate relation in scope")
                        | many -> mention, Ambiguous(ColumnNotAttributable many))
