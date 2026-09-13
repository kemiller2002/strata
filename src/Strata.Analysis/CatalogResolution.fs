namespace Strata.Analysis

open Strata.Semantic.Identity
open Strata.Semantic.Resolution
open Strata.Semantic.Schema
open Strata.Analysis.StatementReferences
open Strata.Analysis.ScopeResolution

/// Resolution against a catalog snapshot.
///
/// Authority for: turning the references scope resolution could not settle into
/// resolved ones, using what the database actually contains.
///
/// `ScopeResolution` works from the statement alone and therefore has to leave
/// unqualified names and wildcards open. This module closes what a catalog can
/// close — and, critically, closes `SELECT *` (`RK-002`), which is the case
/// where leaving it open silently understates column dependencies.
module CatalogResolution =

    /// A relation in the snapshot, indexed for lookup by folded name.
    type private Indexed =
        { Qualified: QualifiedName
          Columns: Identifier list }

    let private indexSnapshot (snapshot: SchemaSnapshot) =
        snapshot.Objects
        |> List.choose (fun o ->
            match o with
            | TableObject t ->
                Some { Qualified = t.Name; Columns = t.Columns |> List.map (fun c -> c.Name) }
            | ViewObject v ->
                Some { Qualified = v.Name; Columns = v.Columns |> List.map (fun c -> c.Name) }
            // None of these has columns to resolve a reference against. A
            // sequence is named in a DEFAULT and an enum or domain type in a
            // column's TYPE; none is ever selected from with a column list.
            | RoutineObject _
            | SequenceObject _
            | EnumObject _
            | DomainObject _ -> None)

    let private matchesName (indexed: Indexed) (name: Identifier) =
        Identifier.sameName indexed.Qualified.Name name

    let private inSchema (indexed: Indexed) (schema: Identifier) =
        match indexed.Qualified.Schema with
        | Some s -> Identifier.sameName s schema
        | None -> false

    /// Resolve an unqualified relation name against the catalog and search_path.
    ///
    /// Unlike `ScopeResolution`'s search_path handling, this knows which schemas
    /// actually CONTAIN the name, so a two-entry search_path where only one
    /// schema has the table resolves cleanly instead of being `Ambiguous`.
    /// Where two schemas both have it, it stays `Ambiguous` — the catalog has
    /// made the ambiguity real rather than hypothetical.
    let resolveRelationName
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (name: QualifiedName)
        : Resolution<QualifiedName> =

        let index = indexSnapshot snapshot

        match name.Schema with
        | Some schema ->
            match index |> List.tryFind (fun i -> matchesName i name.Name && inSchema i schema) with
            | Some found -> Resolved found.Qualified
            | None ->
                // Qualified, well-formed, and not in the snapshot. This is NOT
                // "the object does not exist" — the snapshot may be incomplete,
                // and RK-004 showed objects can be unreadable. It is unresolved
                // against THIS snapshot, which is a different claim.
                Unresolved(AnalyzabilityLimit "not present in the catalog snapshot")
        | None ->
            let candidates =
                searchPath
                |> List.choose (fun schema ->
                    index |> List.tryFind (fun i -> matchesName i name.Name && inSchema i schema))

            match candidates with
            | [ single ] -> Resolved single.Qualified
            | [] ->
                if List.isEmpty searchPath then
                    PartiallyResolved(name, SchemaNotQualified)
                else
                    Unresolved(AnalyzabilityLimit "not found in any schema on the search_path")
            | many -> Ambiguous(MultipleCandidates(many |> List.map (fun i -> i.Qualified)))

    /// A column reference, after catalog resolution.
    type ResolvedColumn =
        { Relation: QualifiedName
          Column: Identifier
          /// True when this column came from expanding `*` rather than from an
          /// explicit mention. Kept because "the query names this column" and
          /// "the query reads this column via *" are different facts, and a
          /// human reviewing an impact report deserves to see which.
          FromWildcard: bool }

    /// Expand `SELECT *` into the columns the catalog says it covers.
    ///
    /// This is the `RK-002` mitigation. Without it, a `SELECT *` reader of a
    /// column is reported as referencing no columns, so "drop column blocked
    /// because N readers found" undercounts precisely on the destructive
    /// operation the guardrail exists to stop.
    ///
    /// If the wildcard's relation cannot be resolved, the result stays
    /// `Unresolved (WildcardNotExpanded)`. It never silently yields nothing.
    let expandWildcard
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (relations: QualifiedName list)
        (qualifier: Identifier option)
        : Resolution<ResolvedColumn list> =

        let index = indexSnapshot snapshot

        let targetRelations =
            match qualifier with
            | Some q ->
                // `t.*` — only that relation.
                relations |> List.filter (fun r -> Identifier.sameName r.Name q)
            | None ->
                // Bare `*` — every relation in the FROM clause.
                relations

        match targetRelations with
        | [] -> Unresolved WildcardNotExpanded
        | _ ->
            let expanded =
                targetRelations
                |> List.collect (fun relation ->
                    match resolveRelationName snapshot searchPath relation with
                    | Resolved resolved ->
                        match index |> List.tryFind (fun i -> i.Qualified = resolved) with
                        | Some found ->
                            found.Columns
                            |> List.map (fun c -> Some { Relation = resolved; Column = c; FromWildcard = true })
                        | None -> [ None ]
                    | PartiallyResolved _
                    | Ambiguous _
                    | Unsupported _
                    | Unresolved _ -> [ None ])

            if expanded |> List.exists Option.isNone then
                // At least one relation could not be expanded. Reporting the
                // partial list would understate the dependency set, which is the
                // exact failure this function exists to prevent.
                Unresolved WildcardNotExpanded
            else
                Resolved(expanded |> List.choose id)

    /// Resolve a column against the catalog when the statement did not qualify it.
    ///
    /// Where `ScopeResolution` must return `Ambiguous` for any unqualified column
    /// in a multi-relation query, the catalog can often settle it: if only one
    /// of the relations actually HAS a column of that name, that is the one.
    /// If several do, it stays `Ambiguous` — and PostgreSQL agrees, raising
    /// "column reference is ambiguous" for the same shape (EV-STRATA-2026-C5D2).
    let resolveUnqualifiedColumn
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (relations: QualifiedName list)
        (column: Identifier)
        : Resolution<ResolvedColumn> =

        let index = indexSnapshot snapshot

        let owners =
            relations
            |> List.choose (fun relation ->
                match resolveRelationName snapshot searchPath relation with
                | Resolved resolved ->
                    index
                    |> List.tryFind (fun i ->
                        i.Qualified = resolved
                        && i.Columns |> List.exists (fun c -> Identifier.sameName c column))
                    |> Option.map (fun i -> i.Qualified)
                | PartiallyResolved _
                | Ambiguous _
                | Unsupported _
                | Unresolved _ -> None)

        match owners with
        | [ single ] -> Resolved { Relation = single; Column = column; FromWildcard = false }
        | [] -> Unresolved(AnalyzabilityLimit "no relation in scope declares this column")
        | many -> Ambiguous(ColumnNotAttributable many)

    /// Resolve a column that names its own qualifier.
    ///
    /// The qualifier is an alias, a bare relation name, or a CTE name. Only the
    /// first two can yield a catalog column; a CTE-qualified column refers to a
    /// query-local result, not a database object (RK-001), and returning a
    /// catalog column for one would be a fabricated dependency.
    let resolveQualifiedColumn
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (extraction: StatementExtraction)
        (relations: QualifiedName list)
        (qualifier: Identifier)
        (column: Identifier)
        : Resolution<ResolvedColumn> =

        let chain = ScopeChain.ofExtraction extraction

        let target =
            match ScopeChain.tryResolveLocally qualifier 0 chain with
            | Some (AliasBinding (_, target, _)) -> Some target
            // Query-local. Never a catalog column.
            | Some (CteBinding _)
            | Some (TempRelationBinding _) -> None
            | None ->
                // Not a local binding, so the qualifier is a relation name in
                // its own right: `SELECT orders.id FROM orders`.
                relations
                |> List.tryFind (fun r -> Identifier.sameName r.Name qualifier)
                |> Option.orElseWith (fun () ->
                    match resolveRelationName snapshot searchPath (QualifiedName.unqualified qualifier) with
                    | Resolved resolved -> Some resolved
                    | PartiallyResolved _
                    | Ambiguous _
                    | Unsupported _
                    | Unresolved _ -> None)

        match target with
        | None ->
            Unresolved(
                AnalyzabilityLimit(
                    sprintf "qualifier '%s' is query-local or did not resolve to a relation" qualifier.Text))
        | Some relation ->
            match resolveRelationName snapshot searchPath relation with
            | Resolved resolved ->
                let declaresColumn =
                    indexSnapshot snapshot
                    |> List.exists (fun i ->
                        i.Qualified = resolved
                        && i.Columns |> List.exists (fun c -> Identifier.sameName c column))

                if declaresColumn then
                    Resolved { Relation = resolved; Column = column; FromWildcard = false }
                else
                    Unresolved(AnalyzabilityLimit "no relation in scope declares this column")
            | PartiallyResolved (_, gap)
            | Ambiguous gap
            | Unsupported gap
            | Unresolved gap -> Unresolved gap

    /// Every column a statement depends on, wildcards expanded.
    ///
    /// Returns the resolved set plus the gaps encountered. A caller must report
    /// both: the column list alone would look complete (PR-021, §144.11).
    /// Relations a statement references, resolved against the CATALOG.
    ///
    /// `ScopeResolution.dependencyEdgeCandidates` is catalog-blind: with more
    /// than one schema on the `search_path` it must return `Ambiguous` for every
    /// bare name, because from the statement alone it cannot know which schema
    /// actually holds the table. The catalog can. `EV-STRATA-2026-E3D7` found
    /// that using the blind path here made Strata miss every bare-name reader of
    /// a column — the exact case a messy corpus is full of.
    ///
    /// Scope resolution still runs FIRST and still decides what is a database
    /// object at all, so a CTE-shadowed name is excluded before the catalog is
    /// consulted. Losing that ordering would reintroduce `RK-001`.
    let private catalogResolvedRelations
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (extraction: StatementExtraction)
        : QualifiedName list =

        resolveRelations searchPath extraction
        |> List.choose (fun (mention, outcome) ->
            match outcome with
            // A real database-object reference. Which object is the catalog's
            // question, not scope resolution's.
            | DatabaseObject _ -> Some mention.Name
            // Bound by the statement itself: a CTE, an alias target, or a temp
            // relation. Never a database object.
            | LocallyBound _
            | BindingSite _ -> None)
        |> List.choose (fun name ->
            match resolveRelationName snapshot searchPath name with
            | Resolved resolved -> Some resolved
            | PartiallyResolved _
            | Ambiguous _
            | Unsupported _
            | Unresolved _ -> None)
        |> List.distinct

    let columnDependencies
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (extraction: StatementExtraction)
        : ResolvedColumn list * ResolutionGap list =

        let relations =
            catalogResolvedRelations snapshot searchPath extraction

        let resolvedColumns = ResizeArray<ResolvedColumn>()
        let gaps = ResizeArray<ResolutionGap>()

        for mention in extraction.Columns do
            if mention.IsWildcard then
                match expandWildcard snapshot searchPath relations mention.Qualifier with
                | Resolved columns -> resolvedColumns.AddRange columns
                | PartiallyResolved (columns, gap) ->
                    resolvedColumns.AddRange columns
                    gaps.Add gap
                | Ambiguous gap
                | Unsupported gap
                | Unresolved gap -> gaps.Add gap
            else
                match mention.Column with
                | None -> gaps.Add(AnalyzabilityLimit "column reference carried no name")
                | Some column ->
                    match mention.Qualifier with
                    | Some qualifier ->
                        // An alias-qualified column names exactly one relation,
                        // so it must be resolved against THAT relation and not
                        // against everything in scope.
                        //
                        // This previously fell through to the unqualified path
                        // with a comment claiming scope resolution had handled
                        // it. It had not: the qualifier was discarded, so
                        // `a.id = b.id` searched both relations, found `id` in
                        // each, and returned Ambiguous for a reference that is
                        // not ambiguous at all. Every join on a shared column
                        // name produced a spurious gap.
                        match resolveQualifiedColumn snapshot searchPath extraction relations qualifier column with
                        | Resolved resolved -> resolvedColumns.Add resolved
                        | PartiallyResolved (resolved, gap) ->
                            resolvedColumns.Add resolved
                            gaps.Add gap
                        | Ambiguous gap
                        | Unsupported gap
                        | Unresolved gap -> gaps.Add gap
                    | None ->
                        match resolveUnqualifiedColumn snapshot searchPath relations column with
                        | Resolved resolved -> resolvedColumns.Add resolved
                        | PartiallyResolved (resolved, gap) ->
                            resolvedColumns.Add resolved
                            gaps.Add gap
                        | Ambiguous gap
                        | Unsupported gap
                        | Unresolved gap -> gaps.Add gap

        List.ofSeq resolvedColumns, List.ofSeq gaps
