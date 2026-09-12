module Strata.Tests.ShadowNormalisationTests

open Xunit
open Strata.Host.Postgres

/// Finding where a view's body starts.
///
/// This is the whole of `normaliseViews` that can be tested without a server,
/// and it is the part that was wrong. The old implementation searched for the
/// literal `" AS "`, so it needed a space on BOTH sides of the keyword; the
/// ordinary way to write a view puts a newline there, and every such view was
/// dropped from normalisation and never compared. Nothing failed — the plan
/// came back clean while the deployed body said something else.
///
/// The cases below are the ones a looser match gets wrong in the other
/// direction: taking a column list, an option list, a quoted name or a comment
/// for the keyword and handing the server a fragment.

let private body (ddl: string) =
    ShadowNormalisation.viewBodyStart ddl
    |> Option.map (fun i -> ddl.Substring(i).Trim())

[<Fact>]
let ``AS followed by a newline is the keyword`` () =
    // The defect. This is how the fixture that found it is written, and how
    // almost every view in a real project is written.
    Assert.Equal(
        Some "SELECT id FROM shop.product",
        body "CREATE VIEW shop.open_product AS\n    SELECT id FROM shop.product")

[<Fact>]
let ``AS surrounded by spaces still works`` () =
    Assert.Equal(Some "SELECT 1", body "CREATE VIEW v AS SELECT 1")

[<Fact>]
let ``AS at the very end of the header, with a tab or a carriage return`` () =
    Assert.Equal(Some "SELECT 1", body "CREATE VIEW v AS\tSELECT 1")
    Assert.Equal(Some "SELECT 1", body "CREATE VIEW v AS\r\nSELECT 1")

[<Fact>]
let ``lower case as is the keyword too`` () =
    Assert.Equal(Some "select 1", body "create view v as\nselect 1")

[<Fact>]
let ``a column list is not where the body starts`` () =
    // `(a, b)` sits at depth one. A scanner that ignored depth would still land
    // on the right AS here, but only because there is no AS inside the list —
    // the next case removes that luck.
    Assert.Equal(Some "SELECT 1, 2", body "CREATE VIEW v (a, b) AS\nSELECT 1, 2")

[<Fact>]
let ``an AS inside a parenthesised list is not the keyword`` () =
    Assert.Equal(
        Some "SELECT 1",
        body "CREATE VIEW v WITH (security_barrier, check_option = 'as') AS\nSELECT 1")

[<Fact>]
let ``an AS inside a quoted identifier is not the keyword`` () =
    // A view really can be called this, and the old substring search would have
    // split the name in half.
    Assert.Equal(Some "SELECT 1", body "CREATE VIEW \"my AS view\" AS\nSELECT 1")

[<Fact>]
let ``an AS inside a string literal is not the keyword`` () =
    Assert.Equal(
        Some "SELECT 1",
        body "CREATE VIEW v WITH (check_option = ' AS ') AS\nSELECT 1")

[<Fact>]
let ``an AS inside a leading line comment is not the keyword`` () =
    Assert.Equal(
        Some "SELECT 1",
        body "-- treat this AS documentation\nCREATE VIEW v AS\nSELECT 1")

[<Fact>]
let ``an AS inside a leading block comment is not the keyword`` () =
    Assert.Equal(
        Some "SELECT 1",
        body "/* rendered AS a note /* nested AS well */ still a note */\nCREATE VIEW v AS SELECT 1")

[<Fact>]
let ``a word ending in as is not the keyword`` () =
    // `alias` ends in `as`, and `as_of` begins with it. Both must be skipped or
    // the body starts in the middle of an identifier.
    Assert.Equal(Some "SELECT 1", body "CREATE VIEW alias_as_of AS\nSELECT 1")

[<Fact>]
let ``the first AS wins, so column aliases in the body are left alone`` () =
    Assert.Equal(Some "SELECT 1 AS a, 2 AS b", body "CREATE VIEW v AS SELECT 1 AS a, 2 AS b")

[<Fact>]
let ``DDL with no AS keyword yields nothing rather than a guess`` () =
    // The caller drops the view and discloses it as not-compared. Returning an
    // offset here would hand the server a fragment and get the same outcome
    // with a worse error.
    Assert.Equal(None, body "CREATE TABLE t (a int)")

/// Finding where a table's column list starts.
///
/// Same reasoning, same scanner, and the same failure waiting to happen: the
/// first `(` in the text is not the first `(` in the statement when a comment
/// gets there first. `-- products (catalogue)` is an ordinary thing to write
/// above a table, and rename annotations are comments too.

let private columns (ddl: string) =
    ShadowNormalisation.tableBodyStart ddl
    |> Option.map (fun i -> ddl.Substring(i).Trim())

[<Fact>]
let ``the column list is found in ordinary DDL`` () =
    Assert.Equal(Some "(a int)", columns "CREATE TABLE shop.product (a int)")

[<Fact>]
let ``a parenthesis in a leading comment is not the column list`` () =
    Assert.Equal(
        Some "(a int)",
        columns "-- products (catalogue)\nCREATE TABLE shop.product (a int)")

[<Fact>]
let ``a parenthesis in a rename annotation is not the column list`` () =
    // Rename intent is carried in comments, so this is the shape most likely to
    // reach the scanner in a real project.
    Assert.Equal(
        Some "(a int)",
        columns "-- strata:renamed_from (shop.item)\nCREATE TABLE shop.product (a int)")

[<Fact>]
let ``a parenthesis in a quoted table name is not the column list`` () =
    Assert.Equal(Some "(a int)", columns "CREATE TABLE \"odd(name\" (a int)")

[<Fact>]
let ``CREATE TABLE AS SELECT has no column list and yields nothing`` () =
    // Skipped rather than mangled: there is no column list to normalise.
    Assert.Equal(None, columns "CREATE TABLE t AS SELECT 1")

[<Fact>]
let ``a large declaration is scanned in linear time`` () =
    // Not a timing assertion — a shape assertion. The first version of this
    // scanner was a `seq { yield! }` generator, which composes one enumerator
    // per character and turns reading it into quadratic work. A table with a
    // few hundred columns is ordinary, and this one would have taken minutes.
    let columnsText =
        [ 1..2000 ]
        |> List.map (fun i -> sprintf "    c%d text DEFAULT 'x', -- column (%d)" i i)
        |> String.concat "\n"

    let ddl = sprintf "-- a wide table (very)\nCREATE TABLE t (\n%s\n    last int\n)" columnsText

    let started = System.Diagnostics.Stopwatch.StartNew()
    let found = ShadowNormalisation.tableBodyStart ddl
    started.Stop()

    Assert.Equal(Some(ddl.IndexOf "(\n"), found)

    Assert.True(
        started.ElapsedMilliseconds < 2000L,
        sprintf "scanning %d characters took %dms, which is not linear" ddl.Length started.ElapsedMilliseconds)
