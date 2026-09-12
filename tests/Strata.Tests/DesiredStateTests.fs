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

let private qn schema name =
    QualifiedName.qualified (Identifier.unquoted schema) (Identifier.unquoted name)

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
    Assert.Equal(Some "pk_orders", orders.PrimaryKey.Value.ConstraintName |> Option.map (fun n -> n.Text))
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


// ---- triggers -------------------------------------------------------------
//
// Everything here protects the same distinction indexes needed: a project with
// no trigger file says NOTHING about triggers, and a project with one takes
// ownership of them all.

let private triggerSql =
    """
CREATE TRIGGER touch_orders
    BEFORE UPDATE OF total, status ON sales.orders
    FOR EACH ROW
    WHEN (OLD.total IS DISTINCT FROM NEW.total)
    EXECUTE FUNCTION sales.touch('audit');
"""

let private triggerOn (loaded: DesiredState.Loaded) table name =
    (tableNamed loaded table).Value.Triggers
    |> List.tryFind (fun t -> t.Name.Text = name)

[<Fact>]
let ``triggers are NotRequested rather than complete-and-empty`` () =
    let loaded = load [ "orders.sql", ordersSql ]

    Assert.Equal(NotRequested, Completeness.stateOf "triggers" loaded.Snapshot.Completeness)

[<Fact>]
let ``declaring one trigger takes ownership of triggers`` () =
    let loaded = load [ "orders.sql", ordersSql; "touch.sql", triggerSql ]

    Assert.Equal(Complete, Completeness.stateOf "triggers" loaded.Snapshot.Completeness)

[<Fact>]
let ``a declared trigger attaches to its table with every field read`` () =
    let loaded = load [ "orders.sql", ordersSql; "touch.sql", triggerSql ]

    match triggerOn loaded "sales.orders" "touch_orders" with
    | None -> failwith "the trigger did not attach to sales.orders"
    | Some t ->
        Assert.Equal(TriggerTiming.Before, t.Timing)
        Assert.Equal<string list>([ "update" ], t.Events)
        Assert.Equal(TriggerLevel.Row, t.Level)
        Assert.Equal<string list>([ "total"; "status" ], t.UpdateColumns |> List.map (fun c -> c.Text))
        Assert.Equal("sales.touch", QualifiedName.display t.Function)
        Assert.Equal<string list>([ "audit" ], t.Arguments)
        Assert.True(t.HasCondition, "the WHEN clause must be visible as present")

[<Fact>]
let ``events are read in a fixed order, not the order they were written`` () =
    // Both sides store these as a bitmask, so neither preserves the author's
    // order. Comparing them in written order would report churn on a trigger
    // nobody changed.
    let written =
        load
            [ "orders.sql", ordersSql
              "t.sql", "CREATE TRIGGER t AFTER UPDATE OR DELETE OR INSERT ON sales.orders \
                        FOR EACH STATEMENT EXECUTE FUNCTION sales.touch();" ]

    Assert.Equal<string list>(
        [ "insert"; "delete"; "update" ],
        (triggerOn written "sales.orders" "t").Value.Events)

[<Fact>]
let ``an AFTER trigger is read as AFTER rather than as an unset timing`` () =
    // TRIGGER_TYPE_AFTER is 0, not a bit of its own, so reading the timing as a
    // flag test alone would make every AFTER trigger indistinguishable from a
    // value nobody set.
    let loaded =
        load
            [ "orders.sql", ordersSql
              "t.sql", "CREATE TRIGGER t AFTER INSERT ON sales.orders FOR EACH ROW EXECUTE FUNCTION sales.touch();" ]

    Assert.Equal(TriggerTiming.After, (triggerOn loaded "sales.orders" "t").Value.Timing)

[<Fact>]
let ``an INSTEAD OF trigger is read as such`` () =
    let loaded =
        load
            [ "orders.sql", ordersSql
              "t.sql", "CREATE TRIGGER t INSTEAD OF INSERT ON sales.orders FOR EACH ROW EXECUTE FUNCTION sales.touch();" ]

    Assert.Equal(TriggerTiming.InsteadOf, (triggerOn loaded "sales.orders" "t").Value.Timing)

[<Fact>]
let ``a trigger on a table the project does not declare is a failure`` () =
    // Not something to drop quietly: the project asserts a trigger on an object
    // it does not own, and acting on half of that is worse than acting on none.
    let loaded = load [ "touch.sql", triggerSql ]

    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "does not declare as a table")
    Assert.NotEqual(Complete, Completeness.stateOf "relations" loaded.Snapshot.Completeness)

