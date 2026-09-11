module Strata.Tests.DesiredStateTests

open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Application
open Strata.Host.PgParser

/// Tests for PR-025 — reading desired state from per-object files.
///
/// The load itself is straightforward. What these tests are really protecting
/// is the completeness reporting: a half-loaded desired state that calls itself
/// complete makes a diff propose dropping every object whose file failed.

let private parser =
    PgParserAdapter.PostgresParser() :> Strata.Analysis.DialectPort.IDialectParser

let private load files = DesiredState.load parser files

let private tableNamed (loaded: DesiredState.Loaded) name =
    loaded.Snapshot.Objects
    |> List.tryPick (fun o ->
        match o with
        | TableObject t when QualifiedName.display t.Name = name -> Some t
        | _ -> None)

let private ordersSql =
    """
CREATE TABLE sales.orders (
    id          bigint        NOT NULL,
    customer_id bigint        NOT NULL REFERENCES sales.customers (id),
    total       numeric(12,2) NOT NULL DEFAULT 0,
    status      text,
    CONSTRAINT pk_orders PRIMARY KEY (id),
    CONSTRAINT ck_total_nonneg CHECK (total >= 0)
);
"""

// ---- the capability that did not exist at all -----------------------------

[<Fact>]
let ``every column of a declared table is read`` () =
    // Before this slice a five-column CREATE TABLE yielded ONE column mention,
    // and only because it appeared inside the CHECK expression. The ColumnDef
    // nodes carrying name, type, nullability and default were never read.
    let loaded = load [ "schema/sales/tables/orders.sql", ordersSql ]
    let orders = tableNamed loaded "sales.orders"

    Assert.True(orders.IsSome, "sales.orders should have been declared")
    Assert.Equal(4, orders.Value.Columns.Length)

    let names = orders.Value.Columns |> List.map (fun c -> c.Name.Text)
    Assert.Equal<string list>([ "id"; "customer_id"; "total"; "status" ], names)

[<Fact>]
let ``column types and nullability are read`` () =
    let orders = (load [ "f.sql", ordersSql ] |> fun l -> tableNamed l "sales.orders").Value
    let column name = orders.Columns |> List.find (fun c -> c.Name.Text = name)

    // pg_catalog is stripped and the type is rendered as the catalog renders
    // it, modifiers included, so a declared type compares equal to an
    // introspected one. Verified round-trip against a live database.
    Assert.Equal("numeric(12,2)", QualifiedName.display (column "total").Type.TypeName)
    Assert.Equal("text", QualifiedName.display (column "status").Type.TypeName)

    Assert.False((column "id").Type.IsNullable)
    Assert.True((column "status").Type.IsNullable)
    Assert.True((column "total").HasDefault)
    Assert.False((column "status").HasDefault)

[<Fact>]
let ``constraints written at table level are read`` () =
    let orders = (load [ "f.sql", ordersSql ] |> fun l -> tableNamed l "sales.orders").Value

    Assert.True(orders.PrimaryKey.IsSome)
    Assert.Equal("pk_orders", orders.PrimaryKey.Value.ConstraintName.Text)
    Assert.Equal<string list>([ "id" ], orders.PrimaryKey.Value.Columns |> List.map (fun c -> c.Text))
    Assert.Single orders.CheckConstraints |> ignore
    Assert.Single orders.ForeignKeys |> ignore
    Assert.Equal("sales.customers", QualifiedName.display (List.head orders.ForeignKeys).ReferencedTable)

