module Strata.Tests.ScopeResolutionTests

open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.Resolution
open Strata.Analysis.StatementReferences
open Strata.Analysis.ScopeResolution

/// Tests for DF-STRATA-2026-9B2E.
///
/// The first test is a regression test for the defect EV-STRATA-2026-B9C4
/// discovered: a naive extractor emits a dependency edge on a real table when a
/// CTE shadows its name. These assert behaviour, not structure — per SDE's
/// verification method, a structurally present branch that does nothing is a
/// first-class defect, so each test observes the semantic consequence.

let private relation name role alias level =
    { Name = QualifiedName.unqualified (Identifier.unquoted name)
      Role = role
      Alias = alias |> Option.map Identifier.unquoted
      QueryLevel = level }

let private qualifiedRelation schema name =
    { Name = QualifiedName.qualified (Identifier.unquoted schema) (Identifier.unquoted name)
      Role = RelationReference
      Alias = None
      QueryLevel = 0 }

[<Fact>]
let ``CTE name shadowing a table does not become a dependency edge`` () =
    // WITH orders AS (SELECT 1 AS id) SELECT id FROM orders
    //
    // A single-entry search_path is used deliberately. With an EMPTY search_path
    // an unqualified name is PartiallyResolved and yields no edge anyway, so the
    // test would pass without the scope resolution doing anything — a semantic
    // no-op. With "public" in the path the same name WOULD resolve to
    // public.orders, so the only thing suppressing the edge is the CTE binding.
    let searchPath = [ Identifier.unquoted "public" ]

    let shadowed =
        { StatementExtraction.empty SelectShape with
            Relations =
                [ relation "orders" CommonTableExpressionDefinition None 0
                  relation "orders" RelationReference None 0 ] }

    let notShadowed =
        { StatementExtraction.empty SelectShape with
            Relations = [ relation "orders" RelationReference None 0 ] }

    // Control: without the CTE, this name resolves to a real table.
    let controlEdges = dependencyEdgeCandidates searchPath notShadowed
    Assert.Single controlEdges |> ignore
    Assert.Equal("public.orders", QualifiedName.display (List.head controlEdges))

    // Subject: with the CTE, the identical name must produce no edge.
    Assert.Empty(dependencyEdgeCandidates searchPath shadowed)

[<Fact>]
let ``a real table reference does become a dependency edge`` () =
    // Control for the test above: without the CTE, the same name IS an edge.
    // Without this control, the previous test would also pass if the function
    // simply never returned anything.
    let extraction =
        { StatementExtraction.empty SelectShape with
            Relations = [ qualifiedRelation "sales" "orders" ] }

    let edges = dependencyEdgeCandidates [] extraction

    Assert.Single edges |> ignore
    Assert.Equal("sales.orders", QualifiedName.display (List.head edges))

[<Fact>]
let ``unqualified relation without search_path is partially resolved, not guessed`` () =
    let extraction =
        { StatementExtraction.empty SelectShape with
            Relations = [ relation "orders" RelationReference None 0 ] }

    let outcomes = resolveRelations [] extraction

    match outcomes with
    | [ (_, DatabaseObject (PartiallyResolved (name, SchemaNotQualified))) ] ->
        Assert.Equal("orders", QualifiedName.display name)
    | other -> failwithf "expected PartiallyResolved/SchemaNotQualified, got %A" other

[<Fact>]
let ``unqualified relation is not an edge candidate`` () =
    // RK-006: never default an unqualified name to "public".
    let extraction =
        { StatementExtraction.empty SelectShape with
            Relations = [ relation "orders" RelationReference None 0 ] }

    Assert.Empty(dependencyEdgeCandidates [] extraction)

[<Fact>]
let ``multiple search_path entries produce Ambiguous, never a choice`` () =
    let extraction =
        { StatementExtraction.empty SelectShape with
            Relations = [ relation "orders" RelationReference None 0 ] }

    let searchPath = [ Identifier.unquoted "sales"; Identifier.unquoted "public" ]

    match resolveRelations searchPath extraction with
    | [ (_, DatabaseObject (Ambiguous (MultipleCandidates candidates))) ] ->
        Assert.Equal(2, List.length candidates)
    | other -> failwithf "expected Ambiguous/MultipleCandidates, got %A" other

[<Fact>]
let ``single search_path entry resolves an unqualified relation`` () =
    let extraction =
        { StatementExtraction.empty SelectShape with
            Relations = [ relation "orders" RelationReference None 0 ] }

    let edges = dependencyEdgeCandidates [ Identifier.unquoted "sales" ] extraction

    Assert.Single edges |> ignore
    Assert.Equal("sales.orders", QualifiedName.display (List.head edges))

[<Fact>]
let ``temporary relation is not a dependency edge`` () =
    let extraction =
        { StatementExtraction.empty (DdlShape "CREATE TABLE") with
            Relations =
                [ relation "tmp_x" TemporaryRelationDefinition None 0
                  qualifiedRelation "sales" "orders" ] }

    let edges = dependencyEdgeCandidates [] extraction

    Assert.Single edges |> ignore
    Assert.Equal("sales.orders", QualifiedName.display (List.head edges))

[<Fact>]
let ``SELECT star is reported as unexpanded, never as zero columns`` () =
    // RK-002. The dangerous failure is an empty column list.
    let extraction =
        { StatementExtraction.empty SelectShape with
            Relations = [ qualifiedRelation "sales" "orders" ]
            Columns = [ { Qualifier = None; Column = None; IsWildcard = true; QueryLevel = 0 } ] }

    match resolveColumns extraction with
    | [ (_, Unresolved WildcardNotExpanded) ] -> ()
    | other -> failwithf "expected Unresolved/WildcardNotExpanded, got %A" other

[<Fact>]
let ``unqualified column across two relations is Ambiguous with candidates`` () =
    let extraction =
        { StatementExtraction.empty SelectShape with
            Relations = [ qualifiedRelation "sales" "orders"; qualifiedRelation "sales" "customer" ]
            Columns =
                [ { Qualifier = None
                    Column = Some(Identifier.unquoted "id")
                    IsWildcard = false
                    QueryLevel = 0 } ] }

    match resolveColumns extraction with
    | [ (_, Ambiguous (ColumnNotAttributable candidates)) ] -> Assert.Equal(2, List.length candidates)
    | other -> failwithf "expected Ambiguous/ColumnNotAttributable, got %A" other

[<Fact>]
let ``alias-qualified column resolves to the aliased relation`` () =
    let extraction =
        { StatementExtraction.empty SelectShape with
            Relations =
                [ { Name = QualifiedName.qualified (Identifier.unquoted "sales") (Identifier.unquoted "orders")
                    Role = RelationReference
                    Alias = Some(Identifier.unquoted "o")
                    QueryLevel = 0 } ]
            Columns =
                [ { Qualifier = Some(Identifier.unquoted "o")
                    Column = Some(Identifier.unquoted "id")
                    IsWildcard = false
                    QueryLevel = 0 } ] }

    match resolveColumns extraction with
    | [ (_, Resolved (relation, column)) ] ->
        Assert.Equal("sales.orders", QualifiedName.display relation)
        Assert.Equal("id", column.Text)
    | other -> failwithf "expected Resolved, got %A" other

[<Fact>]
let ``quoted identifiers are not folded together with unquoted ones`` () =
    // "Tbl" and Tbl are different objects in PostgreSQL.
    let quoted = Identifier.quoted "Tbl"
    let unquoted = Identifier.unquoted "Tbl"

    Assert.False(Identifier.sameName quoted unquoted)
    Assert.True(Identifier.sameName unquoted (Identifier.unquoted "TBL"))
