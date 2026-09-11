module Strata.Tests.DeploymentGateTests

open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.AnalysisScope
open Strata.Analysis.Graph
open Strata.Analysis.ProposedChange
open Strata.Application.DeploymentGate

/// Tests for the deployment gate — the capability HY-STRATA-2026-6F14 claims
/// stands independently of agent leverage.

let private id' = Identifier.unquoted
let private qn schema name = QualifiedName.qualified (id' schema) (id' name)
let private orders = qn "sales" "orders"

let private graphWithReader column viaWildcard =
    { SemanticGraph.empty with
        ColumnDependencies =
            [ { SourceId = "file:r.sql"
                Table = orders
                Column = id' column
                Kind = Reads
                ViaWildcard = viaWildcard } ] }

/// A scope that DOES support absence claims: live DB inspected, schema
/// complete, corpus non-empty.
let private completeScope =
    { Scope.nothingAnalyzed with
        LiveDatabaseInspected = true
        SchemaCompleteness = Completeness.ofList [ "relations", Complete ]
        Corpus = { CorpusScope.empty with IndexedSources = [ "file:r.sql" ] } }

let private emptyScope = Scope.nothingAnalyzed

[<Fact>]
let ``dropping a column with a named reader is blocked`` () =
    let result = run (graphWithReader "status" false) completeScope [ DropColumn(orders, id' "status") ]

    Assert.Equal(Block, result.Verdict)
    Assert.Equal(1, Verdict.exitCode result.Verdict)
    Assert.Contains("file:r.sql", (List.head result.Findings).AffectedSources)

[<Fact>]
let ``dropping a column read ONLY via SELECT star is still blocked`` () =
    // RK-002 at the gate. A wildcard reader genuinely breaks, and is exactly
    // what a text search for the column name would miss.
    let result = run (graphWithReader "total" true) completeScope [ DropColumn(orders, id' "total") ]

    Assert.Equal(Block, result.Verdict)
    Assert.Contains("SELECT *", (List.head result.Findings).Rationale)

[<Fact>]
let ``a clean result under an INCOMPLETE scope is not clearance`` () =
    // §144.11 / P-009. THE load-bearing rule: absence of evidence inside a
    // bounded scope is not evidence of absence, so it cannot return Allow.
    let result = run SemanticGraph.empty emptyScope [ DropColumn(orders, id' "note") ]

    Assert.Equal(RequiresApproval, result.Verdict)
    Assert.Contains("not clearance", (List.head result.Findings).Rationale)

[<Fact>]
let ``a clean result under a COMPLETE scope is allowed`` () =
    // Control: the gate must be capable of saying yes, or it is useless.
    let result = run SemanticGraph.empty completeScope [ DropColumn(orders, id' "note") ]

    Assert.Equal(Allow, result.Verdict)
    Assert.Equal(0, Verdict.exitCode result.Verdict)

[<Fact>]
let ``additive changes are allowed even under an incomplete scope`` () =
    // RK-016 / §136: a gate that blocks safe changes gets disabled. No existing
    // reader can depend on a column that does not yet exist, so scope is
    // irrelevant here.
    let added = run SemanticGraph.empty emptyScope [ AddColumn(orders, id' "priority") ]
    let created = run SemanticGraph.empty emptyScope [ CreateTable(qn "sales" "shipment") ]

    Assert.Equal(Allow, added.Verdict)
    Assert.Equal(Allow, created.Verdict)

[<Fact>]
let ``truncate is blocked unconditionally`` () =
    // No predicate can narrow it; §11.3 lists it separately from DELETE.
    let result = run SemanticGraph.empty completeScope [ TruncateTable orders ]

    Assert.Equal(Block, result.Verdict)

[<Fact>]
let ``a type change with affected sources requires approval rather than blocking`` () =
    // A widening type change is safe and a narrowing one is not. Strata knows
    // WHO is affected, not whether the new type is compatible — claiming
    // otherwise would be the false-confidence failure §144.2 warns about.
    let result =
        run (graphWithReader "status" false) completeScope [ AlterColumnType(orders, id' "status", "varchar(10)") ]

    Assert.Equal(RequiresApproval, result.Verdict)

[<Fact>]
let ``a statement Strata cannot model requires approval, never allow`` () =
    // ER-008: not knowing what a statement does is not the same as it being safe.
    let result = run SemanticGraph.empty completeScope [ UnclassifiedChange "CREATE TRIGGER" ]

    Assert.Equal(RequiresApproval, result.Verdict)

[<Fact>]
let ``an empty migration is not approved by default`` () =
    let result = run SemanticGraph.empty completeScope []

    Assert.Equal(RequiresApproval, result.Verdict)

[<Fact>]
let ``a migration is as safe as its least safe statement`` () =
    let result =
        run
            (graphWithReader "status" false)
            completeScope
            [ AddColumn(orders, id' "priority"); DropColumn(orders, id' "status") ]

    Assert.Equal(Block, result.Verdict)

[<Fact>]
let ``every verdict carries a next safe move`` () =
    // §130: a gate that only refuses gets routed around.
    let changes =
        [ DropColumn(orders, id' "status")
          AddColumn(orders, id' "x")
          TruncateTable orders
          AlterColumnType(orders, id' "status", "text")
          UnclassifiedChange "something" ]

    let result = run (graphWithReader "status" false) completeScope changes

    Assert.All(result.Findings, fun f -> Assert.False(System.String.IsNullOrWhiteSpace f.NextSafeMove))

[<Fact>]
let ``gate output is byte-identical across runs`` () =
    // The property that distinguishes a gate from an agent: same input, same
    // verdict, every time. A CI gate cannot be built on a probabilistic answer.
    let changes = [ DropColumn(orders, id' "status"); AddColumn(orders, id' "x") ]
    let graph = graphWithReader "status" false

    let a = toJson (run graph completeScope changes)
    let b = toJson (run graph completeScope changes)

    Assert.Equal<string>(a, b)

[<Fact>]
let ``exit codes distinguish all three verdicts`` () =
    Assert.Equal(0, Verdict.exitCode Allow)
    Assert.Equal(1, Verdict.exitCode Block)
    Assert.Equal(2, Verdict.exitCode RequiresApproval)