[<Fact>]
let ``a primary key written inline is read the same as one written at table level`` () =
    // The catalog cannot tell which form the author used, so a loader that
    // read only one would diff a table against itself forever.
    let inline' = load [ "f.sql", "CREATE TABLE sales.t (id bigint PRIMARY KEY)" ]
    let table' = load [ "f.sql", "CREATE TABLE sales.t (id bigint, PRIMARY KEY (id))" ]

    let pkOf l = (tableNamed l "sales.t").Value.PrimaryKey

    Assert.True((pkOf inline').IsSome)
    Assert.True((pkOf table').IsSome)
    Assert.Equal<string list>(
        (pkOf inline').Value.Columns |> List.map (fun c -> c.Text),
        (pkOf table').Value.Columns |> List.map (fun c -> c.Text))

[<Fact>]
let ``an inline primary key implies NOT NULL`` () =
    // PostgreSQL reports a primary key column as NOT NULL whether or not the
    // author wrote it. Without this every primary key column would diff.
    let loaded = load [ "f.sql", "CREATE TABLE sales.t (id bigint PRIMARY KEY)" ]
    let column = (tableNamed loaded "sales.t").Value.Columns |> List.head

    Assert.False column.Type.IsNullable

[<Fact>]
let ``a declared object is Managed`` () =
    let loaded = load [ "f.sql", ordersSql ]
    Assert.Equal(Managed, (tableNamed loaded "sales.orders").Value.Scope)

// ---- what protects the diff ----------------------------------------------

[<Fact>]
let ``a clean load reports complete`` () =
    let loaded = load [ "f.sql", ordersSql ]

    Assert.Empty loaded.Failures
    Assert.Equal(Complete, Completeness.stateOf "relations" loaded.Snapshot.Completeness)

[<Fact>]
let ``a file that does not parse makes the snapshot incomplete`` () =
    // THE test. A diff reasons about absence, so a desired state missing an
    // object because its file failed would propose dropping that object. The
    // snapshot must refuse to call itself complete.
    let loaded =
        load
            [ "good.sql", ordersSql
              "broken.sql", "CREATE TABLE ( this is not sql" ]

    Assert.NotEmpty loaded.Failures
    Assert.NotEqual(Complete, Completeness.stateOf "relations" loaded.Snapshot.Completeness)
    // The object that DID load is still there — a failure elsewhere does not
    // discard what was read.
    Assert.True((tableNamed loaded "sales.orders").IsSome)

[<Fact>]
let ``an object type Strata does not model yet is a failure, not an empty file`` () =
    // A view file that loads as "nothing declared" reads to a diff as "this
    // object should not exist". ER-008: unmodelled is not absent.
    let loaded = load [ "v.sql", "CREATE VIEW sales.v AS SELECT 1 AS x" ]

    Assert.Empty loaded.Snapshot.Objects
    Assert.NotEmpty loaded.Failures
    Assert.NotEqual(Complete, Completeness.stateOf "relations" loaded.Snapshot.Completeness)

[<Fact>]
let ``an object declared in two files is reported`` () =
    let loaded =
        load
            [ "a.sql", "CREATE TABLE sales.t (id bigint)"
              "b.sql", "CREATE TABLE sales.t (id bigint)" ]

    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "declared 2 times")
    Assert.NotEqual(Complete, Completeness.stateOf "relations" loaded.Snapshot.Completeness)

[<Fact>]
let ``indexes are NotRequested rather than complete-and-empty`` () =
    // CREATE INDEX is a separate statement, so a CREATE TABLE declares none.
    // Reporting Complete here would let a diff conclude the project wants
    // every existing index dropped.
    let loaded = load [ "f.sql", ordersSql ]

    Assert.Equal(NotRequested, Completeness.stateOf "indexes" loaded.Snapshot.Completeness)

// ---- type canonicalisation ------------------------------------------------
//
// The parser canonicalises `bigint` to `int8`; the catalog's format_type()
// renders the same type as `bigint`. Comparing the two spellings reports every
// column of every table as changed, forever. Measured against a live database
// before this was fixed: 5 of 7 columns differed on type alone while every
// structural property matched.

[<Theory>]
[<InlineData("bigint", "bigint")>]
[<InlineData("integer", "integer")>]
[<InlineData("smallint", "smallint")>]
[<InlineData("boolean", "boolean")>]
[<InlineData("double precision", "double precision")>]
[<InlineData("real", "real")>]
[<InlineData("text", "text")>]
[<InlineData("timestamptz", "timestamp with time zone")>]
[<InlineData("timestamp", "timestamp without time zone")>]
[<InlineData("varchar(50)", "character varying(50)")>]
[<InlineData("numeric(12,2)", "numeric(12,2)")>]
[<InlineData("numeric", "numeric")>]
let ``a declared type is rendered the way the catalog renders it`` (declared: string) (expected: string) =
    let loaded = load [ "f.sql", sprintf "CREATE TABLE s.t (c %s)" declared ]
    let column = (tableNamed loaded "s.t").Value.Columns |> List.head

    Assert.Equal(expected, QualifiedName.display column.Type.TypeName)

[<Fact>]
let ``a precision on a phrase-spelled type goes inside the phrase`` () =
    // `timestamp(3) with time zone`, not `timestamp with time zone(3)`.
    // Appending naively breaks exactly the types whose canonical spelling is a
    // multi-word phrase.
    let loaded = load [ "f.sql", "CREATE TABLE s.t (c timestamptz(3))" ]
    let column = (tableNamed loaded "s.t").Value.Columns |> List.head

    Assert.Equal("timestamp(3) with time zone", QualifiedName.display column.Type.TypeName)