[<Fact>]
let ``a constraint trigger is reported as unmodelled, never loaded as an ordinary one`` () =
    // A CONSTRAINT TRIGGER carries deferrability this model has no room for.
    // Loading it as an ordinary trigger would make a deferred trigger compare
    // equal to an immediate one.
    let loaded =
        load
            [ "orders.sql", ordersSql
              "t.sql", "CREATE CONSTRAINT TRIGGER t AFTER INSERT ON sales.orders \
                        DEFERRABLE FOR EACH ROW EXECUTE FUNCTION sales.touch();" ]

    Assert.Empty((tableNamed loaded "sales.orders").Value.Triggers)
    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "CONSTRAINT TRIGGER")

[<Fact>]
let ``a single-trigger file records its verbatim text`` () =
    // A WHEN clause and an UPDATE OF column list are not in the model, so a
    // reconstruction would create a trigger that fires more often than the file
    // asked for — and report success doing it.
    let loaded = load [ "orders.sql", ordersSql; "touch.sql", triggerSql ]

    Assert.Contains(
        loaded.TriggerDeclarations,
        fun ((table, name), text) ->
            QualifiedName.display table = "sales.orders"
            && name.Text = "touch_orders"
            && text = triggerSql)


// ---- reference data --------------------------------------------------------

let private accountTypeSql =
    """
CREATE TABLE ref.account_type (
    id    smallint PRIMARY KEY,
    code  text     NOT NULL,
    label text     NOT NULL
);
"""

let private dataSql =
    """
INSERT INTO ref.account_type (id, code, label) VALUES
    (1, 'checking', 'Checking'),
    (2, 'savings',  'Savings');
"""

[<Fact>]
let ``reference data is NotRequested rather than complete-and-empty`` () =
    let loaded = load [ "t.sql", accountTypeSql ]

    Assert.Equal(NotRequested, Completeness.stateOf "reference_data" loaded.Snapshot.Completeness)

[<Fact>]
let ``declared rows carry their literals as written`` () =
    // Re-emitted TOKENS, not values Strata interpreted. `1` stays `1` and
    // `'checking'` stays quoted, so the server decides what each means in the
    // column it lands in.
    let loaded = load [ "t.sql", accountTypeSql; "d.sql", dataSql ]
    let data = Assert.Single loaded.Data

    Assert.Equal("ref.account_type", QualifiedName.display data.Table)
    Assert.Equal<string list>([ "id"; "code"; "label" ], data.Columns |> List.map (fun c -> c.Text))
    Assert.Equal<string list list>(
        [ [ "1"; "'checking'"; "'Checking'" ]; [ "2"; "'savings'"; "'Savings'" ] ],
        data.Rows)

[<Fact>]
let ``a float keeps its exact spelling`` () =
    // `1.250` and `1.25` are the same number and different texts. Rendering the
    // token the author wrote leaves the decision with the server, which is the
    // only thing that knows the column is numeric(6,2).
    let loaded =
        load
            [ "t.sql", accountTypeSql
              "d.sql", "INSERT INTO ref.account_type (id, code, label) VALUES (1.250, 'a', 'b');" ]

    Assert.Equal<string list>([ "1.250"; "'a'"; "'b'" ], (Assert.Single loaded.Data).Rows |> List.head)

[<Fact>]
let ``a false is read as false, not as an absent value`` () =
    // PostgreSQL encodes `false` as a Boolval message holding the protobuf
    // default, so a null check on the message reads every `false` as "no value
    // given" and would write `true` in its place.
    let loaded =
        load
            [ "t.sql", accountTypeSql
              "d.sql", "INSERT INTO ref.account_type (id, code, label) VALUES (true, false, NULL);" ]

    Assert.Equal<string list>([ "true"; "false"; "NULL" ], (Assert.Single loaded.Data).Rows |> List.head)

[<Fact>]
let ``a quote inside a declared string survives re-emission`` () =
    let loaded =
        load
            [ "t.sql", accountTypeSql
              "d.sql", "INSERT INTO ref.account_type (id, code, label) VALUES (1, 'a', 'it''s');" ]

    Assert.Equal<string list>([ "1"; "'a'"; "'it''s'" ], (Assert.Single loaded.Data).Rows |> List.head)

