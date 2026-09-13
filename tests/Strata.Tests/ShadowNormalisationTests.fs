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

/// Carrying a view's column-alias list into the shadow.
///
/// `pg_get_viewdef` folds a view's alias list into the query it renders, so a
/// shadow built without the list renders differently from the deployed view and
/// Strata proposes `replace-view` forever. The fix is to keep everything the
/// author wrote between the name and the keyword, so the shadow declaration is
/// their text with only the name changed.

let private suffix (ddl: string) = ShadowNormalisation.viewNameSuffix ddl

[<Fact>]
let ``a plain view has nothing between its name and AS`` () =
    Assert.Equal("", suffix "CREATE VIEW v AS SELECT 1")

[<Fact>]
let ``a column alias list is carried`` () =
    Assert.Equal("(a, b)", suffix "CREATE VIEW v (a, b) AS\nSELECT 1, 2")

[<Fact>]
let ``view options are carried`` () =
    Assert.Equal(
        "WITH (security_barrier)",
        suffix "CREATE OR REPLACE VIEW v WITH (security_barrier) AS\nSELECT 1")

[<Fact>]
let ``a column list and options are both carried`` () =
    Assert.Equal(
        "(a) WITH (security_barrier)",
        suffix "CREATE VIEW v (a) WITH (security_barrier) AS SELECT 1")

[<Fact>]
let ``a schema-qualified name is not mistaken for part of the suffix`` () =
    Assert.Equal("(a, b)", suffix "CREATE VIEW shop.v (a, b) AS SELECT 1, 2")

[<Fact>]
let ``a quoted name containing a dot is one name`` () =
    Assert.Equal("(a)", suffix "CREATE VIEW \"odd.name\" (a) AS SELECT 1")

[<Fact>]
let ``a view named view does not match its own keyword`` () =
    // `VIEW` is found as a bare word at depth zero; the quoted name is hidden
    // from the scanner, so the keyword is the keyword.
    Assert.Equal("(a)", suffix "CREATE VIEW \"view\" (a) AS SELECT 1")

[<Fact>]
let ``the name end is past the whole qualified name`` () =
    let ddl = "CREATE VIEW shop.v (a) AS SELECT 1"
    Assert.Equal(Some(ddl.IndexOf " (a)"), ShadowNormalisation.viewNameEnd ddl)

/// Rewriting a name without rewriting the data.
///
/// A policy's table has to be renamed to the shadow's before the policy can be
/// created there, and that was done with a plain `String.Replace`. A policy
/// expression can legitimately contain its own table's name as a string —
/// `USING (note <> 'see shop.product')` — and rewriting that changes what the
/// policy MEANS. The shadow then renders a different expression than the file
/// declares, the two compare unequal on every run, and a policy nobody touched
/// is proposed for replacement forever.

let private rewrite (text: string) =
    ShadowNormalisation.replaceSignificant
        [ "\"shop\".\"product\""; "\"shop\".product"; "shop.\"product\""; "shop.product" ]
        "_shadow.\"product\""
        text

[<Fact>]
let ``a name in code is rewritten`` () =
    Assert.Equal(
        "CREATE POLICY p ON _shadow.\"product\" USING (a)",
        rewrite "CREATE POLICY p ON shop.product USING (a)")

[<Fact>]
let ``every spelling of the name is rewritten`` () =
    Assert.Equal(
        "_shadow.\"product\" _shadow.\"product\" _shadow.\"product\" _shadow.\"product\"",
        rewrite "shop.product \"shop\".product shop.\"product\" \"shop\".\"product\"")

[<Fact>]
let ``a name inside a string literal is left alone`` () =
    // The defect. The literal is data; changing it changes the policy.
    Assert.Equal(
        "CREATE POLICY p ON _shadow.\"product\" USING (note <> 'see shop.product')",
        rewrite "CREATE POLICY p ON shop.product USING (note <> 'see shop.product')")

[<Fact>]
let ``a name inside a comment is left alone`` () =
    Assert.Equal(
        "-- guards shop.product\nCREATE POLICY p ON _shadow.\"product\" USING (a)",
        rewrite "-- guards shop.product\nCREATE POLICY p ON shop.product USING (a)")

[<Fact>]
let ``a name inside a dollar-quoted string is left alone`` () =
    Assert.Equal(
        "SELECT $tag$shop.product$tag$ FROM _shadow.\"product\"",
        rewrite "SELECT $tag$shop.product$tag$ FROM shop.product")

[<Fact>]
let ``a doubled quote inside a literal does not end it`` () =
    Assert.Equal(
        "USING (note <> 'it''s shop.product')",
        rewrite "USING (note <> 'it''s shop.product')")

[<Fact>]
let ``the longest spelling wins so a quoted name is not half-rewritten`` () =
    Assert.Equal("_shadow.\"product\"", rewrite "\"shop\".\"product\"")

[<Fact>]
let ``text with no occurrence is returned unchanged`` () =
    Assert.Equal("SELECT 1 FROM other.thing", rewrite "SELECT 1 FROM other.thing")

[<Fact>]
let ``a quoted name that merely contains the spelling keeps its own name`` () =
    // The quoted identifier is skipped whole because no spelling matches at the
    // quote. A table really can be called this.
    Assert.Equal(
        "SELECT \"my shop.product notes\" FROM _shadow.\"product\"",
        rewrite "SELECT \"my shop.product notes\" FROM shop.product")

/// Splitting a `CREATE DOMAIN` at its name.
///
/// Everything after the name — `AS text`, a `COLLATE`, the `DEFAULT`, every
/// `CHECK` — is carried into the shadow declaration verbatim, so the only thing
/// this has to get right is where the name ends. Getting it wrong hands the
/// server a fragment, the declaration fails on its savepoint, and the domain is
/// disclosed as not-compared: honest, and a comparison that should have happened.

let private domainRest (ddl: string) =
    ShadowNormalisation.domainNameEnd ddl
    |> Option.map (fun i -> ddl.Substring(i).Trim())

[<Fact>]
let ``a qualified domain name is split from the rest of its declaration`` () =
    Assert.Equal(Some "AS text NOT NULL", domainRest "CREATE DOMAIN app.email AS text NOT NULL")

[<Fact>]
let ``the AS is optional, as PostgreSQL allows`` () =
    Assert.Equal(Some "text NOT NULL", domainRest "CREATE DOMAIN app.email text NOT NULL")

[<Fact>]
let ``a quoted domain name is one name, dot included`` () =
    Assert.Equal(Some "AS text", domainRest "CREATE DOMAIN \"odd.name\" AS text")

[<Fact>]
let ``a domain named domain does not match its own keyword`` () =
    // The quoted name is hidden from the scanner, so the keyword is the keyword.
    Assert.Equal(Some "AS text", domainRest "CREATE DOMAIN \"domain\" AS text")

[<Fact>]
let ``the keyword is not found inside a leading comment`` () =
    // `-- the email DOMAIN` above the statement would make a looser match split
    // at the comment and hand the server everything after it.
    Assert.Equal(
        Some "AS text",
        domainRest "-- the email DOMAIN, for addresses\nCREATE DOMAIN app.email AS text")

[<Fact>]
let ``a declaration with no DOMAIN keyword yields None rather than a guess`` () =
    Assert.Equal(None, ShadowNormalisation.domainNameEnd "CREATE TABLE t (a int)")

[<Fact>]
let ``whitespace and newlines between the parts of the name are skipped`` () =
    Assert.Equal(
        Some "AS text",
        domainRest "CREATE DOMAIN\n    app\n    .\n    email\n    AS text")
