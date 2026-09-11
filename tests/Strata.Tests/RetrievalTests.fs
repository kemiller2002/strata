module Strata.Tests.RetrievalTests

open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.Graph
open Strata.Application

/// Tests for PR-016/PR-017/PR-020/PR-021 and ER-017: retrieval is targeted and
/// always carries its scope.

let private id' = Identifier.unquoted
let private qn schema name = QualifiedName.qualified (id' schema) (id' name)

let private col name position =
    { Name = id' name
      Type = { TypeName = QualifiedName.unqualified (id' "text"); IsNullable = true }
      Position = position
      HasDefault = false
      IsGenerated = false
      IsIdentity = false }

let private orders =
    TableObject
        { Name = qn "sales" "orders"
          Columns = [ col "order_id" 1; col "customer_id" 2 ]
          PrimaryKey = Some { ConstraintName = id' "pk"; Columns = [ id' "order_id" ] }
          UniqueConstraints = []
          CheckConstraints = []
          ForeignKeys =
            [ { ConstraintName = id' "fk"
                Columns = [ id' "customer_id" ]
                ReferencedTable = qn "sales" "customer"
                ReferencedColumns = [ id' "id" ] } ]
          Indexes = []
          Scope = ManagementScope.Observed }

let private snapshot =
    { Objects = [ orders ]
      ServerVersion = None
      Completeness = Completeness.ofList [ "relations", Complete ] }

let private graph =
    { SemanticGraph.empty with Relationships = SemanticGraph.declaredFrom snapshot }

let private fullScope =
    { Scope.nothingAnalyzed with
        LiveDatabaseInspected = true
        SchemaCompleteness = Completeness.ofList [ "relations", Complete ]
        Corpus = { CorpusScope.empty with IndexedSources = [ "a.sql" ] }
        ExternalConsumersIndexed = true }

[<Fact>]
let ``an empty readers result is explicitly not an absence claim`` () =
    // §144.11 — the single most important behaviour in this module.
    let answer = Retrieval.answer snapshot graph Scope.nothingAnalyzed (Retrieval.Readers(qn "sales" "orders"))

    Assert.Contains(answer.Caveats, fun c -> c.Contains "not an absence claim")

[<Fact>]
let ``a fully scoped readers result drops the bounded-result caveat`` () =
    // Control: the caveat must be a consequence of scope, not boilerplate that
    // is always emitted.
    let answer = Retrieval.answer snapshot graph fullScope (Retrieval.Readers(qn "sales" "orders"))

    Assert.DoesNotContain(answer.Caveats, fun c -> c.Contains "not an absence claim")

[<Fact>]
let ``inaccessible metadata becomes a caveat automatically`` () =
    // Derived from scope so a new gap reaches every answer without each call
    // site remembering.
    let scope =
        { fullScope with
            SchemaCompleteness =
                Completeness.ofList [ "relations", Complete; "rls_policies", Inaccessible "permission denied" ] }

    let answer = Retrieval.answer snapshot graph scope (Retrieval.Inspect(qn "sales" "orders"))

    Assert.Contains(answer.Caveats, fun c -> c.Contains "rls_policies" && c.Contains "permission denied")

[<Fact>]
let ``visible-but-unreadable objects become a caveat`` () =
    // RK-004 surfacing all the way to the answer.
    let scope =
        { fullScope with
            SchemaCompleteness =
                Completeness.ofList
                    [ "relations", Complete
                      "relation_access", Partial "1 relation(s) visible in the catalog but not readable by the connected role: hidden.secret" ] }

    let answer = Retrieval.answer snapshot graph scope (Retrieval.Inspect(qn "sales" "orders"))

    Assert.Contains(answer.Caveats, fun c -> c.Contains "hidden.secret")

[<Fact>]
let ``parse failures in the corpus become a caveat`` () =
    let scope =
        { fullScope with Corpus = { fullScope.Corpus with ParseFailures = 3 } }

    let answer = Retrieval.answer snapshot graph scope (Retrieval.Readers(qn "sales" "orders"))

    Assert.Contains(answer.Caveats, fun c -> c.Contains "3 SQL unit(s) failed to parse")

[<Fact>]
let ``an object absent from the snapshot is not reported as nonexistent`` () =
    let answer = Retrieval.answer snapshot graph fullScope (Retrieval.Inspect(qn "sales" "nope"))
    let json = Retrieval.toJson answer

    Assert.Contains("\"found\":false", json)
    Assert.Contains("does not establish that the object does not exist", json)

[<Fact>]
let ``a declared path reports that every edge is database enforced`` () =
    let answer = Retrieval.answer snapshot graph fullScope (Retrieval.Path(qn "sales" "orders", qn "sales" "customer"))
    let json = Retrieval.toJson answer

    Assert.Contains("\"allEdgesDatabaseEnforced\":true", json)
    Assert.Contains("\"kind\":\"declared\"", json)

[<Fact>]
let ``a path through an observed edge is not reported as database enforced`` () =
    // The distinction a reader needs in order to trust a path (§9, RK-017).
    let observed =
        SemanticGraph.observedFrom
            [ qn "sales" "customer", id' "id", qn "sales" "invoice", id' "customer_id", "a.sql" ]

    let mixedGraph =
        { SemanticGraph.empty with
            Relationships = SemanticGraph.combine (SemanticGraph.declaredFrom snapshot) observed }

    let answer =
        Retrieval.answer snapshot mixedGraph fullScope (Retrieval.Path(qn "sales" "orders", qn "sales" "invoice"))

    let json = Retrieval.toJson answer

    Assert.Contains("\"allEdgesDatabaseEnforced\":false", json)

[<Fact>]
let ``relationship output marks observed edges as not enforced`` () =
    let observed =
        SemanticGraph.observedFrom
            [ qn "sales" "orders", id' "customer_id", qn "sales" "other", id' "id", "a.sql" ]

    let g = { SemanticGraph.empty with Relationships = observed }

    let json =
        Retrieval.answer snapshot g fullScope (Retrieval.Relationships(qn "sales" "orders"))
        |> Retrieval.toJson

    Assert.Contains("\"kind\":\"observed\"", json)
    Assert.Contains("\"enforcedByDatabase\":false", json)

[<Fact>]
let ``inspect returns a compact summary, not the whole snapshot`` () =
    // ER-017 / NG-010: retrieval, not dumping.
    let json = Retrieval.answer snapshot graph fullScope (Retrieval.Inspect(qn "sales" "orders")) |> Retrieval.toJson

    Assert.Contains("sales.orders", json)
    // The answer describes the requested object only.
    Assert.DoesNotContain("\"kind\":\"routine\"", json)

[<Fact>]
let ``text output always ends with the caveats`` () =
    // PR-020: a reader must not be able to finish without seeing what was not
    // analysed.
    let text = Retrieval.answer snapshot graph Scope.nothingAnalyzed (Retrieval.Readers(qn "sales" "orders")) |> Retrieval.toText

    Assert.Contains("Analysis scope caveats:", text)
    let caveatIndex = text.IndexOf "Analysis scope caveats:"
    Assert.True(caveatIndex > text.IndexOf "readers:", "caveats should follow the result")

[<Fact>]
let ``json output always carries the scope`` () =
    let json = Retrieval.answer snapshot graph fullScope (Retrieval.Inspect(qn "sales" "orders")) |> Retrieval.toJson

    Assert.Contains("\"scope\":", json)
    Assert.Contains("\"supportsAbsenceClaim\":", json)