[<Fact>]
let ``a non-literal value is refused, not rendered`` () =
    // The shadow pass would turn now() into a concrete timestamp, the real row
    // would hold a different one, and the two would differ on every run
    // forever.
    let loaded =
        load
            [ "t.sql", accountTypeSql
              "d.sql", "INSERT INTO ref.account_type (id, code, label) VALUES (1, 'a', upper('b'));" ]

    Assert.Empty loaded.Data
    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "must be literals")

[<Fact>]
let ``ON CONFLICT is refused: reconciling is the diff's decision`` () =
    let loaded =
        load
            [ "t.sql", accountTypeSql
              "d.sql",
              "INSERT INTO ref.account_type (id, code, label) VALUES (1, 'a', 'b') ON CONFLICT (id) DO NOTHING;" ]

    Assert.Empty loaded.Data
    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "ON CONFLICT")

[<Fact>]
let ``an INSERT without a column list is refused`` () =
    // It depends on the table's current column ORDER, which the file cannot
    // see and a later ALTER can change underneath it.
    let loaded =
        load [ "t.sql", accountTypeSql; "d.sql", "INSERT INTO ref.account_type VALUES (1, 'a', 'b');" ]

    Assert.Empty loaded.Data
    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "explicit column list")

[<Fact>]
let ``a file mixing an object and rows is refused`` () =
    // The CREATE would carry the INSERTs with it and load the data as a side
    // effect of creating the table, bypassing the diff entirely.
    let loaded = load [ "both.sql", accountTypeSql + dataSql ]

    Assert.Empty loaded.Data
    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "both an object and rows")

[<Fact>]
let ``a file declaring rows with differing column lists is refused`` () =
    let loaded =
        load
            [ "t.sql", accountTypeSql
              "d.sql",
              "INSERT INTO ref.account_type (id, code, label) VALUES (1,'a','b');\n\
               INSERT INTO ref.account_type (id, code) VALUES (2,'c');" ]

    Assert.Empty loaded.Data
    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "differing column lists")

[<Fact>]
let ``rows declared for a table the project does not declare are a failure`` () =
    let loaded = load [ "d.sql", dataSql ]

    Assert.Empty loaded.Data
    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "does not declare as a table")


// ---- sequences -------------------------------------------------------------
//
// `CREATE SEQUENCE app.s;` declares no options at all, so every value has to be
// supplied here — exactly, or a sequence nobody touched reports a difference
// forever. These pin PostgreSQL's defaults; the live round-trip pins them to a
// real server.

let private sequenceNamed (loaded: DesiredState.Loaded) name =
    loaded.Snapshot.Objects
    |> List.tryPick (fun o ->
        match o with
        | SequenceObject s when QualifiedName.display s.Name = name -> Some s
        | _ -> None)

[<Fact>]
let ``a bare CREATE SEQUENCE takes PostgreSQL's ascending defaults`` () =
    let s = (sequenceNamed (load [ "s.sql", "CREATE SEQUENCE app.s;" ]) "app.s").Value

    Assert.Equal("bigint", s.DataType)
    Assert.Equal(1L, s.Increment)
    Assert.Equal(1L, s.MinValue)
    Assert.Equal(9223372036854775807L, s.MaxValue)
    Assert.Equal(1L, s.Start)
    Assert.Equal(1L, s.Cache)
    Assert.False s.Cycle

[<Fact>]
let ``AS integer narrows the default maximum, not just the type`` () =
    let s = (sequenceNamed (load [ "s.sql", "CREATE SEQUENCE app.s AS integer;" ]) "app.s").Value

    Assert.Equal("integer", s.DataType)
    Assert.Equal(2147483647L, s.MaxValue)

[<Fact>]
let ``a descending sequence defaults the other way round`` () =
    // MINVALUE becomes the type's floor, MAXVALUE is -1, and START follows
    // MAXVALUE rather than MINVALUE.
    let s = (sequenceNamed (load [ "s.sql", "CREATE SEQUENCE app.s INCREMENT BY -1;" ]) "app.s").Value

    Assert.Equal(-1L, s.Increment)
    Assert.Equal(System.Int64.MinValue, s.MinValue)
    Assert.Equal(-1L, s.MaxValue)
    Assert.Equal(-1L, s.Start)

