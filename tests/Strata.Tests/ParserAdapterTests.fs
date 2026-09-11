module Strata.Tests.ParserAdapterTests

open Xunit
open Strata.Semantic.Identity
open Strata.Analysis.StatementReferences
open Strata.Analysis.ScopeResolution
open Strata.Host.PgParser.DialectParser
open Strata.Host.PgParser.PgParserAdapter

/// End-to-end tests: real SQL text through the real parser, into Tier 2
/// resolution. These are the integration proof that the adapter's role
/// assignment actually reaches the scope resolver — SDE's verification method
/// requires behavioural evidence for a dispatch arm, not merely its presence.

let private parser = PostgresParser() :> IDialectParser

let private parseOne (sql: string) =
    match parser.ParseScript sql with
    | [ Parsed (_, extraction) ] -> extraction
    | other -> failwithf "expected exactly one parsed statement, got %A" other

[<Fact>]
let ``parser identity reports its PostgreSQL major`` () =
    Assert.Equal("pgsqlparser 1.0.0", parser.Identity.AdapterName)
    Assert.True(parser.Identity.DialectMajor > 0, "dialect major should be known")

[<Fact>]
let ``END TO END a CTE shadowing a table produces no dependency edge`` () =
    // The defect from EV-STRATA-2026-B9C4, driven through the real parser.
    //
    // A single-entry search_path makes this discriminating: the control below
    // shows the identical bare name DOES become an edge when no CTE binds it,
    // so an empty result for the shadowed case cannot be an accident of
    // unqualified names never resolving.
    let searchPath = [ Identifier.unquoted "public" ]

    let control = parseOne "SELECT id FROM orders"
    let controlEdges = dependencyEdgeCandidates searchPath control
    Assert.Single controlEdges |> ignore
    Assert.Equal("public.orders", QualifiedName.display (List.head controlEdges))

    let shadowed = parseOne "WITH orders AS (SELECT 1 AS id) SELECT id FROM orders"
    Assert.Empty(dependencyEdgeCandidates searchPath shadowed)

[<Fact>]
let ``END TO END a schema-qualified table produces exactly one edge`` () =
    let extraction = parseOne "SELECT o.id FROM sales.orders o"

    let edges = dependencyEdgeCandidates [] extraction

    Assert.Single edges |> ignore
    Assert.Equal("sales.orders", QualifiedName.display (List.head edges))

[<Fact>]
let ``END TO END a join yields both qualified relations`` () =
    let extraction =
        parseOne "SELECT o.id, c.name FROM sales.orders o JOIN sales.customer c ON c.id = o.customer_id"

    let edges =
        dependencyEdgeCandidates [] extraction
        |> List.map QualifiedName.display
        |> List.sort

    Assert.Equal<string list>([ "sales.customer"; "sales.orders" ], edges)

[<Fact>]
let ``a failed parse is reported as Failed, not as a statement with no references`` () =
    // ER-008. The dangerous alternative is an empty extraction that looks like
    // a statement touching nothing.
    match parser.ParseScript "SELECT FROM WHERE;" with
    | [ Failed (_, error) ] ->
        Assert.False(System.String.IsNullOrWhiteSpace error.Message)
        Assert.True(error.CursorPosition > 0)
    | other -> failwithf "expected Failed, got %A" other

[<Fact>]
let ``a multi-statement script yields one entry per statement`` () =
    let statements = parser.ParseScript "SELECT 1; SELECT 2; SELECT 3;"

    Assert.Equal(3, List.length statements)

[<Fact>]
let ``dynamic SQL is flagged rather than silently ignored`` () =
    // §11.4: dynamic SQL degrades analyzability and that must be visible.
    let extraction = parseOne "EXECUTE stmt_name"

    Assert.True(extraction.ContainsDynamicSql)

[<Fact>]
let ``a temp table target is not treated as a managed relation`` () =
    let extraction = parseOne "CREATE TEMP TABLE tmp_x AS SELECT * FROM sales.orders"

    let edges = dependencyEdgeCandidates [] extraction |> List.map QualifiedName.display

    Assert.DoesNotContain("tmp_x", edges)
    Assert.Contains("sales.orders", edges)

[<Fact>]
let ``SELECT star through the real parser is unexpanded, not zero columns`` () =
    let extraction = parseOne "SELECT * FROM sales.orders"

    Assert.NotEmpty extraction.Columns
    Assert.True(extraction.Columns |> List.exists (fun c -> c.IsWildcard))

[<Fact>]
let ``statement shape is classified for writes`` () =
    Assert.Equal(UpdateShape, (parseOne "UPDATE sales.orders SET status = 'x'").Shape)
    Assert.Equal(DeleteShape, (parseOne "DELETE FROM sales.orders").Shape)
    Assert.Equal(InsertShape, (parseOne "INSERT INTO sales.orders (id) VALUES (1)").Shape)

[<Fact>]
let ``PL/pgSQL routine bodies parse`` () =
    let body =
        "CREATE FUNCTION f() RETURNS void AS $$ BEGIN RAISE NOTICE 'x'; END; $$ LANGUAGE plpgsql;"

    match parser.ParseRoutineBody body with
    | Ok () -> ()
    | Error e -> failwithf "expected routine body to parse, got %s" e.Message

[<Fact>]
let ``fingerprints ignore literal values`` () =
    // §123: needed so a corpus deduplicates by query shape.
    let a = parser.Fingerprint "SELECT * FROM t WHERE id = 1"
    let b = parser.Fingerprint "SELECT * FROM t WHERE id = 999"

    match a, b with
    | Ok fa, Ok fb -> Assert.Equal(fa, fb)
    | _ -> failwith "expected both fingerprints to succeed"
