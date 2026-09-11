module Strata.Tests.CorpusPipelineTests

open System.IO
open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.Corpus
open Strata.Analysis.Graph
open Strata.Analysis.DialectPort
open Strata.Application
open Strata.Host.PgParser
open Strata.Host.Files

/// Tests for WI-0017: a corpus on disk becomes dependencies and observed
/// relationships.
///
/// These drive the REAL parser over REAL SQL text. The point of this work item
/// was to stop the graph being tested only in principle, so a test that fed it
/// hand-built extractions would defeat its own purpose.

let private id' = Identifier.unquoted
let private qn schema name = QualifiedName.qualified (id' schema) (id' name)

let private col name position =
    { Name = id' name
      Type = { TypeName = QualifiedName.unqualified (id' "text"); IsNullable = true }
      Position = position
      HasDefault = false
      DefaultExpression = None
      IsGenerated = false
      IsIdentity = false }

let private table schema name columns fks =
    TableObject
        { Name = qn schema name
          Columns = columns |> List.mapi (fun i c -> col c (i + 1))
          PrimaryKey = None
          UniqueConstraints = []
          CheckConstraints = []
          ForeignKeys = fks
          Indexes = []
          Triggers = []
          Scope = ManagementScope.Observed }

let private snapshot =
    { Objects =
        [ table
              "sales"
              "orders"
              [ "order_id"; "customer_id"; "status"; "total" ]
              [ { ConstraintName = id' "fk_orders_customer"
                  Columns = [ id' "customer_id" ]
                  ReferencedTable = qn "sales" "customer"
                  ReferencedColumns = [ id' "id" ] } ]
          table "sales" "customer" [ "id"; "customer_number"; "name" ] []
          table "sales" "invoice" [ "invoice_id"; "customer_number"; "amount" ] [] ]
      ServerVersion = None
      Completeness = Completeness.ofList [ "relations", Complete ] }

let private parser = PgParserAdapter.PostgresParser() :> IDialectParser
let private searchPath = [ id' "sales" ]

let private analyse sources =
    CorpusPipeline.analyse parser snapshot searchPath sources

[<Fact>]
let ``a SELECT becomes a read dependency`` () =
    let analysis =
        analyse [ SqlFile "r.sql", "SELECT o.order_id FROM sales.orders o WHERE o.status = 'open'" ]

    let graph = CorpusPipeline.buildGraph snapshot analysis

    Assert.Equal<string list>([ "file:r.sql" ], SemanticGraph.readersOf (qn "sales" "orders") graph)
    Assert.Empty(SemanticGraph.writersOf (qn "sales" "orders") graph)

[<Fact>]
let ``an UPDATE becomes a write dependency, not a read`` () =
    let analysis =
        analyse [ SqlFile "w.sql", "UPDATE sales.orders SET status = 'closed' WHERE order_id = 1" ]

    let graph = CorpusPipeline.buildGraph snapshot analysis

    Assert.Equal<string list>([ "file:w.sql" ], SemanticGraph.writersOf (qn "sales" "orders") graph)
    Assert.Empty(SemanticGraph.readersOf (qn "sales" "orders") graph)

[<Fact>]
let ``a CTE shadowing a real table creates no dependency on that table`` () =
    // RK-001 on real SQL text, through the real parser, all the way to the graph.
    let analysis =
        analyse
            [ SqlFile "cte.sql", "WITH orders AS (SELECT 1 AS id) SELECT id FROM orders" ]

    let graph = CorpusPipeline.buildGraph snapshot analysis

    Assert.Empty(SemanticGraph.readersOf (qn "sales" "orders") graph)

[<Fact>]
let ``a real reference to the same table DOES create a dependency`` () =
    // Control for the test above, so an empty result cannot pass by accident.
    let analysis = analyse [ SqlFile "plain.sql", "SELECT order_id FROM sales.orders" ]
    let graph = CorpusPipeline.buildGraph snapshot analysis

    Assert.Single(SemanticGraph.readersOf (qn "sales" "orders") graph) |> ignore

[<Fact>]
let ``an explicit JOIN produces an observed relationship`` () =
    let analysis =
        analyse
            [ SqlFile "j.sql",
              "SELECT i.amount FROM sales.invoice i JOIN sales.customer c ON c.customer_number = i.customer_number" ]

    Assert.Single analysis.ObservedJoins |> ignore

[<Fact>]
let ``an old-style implicit join is also observed`` () =
    // `FROM a, b WHERE a.x = b.y` is a join. Ignoring it would systematically
    // under-count relationships in older corpora.
    let analysis =
        analyse
            [ SqlFile "old.sql",
              "SELECT i.amount FROM sales.invoice i, sales.customer c WHERE c.customer_number = i.customer_number" ]

    Assert.Single analysis.ObservedJoins |> ignore

[<Fact>]
let ``the same relationship written both directions is ONE edge`` () =
    // The defect found when first running this pipeline on a real corpus: an
    // observed edge sat beside the declared one for the identical relationship.
    let analysis =
        analyse
            [ SqlFile "a.sql", "SELECT 1 FROM sales.orders o JOIN sales.customer c ON c.id = o.customer_id"
              SqlFile "b.sql", "SELECT 1 FROM sales.customer c JOIN sales.orders o ON o.customer_id = c.id" ]

    let graph = CorpusPipeline.buildGraph snapshot analysis

    let edge = Assert.Single graph.Relationships
    Assert.Equal(RelationshipKind.Declared, edge.Kind)
    // 1 foreign key + 2 observing sources.
    Assert.Equal(3, edge.EvidenceCount)

[<Fact>]
let ``a comparison against a literal is not a join predicate`` () =
    // Only column-to-column equality is relationship evidence.
    let analysis = analyse [ SqlFile "lit.sql", "SELECT 1 FROM sales.orders WHERE status = 'open'" ]

    Assert.Empty analysis.ObservedJoins

[<Fact>]
let ``an unparseable statement is indexed as a failure and contributes nothing`` () =
    // ER-008: dropping it would report a smaller, cleaner-looking dependency set
    // than Strata actually established.
    let analysis = analyse [ SqlFile "bad.sql", "SELECT FROM WHERE;" ]

    Assert.Single(CorpusIndex.parseFailures analysis.Index) |> ignore
    Assert.Empty analysis.Dependencies
    Assert.NotEmpty analysis.Gaps

[<Fact>]
let ``a failed unit still appears in the corpus scope`` () =
    let analysis =
        analyse
            [ SqlFile "ok.sql", "SELECT order_id FROM sales.orders"
              SqlFile "bad.sql", "SELECT FROM WHERE;" ]

    let scope = CorpusPipeline.toScope parser snapshot analysis

    Assert.Equal(1, scope.Corpus.ParseFailures)
    Assert.Equal(2, List.length scope.Corpus.IndexedSources)

[<Fact>]
let ``an expandable SELECT star has no gap, because the catalog resolved it`` () =
    // The RK-002 mitigation succeeding. The wildcard's columns ARE known, so
    // the analysis is genuinely complete and must not claim otherwise.
    let analysis = analyse [ SqlFile "star.sql", "SELECT * FROM sales.orders" ]

    Assert.Empty(CorpusIndex.extractionGaps analysis.Index)

[<Fact>]
let ``an UNEXPANDABLE SELECT star IS recorded as a gap`` () =
    // The case that actually threatens impact analysis: the relation is not in
    // the snapshot, so its columns are unknown and the statement's column
    // dependencies are incomplete. Reporting this as clean would be the RK-002
    // failure.
    let analysis = analyse [ SqlFile "star.sql", "SELECT * FROM sales.not_in_snapshot" ]

    Assert.NotEmpty(CorpusIndex.extractionGaps analysis.Index)

[<Fact>]
let ``a fully explicit statement has no extraction gaps`` () =
    // Control: gaps must reflect real incompleteness, not be ever-present.
    let analysis = analyse [ SqlFile "clean.sql", "SELECT o.order_id FROM sales.orders o" ]

    Assert.Empty(CorpusIndex.extractionGaps analysis.Index)

[<Fact>]
let ``statements carry a content hash and a fingerprint`` () =
    let analysis = analyse [ SqlFile "f.sql", "SELECT order_id FROM sales.orders WHERE order_id = 1" ]

    let statement = List.exactlyOne analysis.Index.Statements
    Assert.False(System.String.IsNullOrWhiteSpace statement.ContentHash)
    Assert.True(statement.Fingerprint.IsSome)

[<Fact>]
let ``differing literals share a fingerprint but not a content hash`` () =
    let a = analyse [ SqlFile "a.sql", "SELECT order_id FROM sales.orders WHERE order_id = 1" ]
    let b = analyse [ SqlFile "b.sql", "SELECT order_id FROM sales.orders WHERE order_id = 999" ]

    let sa = List.exactlyOne a.Index.Statements
    let sb = List.exactlyOne b.Index.Statements

    Assert.Equal(sa.Fingerprint, sb.Fingerprint)
    Assert.NotEqual<string>(sa.ContentHash, sb.ContentHash)

[<Fact>]
let ``analysis is deterministic across runs`` () =
    let sources =
        [ SqlFile "a.sql", "SELECT 1 FROM sales.orders o JOIN sales.customer c ON c.id = o.customer_id"
          SqlFile "b.sql", "SELECT * FROM sales.invoice" ]

    let first = CorpusPipeline.buildGraph snapshot (analyse sources)
    let second = CorpusPipeline.buildGraph snapshot (analyse sources)

    let render (g: SemanticGraph) =
        g.Relationships
        |> List.map (fun r -> QualifiedName.display r.FromTable + "->" + QualifiedName.display r.ToTable)

    Assert.Equal<string list>(render first, render second)

// ---- FileCorpus -------------------------------------------------------------

let private withTempDir (f: string -> unit) =
    let dir = Path.Combine(Path.GetTempPath(), "strata-corpus-" + Path.GetRandomFileName())
    Directory.CreateDirectory dir |> ignore

    try
        f dir
    finally
        try Directory.Delete(dir, true) with _ -> ()

[<Fact>]
let ``FileCorpus reads sql files recursively in sorted order`` () =
    withTempDir (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, "sub")) |> ignore
        File.WriteAllText(Path.Combine(dir, "b.sql"), "SELECT 1")
        File.WriteAllText(Path.Combine(dir, "a.sql"), "SELECT 2")
        File.WriteAllText(Path.Combine(dir, "sub", "c.sql"), "SELECT 3")
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "ignored")

        match FileCorpus.read dir with
        | Ok result ->
            let ids = result.Sources |> List.map (fst >> SqlOrigin.sourceId)
            Assert.Equal<string list>([ "file:a.sql"; "file:b.sql"; "file:sub/c.sql" ], ids)
            Assert.Empty result.Failures
        | Error e -> failwithf "expected a read, got %s" e)

[<Fact>]
let ``FileCorpus classifies a migrations directory as migration provenance`` () =
    withTempDir (fun dir ->
        Directory.CreateDirectory(Path.Combine(dir, "migrations")) |> ignore
        File.WriteAllText(Path.Combine(dir, "migrations", "001.sql"), "SELECT 1")

        match FileCorpus.read dir with
        | Ok result ->
            let origin = result.Sources |> List.head |> fst
            Assert.Equal("migration:migrations/001.sql", SqlOrigin.sourceId origin)
        | Error e -> failwithf "expected a read, got %s" e)

[<Fact>]
let ``FileCorpus reports a missing directory as an error, not an empty corpus`` () =
    // An empty corpus and an absent one are different, and only the first
    // supports any claim at all.
    match FileCorpus.read "/nonexistent/strata/corpus/path" with
    | Error message -> Assert.Contains("not found", message)
    | Ok _ -> failwith "expected an error for a missing directory"

// ---- WI-0018: unsupported join shapes are explicit, not silent --------------

[<Fact>]
let ``a range join is reported as an unmodelled shape rather than silently ignored`` () =
    // Before this, "no relationship found" and "that shape is not analysed"
    // were the same output. ER-008 forbids exactly that collapse.
    let analysis =
        analyse
            [ SqlFile "range.sql",
              "SELECT 1 FROM sales.orders o, sales.customer c WHERE o.customer_id > c.id" ]

    Assert.Empty analysis.ObservedJoins
    Assert.NotEmpty(CorpusIndex.extractionGaps analysis.Index)
    Assert.Contains(analysis.Gaps, fun g -> g.Contains "not modelled as join evidence")

[<Fact>]
let ``an equality join in the same shape IS still observed`` () =
    // Control: the gap must come from the operator, not from the statement form.
    let analysis =
        analyse
            [ SqlFile "eq.sql",
              "SELECT 1 FROM sales.orders o, sales.customer c WHERE o.customer_id = c.id" ]

    Assert.Single analysis.ObservedJoins |> ignore

[<Fact>]
let ``an IN subquery is reported as an unmodelled shape`` () =
    let analysis =
        analyse
            [ SqlFile "sub.sql",
              "SELECT 1 FROM sales.customer c WHERE c.id IN (SELECT o.customer_id FROM sales.orders o)" ]

    Assert.Contains(analysis.Gaps, fun g -> g.Contains "IN (SELECT ...)" || g.Contains "IN predicate")

[<Fact>]
let ``a BETWEEN predicate is reported as an unmodelled shape`` () =
    let analysis =
        analyse [ SqlFile "btw.sql", "SELECT 1 FROM sales.orders o WHERE o.total BETWEEN 1 AND 2" ]

    Assert.Contains(analysis.Gaps, fun g -> g.Contains "BETWEEN")

[<Fact>]
let ``a plain equality statement reports no unmodelled shape`` () =
    // Control: gaps must reflect real omissions, not fire on everything.
    let analysis =
        analyse [ SqlFile "plain.sql", "SELECT o.order_id FROM sales.orders o WHERE o.status = 'open'" ]

    Assert.DoesNotContain(analysis.Gaps, fun g -> g.Contains "not modelled as join evidence")

// ---- WI-0019: dialect divergence is a first-class state ---------------------

[<Fact>]
let ``a diverged parser and server major is reported and blocks the parse implication`` () =
    // EV-STRATA-2026-C5D2: a 17.5 parser accepts syntax a 16.15 server rejects.
    let compatibility = DialectCompatibility.compare 17 (Some 16)

    match compatibility with
    | Diverged (parser, server) ->
        Assert.Equal(17, parser)
        Assert.Equal(16, server)
    | other -> failwithf "expected Diverged, got %A" other

    Assert.False(DialectCompatibility.parseImpliesTargetAccepts compatibility)

[<Fact>]
let ``matching majors permit the parse implication`` () =
    let compatibility = DialectCompatibility.compare 16 (Some 16)

    Assert.True(DialectCompatibility.parseImpliesTargetAccepts compatibility)

[<Fact>]
let ``an unknown server version is not treated as a match`` () =
    // ER-008: not having checked is not the same as having checked and agreed.
    let compatibility = DialectCompatibility.compare 17 None

    match compatibility with
    | ServerVersionUnknown 17 -> ()
    | other -> failwithf "expected ServerVersionUnknown, got %A" other

    Assert.False(DialectCompatibility.parseImpliesTargetAccepts compatibility)

[<Fact>]
let ``the pipeline scope carries dialect compatibility from the real parser`` () =
    let snapshotWith16 =
        { snapshot with
            ServerVersion =
                Some(
                    Strata.Semantic.Evidence.Fact.declared
                        (Strata.Semantic.Evidence.Catalog "pg_settings")
                        "test"
                        { Major = 16; Full = "16.15" }
                ) }

    let analysis = analyse [ SqlFile "a.sql", "SELECT 1 FROM sales.orders" ]
    let scope = CorpusPipeline.toScope parser snapshotWith16 analysis

    // The real parser reports PostgreSQL 17, the snapshot says 16.
    match scope.DialectCompatibility with
    | Diverged (parserMajor, 16) -> Assert.True(parserMajor >= 17)
    | other -> failwithf "expected Diverged against a 16 server, got %A" other

// ---- WI-0025: column-level impact ------------------------------------------

[<Fact>]
let ``an explicit column reader is attributed to that column`` () =
    // EV-STRATA-2026-B6F3 T1: the question Strata could not answer.
    let analysis =
        analyse
            [ SqlFile "a.sql", "SELECT o.status FROM sales.orders o"
              SqlFile "b.sql", "SELECT o.total FROM sales.orders o" ]

    let graph = CorpusPipeline.buildGraph snapshot analysis

    let statusReaders =
        SemanticGraph.columnReadersOf (qn "sales" "orders") (id' "status") graph |> List.map fst

    Assert.Equal<string list>([ "file:a.sql" ], statusReaders)

[<Fact>]
let ``a SELECT star reader IS attributed to every column, flagged as wildcard`` () =
    // This is what a text search cannot do: `SELECT *` contains no column name,
    // so grepping for the column finds nothing, yet the reader genuinely breaks
    // when the column is dropped (RK-002).
    let analysis = analyse [ SqlFile "star.sql", "SELECT * FROM sales.orders" ]
    let graph = CorpusPipeline.buildGraph snapshot analysis

    let readers = SemanticGraph.columnReadersOf (qn "sales" "orders") (id' "status") graph

    Assert.Single readers |> ignore
    let sourceId, viaWildcard = List.head readers
    Assert.Equal("file:star.sql", sourceId)
    Assert.True(viaWildcard, "a SELECT * reader must be flagged as wildcard-derived")

[<Fact>]
let ``an explicit reader is not flagged as wildcard`` () =
    // Control for the flag itself.
    let analysis = analyse [ SqlFile "e.sql", "SELECT o.status FROM sales.orders o" ]
    let graph = CorpusPipeline.buildGraph snapshot analysis

    let _, viaWildcard =
        SemanticGraph.columnReadersOf (qn "sales" "orders") (id' "status") graph |> List.head

    Assert.False viaWildcard

[<Fact>]
let ``a column no source touches has no readers`` () =
    // Control: the query must discriminate, not return everything.
    let analysis = analyse [ SqlFile "a.sql", "SELECT o.status FROM sales.orders o" ]
    let graph = CorpusPipeline.buildGraph snapshot analysis

    Assert.Empty(SemanticGraph.columnReadersOf (qn "sales" "orders") (id' "total") graph)

[<Fact>]
let ``an UPDATE attributes its columns as writes, not reads`` () =
    // Attributing a write as a read would understate a drop's blast radius.
    let analysis =
        analyse [ SqlFile "w.sql", "UPDATE sales.orders SET status = 'x' WHERE order_id = 1" ]

    let graph = CorpusPipeline.buildGraph snapshot analysis

    Assert.NotEmpty(SemanticGraph.columnWritersOf (qn "sales" "orders") (id' "status") graph)
    Assert.Empty(SemanticGraph.columnReadersOf (qn "sales" "orders") (id' "status") graph)

[<Fact>]
let ``column matching respects identifier folding`` () =
    let analysis = analyse [ SqlFile "a.sql", "SELECT o.status FROM sales.orders o" ]
    let graph = CorpusPipeline.buildGraph snapshot analysis

    // Unquoted identifiers fold to lower case, so STATUS finds it.
    Assert.NotEmpty(SemanticGraph.columnReadersOf (qn "sales" "orders") (id' "STATUS") graph)
