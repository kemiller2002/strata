module Strata.Tests.CatalogIntrospectionTests

open System
open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Host.Postgres

/// Integration tests against a real PostgreSQL server.
///
/// SDE's verification method makes these REQUIRED rather than optional for this
/// tier: a catalog adapter is exactly the "present-but-wrong implementation"
/// case that compiles, passes architecture checks, and still returns nothing
/// useful. Only a test that queries a real database observes the semantic
/// consequence.
///
/// Skipped, not failed, when no server is configured — so the suite stays
/// runnable on a machine without PostgreSQL, and a skip is visible rather than
/// silently passing.

let private connectionString =
    match Environment.GetEnvironmentVariable "STRATA_TEST_PG" with
    | null | "" -> None
    | value -> Some value

type RequiresPostgresAttribute() =
    inherit FactAttribute()
    do
        base.Skip <-
            match connectionString with
            | Some _ -> null
            | None -> "STRATA_TEST_PG not set; live PostgreSQL integration test skipped"

let private snapshot = lazy (CatalogIntrospection.introspect connectionString.Value)

let private tableNamed schema name =
    snapshot.Value.Objects
    |> List.tryPick (fun o ->
        match o with
        | TableObject t when QualifiedName.display t.Name = sprintf "%s.%s" schema name -> Some t
        | _ -> None)

[<RequiresPostgres>]
let ``introspection reports the server version`` () =
    match snapshot.Value.ServerVersion with
    | Some fact ->
        let version = Strata.Semantic.Evidence.Fact.value fact
        Assert.True(version.Major > 0, "server major should parse")
    | None -> failwith "expected a server version"

[<RequiresPostgres>]
let ``tables are discovered with their columns in ordinal order`` () =
    match tableNamed "sales" "orders" with
    | Some t ->
        Assert.NotEmpty t.Columns
        let positions = t.Columns |> List.map (fun c -> c.Position)
        Assert.Equal<int list>(List.sort positions, positions)
        Assert.Contains(t.Columns, fun c -> c.Name.Text = "order_id")
    | None -> failwith "expected sales.orders"

[<RequiresPostgres>]
let ``a primary key is discovered`` () =
    match tableNamed "sales" "orders" with
    | Some t ->
        match t.PrimaryKey with
        | Some pk -> Assert.Equal<string list>([ "order_id" ], pk.Columns |> List.map (fun c -> c.Text))
        | None -> failwith "expected a primary key on sales.orders"
    | None -> failwith "expected sales.orders"

[<RequiresPostgres>]
let ``a foreign key is discovered with its referenced table and columns`` () =
    match tableNamed "sales" "orders" with
    | Some t ->
        let fk = Assert.Single t.ForeignKeys
        Assert.Equal("sales.customer", QualifiedName.display fk.ReferencedTable)
        Assert.Equal<string list>([ "customer_id" ], fk.Columns |> List.map (fun c -> c.Text))
        Assert.Equal<string list>([ "id" ], fk.ReferencedColumns |> List.map (fun c -> c.Text))
    | None -> failwith "expected sales.orders"

[<RequiresPostgres>]
let ``a partial index retains its predicate`` () =
    match tableNamed "sales" "orders" with
    | Some t ->
        let partial = t.Indexes |> List.tryFind (fun i -> i.Name.Text = "idx_orders_open")
        match partial with
        | Some idx -> Assert.True(Option.isSome idx.Predicate, "partial index should carry a predicate")
        | None -> failwith "expected idx_orders_open"
    | None -> failwith "expected sales.orders"

[<RequiresPostgres>]
let ``a check constraint is discovered`` () =
    match tableNamed "sales" "orders" with
    | Some t -> Assert.NotEmpty t.CheckConstraints
    | None -> failwith "expected sales.orders"

[<RequiresPostgres>]
let ``views are discovered separately from tables`` () =
    let view =
        snapshot.Value.Objects
        |> List.tryPick (fun o ->
            match o with
            | ViewObject v when QualifiedName.display v.Name = "sales.v_open_orders" -> Some v
            | _ -> None)

    match view with
    | Some v ->
        Assert.False v.IsMaterialized
        Assert.False(String.IsNullOrWhiteSpace v.Definition)
    | None -> failwith "expected sales.v_open_orders"