[<Fact>]
let ``every explicit option is read`` () =
    let s =
        (sequenceNamed
            (load
                [ "s.sql",
                  "CREATE SEQUENCE app.s AS bigint START WITH 5 INCREMENT BY 2 \
                   MINVALUE 1 MAXVALUE 100 CACHE 3 CYCLE;" ])
            "app.s")
            .Value

    Assert.Equal(5L, s.Start)
    Assert.Equal(2L, s.Increment)
    Assert.Equal(100L, s.MaxValue)
    Assert.Equal(3L, s.Cache)
    Assert.True s.Cycle

[<Fact>]
let ``NO CYCLE is read as off, not as unset`` () =
    let s = (sequenceNamed (load [ "s.sql", "CREATE SEQUENCE app.s NO CYCLE;" ]) "app.s").Value
    Assert.False s.Cycle

[<Fact>]
let ``an IDENTITY column is NOT NULL and has no default`` () =
    // Both halves matter and they pull opposite ways. PostgreSQL rejects a NULL
    // into an identity column, so it is NOT NULL — but its value comes from
    // `attidentity`, not `pg_attrdef`, so the catalog reports NO default.
    // Claiming a default produced "default present in desired state and absent
    // in the database" on every run.
    let loaded = load [ "t.sql", "CREATE TABLE app.t (n int GENERATED BY DEFAULT AS IDENTITY);" ]
    let column = (tableNamed loaded "app.t").Value.Columns |> List.head

    Assert.False(column.Type.IsNullable, "an identity column is NOT NULL")
    Assert.False(column.HasDefault, "an identity column has no default expression")


// ---- grants ----------------------------------------------------------------

let private grantsIn (loaded: DesiredState.Loaded) = loaded.Grants |> List.sortBy (fun g -> g.Grantee)

[<Fact>]
let ``grants are NotRequested rather than complete-and-empty`` () =
    Assert.Equal(NotRequested, Completeness.stateOf "grants" (load [ "t.sql", accountTypeSql ]).Snapshot.Completeness)

[<Fact>]
let ``one statement declares a grant per object and grantee`` () =
    let loaded =
        load [ "g.sql", "GRANT SELECT ON ref.a, ref.b TO app_user, reporting;" ]

    Assert.Equal(4, List.length loaded.Grants)

[<Fact>]
let ``GRANT ALL expands to every table privilege`` () =
    // `ALL` arrives with NO privileges list at all — absence is the encoding —
    // so the set is written out in the adapter. Too few and Strata keeps
    // granting; too many and it keeps revoking.
    let loaded = load [ "g.sql", "GRANT ALL ON ref.a TO app_user;" ]

    Assert.Equal<string list>(
        [ "DELETE"; "INSERT"; "REFERENCES"; "SELECT"; "TRIGGER"; "TRUNCATE"; "UPDATE" ],
        (List.head loaded.Grants).Privileges)

[<Fact>]
let ``PUBLIC is read as PUBLIC, not as a role named PUBLIC`` () =
    let loaded = load [ "g.sql", "GRANT SELECT ON ref.a TO PUBLIC;" ]

    Assert.Equal("PUBLIC", (List.head loaded.Grants).Grantee)

[<Fact>]
let ``grants for the same object and grantee are merged, not treated as rivals`` () =
    // Written on two lines means the grantee should hold both, not that the
    // second line replaces the first.
    let loaded =
        load [ "g.sql", "GRANT SELECT ON ref.a TO app_user;\nGRANT INSERT ON ref.a TO app_user;" ]

    let only = Assert.Single loaded.Grants
    Assert.Equal<string list>([ "INSERT"; "SELECT" ], only.Privileges)

[<Fact>]
let ``REVOKE is refused: a file says what should be held`` () =
    let loaded = load [ "g.sql", "REVOKE SELECT ON ref.a FROM app_user;" ]

    Assert.Empty loaded.Grants
    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "REVOKE is not read as declared state")

[<Fact>]
let ``CURRENT_USER is refused: a file cannot fix who is connected`` () =
    let loaded = load [ "g.sql", "GRANT SELECT ON ref.a TO CURRENT_USER;" ]

    Assert.Empty loaded.Grants
    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "not declarable")

