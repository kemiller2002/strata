module Strata.Tests.WireTests

open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.Evidence
open Strata.Semantic.Resolution
open Strata.Semantic.AnalysisScope
open Strata.Semantic.Wire

/// Tests for NFR-001 (determinism) and NFR-003 (stable wire vocabulary).
///
/// These assert exact rendered strings on purpose. A hand-written wire contract
/// whose tests only check "it produced some JSON" would not notice a renamed
/// tag, which is the drift Boundary Preservation exists to prevent.

[<Fact>]
let ``resolution tags are the documented wire vocabulary`` () =
    let cases =
        [ Resolved(Identifier.unquoted "x"), "resolved"
          PartiallyResolved(Identifier.unquoted "x", SchemaNotQualified), "partially-resolved"
          Ambiguous SchemaNotQualified, "ambiguous"
          Unsupported(ConstructNotModelled "x"), "unsupported"
          Unresolved WildcardNotExpanded, "unresolved" ]

    for resolution, expected in cases do
        Assert.Equal(expected, Resolution.tag resolution)

[<Fact>]
let ``a Resolved value still renders its state explicitly`` () =
    // ER-008 at the boundary: a consumer must never infer the state from a
    // field being present or absent.
    let json = resolution identifier (Resolved(Identifier.unquoted "orders"))
    let rendered = Json.render json

    Assert.Contains("\"state\":\"resolved\"", rendered)
    Assert.Contains("\"dependencyEdgeSafe\":true", rendered)
    Assert.Contains("\"gap\":null", rendered)

[<Fact>]
let ``an ambiguous value renders no value but a gap and is not edge safe`` () =
    let json =
        resolution
            qualifiedName
            (Ambiguous(
                MultipleCandidates
                    [ QualifiedName.qualified (Identifier.unquoted "public") (Identifier.unquoted "orders")
                      QualifiedName.qualified (Identifier.unquoted "sales") (Identifier.unquoted "orders") ]
            ))

    let rendered = Json.render json

    Assert.Contains("\"state\":\"ambiguous\"", rendered)
    Assert.Contains("\"value\":null", rendered)
    Assert.Contains("\"dependencyEdgeSafe\":false", rendered)
    Assert.Contains("multiple-candidates", rendered)

[<Fact>]
let ``wildcard renders as unresolved with its gap, never as absent columns`` () =
    // RK-002: the dangerous rendering is one a consumer reads as "no columns".
    let rendered = Json.render (resolution identifier (Unresolved WildcardNotExpanded))

    Assert.Contains("\"state\":\"unresolved\"", rendered)
    Assert.Contains("wildcard-not-expanded", rendered)

[<Fact>]
let ``completeness category order is stable regardless of input order`` () =
    // NFR-001: byte-identical output for the same logical content.
    let a = Completeness.ofList [ "views", Complete; "tables", Complete; "routines", NotRequested ]
    let b = Completeness.ofList [ "routines", NotRequested; "tables", Complete; "views", Complete ]

    Assert.Equal(Json.render (completeness a), Json.render (completeness b))

[<Fact>]
let ``evidence order does not change rendered output`` () =
    let item source detail = { Source = source; Detail = detail }

    let forward =
        Fact.create High [ item (Catalog "pg_class") "a"; item (SqlUnit "q.sql") "b" ] (Identifier.unquoted "x")

    let reverse =
        Fact.create High [ item (SqlUnit "q.sql") "b"; item (Catalog "pg_class") "a" ] (Identifier.unquoted "x")

    match forward, reverse with
    | Some f, Some r -> Assert.Equal(Json.render (fact identifier f), Json.render (fact identifier r))
    | _ -> failwith "expected both facts to construct"

[<Fact>]
let ``a fact cannot be constructed without evidence`` () =
    // ER-007 enforced by construction, not by convention.
    Assert.True((Fact.create High [] (Identifier.unquoted "x")).IsNone)

[<Fact>]
let ``inaccessible and not-requested render as different states`` () =
    let inaccessible = Json.render (categoryState (Inaccessible "permission denied"))
    let notRequested = Json.render (categoryState NotRequested)

    Assert.NotEqual<string>(inaccessible, notRequested)
    Assert.Contains("\"state\":\"inaccessible\"", inaccessible)
    Assert.Contains("\"state\":\"not-requested\"", notRequested)

[<Fact>]
let ``scope renders whether an absence claim is supported`` () =
    // §144.11: a consumer must not read "0 readers" without seeing the bound.
    let rendered = Json.render (scope Scope.nothingAnalyzed)

    Assert.Contains("\"supportsAbsenceClaim\":false", rendered)

[<Fact>]
let ``rendering is repeatable across invocations`` () =
    let s =
        { Scope.nothingAnalyzed with
            LiveDatabaseInspected = true
            SchemaCompleteness = Completeness.ofList [ "tables", Complete; "rls", Inaccessible "denied" ]
            Corpus = { CorpusScope.empty with IndexedSources = [ "b.sql"; "a.sql" ]; ParseFailures = 2 } }

    Assert.Equal(Json.render (scope s), Json.render (scope s))

[<Fact>]
let ``indexed sources are sorted so corpus discovery order does not leak`` () =
    let a = { CorpusScope.empty with IndexedSources = [ "z.sql"; "a.sql" ] }
    let b = { CorpusScope.empty with IndexedSources = [ "a.sql"; "z.sql" ] }

    Assert.Equal(Json.render (corpusScope a), Json.render (corpusScope b))

[<Fact>]
let ``string escaping handles quotes, backslashes and control characters`` () =
    let rendered = Json.render (JString "a\"b\\c\nd\te")

    Assert.Equal("\"a\\\"b\\\\c\\nd\\te\"", rendered)
