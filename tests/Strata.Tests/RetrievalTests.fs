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

// ---- WI-0021: brief scope mode ---------------------------------------------

[<Fact>]
let ``brief mode never drops the absence-claim warning`` () =
    // The whole risk of this optimisation. EV-STRATA-2026-F4C6 showed the scope
    // block costs more than the answer, but shrinking it must NOT reintroduce
    // the failure it exists to prevent: a reader seeing [] and concluding
    // "nothing reads this".
    let answer = Retrieval.answer snapshot graph Scope.nothingAnalyzed (Retrieval.Readers(qn "sales" "orders"))
    let brief = Retrieval.toJsonBrief answer

    Assert.Contains("\"supportsAbsenceClaim\":false", brief)
    Assert.Contains("not an absence claim", brief)

[<Fact>]
let ``brief mode never drops the dialect divergence warning`` () =
    // Also load-bearing: it changes whether a parse result means anything about
    // the target.
    let scope =
        { fullScope with DialectCompatibility = Diverged(17, 16) }

    let brief =
        Retrieval.answer snapshot graph scope (Retrieval.Inspect(qn "sales" "orders"))
        |> Retrieval.toJsonBrief

    Assert.Contains("may still be rejected by the target", brief)

[<Fact>]
let ``brief mode reports how many caveats it elided`` () =
    // Nothing is silently dropped: a reader can always see that more exists.
    let scope =
        { fullScope with
            SchemaCompleteness =
                Completeness.ofList
                    [ "relations", Complete
                      "rls_policies", Inaccessible "permission denied" ]
            Corpus = { fullScope.Corpus with ParseFailures = 2 } }

    let answer = Retrieval.answer snapshot graph scope (Retrieval.Inspect(qn "sales" "orders"))
    let brief = Retrieval.toJsonBrief answer

    Assert.Contains("\"caveatsElided\":", brief)
    Assert.Contains("strata scope", brief)
    // The elided ones are genuinely absent from the brief payload.
    Assert.DoesNotContain("rls_policies", brief)

[<Fact>]
let ``brief mode is smaller than full mode`` () =
    let answer = Retrieval.answer snapshot graph fullScope (Retrieval.Inspect(qn "sales" "orders"))

    let full = Retrieval.toJson answer
    let brief = Retrieval.toJsonBrief answer

    Assert.True(brief.Length < full.Length, sprintf "brief (%d) < full (%d)" brief.Length full.Length)

[<Fact>]
let ``the saving grows with how much scope there is to elide`` () =
    // The optimisation targets REAL scopes, which carry a dozen completeness
    // categories. A minimal test scope barely has anything to elide, so
    // asserting a fixed ratio against one would measure the fixture rather than
    // the behaviour. This asserts the property that actually matters: the more
    // scope detail exists, the more brief mode saves.
    let realisticScope =
        { fullScope with
            SchemaCompleteness =
                Completeness.ofList
                    [ "relations", Complete
                      "columns", Complete
                      "constraints", Complete
                      "indexes", Complete
                      "view_definitions", Complete
                      "routines", Complete
                      "server_version", Complete
                      "rls_policies", NotRequested
                      "triggers", NotRequested
                      "sequences", NotRequested
                      "extensions", NotRequested
                      "grants", NotRequested
                      "relation_access", Partial "2 relation(s) visible but not readable: a.b, c.d" ] }

    let answer = Retrieval.answer snapshot graph realisticScope (Retrieval.Inspect(qn "sales" "orders"))

    let fullLength = (Retrieval.toJson answer).Length
    let briefLength = (Retrieval.toJsonBrief answer).Length

    let minimalAnswer = Retrieval.answer snapshot graph fullScope (Retrieval.Inspect(qn "sales" "orders"))
    let minimalSaving = (Retrieval.toJson minimalAnswer).Length - (Retrieval.toJsonBrief minimalAnswer).Length

    let realisticSaving = fullLength - briefLength

    Assert.True(
        realisticSaving > minimalSaving,
        sprintf "realistic saving (%d) should exceed minimal saving (%d)" realisticSaving minimalSaving
    )

    // And on a realistic scope the saving should be worth having.
    Assert.True(
        briefLength * 2 < fullLength,
        sprintf "brief (%d) should be less than half of full (%d) on a realistic scope" briefLength fullLength
    )