[<Fact>]
let ``a column-level GRANT is one grant per column, never one on the table`` () =
    // The escalation this prevents: `GRANT SELECT (id) ON t` parses with the
    // same RangeVar as a table-wide grant, and the columns live in a list that
    // was not being read — so the diff proposed the TABLE-wide grant and the
    // gate allowed it as additive. Strata would have handed out access to
    // columns the file withheld, and executed it.
    //
    // One target per column, because that is how the ACLs are stored: this
    // writes an entry into id's attacl and another into total's.
    let loaded = load [ "g.sql", "GRANT SELECT (id, total) ON ref.a TO app_user;" ]

    Assert.Empty loaded.Failures

    let targets = loaded.Grants |> List.map (fun g -> g.Target) |> List.sortBy GrantTarget.key

    Assert.Equal<GrantTarget list>(
        [ GrantTarget.RelationColumn(qn "ref" "a", Identifier.unquoted "id")
          GrantTarget.RelationColumn(qn "ref" "a", Identifier.unquoted "total") ],
        targets)

[<Fact>]
let ``one GRANT can mix column and table scope`` () =
    // `GRANT SELECT (id), INSERT ON t` is a column grant AND a table grant.
    // Flattening the privileges into one list loses which was which, and that
    // loss is exactly how the column grant became a table-wide one.
    let loaded = load [ "g.sql", "GRANT SELECT (id), INSERT ON ref.a TO app_user;" ]

    Assert.Empty loaded.Failures

    let privilegesOf target =
        loaded.Grants |> List.find (fun g -> g.Target = target) |> fun g -> g.Privileges

    Assert.Equal<string list>(
        [ "SELECT" ],
        privilegesOf (GrantTarget.RelationColumn(qn "ref" "a", Identifier.unquoted "id")))

    Assert.Equal<string list>([ "INSERT" ], privilegesOf (GrantTarget.Relation(qn "ref" "a")))

[<Fact>]
let ``GRANT ALL on a column is four privileges, not a table's seven`` () =
    // DELETE, TRUNCATE and TRIGGER act on rows or on the table, so there is
    // nothing for them to mean on one column. Using the table's list would
    // propose granting DELETE on a column forever. Verified live: `GRANT ALL
    // (note)` yields `arwx`.
    let loaded = load [ "g.sql", "GRANT ALL (note) ON ref.a TO app_user;" ]

    Assert.Empty loaded.Failures
    Assert.Equal<string list>(
        [ "INSERT"; "REFERENCES"; "SELECT"; "UPDATE" ],
        (List.head loaded.Grants).Privileges)

[<Fact>]
let ``a table-wide GRANT on the same object is still read`` () =
    // The guard must key on the column list, not on the statement shape: the
    // ordinary case parses identically apart from that list.
    let loaded = load [ "g.sql", "GRANT SELECT ON ref.a TO app_user;" ]

    Assert.Equal<string list>([ "SELECT" ], (List.head loaded.Grants).Privileges)

[<Fact>]
let ``a schema GRANT is read as a grant on the schema, not on a relation`` () =
    // The object arrives as a bare String rather than a RangeVar. A version that
    // read only the RangeVar saw no object at all and refused the statement; one
    // that read the name without its KIND would emit `GRANT USAGE ON ref`, which
    // grants on a TABLE called `ref` and is a different object entirely.
    let loaded = load [ "g.sql", "GRANT USAGE ON SCHEMA ref TO app_user;" ]

    Assert.Empty loaded.Failures
    Assert.Equal<GrantTarget>(GrantTarget.Schema(Identifier.unquoted "ref"), (List.head loaded.Grants).Target)

[<Fact>]
let ``GRANT ALL ON SCHEMA means USAGE and CREATE, not a table's privileges`` () =
    // `ALL` carries no privileges list, so the set has to be known per object
    // kind. A schema's ALL is two privileges and a table's is seven; using the
    // table list here would propose granting SELECT on a schema forever.
    let loaded = load [ "g.sql", "GRANT ALL ON SCHEMA ref TO app_user;" ]

    Assert.Empty loaded.Failures
    Assert.Equal<string list>([ "CREATE"; "USAGE" ], (List.head loaded.Grants).Privileges)

[<Fact>]
let ``a routine GRANT carries the argument types, rendered as the routine's are`` () =
    // The grant has to key on the same spelling the routine's own signature
    // produces, or it can never be matched to the routine it is on: `int` folds
    // to `integer`, and a modifier is dropped from an argument type.
    let loaded =
        load
            [ "g.sql",
              "GRANT EXECUTE ON FUNCTION ref.f(int) TO app_user;\n\
               GRANT EXECUTE ON ROUTINE ref.g(numeric(12,2)) TO app_user;\n\
               GRANT EXECUTE ON PROCEDURE ref.p() TO app_user;" ]

    Assert.Empty loaded.Failures

    let targets = loaded.Grants |> List.map (fun g -> g.Target) |> List.sortBy GrantTarget.key

    Assert.Equal<GrantTarget list>(
        [ GrantTarget.Routine(qn "ref" "f", [ "integer" ])
          GrantTarget.Routine(qn "ref" "g", [ "numeric" ])
          GrantTarget.Routine(qn "ref" "p", []) ],
        targets)

