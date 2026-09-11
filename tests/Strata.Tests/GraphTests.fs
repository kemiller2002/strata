module Strata.Tests.GraphTests

open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.Evidence
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.Graph

/// Tests for PR-012/PR-013 and §9: declared and observed never merge into
/// database truth.

let private id' = Identifier.unquoted
let private qn schema name = QualifiedName.qualified (id' schema) (id' name)

let private col name position =
    { Name = id' name
      Type = { TypeName = QualifiedName.unqualified (id' "text"); IsNullable = true }
      Position = position
      HasDefault = false
      IsGenerated = false
      IsIdentity = false }

let private tableWithFk schema name fks =
    TableObject
        { Name = qn schema name
          Columns = [ col "id" 1 ]
          PrimaryKey = None
          UniqueConstraints = []
          CheckConstraints = []
          ForeignKeys = fks
          Indexes = []
          Scope = ManagementScope.Observed }

let private snapshot =
    { Objects =
        [ tableWithFk
              "sales"
              "orders"
              [ { ConstraintName = id' "fk_orders_customer"
                  Columns = [ id' "customer_id" ]
                  ReferencedTable = qn "sales" "customer"
                  ReferencedColumns = [ id' "id" ] } ]
          tableWithFk "sales" "customer" []
          tableWithFk "sales" "invoice" [] ]
      ServerVersion = None
      Completeness = Completeness.ofList [ "relations", Complete ] }

[<Fact>]
let ``declared relationships come from foreign keys and are Certain`` () =
    let declared = SemanticGraph.declaredFrom snapshot

    let edge = Assert.Single declared
    Assert.Equal(RelationshipKind.Declared, edge.Kind)
    Assert.Equal(Certain, edge.Certainty)
    Assert.Equal("sales.customer", QualifiedName.display edge.ToTable)

[<Fact>]
let ``an observed relationship is never enforced by the database`` () =
    // §9: never convert an observed relationship into a declared FK.
    Assert.True(RelationshipKind.isEnforcedByDatabase RelationshipKind.Declared)
    Assert.False(RelationshipKind.isEnforcedByDatabase RelationshipKind.Observed)
    Assert.False(RelationshipKind.isEnforcedByDatabase RelationshipKind.Manual)
    Assert.False(RelationshipKind.isEnforcedByDatabase RelationshipKind.Conflicting)

[<Fact>]
let ``observed certainty rises with distinct evidence but never reaches Certain`` () =
    // RK-017: inference must not produce database truth.
    Assert.Equal(Low, SemanticGraph.certaintyForObserved 1)
    Assert.Equal(Medium, SemanticGraph.certaintyForObserved 5)
    Assert.Equal(High, SemanticGraph.certaintyForObserved 20)
    Assert.NotEqual(Certain, SemanticGraph.certaintyForObserved 1000)

[<Fact>]
let ``repeated joins from the same source do not inflate evidence`` () =
    // Counting the same query twice would manufacture confidence.
    let joins =
        List.replicate
            8
            (qn "sales" "invoice", id' "customer_number", qn "sales" "customer", id' "customer_number", "a.sql")

    let observed = SemanticGraph.observedFrom joins
    let edge = Assert.Single observed

    Assert.Equal(1, edge.EvidenceCount)
    Assert.Equal(Low, edge.Certainty)

[<Fact>]
let ``joins from distinct sources accumulate evidence`` () =
    // Control for the test above.
    let joins =
        [ for i in 1..5 ->
            qn "sales" "invoice",
            id' "customer_number",
            qn "sales" "customer",
            id' "customer_number",
            sprintf "q%d.sql" i ]

    let edge = Assert.Single(SemanticGraph.observedFrom joins)

    Assert.Equal(5, edge.EvidenceCount)
    Assert.Equal(Medium, edge.Certainty)

[<Fact>]
let ``a declared edge absorbs matching observed evidence without changing kind`` () =
    let declared = SemanticGraph.declaredFrom snapshot

    let observed =
        SemanticGraph.observedFrom
            [ qn "sales" "orders", id' "customer_id", qn "sales" "customer", id' "id", "a.sql"
              qn "sales" "orders", id' "customer_id", qn "sales" "customer", id' "id", "b.sql" ]

    let combined = SemanticGraph.combine declared observed

    let edge = Assert.Single combined
    Assert.Equal(RelationshipKind.Declared, edge.Kind)
    Assert.Equal(Certain, edge.Certainty)
    // 1 constraint + 2 distinct sources
    Assert.Equal(3, edge.EvidenceCount)

[<Fact>]
let ``an observed edge with no declared counterpart survives combination`` () =
    let declared = SemanticGraph.declaredFrom snapshot

    let observed =
        SemanticGraph.observedFrom
            [ qn "sales" "invoice", id' "customer_number", qn "sales" "customer", id' "customer_number", "a.sql" ]

    let combined = SemanticGraph.combine declared observed

    Assert.Equal(2, List.length combined)
    Assert.Contains(combined, fun e -> e.Kind = RelationshipKind.Observed)

[<Fact>]
let ``readers and writers are distinguished`` () =
    let graph =
        { SemanticGraph.empty with
            Dependencies =
                [ { SourceId = "r.sql"; Target = qn "sales" "orders"; Kind = Reads; Evidence = [] }
                  { SourceId = "w.sql"; Target = qn "sales" "orders"; Kind = Writes; Evidence = [] } ] }

    Assert.Equal<string list>([ "r.sql" ], SemanticGraph.readersOf (qn "sales" "orders") graph)
    Assert.Equal<string list>([ "w.sql" ], SemanticGraph.writersOf (qn "sales" "orders") graph)

[<Fact>]
let ``a path returns the edges traversed, not just the endpoints`` () =
    // §9 path ranking / ER-020: a path through a Low-certainty observed edge is
    // a different answer from one through foreign keys, and the caller must be
    // able to see which.
    let graph =
        { SemanticGraph.empty with
            Relationships =
                SemanticGraph.combine
                    (SemanticGraph.declaredFrom snapshot)
                    (SemanticGraph.observedFrom
                        [ qn "sales" "invoice", id' "customer_number", qn "sales" "customer", id' "customer_number", "a.sql" ]) }

    match SemanticGraph.pathBetween (qn "sales" "orders") (qn "sales" "invoice") graph with
    | Some edges ->
        Assert.Equal(2, List.length edges)
        // The path is only as trustworthy as its weakest edge.
        Assert.Contains(edges, fun e -> e.Kind = RelationshipKind.Observed)
    | None -> failwith "expected a path orders -> customer -> invoice"

[<Fact>]
let ``no path is reported when none exists`` () =
    let graph = { SemanticGraph.empty with Relationships = SemanticGraph.declaredFrom snapshot }

    Assert.True((SemanticGraph.pathBetween (qn "sales" "orders") (qn "sales" "invoice") graph).IsNone)

[<Fact>]
let ``a path from an object to itself is empty rather than missing`` () =
    let graph = { SemanticGraph.empty with Relationships = SemanticGraph.declaredFrom snapshot }

    match SemanticGraph.pathBetween (qn "sales" "orders") (qn "sales" "orders") graph with
    | Some edges -> Assert.Empty edges
    | None -> failwith "expected an empty path, not absence"

[<Fact>]
let ``relationship listing is deterministic`` () =
    let a = SemanticGraph.declaredFrom snapshot
    let b = SemanticGraph.declaredFrom snapshot

    Assert.Equal<string list>(
        a |> List.map (fun r -> QualifiedName.display r.FromTable),
        b |> List.map (fun r -> QualifiedName.display r.FromTable)
    )