[<Fact>]
let ``the same scope produces the same digest and a different scope does not`` () =
    // The digest has to actually identify a scope, or a brief answer cannot be
    // tied back to the scope it was produced under.
    let a = Retrieval.scopeDigest fullScope
    let b = Retrieval.scopeDigest fullScope
    let c = Retrieval.scopeDigest Scope.nothingAnalyzed

    Assert.Equal<string>(a, b)
    Assert.NotEqual<string>(a, c)

[<Fact>]
let ``the scope command returns the digest that brief answers reference`` () =
    let scopeAnswer = Retrieval.answer snapshot graph fullScope Retrieval.ScopeOnly
    let briefAnswer = Retrieval.answer snapshot graph fullScope (Retrieval.Inspect(qn "sales" "orders"))

    Assert.Contains(Retrieval.scopeDigest fullScope, Retrieval.toJson scopeAnswer)
    Assert.Contains(Retrieval.scopeDigest fullScope, Retrieval.toJsonBrief briefAnswer)

[<Fact>]
let ``the scope command still carries the full scope`` () =
    // `strata scope` is where the elided detail must be recoverable.
    let scope =
        { fullScope with
            SchemaCompleteness =
                Completeness.ofList [ "relations", Complete; "rls_policies", Inaccessible "denied" ] }

    let json = Retrieval.answer snapshot graph scope Retrieval.ScopeOnly |> Retrieval.toJson

    Assert.Contains("rls_policies", json)
    Assert.Contains("\"scope\":", json)

// ---- WI-0022: retrieval must not be small by being lossy --------------------

[<Fact>]
let ``inspect includes constraints and indexes, not just columns and keys`` () =
    // EV-STRATA-2026-A2B8: omitting these made three ordinary questions
    // unanswerable from Strata while the raw DDL answered them — what values a
    // column allows, whether a column is indexed, whether it is unique. A
    // retrieval answer that is small because it drops data is the inverse of
    // NG-010, not a saving.
    let withConstraints =
        TableObject
            { Name = qn "sales" "orders"
              Columns = [ col "order_id" 1; col "status" 2 ]
              PrimaryKey = Some { ConstraintName = id' "pk"; Columns = [ id' "order_id" ] }
              UniqueConstraints = [ { ConstraintName = id' "uq_status"; Columns = [ id' "status" ] } ]
              CheckConstraints =
                [ { ConstraintName = id' "ck_status"
                    Expression = "CHECK (status IN ('open','closed'))" } ]
              ForeignKeys = []
              Indexes =
                [ { Name = id' "idx_orders_status"
                    Columns = [ id' "status" ]
                    IsUnique = false
                    Predicate = Some "status = 'open'" } ]
              Scope = ManagementScope.Observed }

    let snapshotWith = { snapshot with Objects = [ withConstraints ] }

    let json =
        Retrieval.answer snapshotWith graph fullScope (Retrieval.Inspect(qn "sales" "orders"))
        |> Retrieval.toJson

    Assert.Contains("ck_status", json)
    Assert.Contains("open", json)
    Assert.Contains("idx_orders_status", json)
    Assert.Contains("uq_status", json)

[<Fact>]
let ``a partial index predicate survives into the answer`` () =
    // The predicate is the whole point of a partial index; dropping it would
    // make the index look like it covers every row.
    let withPartial =
        TableObject
            { Name = qn "sales" "orders"
              Columns = [ col "status" 1 ]
              PrimaryKey = None
              UniqueConstraints = []
              CheckConstraints = []
              ForeignKeys = []
              Indexes =
                [ { Name = id' "idx_open"
                    Columns = [ id' "status" ]
                    IsUnique = false
                    Predicate = Some "status = 'open'" } ]
              Scope = ManagementScope.Observed }

    let json =
        Retrieval.answer { snapshot with Objects = [ withPartial ] } graph fullScope
            (Retrieval.Inspect(qn "sales" "orders"))
        |> Retrieval.toJson

    Assert.Contains("\"predicate\":\"status = 'open'\"", json)
