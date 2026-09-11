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
    // A file that loads as "nothing declared" reads to a diff as "this object
    // should not exist". ER-008: unmodelled is not absent. Views and routines
    // now load, so this uses CREATE INDEX, which genuinely does not.
    let loaded = load [ "i.sql", "CREATE INDEX idx_orders_total ON sales.orders (total)" ]

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

// ---- serial pseudo-types --------------------------------------------------
//
// `serial` is not a type. `id bigserial` becomes `id bigint NOT NULL DEFAULT
// nextval(...)` plus a sequence, so the catalog reports bigint WITH a default
// while the file says bigserial with none. Read literally that is two
// differences on a column nobody changed, and serial is common enough that a
// diff would report permanent churn on most real schemas. Found by
// round-tripping a created table back through the diff.

[<Theory>]
[<InlineData("smallserial", "smallint")>]
[<InlineData("serial2", "smallint")>]
[<InlineData("serial", "integer")>]
[<InlineData("serial4", "integer")>]
[<InlineData("bigserial", "bigint")>]
[<InlineData("serial8", "bigint")>]
let ``a serial column reads as the type the catalog reports`` (declared: string) (expected: string) =
    let loaded = load [ "f.sql", sprintf "CREATE TABLE s.t (id %s)" declared ]
    let column = (tableNamed loaded "s.t").Value.Columns |> List.head

    Assert.Equal(expected, QualifiedName.display column.Type.TypeName)

[<Fact>]
let ``a serial column carries the default and NOT NULL the author did not write`` () =
    let loaded = load [ "f.sql", "CREATE TABLE s.t (id bigserial)" ]
    let column = (tableNamed loaded "s.t").Value.Columns |> List.head

    Assert.True(column.HasDefault, "serial implies a nextval default the catalog reports")
    Assert.False(column.Type.IsNullable, "serial implies NOT NULL")

[<Fact>]
let ``a schema-qualified type named serial is left alone`` () =
    // The control: `myschema.serial` is a user type, not the pseudo-type, and
    // rewriting it would be a fabricated difference in the other direction.
    let loaded = load [ "f.sql", "CREATE TABLE s.t (id myschema.serial)" ]
    let column = (tableNamed loaded "s.t").Value.Columns |> List.head

    Assert.Equal("myschema.serial", QualifiedName.display column.Type.TypeName)

// ---- the declaring text ---------------------------------------------------

[<Fact>]
let ``a single-declaration file records its verbatim text`` () =
    // Creating from a reconstructed parse tree loses whatever the model does
    // not carry — a default expression, a check expression — and 96% of real
    // tables have at least one. The file already holds exactly the DDL the
    // author wrote.
    let loaded = load [ "schema/sales/tables/orders.sql", ordersSql ]

    Assert.Single loaded.Declarations |> ignore
    let name, text = List.head loaded.Declarations
    Assert.Equal("sales.orders", QualifiedName.display name)
    Assert.Equal(ordersSql, text)

[<Fact>]
let ``a file declaring two objects records neither`` () =
    // Executing a two-table file to create ONE of them would create the other
    // as a side effect — a change nobody planned.
    let loaded =
        load [ "f.sql", "CREATE TABLE s.a (id bigint); CREATE TABLE s.b (id bigint);" ]

    Assert.Equal(2, List.length loaded.Snapshot.Objects)
    Assert.Empty loaded.Declarations

// ---- views and routines ---------------------------------------------------

[<Fact>]
let ``a view declaration loads`` () =
    let loaded = load [ "v.sql", "CREATE VIEW sales.v_open AS SELECT id FROM sales.orders" ]

    Assert.Empty loaded.Failures
    Assert.Contains(loaded.Snapshot.Objects, fun o ->
        match o with
        | ViewObject v -> QualifiedName.display v.Name = "sales.v_open" && not v.IsMaterialized
        | _ -> false)

[<Fact>]
let ``a materialized view is distinguished from a plain one`` () =
    // CREATE MATERIALIZED VIEW is a CreateTableAsStmt with an objtype of
    // matview, not a ViewStmt, so a reader that only handles ViewStmt loses it
    // entirely — and a lost object reads to a diff as one that should not exist.
    let loaded =
        load [ "m.sql", "CREATE MATERIALIZED VIEW sales.m_totals AS SELECT id FROM sales.orders" ]

    Assert.Contains(loaded.Snapshot.Objects, fun o ->
        match o with
        | ViewObject v -> QualifiedName.display v.Name = "sales.m_totals" && v.IsMaterialized
        | _ -> false)

[<Fact>]
let ``a routine declaration carries its signature`` () =
    let loaded =
        load
            [ "f.sql",
              "CREATE FUNCTION sales.total(p_id bigint, p_when timestamptz) RETURNS numeric AS $$ SELECT 1 $$ LANGUAGE sql" ]

    let routine =
        loaded.Snapshot.Objects
        |> List.pick (fun o -> match o with RoutineObject r -> Some r | _ -> None)

    Assert.Equal("sales.total", QualifiedName.display routine.Name)
    Assert.Equal(Function, routine.Kind)
    // Types only — a parameter NAME is not part of a routine's identity, and
    // types are canonicalised the same way a column's are.
    Assert.Equal<string list>([ "bigint"; "timestamp with time zone" ], routine.ArgumentTypes)
    Assert.Equal("sql", routine.Language)

[<Fact>]
let ``a procedure is distinguished from a function`` () =
    let loaded = load [ "p.sql", "CREATE PROCEDURE sales.do_it() AS $$ BEGIN END $$ LANGUAGE plpgsql" ]

    let routine =
        loaded.Snapshot.Objects
        |> List.pick (fun o -> match o with RoutineObject r -> Some r | _ -> None)

    Assert.Equal(Procedure, routine.Kind)

[<Fact>]
let ``an OUT parameter is not part of the signature`` () =
    // An OUT parameter does not participate in overload resolution, so
    // including it would give the routine an identity PostgreSQL does not
    // recognise — and the same routine would never match itself.
    let loaded =
        load
            [ "f.sql",
              "CREATE FUNCTION sales.f(p_in bigint, OUT p_out text) RETURNS text AS $$ SELECT 'x' $$ LANGUAGE sql" ]

    let routine =
        loaded.Snapshot.Objects
        |> List.pick (fun o -> match o with RoutineObject r -> Some r | _ -> None)

    Assert.Equal<string list>([ "bigint" ], routine.ArgumentTypes)

[<Fact>]
let ``a view carries no columns and no definition, and neither means none`` () =
    // A view's column list is a property of the query it wraps and is only
    // knowable by resolving that query; the definition text is not recoverable
    // from the parse tree. The record cannot express "unknown", so the diff is
    // required to compare views by presence only. This test exists to make that
    // contract visible at the point the empties are produced.
    let loaded = load [ "v.sql", "CREATE VIEW sales.v AS SELECT id, total FROM sales.orders" ]

    let view =
        loaded.Snapshot.Objects
        |> List.pick (fun o -> match o with ViewObject v -> Some v | _ -> None)

    Assert.Empty view.Columns
    Assert.Equal("", view.Definition)
