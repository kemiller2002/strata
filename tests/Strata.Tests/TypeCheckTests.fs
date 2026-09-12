/// Shares the live database with CatalogIntrospectionTests — see the note there.
[<Xunit.Collection("live-database")>]
module Strata.Tests.TypeCheckTests

open System
open Xunit
open Npgsql
open Strata.Host.Postgres

/// Tier 3 of the compile-time-safety ladder (`DF-STRATA-2026-8B60`).
///
/// Every case below PASSES the reference check. That is the point: tier 2 asks
/// whether `customer.emial` exists and cannot ask whether `total = 'abc'` makes
/// sense, and implementing the second means implementing PostgreSQL's type
/// resolution, overload selection and implicit-cast rules. `PREPARE` plans the
/// statement and hands back exactly the error the server would have raised.

let private connectionString =
    match Environment.GetEnvironmentVariable "STRATA_TEST_PG" with
    | null | "" -> None
    | value -> Some value

type private RequiresPostgresAttribute() =
    inherit FactAttribute()
    do
        base.Skip <-
            match connectionString with
            | Some _ -> null
            | None ->
                "STRATA_TEST_PG not set; live PostgreSQL integration test skipped. \
                 Build the fixture with scripts/test-fixture.sh and set the connection string it prints."

let private schema = "tc"

let private exec (sql: string) =
    use c = new NpgsqlConnection(connectionString.Value)
    c.Open()
    use command = new NpgsqlCommand(sql, c)
    command.ExecuteNonQuery() |> ignore

let private withFixture (body: string -> unit) =
    exec (sprintf "DROP SCHEMA IF EXISTS %s CASCADE" schema)

    exec (
        sprintf
            "CREATE SCHEMA %s; CREATE TABLE %s.product (id bigint PRIMARY KEY, name text NOT NULL, price numeric(12,2) NOT NULL)"
            schema
            schema
    )

    try
        body connectionString.Value
    finally
        try exec (sprintf "DROP SCHEMA IF EXISTS %s CASCADE" schema) with _ -> ()

let private one (sql: string) = [ 1, 0, sql ]

// ---- what PREPARE can be asked ----------------------------------------------

[<Fact>]
let ``the statement kind is read past a leading comment`` () =
    // `-- create the orders view` above a SELECT would make a naive reader call
    // the statement a CREATE, which here is the difference between "cannot be
    // type-checked" and "type-checks clean".
    Assert.True(TypeCheck.isPreparable "-- create the orders view\nSELECT 1")
    Assert.False(TypeCheck.isPreparable "-- select the orders\nCREATE TABLE t (a int)")

[<Fact>]
let ``the preparable statement kinds are recognised`` () =
    for sql in
        [ "SELECT 1"
          "insert into t values (1)"
          "UPDATE t SET a = 1"
          "DELETE FROM t"
          "VALUES (1)"
          "WITH x AS (SELECT 1) SELECT * FROM x" ] do
        Assert.True(TypeCheck.isPreparable sql, sql)

[<Fact>]
let ``DDL and utility statements are not preparable`` () =
    for sql in
        [ "CREATE TABLE t (a int)"
          "ALTER TABLE t ADD COLUMN b int"
          "DROP TABLE t"
          "GRANT SELECT ON t TO r"
          "VACUUM" ] do
        Assert.False(TypeCheck.isPreparable sql, sql)

// ---- what the server decides ------------------------------------------------

[<RequiresPostgres>]
let ``a well-typed statement plans without complaint`` () =
    withFixture (fun connection ->
        match TypeCheck.check connection (one (sprintf "SELECT id, name FROM %s.product WHERE price > 1.0" schema)) with
        | Ok result ->
            Assert.Empty result.Findings
            Assert.Empty result.NotChecked
        | Microsoft.FSharp.Core.Error message -> failwithf "the check could not run: %s" message)