[<RequiresPostgres>]
let ``routines are discovered`` () =
    let routine =
        snapshot.Value.Objects
        |> List.tryPick (fun o ->
            match o with
            | RoutineObject r when QualifiedName.display r.Name = "sales.order_count" -> Some r
            | _ -> None)

    match routine with
    | Some r ->
        Assert.Equal(Function, r.Kind)
        Assert.Equal("sql", r.Language)
    | None -> failwith "expected sales.order_count"

[<RequiresPostgres>]
let ``system and information schema objects are excluded`` () =
    // RK-009: system objects must not arrive looking like user objects.
    let systemObjects =
        snapshot.Value.Objects
        |> List.filter (fun o ->
            match (SchemaObject.name o).Schema with
            | Some s -> s.Text = "pg_catalog" || s.Text = "information_schema"
            | None -> false)

    Assert.Empty systemObjects

[<RequiresPostgres>]
let ``discovered objects are never marked Managed without explicit ownership`` () =
    // §144.14: model management ownership explicitly BEFORE deletion is possible.
    // Introspection observes; it does not confer management.
    Assert.All(snapshot.Value.Objects, fun o -> Assert.NotEqual(Managed, SchemaObject.scope o))

[<RequiresPostgres>]
let ``completeness names categories Strata did not read`` () =
    // §129 / §6: absence must be stated, not implied.
    let completeness = snapshot.Value.Completeness

    Assert.Equal(NotRequested, Completeness.stateOf "rls_policies" completeness)
    Assert.Equal(NotRequested, Completeness.stateOf "triggers" completeness)
    Assert.Equal(Complete, Completeness.stateOf "relations" completeness)

[<RequiresPostgres>]
let ``a catalog-visible but unreadable object is reported, not silently dropped`` () =
    // RK-004, CORRECTED by EV-STRATA-2026-E7A9.
    //
    // The notebook (§135.1) assumes incomplete permissions make objects
    // invisible. PostgreSQL's catalog is world-readable, so that is false for
    // METADATA: the `hidden` schema has no USAGE granted to the test role, yet
    // hidden.secret still appears in pg_class.
    //
    // Strata must not drop it — dropping would make it look absent, and
    // fabricated absence is exactly NG-006. It appears in the snapshot.
    let hidden =
        snapshot.Value.Objects
        |> List.filter (fun o ->
            match (SchemaObject.name o).Schema with
            | Some s -> s.Text = "hidden"
            | None -> false)

    Assert.NotEmpty hidden

[<RequiresPostgres>]
let ``an unreadable object makes relation_access Partial, never Complete`` () =
    // The consequence of the test above. Visibility without access must be
    // visible IN THE OUTPUT, or Strata claims analysis it could not perform.
    match Completeness.stateOf "relation_access" snapshot.Value.Completeness with
    | Partial reason ->
        Assert.Contains("hidden.secret", reason)
        Assert.Contains("not readable", reason)
    | other -> failwithf "expected Partial for relation_access, got %A" other

[<RequiresPostgres>]
let ``an unreadable object blocks the fully-complete claim`` () =
    // And therefore blocks any absence claim built on this snapshot.
    Assert.False(Completeness.isFullyComplete snapshot.Value.Completeness)

[<RequiresPostgres>]
let ``a snapshot alone does not support an absence claim`` () =
    // The consequence of the test above, stated as the rule it implies:
    // a complete-looking catalog says nothing about who reads an object.
    let scope =
        { Scope.nothingAnalyzed with
            LiveDatabaseInspected = true
            SchemaCompleteness = snapshot.Value.Completeness }

    Assert.False(Scope.supportsAbsenceClaim scope)

[<RequiresPostgres>]
let ``search_path is read and $user is not guessed at`` () =
    match CatalogIntrospection.readSearchPath connectionString.Value with
    | Ok path ->
        Assert.DoesNotContain(path, fun (i: Identifier) -> i.Text.StartsWith "$")
        Assert.Contains(path, fun (i: Identifier) -> i.Text = "public")
    | Error e -> failwithf "expected search_path, got %s" e

[<RequiresPostgres>]
let ``introspection is deterministic across runs`` () =
    // NFR-001: object order must not depend on catalog return order.
    let a = CatalogIntrospection.introspect connectionString.Value
    let b = CatalogIntrospection.introspect connectionString.Value

    let names s = s.Objects |> List.map (SchemaObject.name >> QualifiedName.display)

    Assert.Equal<string list>(names a, names b)
