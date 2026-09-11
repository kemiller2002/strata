module Strata.Tests.CatalogResolutionTests

open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.Resolution
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.StatementReferences
open Strata.Analysis.CatalogResolution

/// Tests for RK-002 (wildcard expansion) and catalog-backed resolution.

let private id' = Identifier.unquoted
let private qn schema name = QualifiedName.qualified (id' schema) (id' name)

let private column name position =
    { Name = id' name
      Type = { TypeName = QualifiedName.unqualified (id' "text"); IsNullable = true }
      Position = position
      HasDefault = false
      IsGenerated = false
      IsIdentity = false }

let private table schema name columns =
    TableObject
        { Name = qn schema name
          Columns = columns |> List.mapi (fun i c -> column c (i + 1))
          PrimaryKey = None
          UniqueConstraints = []
          CheckConstraints = []
          ForeignKeys = []
          Indexes = []
          Scope = Observed }

let private snapshot =
    { Objects =
        [ table "sales" "orders" [ "order_id"; "customer_id"; "status" ]
          table "sales" "customer" [ "id"; "name" ]
          table "archive" "orders" [ "order_id"; "archived_at" ] ]
      ServerVersion = None
      Completeness = Completeness.ofList [ "relations", Complete ] }

let private searchPath = [ id' "sales" ]

[<Fact>]
let ``SELECT star expands to the catalog column list`` () =
    // RK-002: without this, a SELECT * reader reports zero column references.
    match expandWildcard snapshot searchPath [ qn "sales" "orders" ] None with
    | Resolved columns ->
        Assert.Equal(3, List.length columns)
        Assert.Contains(columns, fun c -> c.Column.Text = "status")
        Assert.All(columns, fun c -> Assert.True c.FromWildcard)
    | other -> failwithf "expected expansion, got %A" other

[<Fact>]
let ``an unexpandable wildcard stays unresolved rather than yielding nothing`` () =
    // The dangerous alternative is an empty column list that reads as "no
    // columns referenced".
    match expandWildcard snapshot searchPath [ qn "nowhere" "missing" ] None with
    | Unresolved WildcardNotExpanded -> ()
    | other -> failwithf "expected Unresolved/WildcardNotExpanded, got %A" other

[<Fact>]
let ``a partially expandable wildcard does not report the partial list`` () =
    // Two relations, one resolvable. Reporting only the resolvable half would
    // understate the dependency set — the precise RK-002 failure.
    match expandWildcard snapshot searchPath [ qn "sales" "orders"; qn "nowhere" "missing" ] None with
    | Unresolved WildcardNotExpanded -> ()
    | other -> failwithf "expected the whole expansion to fail, got %A" other

[<Fact>]
let ``a qualified wildcard expands only its own relation`` () =
    let relations = [ qn "sales" "orders"; qn "sales" "customer" ]

    match expandWildcard snapshot searchPath relations (Some(id' "customer")) with
    | Resolved columns ->
        Assert.Equal(2, List.length columns)
        Assert.All(columns, fun c -> Assert.Equal("sales.customer", QualifiedName.display c.Relation))
    | other -> failwithf "expected qualified expansion, got %A" other

[<Fact>]
let ``an unqualified column is attributed when only one relation declares it`` () =
    // Where ScopeResolution must say Ambiguous, the catalog settles it.
    let relations = [ qn "sales" "orders"; qn "sales" "customer" ]

    match resolveUnqualifiedColumn snapshot searchPath relations (id' "status") with
    | Resolved resolved -> Assert.Equal("sales.orders", QualifiedName.display resolved.Relation)
    | other -> failwithf "expected Resolved, got %A" other

[<Fact>]
let ``an unqualified column declared by two relations stays ambiguous`` () =
    // PostgreSQL raises "column reference is ambiguous" for this shape
    // (EV-STRATA-2026-C5D2). Strata agrees rather than picking.
    let relations = [ qn "sales" "orders"; qn "archive" "orders" ]

    match resolveUnqualifiedColumn snapshot [ id' "sales"; id' "archive" ] relations (id' "order_id") with
    | Ambiguous (ColumnNotAttributable candidates) -> Assert.Equal(2, List.length candidates)
    | other -> failwithf "expected Ambiguous, got %A" other

[<Fact>]
let ``a name present in two search_path schemas is ambiguous`` () =
    match resolveRelationName snapshot [ id' "sales"; id' "archive" ] (QualifiedName.unqualified (id' "orders")) with
    | Ambiguous (MultipleCandidates candidates) -> Assert.Equal(2, List.length candidates)
    | other -> failwithf "expected Ambiguous, got %A" other

[<Fact>]
let ``a name present in only one search_path schema resolves`` () =
    // Control: the catalog makes a previously-ambiguous case decidable.
    match resolveRelationName snapshot [ id' "sales"; id' "archive" ] (QualifiedName.unqualified (id' "customer")) with
    | Resolved name -> Assert.Equal("sales.customer", QualifiedName.display name)
    | other -> failwithf "expected Resolved, got %A" other

[<Fact>]
let ``a qualified name absent from the snapshot is unresolved, not absent`` () =
    // The snapshot may be incomplete, and RK-004 showed objects can be
    // unreadable. "Not in this snapshot" is a different claim from "does not
    // exist", and only the first is true.
    match resolveRelationName snapshot searchPath (qn "sales" "nonexistent") with
    | Unresolved (AnalyzabilityLimit reason) -> Assert.Contains("snapshot", reason)
    | other -> failwithf "expected Unresolved, got %A" other

[<Fact>]
let ``columnDependencies reports gaps alongside resolved columns`` () =
    // PR-021: the column list alone would look complete.
    let extraction =
        { StatementExtraction.empty SelectShape with
            Relations =
                [ { Name = qn "sales" "orders"; Role = RelationReference; Alias = None; QueryLevel = 0 } ]
            Columns =
                [ { Qualifier = None; Column = None; IsWildcard = true; QueryLevel = 0 }
                  { Qualifier = None; Column = Some(id' "nonexistent_col"); IsWildcard = false; QueryLevel = 0 } ] }

    let columns, gaps = columnDependencies snapshot searchPath extraction

    Assert.Equal(3, List.length columns)
    Assert.Single gaps |> ignore

// ---- catalog-aware relation resolution in the column path -------------------

[<Fact>]
let ``a bare table name resolves to a column dependency via the catalog`` () =
    // EV-STRATA-2026-E3D7. columnDependencies previously used the CATALOG-BLIND
    // resolver, which must return Ambiguous for a bare name whenever more than
    // one schema is on the search_path — so every bare-name reader of a column
    // contributed nothing. A messy corpus is full of bare names.
    let extraction =
        { StatementExtraction.empty SelectShape with
            Relations =
                [ { Name = QualifiedName.unqualified (id' "orders")
                    Role = RelationReference
                    Alias = None
                    QueryLevel = 0 } ]
            Columns =
                [ { Qualifier = None
                    Column = Some(id' "status")
                    IsWildcard = false
                    QueryLevel = 0 } ] }

    // TWO schemas on the path; only `sales` actually holds `orders`.
    let columns, _ = columnDependencies snapshot [ id' "sales"; id' "public" ] extraction

    let resolved = Assert.Single columns
    Assert.Equal("sales.orders", QualifiedName.display resolved.Relation)
    Assert.Equal("status", resolved.Column.Text)

[<Fact>]
let ``a bare name genuinely ambiguous across schemas still yields no dependency`` () =
    // Control: the fix must not turn a REAL ambiguity into a guess. Here two
    // schemas on the path both hold `orders`, so the catalog cannot choose
    // either — and must not.
    let twoOrders =
        { snapshot with
            Objects =
                snapshot.Objects
                @ [ table "archive" "orders" [ "order_id"; "status" ] ] }

    let extraction =
        { StatementExtraction.empty SelectShape with
            Relations =
                [ { Name = QualifiedName.unqualified (id' "orders")
                    Role = RelationReference
                    Alias = None
                    QueryLevel = 0 } ]
            Columns =
                [ { Qualifier = None
                    Column = Some(id' "status")
                    IsWildcard = false
                    QueryLevel = 0 } ] }

    let columns, _ = columnDependencies twoOrders [ id' "sales"; id' "archive" ] extraction

    Assert.Empty columns

[<Fact>]
let ``a CTE shadowing a bare name still contributes no column dependency`` () =
    // The fix consults the catalog only AFTER scope resolution has decided what
    // is a database object at all. Losing that ordering would reintroduce RK-001
    // in the column path.
    let extraction =
        { StatementExtraction.empty SelectShape with
            Relations =
                [ { Name = QualifiedName.unqualified (id' "orders")
                    Role = CommonTableExpressionDefinition
                    Alias = None
                    QueryLevel = 0 }
                  { Name = QualifiedName.unqualified (id' "orders")
                    Role = RelationReference
                    Alias = None
                    QueryLevel = 0 } ]
            Columns =
                [ { Qualifier = None
                    Column = Some(id' "status")
                    IsWildcard = false
                    QueryLevel = 0 } ] }

    let columns, _ = columnDependencies snapshot [ id' "sales" ] extraction

    Assert.Empty columns