[<Fact>]
let ``a routine GRANT without argument types is refused, not read as zero-argument`` () =
    // `args_unspecified` is its own state. PostgreSQL resolves the name against
    // whatever is deployed, so the FILE does not say which overload it means —
    // and read without the flag it is indistinguishable from `f()`, so the
    // grant would land on a routine the file never named.
    let loaded = load [ "g.sql", "GRANT EXECUTE ON FUNCTION ref.f TO app_user;" ]

    Assert.Empty loaded.Grants
    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "argument types")

[<Fact>]
let ``a GRANT WITH GRANT OPTION is refused, never read as a plain grant`` () =
    // The same shape as the column-level refusal. The model holds which
    // privileges a grantee has, not the power to pass them on, so reading this
    // as plain would have the plan understate what the file asks for — and the
    // diff could never take the grant option away again.
    let loaded = load [ "g.sql", "GRANT SELECT ON ref.a TO app_user WITH GRANT OPTION;" ]

    Assert.Empty loaded.Grants
    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "GRANT OPTION")

[<Fact>]
let ``a GRANT on a kind Strata does not model names the kind`` () =
    // A database is not in any schema, so the managed-schema rule cannot reason
    // about it at all. "no resolvable object" reads like a malformed statement;
    // naming the kind says it is a real grant Strata does not model.
    let loaded = load [ "g.sql", "GRANT CONNECT ON DATABASE app TO app_user;" ]

    Assert.Empty loaded.Grants
    // Spelled the way SQL spells it. A message reading "GRANT on ObjectDatabase"
    // leaks the parser library's own C# enum name to someone trying to fix
    // their file.
    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "GRANT on DATABASE")

[<Fact>]
let ``overloaded routines are two objects, not one declared twice`` () =
    // Keyed on the name alone, `f(integer)` and `f(text)` looked like the same
    // object declared twice. That is a load failure, which marks desired state
    // incomplete, which suppresses EVERY drop in the project — so an ordinary
    // overload made removals impossible anywhere. Found by the awkward-forms
    // corpus on its first run.
    let loaded =
        load
            [ "f.sql",
              "CREATE FUNCTION s.f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;\n\
               CREATE FUNCTION s.f(a text) RETURNS text LANGUAGE sql AS $$ SELECT a $$;" ]

    Assert.Empty loaded.Failures
    Assert.Equal(Complete, Completeness.stateOf "relations" loaded.Snapshot.Completeness)

[<Fact>]
let ``the same routine really declared twice is still reported`` () =
    // The guard must key on identity, not merely stop checking.
    let loaded =
        load
            [ "f.sql",
              "CREATE FUNCTION s.f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;\n\
               CREATE FUNCTION s.f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;" ]

    Assert.Contains(loaded.Failures, fun f -> f.Reason.Contains "declared 2 times")

[<Fact>]
let ``a routine's argument types carry no modifier`` () =
    // A modifier is not part of a routine's identity. PostgreSQL rejects
    // `f(numeric)` as "already exists with same argument types" when
    // `f(numeric(12,2))` exists, and the catalog stores plain `numeric` — so
    // carrying (12,2) made the declared signature differ from the deployed one
    // and the routine was proposed as a create AND a removal, forever.
    let loaded =
        load
            [ "f.sql",
              "CREATE FUNCTION s.f(a numeric(12,2), b varchar(50)) RETURNS numeric LANGUAGE sql AS $$ SELECT a $$;" ]

    let routine =
        loaded.Snapshot.Objects
        |> List.pick (function RoutineObject r -> Some r | _ -> None)

    Assert.Equal<string list>([ "numeric"; "character varying" ], routine.ArgumentTypes)

[<Fact>]
let ``a column keeps the modifier a routine argument drops`` () =
    // The two rules pull opposite ways and both matter: a column's modifier is
    // part of what is declared, a routine argument's is not.
    let loaded = load [ "t.sql", "CREATE TABLE s.t (a numeric(12,2))" ]
    let column = (tableNamed loaded "s.t").Value.Columns |> List.head

    Assert.Equal("numeric(12,2)", QualifiedName.display column.Type.TypeName)