[<RequiresPostgres>]
let ``a literal that is not of the column's type is a finding`` () =
    // The canonical tier-3 case. Every name in it exists, so tier 2 says VALID.
    withFixture (fun connection ->
        match
            TypeCheck.check connection (one (sprintf "SELECT id FROM %s.product WHERE price = 'not a number'" schema))
        with
        | Ok result ->
            Assert.Single result.Findings |> ignore
            Assert.Contains("numeric", (List.head result.Findings).Message)
        | Microsoft.FSharp.Core.Error message -> failwithf "the check could not run: %s" message)

[<RequiresPostgres>]
let ``a function with no such signature is a finding`` () =
    withFixture (fun connection ->
        match TypeCheck.check connection (one (sprintf "SELECT lower(price) FROM %s.product" schema)) with
        | Ok result ->
            Assert.Single result.Findings |> ignore
            let finding = List.head result.Findings
            // 42883 is undefined_function. Carried so a caller can tell it from
            // a type mismatch without reading English.
            Assert.Equal("42883", finding.SqlState)
        | Microsoft.FSharp.Core.Error message -> failwithf "the check could not run: %s" message)

[<RequiresPostgres>]
let ``an ambiguous column is a finding`` () =
    withFixture (fun connection ->
        let sql =
            sprintf "SELECT id FROM %s.product a JOIN %s.product b ON a.id = b.id" schema schema

        match TypeCheck.check connection (one sql) with
        | Ok result ->
            Assert.Single result.Findings |> ignore
            Assert.Contains("ambiguous", (List.head result.Findings).Message)
        | Microsoft.FSharp.Core.Error message -> failwithf "the check could not run: %s" message)

[<RequiresPostgres>]
let ``DDL comes back NOT CHECKED rather than checked and passed`` () =
    // The ER-008 line. A checker that answers "OK" when it means "I could not
    // tell" is worse than no checker, because people stop looking.
    withFixture (fun connection ->
        match TypeCheck.check connection (one (sprintf "CREATE TABLE %s.later (id bigint)" schema)) with
        | Ok result ->
            Assert.Empty result.Findings
            Assert.Single result.NotChecked |> ignore
            Assert.Contains("PREPARE takes only", (List.head result.NotChecked).Reason)
        | Microsoft.FSharp.Core.Error message -> failwithf "the check could not run: %s" message)

[<RequiresPostgres>]
let ``one rejected statement does not stop the rest`` () =
    // A savepoint per statement. Without it the first failure aborts the
    // transaction and every later statement is reported as broken too.
    withFixture (fun connection ->
        let statements =
            [ 1, 0, sprintf "SELECT id FROM %s.product WHERE price = 'nope'" schema
              2, 10, sprintf "SELECT id FROM %s.product" schema
              3, 20, sprintf "SELECT lower(price) FROM %s.product" schema ]

        match TypeCheck.check connection statements with
        | Ok result ->
            Assert.Equal(2, List.length result.Findings)
            Assert.Equal<int list>([ 1; 3 ], result.Findings |> List.map (fun f -> f.Statement))
        | Microsoft.FSharp.Core.Error message -> failwithf "the check could not run: %s" message)

[<RequiresPostgres>]
let ``the check leaves nothing behind`` () =
    // PREPARE plans rather than executes, and the whole thing is one rolled-back
    // transaction. An INSERT that type-checks must not have inserted.
    withFixture (fun connection ->
        match
            TypeCheck.check
                connection
                (one (sprintf "INSERT INTO %s.product (id, name, price) VALUES (1, 'x', 1.0)" schema))
        with
        | Ok result ->
            Assert.Empty result.Findings

            use c = new NpgsqlConnection(connection)
            c.Open()
            use command = new NpgsqlCommand(sprintf "SELECT count(*) FROM %s.product" schema, c)
            Assert.Equal(0L, command.ExecuteScalar() :?> int64)
        | Microsoft.FSharp.Core.Error message -> failwithf "the check could not run: %s" message)
