module Strata.Tests.SchemaDiffTests

open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.ProposedChange
open Strata.Application.SchemaDiff

/// Tests for PR-022 — comparing desired state against actual state.
///
/// The additive cases are easy and are covered for completeness. The tests that
/// earn their place are the ones proving Strata REFUSES to propose a drop when
/// the evidence does not support one. NG-006 names "a schema diff that assumes
/// absence means delete" as an explicit non-goal, and §1437 says it outright.

let private id' = Identifier.unquoted
let private qn schema name = QualifiedName.qualified (id' schema) (id' name)

let private col position name nullable =
    { Name = id' name
      Type = { TypeName = QualifiedName.unqualified (id' "text"); IsNullable = nullable }
      Position = position
      HasDefault = false
      DefaultExpression = None
      IsGenerated = false
      IsIdentity = false }

let private tbl scope schema name columns =
    TableObject
        { Name = qn schema name
          Columns = columns |> List.mapi (fun i (n, nullable) -> col (i + 1) n nullable)
          PrimaryKey = None
          UniqueConstraints = []
          CheckConstraints = []
          ForeignKeys = []
          Indexes = []
          Triggers = []
          Scope = scope }

let private snapshot completeness objects =
    { Objects = objects
      ServerVersion = None
      Completeness = Completeness.ofList [ "relations", completeness ] }

let private complete = snapshot Complete
let private partial' = snapshot (Partial "a file did not parse")

let private managed = [ "sales" ]

/// `None` for the database's schema list means "could not read it", which is
/// deliberately not an empty list: these tests build snapshots directly and
/// declare objects in no schema, so nothing is proposed either way.
///
/// `SchemaDiff.run` also takes the verbatim text that declared each object, so
/// a CREATE can execute the author's own DDL rather than a reconstruction.
/// These tests build snapshots directly and have no files, so they pass none
/// and exercise the reconstruction path deliberately.
let private run allowDrops managedSchemas desired actual =
    Strata.Application.SchemaDiff.run allowDrops managedSchemas [] [] None [] [] [] [] [] [] desired actual

/// Existing guard tests pass allowDrops=true deliberately: a test that left
/// drops globally disabled would pass even if the guard it names were deleted.

let private orders = [ "id", false; "total", true ]

// ---- additive ------------------------------------------------------------

[<Fact>]
let ``a table in desired state and not in the database is created`` () =
    let result = run true managed (complete [ tbl Managed "sales" "orders" orders ]) (complete [])

    Assert.Contains(result.Changes, fun c -> c = CreateTable(qn "sales" "orders"))

[<Fact>]
let ``a column added in desired state is added`` () =
    let result =
        run true managed
            (complete [ tbl Managed "sales" "orders" (orders @ [ "note", true ]) ])
            (complete [ tbl Observed "sales" "orders" orders ])

    Assert.Contains(result.Changes, fun c -> c = AddColumn(qn "sales" "orders", id' "note"))

[<Fact>]
let ``identical snapshots produce no changes`` () =
    let result =
        run true managed
            (complete [ tbl Managed "sales" "orders" orders ])
            (complete [ tbl Observed "sales" "orders" orders ])

    Assert.Empty result.Changes

// ---- the destructive direction, where every guard lives -------------------

[<Fact>]
let ``a managed table absent from a COMPLETE desired state is dropped`` () =
    let result = run true managed (complete []) (complete [ tbl Observed "sales" "legacy" orders ])

    Assert.Contains(result.Changes, fun c -> c = DropTable(qn "sales" "legacy"))

[<Fact>]
let ``a table outside the managed schemas is NEVER dropped`` () =
    // §1437 verbatim: "Do not delete unmanaged objects simply because they are
    // not in desired state."
    let result = run true managed (complete []) (complete [ tbl Observed "other" "things" orders ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = OutsideManagedSchemas)

[<Fact>]
let ``nothing is dropped when desired state did not load completely`` () =
    // THE test. A file that failed to parse removes its object from desired
    // state. Without this guard that parse failure silently becomes a DROP of
    // a table nobody asked to remove.
    let result = run true managed (partial' []) (complete [ tbl Observed "sales" "orders" orders ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = DesiredStateIncomplete)
    Assert.False result.DesiredStateComplete

[<Fact>]
let ``an extension-owned table is never dropped`` () =
    // RK-008. It is absent from desired state because the extension owns it,
    // not because anyone wants it gone.
    let result = run true managed (complete []) (complete [ tbl ExtensionOwned "sales" "pg_stat_thing" orders ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = ExtensionOwnedObject)

[<Fact>]
let ``a column absent from an incomplete desired state is not dropped`` () =
    let result =
        run true managed
            (partial' [ tbl Managed "sales" "orders" [ "id", false ] ])
            (complete [ tbl Observed "sales" "orders" orders ])

    Assert.DoesNotContain(result.Changes, fun c -> c = DropColumn(qn "sales" "orders", id' "total"))
    Assert.Contains(result.Suppressed, fun s -> s.Reason = DesiredStateIncomplete)

[<Fact>]
let ``a column absent from a COMPLETE desired state is dropped`` () =
    // The control for the case above: the guard must not become a blanket
    // refusal, or the tool cannot deploy anything destructive at all.
    let result =
        run true managed
            (complete [ tbl Managed "sales" "orders" [ "id", false ] ])
            (complete [ tbl Observed "sales" "orders" orders ])

    Assert.Contains(result.Changes, fun c -> c = DropColumn(qn "sales" "orders", id' "total"))

// ---- differences Strata sees but does not model ---------------------------

[<Fact>]
let ``a nullability change is reported as unclassified rather than invented`` () =
    // The Change vocabulary has no nullability case. Reporting it as something
    // else would have the gate judge it by the wrong rules.
    let result =
        run true managed
            (complete [ tbl Managed "sales" "orders" [ "id", false; "total", false ] ])
            (complete [ tbl Observed "sales" "orders" orders ])

    Assert.Contains(result.Changes, fun c ->
        match c with
        | UnclassifiedChange detail -> detail.Contains "nullability"
        | _ -> false)

[<Fact>]
let ``indexes are disclosed as not compared rather than ignored`` () =
    // CREATE INDEX is a separate statement, so the declared side always reports
    // none. Comparing them would propose dropping every index in the database;
    // ignoring them silently is the other half of the same mistake.
    let indexed =
        TableObject
            { Name = qn "sales" "orders"
              Columns = [ col 1 "id" false ]
              PrimaryKey = None
              UniqueConstraints = []
              CheckConstraints = []
              ForeignKeys = []
              Indexes = [ { Name = id' "idx_total"; Columns = [ id' "id" ]; IsUnique = false; Predicate = None } ]
              Triggers = []
              Scope = Observed }

    let result = run true managed (complete [ tbl Managed "sales" "orders" [ "id", false ] ]) (complete [ indexed ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Detail.Contains "indexes were NOT compared")

[<Fact>]
let ``an index backing a constraint is not reported as uncompared`` () =
    // PostgreSQL names the implicit index after its constraint, and the
    // constraint IS compared. Listing it would report an uncompared difference
    // for every table with a key — noise that trains people to ignore the
    // disclosures that matter.
    let withPk =
        TableObject
            { Name = qn "sales" "orders"
              Columns = [ col 1 "id" false ]
              PrimaryKey = Some { ConstraintName = Some(id' "orders_pkey"); Columns = [ id' "id" ] }
              UniqueConstraints = []
              CheckConstraints = []
              ForeignKeys = []
              Indexes = [ { Name = id' "orders_pkey"; Columns = [ id' "id" ]; IsUnique = true; Predicate = None } ]
              Triggers = []
              Scope = Observed }

    let desired =
        TableObject
            { Name = qn "sales" "orders"
              Columns = [ col 1 "id" false ]
              PrimaryKey = Some { ConstraintName = Some(id' "orders_pkey"); Columns = [ id' "id" ] }
              UniqueConstraints = []
              CheckConstraints = []
              ForeignKeys = []
              Indexes = []
              Triggers = []
              Scope = Managed }

    let result = run true managed (complete [ desired ]) (complete [ withPk ])

    Assert.Empty result.Changes
    Assert.DoesNotContain(result.Suppressed, fun s -> s.Detail.Contains "indexes were NOT compared")

[<Fact>]
let ``suppressions name the object so a clean change list is never mistaken for no difference`` () =
    let result = run true managed (complete []) (complete [ tbl Observed "other" "things" orders ])

    Assert.Empty result.Changes
    Assert.Equal("other.things", QualifiedName.display (List.head result.Suppressed).Object)

// ---- removals are opt-in --------------------------------------------------

[<Fact>]
let ``a drop is NOT proposed unless removals are enabled`` () =
    // Managed schemas are inferred from the directory tree, so creating
    // schema/sales/ would otherwise be an implicit claim to own every object in
    // `sales` and remove anything undeclared. SSDT, sqldef, migra and
    // pg-schema-diff all default the same way.
    let result = run false managed (complete []) (complete [ tbl Observed "sales" "legacy" orders ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = DropsNotEnabled)

[<Fact>]
let ``a column drop is NOT proposed unless removals are enabled`` () =
    let result =
        run false managed
            (complete [ tbl Managed "sales" "orders" [ "id", false ] ])
            (complete [ tbl Observed "sales" "orders" orders ])

    Assert.DoesNotContain(result.Changes, fun c -> c = DropColumn(qn "sales" "orders", id' "total"))
    Assert.Contains(result.Suppressed, fun s -> s.Reason = DropsNotEnabled)

[<Fact>]
let ``additive changes still happen with removals disabled`` () =
    // The flag withholds removals, not the whole diff. A tool that refused to
    // do anything without --allow-drops would just be trained around.
    let result =
        run false managed
            (complete [ tbl Managed "sales" "orders" (orders @ [ "note", true ]) ])
            (complete [ tbl Observed "sales" "orders" orders ])

    Assert.Contains(result.Changes, fun c -> c = AddColumn(qn "sales" "orders", id' "note"))

[<Fact>]
let ``a withheld removal is still reported`` () =
    // The difference is real and the user needs to see it. What is withheld is
    // the proposal, not the fact.
    let result = run false managed (complete []) (complete [ tbl Observed "sales" "legacy" orders ])

    Assert.Contains(result.Suppressed, fun s ->
        QualifiedName.display s.Object = "sales.legacy" && s.Detail.Contains "--allow-drops")

// ---- DDL emission ---------------------------------------------------------
//
// A Change alone cannot be executed: AddColumn carries a name, not a type. The
// diff knows the desired state, so the diff emits. Every `Sql = None` below is
// a deliberate refusal, and refusing is the point — applying the rest of a plan
// whose middle statement cannot be written leaves the database matching neither
// side.

let private sqlFor (result: DiffResult) (predicate: Change -> bool) =
    result.Statements |> List.find (fun s -> predicate s.Change) |> fun s -> s.Sql

[<Fact>]
let ``an added column is emitted with its type and nullability`` () =
    let result =
        run true managed
            (complete [ tbl Managed "sales" "orders" (orders @ [ "note", false ]) ])
            (complete [ tbl Observed "sales" "orders" orders ])

    let sql = sqlFor result (function AddColumn _ -> true | _ -> false)
    Assert.Equal(Some "ALTER TABLE \"sales\".\"orders\" ADD COLUMN \"note\" text NOT NULL", sql)

[<Fact>]
let ``a created table carries its primary key`` () =
    let table =
        TableObject
            { Name = qn "sales" "t"
              Columns = [ col 1 "id" false ]
              PrimaryKey = Some { ConstraintName = Some(id' "t_pkey"); Columns = [ id' "id" ] }
              UniqueConstraints = []
              CheckConstraints = []
              ForeignKeys = []
              Indexes = []
              Triggers = []
              Scope = Managed }

    let result = run true managed (complete [ table ]) (complete [])
    let sql = (sqlFor result (function CreateTable _ -> true | _ -> false)).Value

    Assert.Contains("CREATE TABLE \"sales\".\"t\"", sql)
    Assert.Contains("CONSTRAINT \"t_pkey\" PRIMARY KEY (\"id\")", sql)

[<Fact>]
let ``identifiers are always quoted`` () =
    // A catalog name is already case-folded, so quoting changes nothing; a name
    // that was quoted in the file keeps the case it needs. Emitting unquoted
    // would silently fold a mixed-case identifier into a different object.
    let result = run true managed (complete []) (complete [ tbl Observed "sales" "MixedCase" orders ])
    let sql = (sqlFor result (function DropTable _ -> true | _ -> false)).Value

    Assert.Equal("DROP TABLE \"sales\".\"MixedCase\"", sql)

[<Fact>]
let ``a table with a check constraint is NOT emitted`` () =
    // The model carries that a check EXISTS but not its expression, because the
    // catalog reports those already normalised. Creating the table without its
    // checks would produce an object differing from what was declared while
    // reporting success.
    let table =
        TableObject
            { Name = qn "sales" "t"
              Columns = [ col 1 "id" false ]
              PrimaryKey = None
              UniqueConstraints = []
              CheckConstraints = [ { ConstraintName = Some(id' "ck"); Expression = "" } ]
              ForeignKeys = []
              Indexes = []
              Triggers = []
              Scope = Managed }

    let result = run true managed (complete [ table ]) (complete [])

    Assert.Equal(None, sqlFor result (function CreateTable _ -> true | _ -> false))

[<Fact>]
let ``a column with a default is NOT emitted`` () =
    // Adding it without the default would populate existing rows with NULL
    // instead of the declared value — a silently different outcome.
    let withDefault =
        TableObject
            { Name = qn "sales" "orders"
              Columns =
                [ col 1 "id" false
                  { col 2 "note" true with HasDefault = true } ]
              PrimaryKey = None
              UniqueConstraints = []
              CheckConstraints = []
              ForeignKeys = []
              Indexes = []
              Triggers = []
              Scope = Managed }

    let result =
        run true managed (complete [ withDefault ]) (complete [ tbl Observed "sales" "orders" [ "id", false ] ])

    Assert.Equal(None, sqlFor result (function AddColumn _ -> true | _ -> false))

[<Fact>]
let ``an unclassified change emits nothing`` () =
    let result =
        run true managed
            (complete [ tbl Managed "sales" "orders" [ "id", false; "total", false ] ])
            (complete [ tbl Observed "sales" "orders" orders ])

    Assert.Equal(None, sqlFor result (function UnclassifiedChange _ -> true | _ -> false))

[<Fact>]
let ``creates are ordered before drops`` () =
    // A statement must never run against an object a later one removes, and
    // drops must come after everything depending on them.
    let result =
        run true managed
            (complete [ tbl Managed "sales" "new" orders ])
            (complete [ tbl Observed "sales" "old" orders ])

    let kinds = result.Changes |> List.map Change.tag
    let indexOf tag = kinds |> List.findIndex (fun k -> k = tag)

    Assert.True(indexOf "create-table" < indexOf "drop-table")

// ---- constraints and defaults ---------------------------------------------
//
// None of this was compared until EV-STRATA-2026-D3A8 found `plan` reporting
// "already matches desired state" for a table whose foreign key, check
// constraint and column default all differed. Every one of those facts was in
// the model on both sides; nothing looked at them.

let private tableWith schema name columns pk uniques checks fks =
    TableObject
        { Name = qn schema name
          Columns = columns |> List.mapi (fun i (n, nullable) -> col (i + 1) n nullable)
          PrimaryKey = pk
          UniqueConstraints = uniques
          CheckConstraints = checks
          ForeignKeys = fks
          Indexes = []
          Triggers = []
          Scope = Managed }

let private fk name cols target targetCols =
    { ConstraintName = Some(id' name)
      Columns = cols |> List.map id'
      ReferencedTable = qn "sales" target
      ReferencedColumns = targetCols |> List.map id' }

let private plain = tableWith "sales" "orders" orders None [] [] []

[<Fact>]
let ``a foreign key in the database and not in desired state is reported`` () =
    let withFk = tableWith "sales" "orders" orders None [] [] [ fk "fk_o_c" [ "id" ] "customers" [ "id" ] ]
    let result = run true managed (complete [ plain ]) (complete [ withFk ])

    Assert.Contains(result.Changes, fun c ->
        c = DropConstraint(qn "sales" "orders", id' "fk_o_c", ConstraintKind.ForeignKey))

[<Fact>]
let ``a foreign key in desired state and not in the database is added`` () =
    let withFk = tableWith "sales" "orders" orders None [] [] [ fk "fk_o_c" [ "id" ] "customers" [ "id" ] ]
    let result = run true managed (complete [ withFk ]) (complete [ plain ])

    Assert.Contains(result.Changes, fun c -> c = AddConstraint(qn "sales" "orders", Some(id' "fk_o_c"), ConstraintKind.ForeignKey, [ id' "id" ]))

[<Fact>]
let ``a check constraint present on one side only is reported`` () =
    let withCheck =
        tableWith "sales" "orders" orders None [] [ { ConstraintName = Some(id' "ck_x"); Expression = "" } ] []

    let result = run true managed (complete [ plain ]) (complete [ withCheck ])

    // Named as the constraint it is, not as an unclassified string. The gate
    // treats both as destructive, but only one can be written as DDL.
    Assert.Contains(result.Changes, fun c ->
        c = DropConstraint(qn "sales" "orders", id' "ck_x", ConstraintKind.Check))

[<Fact>]
let ``a primary key covering different columns is reported`` () =
    let pkOn cols =
        Some { PrimaryKey.ConstraintName = Some(id' "pk"); Columns = cols |> List.map id' }

    let result =
        run true managed
            (complete [ tableWith "sales" "orders" orders (pkOn [ "id" ]) [] [] [] ])
            (complete [ tableWith "sales" "orders" orders (pkOn [ "id"; "total" ]) [] [] [] ])

    Assert.Contains(result.Changes, fun c ->
        match c with
        | UnclassifiedChange d -> d.Contains "primary key covers"
        | _ -> false)

[<Fact>]
let ``a foreign key redefined under the same name is reported`` () =
    // Matching by name alone would call these equal, which is how a repointed
    // foreign key would slip through.
    let result =
        run true managed
            (complete [ tableWith "sales" "orders" orders None [] [] [ fk "fk" [ "id" ] "customers" [ "id" ] ] ])
            (complete [ tableWith "sales" "orders" orders None [] [] [ fk "fk" [ "total" ] "customers" [ "id" ] ] ])

    // PostgreSQL cannot alter what a constraint covers, so the only route is
    // to drop it and add it back — and the drop must run first, because the
    // name is still taken.
    Assert.Contains(
        result.Changes,
        fun c -> c = DropConstraint(qn "sales" "orders", id' "fk", ConstraintKind.ForeignKey))

    Assert.Contains(
        result.Changes,
        fun c -> c = AddConstraint(qn "sales" "orders", Some(id' "fk"), ConstraintKind.ForeignKey, [ id' "id" ]))

    let tags = result.Changes |> List.map Change.tag
    Assert.True(
        List.findIndex ((=) "drop-constraint") tags < List.findIndex ((=) "add-constraint") tags,
        "the old constraint must go before the new one takes its name")

[<Fact>]
let ``a redefined constraint proposes NEITHER half when drops are off`` () =
    // Emitting the add alone would fail on a name that is still taken, so a
    // redefinition Strata cannot drop is a redefinition it cannot make. It is
    // reported rather than half-proposed.
    let result =
        run false managed
            (complete [ tableWith "sales" "orders" orders None [] [] [ fk "fk" [ "id" ] "customers" [ "id" ] ] ])
            (complete [ tableWith "sales" "orders" orders None [] [] [ fk "fk" [ "total" ] "customers" [ "id" ] ] ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = DropsNotEnabled && s.Detail.Contains "--allow-drops")

[<Fact>]
let ``a default appearing or disappearing is reported`` () =
    let withDefault =
        TableObject
            { Name = qn "sales" "orders"
              Columns = [ col 1 "id" false; { col 2 "total" true with HasDefault = true } ]
              PrimaryKey = None
              UniqueConstraints = []
              CheckConstraints = []
              ForeignKeys = []
              Indexes = []
              Triggers = []
              Scope = Managed }

    let result = run true managed (complete [ withDefault ]) (complete [ plain ])

    Assert.Contains(result.Changes, fun c ->
        match c with
        | UnclassifiedChange d -> d.Contains "default present in desired state"
        | _ -> false)

[<Fact>]
let ``expressions Strata cannot read are reported as not-compared`` () =
    // THE honest half. Strata holds a check on both sides and cannot compare
    // the expressions — the declared side carries no text and the catalog
    // reports its own normalised rendering. Saying nothing would render "I did
    // not look" identically to "they are equal".
    let withCheck =
        tableWith "sales" "orders" orders None [] [ { ConstraintName = Some(id' "ck"); Expression = "x > 0" } ] []

    let result = run true managed (complete [ withCheck ]) (complete [ withCheck ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s ->
        s.Reason = NotCompared && s.Detail.Contains "check constraint")

[<Fact>]
let ``an identical table still proposes nothing`` () =
    // The control. Constraint comparison must not manufacture differences, or
    // every run reports churn and the signal is worthless.
    let full =
        tableWith "sales" "orders" orders
            (Some { PrimaryKey.ConstraintName = Some(id' "pk"); Columns = [ id' "id" ] })
            [ { UniqueConstraint.ConstraintName = Some(id' "uq"); Columns = [ id' "total" ] } ]
            []
            [ fk "fk" [ "id" ] "customers" [ "id" ] ]

    let result = run true managed (complete [ full ]) (complete [ full ])

    Assert.Empty result.Changes

// ---- views and routines ---------------------------------------------------

let private view name materialized =
    ViewObject
        { Name = qn "sales" name
          Columns = []
          IsMaterialized = materialized
          Definition = ""
          Scope = Managed }

let private routineWithBody name args body =
    RoutineObject
        { Name = qn "sales" name
          Kind = Function
          ArgumentTypes = args
          ReturnType = None
          Language = "sql"
          Body = body
          Scope = Managed }

let private routine name args = routineWithBody name args None

[<Fact>]
let ``a declared view that does not exist is created`` () =
    let result = run true managed (complete [ view "v_open" false ]) (complete [])

    Assert.Contains(result.Changes, fun c -> c = CreateView(qn "sales" "v_open"))

[<Fact>]
let ``a declared routine that does not exist is created`` () =
    let result = run true managed (complete [ routine "f" [ "bigint" ] ]) (complete [])

    Assert.Contains(result.Changes, fun c -> c = CreateRoutine(qn "sales" "f"))

[<Fact>]
let ``creating a view or routine is additive, not approval-gated`` () =
    // These were UnclassifiedChange first, which the gate judges as potentially
    // destructive — so a project containing any view could never be applied
    // without manual approval. A creation is additive; the vocabulary now says so.
    Assert.False(Change.isPotentiallyDestructive (CreateView(qn "sales" "v")))
    Assert.False(Change.isPotentiallyDestructive (CreateRoutine(qn "sales" "f")))

[<Fact>]
let ``overloads are separate objects`` () =
    // f(bigint) and f(text) are different routines. Matching on name alone
    // would read one as a redefinition of the other and propose dropping it.
    let result =
        run true managed
            (complete [ routine "f" [ "bigint" ]; routine "f" [ "text" ] ])
            (complete [ routine "f" [ "bigint" ] ])

    Assert.Single result.Changes |> ignore
    Assert.Contains(result.Changes, fun c -> c = CreateRoutine(qn "sales" "f"))

[<Fact>]
let ``a view on both sides is not proposed, and its definition is disclosed as uncompared`` () =
    let result = run true managed (complete [ view "v" false ]) (complete [ view "v" false ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s ->
        s.Reason = NotCompared && s.Detail.Contains "definition was NOT compared")

[<Fact>]
let ``a view in the database and not in desired state is guarded like a table`` () =
    let result = run false managed (complete []) (complete [ view "v" false ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = DropsNotEnabled)

// ---- view redefinition ----------------------------------------------------
//
// A view's declared text and its catalog form never match: PostgreSQL stores a
// rewritten tree and pg_get_viewdef deparses it schema-qualified, reformatted
// and with ::text casts. Comparison is only possible once the DECLARED side has
// been through the same renderer, which ShadowNormalisation does by executing
// it in a rolled-back transaction. These tests take that rendering as given.

let private viewDefined name definition =
    ViewObject
        { Name = qn "sales" name
          Columns = []
          IsMaterialized = false
          Definition = definition
          Scope = Managed }

let private runWithViews normalised desired actual =
    Strata.Application.SchemaDiff.run true managed [] [] None [] [] [] normalised [] [] desired actual

[<Fact>]
let ``a view whose normalised definition differs is redefined`` () =
    let result =
        runWithViews
            [ "sales.v", " SELECT id FROM sales.orders WHERE total > 0;" ]
            (complete [ viewDefined "v" "" ])
            (complete [ viewDefined "v" " SELECT id FROM sales.orders;" ])

    Assert.Contains(result.Changes, fun c -> c = ReplaceView(qn "sales" "v"))

[<Fact>]
let ``a view whose normalised definition matches produces nothing AND no disclosure`` () =
    // Once the comparison actually happened, silence is correct. Continuing to
    // disclose it would train people to ignore the disclosures that matter.
    let definition = " SELECT id FROM sales.orders;"

    let result =
        runWithViews
            [ "sales.v", definition ]
            (complete [ viewDefined "v" "" ])
            (complete [ viewDefined "v" definition ])

    Assert.Empty result.Changes
    Assert.DoesNotContain(result.Suppressed, fun s -> s.Reason = NotCompared)

[<Fact>]
let ``a view that could not be normalised is still disclosed, never assumed equal`` () =
    // No CREATE privilege, a read-only target, or DDL the server rejected. The
    // fallback must be "I could not check", not "it matches".
    let result =
        runWithViews
            []
            (complete [ viewDefined "v" "" ])
            (complete [ viewDefined "v" " SELECT id FROM sales.orders;" ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s ->
        s.Reason = NotCompared && s.Detail.Contains "definition was NOT compared")

[<Fact>]
let ``redefining a view is destructive, so the gate weighs its dependents`` () =
    // CREATE OR REPLACE VIEW fails outright if the column list changes. The
    // dangerous case is the one that SUCCEEDS: a narrowed filter changes what
    // every reader gets and nothing errors.
    Assert.True(Change.isPotentiallyDestructive (ReplaceView(qn "sales" "v")))

[<Fact>]
let ``a materialized view is never proposed for replacement`` () =
    // CREATE OR REPLACE does not exist for a materialized view; it needs DROP
    // and CREATE, which is a different and destructive plan.
    let matview name definition =
        ViewObject
            { Name = qn "sales" name
              Columns = []
              IsMaterialized = true
              Definition = definition
              Scope = Managed }

    let result =
        runWithViews
            [ "sales.m", " SELECT id FROM sales.orders WHERE total > 0;" ]
            (complete [ matview "m" "" ])
            (complete [ matview "m" " SELECT id FROM sales.orders;" ])

    Assert.DoesNotContain(result.Changes, fun c -> c = ReplaceView(qn "sales" "m"))
    Assert.Contains(result.Suppressed, fun s -> s.Reason = NotCompared)

// ---- routine bodies -------------------------------------------------------
//
// Unlike a view, a routine needs no shadow: PostgreSQL stores a classic
// `AS $$...$$` body VERBATIM in prosrc, so the declared text and the deployed
// text compare directly. Verified against a live server before relying on it.

[<Fact>]
let ``a routine whose body differs is redefined`` () =
    let result =
        run true managed
            (complete [ routineWithBody "f" [ "bigint" ] (Some " SELECT 2; ") ])
            (complete [ routineWithBody "f" [ "bigint" ] (Some " SELECT 1; ") ])

    Assert.Contains(result.Changes, fun c -> c = ReplaceRoutine(qn "sales" "f"))

[<Fact>]
let ``a routine body differing only in surrounding whitespace is not a change`` () =
    // The file ends with a newline before the closing $$; prosrc keeps it.
    // Treating that as a difference would report churn on every run.
    let result =
        run true managed
            (complete [ routineWithBody "f" [ "bigint" ] (Some "\n  SELECT 1;\n") ])
            (complete [ routineWithBody "f" [ "bigint" ] (Some " SELECT 1; ") ])

    Assert.Empty result.Changes
    Assert.DoesNotContain(result.Suppressed, fun s -> s.Reason = NotCompared)

[<Fact>]
let ``a routine whose body the server holds as a tree is disclosed, not assumed equal`` () =
    // A SQL-standard BEGIN ATOMIC body is parsed and stored as a tree, so
    // prosrc is empty — which means "no text", not "empty body". Verified: a
    // BEGIN ATOMIC function really does come back with an empty prosrc.
    let result =
        run true managed
            (complete [ routineWithBody "f" [ "bigint" ] (Some " SELECT 1; ") ])
            (complete [ routineWithBody "f" [ "bigint" ] None ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = NotCompared)

[<Fact>]
let ``redefining a routine is destructive, so the gate weighs its callers`` () =
    // Every caller gets the new behaviour immediately, and a body change that
    // compiles reports nothing.
    Assert.True(Change.isPotentiallyDestructive (ReplaceRoutine(qn "sales" "f")))

// ---- default and check expressions ----------------------------------------
//
// `DEFAULT 'open'` is stored and reported as `'open'::text`, and
// `CHECK (balance >= 0)` as `CHECK ((balance >= (0)::numeric))`. Neither
// matches what the author wrote, which is why both were disclosed rather than
// compared. ShadowNormalisation round-trips the declared DDL through the server
// so both sides carry the same rendering; these tests take that as given.

let private tableWithDefault name column deployedDefault =
    TableObject
        { Name = qn "sales" name
          Columns =
            [ { Name = id' column
                Type = { TypeName = QualifiedName.unqualified (id' "text"); IsNullable = true }
                Position = 1
                HasDefault = true
                DefaultExpression = deployedDefault
                IsGenerated = false
                IsIdentity = false } ]
          PrimaryKey = None
          UniqueConstraints = []
          CheckConstraints = []
          ForeignKeys = []
          Indexes = []
          Triggers = []
          Scope = Managed }

let private runWithTables normalisedTables desired actual =
    Strata.Application.SchemaDiff.run true managed [] [] None [] [] [] [] normalisedTables [] desired actual

let private normalisedTable name defaults checks : Strata.Application.SchemaDiff.NormalisedTable =
    { Table = name; Defaults = defaults; Checks = checks }

[<Fact>]
let ``a default whose rendered expression differs is reported`` () =
    let result =
        runWithTables
            [ normalisedTable "sales.t" [ "status", "'pending'::text" ] [] ]
            (complete [ tableWithDefault "t" "status" None ])
            (complete [ tableWithDefault "t" "status" (Some "'active'::text") ])

    Assert.Contains(result.Changes, fun c ->
        match c with
        | UnclassifiedChange d -> d.Contains "'pending'::text" && d.Contains "'active'::text"
        | _ -> false)

[<Fact>]
let ``a default whose rendered expression matches produces nothing AND no disclosure`` () =
    let result =
        runWithTables
            [ normalisedTable "sales.t" [ "status", "'active'::text" ] [] ]
            (complete [ tableWithDefault "t" "status" None ])
            (complete [ tableWithDefault "t" "status" (Some "'active'::text") ])

    Assert.Empty result.Changes
    Assert.DoesNotContain(result.Suppressed, fun s -> s.Reason = NotCompared)

[<Fact>]
let ``a sequence default is excluded from comparison and disclosed instead`` () =
    // A serial column's default names its SEQUENCE, and the shadow table's
    // sequence is named after the shadow table — so the renderings can never
    // match however identical the declarations are. Comparing would report a
    // permanent difference on every serial column in the schema.
    let result =
        runWithTables
            [ normalisedTable "sales.t" [ "status", "nextval('_strata_shadow_x.t_y_seq'::regclass)" ] [] ]
            (complete [ tableWithDefault "t" "status" None ])
            (complete [ tableWithDefault "t" "status" (Some "nextval('sales.t_status_seq'::regclass)") ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = NotCompared)

[<Fact>]
let ``a check whose rendered definition differs is reported`` () =
    let withCheck expression =
        TableObject
            { Name = qn "sales" "t"
              Columns = [ col 1 "id" false ]
              PrimaryKey = None
              UniqueConstraints = []
              CheckConstraints = [ { ConstraintName = Some(id' "ck"); Expression = expression } ]
              ForeignKeys = []
              Indexes = []
              Triggers = []
              Scope = Managed }

    let result =
        runWithTables
            [ normalisedTable "sales.t" [] [ "ck", "CHECK ((balance > (0)::numeric))" ] ]
            (complete [ withCheck "" ])
            (complete [ withCheck "CHECK ((balance >= (0)::numeric))" ])

    Assert.Contains(result.Changes, fun c ->
        match c with
        | UnclassifiedChange d -> d.Contains "check constraint 'ck'" && d.Contains "balance > "
        | _ -> false)

[<Fact>]
let ``a table that could not be normalised keeps its disclosure`` () =
    // No CREATE privilege, a read-only target, or DDL the server rejected. The
    // fallback must stay "I could not check", never "it matches".
    let result =
        runWithTables
            []
            (complete [ tableWithDefault "t" "status" None ])
            (complete [ tableWithDefault "t" "status" (Some "'active'::text") ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = NotCompared)

// ---- renames --------------------------------------------------------------

let private runWithRenames renames desired actual =
    Strata.Application.SchemaDiff.run true managed [] [] None [] [] [] [] [] renames desired actual

let private declaredRename object' renamedFrom columns : Strata.Application.SchemaDiff.DeclaredRename =
    { Object = object'; RenamedFrom = renamedFrom; Columns = columns }

[<Fact>]
let ``a declared table rename replaces the create and the drop`` () =
    // The whole point: without the annotation this is a CreateTable plus a
    // DropTable, which destroys the data. Neither must survive into the plan.
    let result =
        runWithRenames
            [ declaredRename (qn "sales" "orders") (Some(qn "sales" "legacy")) [] ]
            (complete [ tbl Managed "sales" "orders" orders ])
            (complete [ tbl Observed "sales" "legacy" orders ])

    Assert.Contains(result.Changes, fun c -> c = RenameTable(qn "sales" "legacy", qn "sales" "orders"))
    Assert.DoesNotContain(result.Changes, fun c -> c = CreateTable(qn "sales" "orders"))
    Assert.DoesNotContain(result.Changes, fun c -> c = DropTable(qn "sales" "legacy"))

[<Fact>]
let ``a declared column rename replaces the drop and the add`` () =
    let result =
        runWithRenames
            [ declaredRename (qn "sales" "orders") None [ "amount", "total" ] ]
            (complete [ tbl Managed "sales" "orders" [ "id", false; "total", true ] ])
            (complete [ tbl Observed "sales" "orders" [ "id", false; "amount", true ] ])

    Assert.Contains(result.Changes, fun c ->
        c = RenameColumn(qn "sales" "orders", id' "amount", id' "total"))
    Assert.DoesNotContain(result.Changes, fun c -> c = DropColumn(qn "sales" "orders", id' "amount"))
    Assert.DoesNotContain(result.Changes, fun c -> c = AddColumn(qn "sales" "orders", id' "total"))

[<Fact>]
let ``a spent annotation proposes nothing`` () =
    // The rename already happened: the old name is gone and the new one is
    // there. Re-proposing it every run would be churn, and the ALTER would fail.
    let result =
        runWithRenames
            [ declaredRename (qn "sales" "orders") (Some(qn "sales" "legacy")) [ "amount", "total" ] ]
            (complete [ tbl Managed "sales" "orders" [ "id", false; "total", true ] ])
            (complete [ tbl Observed "sales" "orders" [ "id", false; "total", true ] ])

    Assert.Empty result.Changes

[<Fact>]
let ``a rename is destructive, so the gate weighs readers of the OLD name`` () =
    // The data survives; every reader of the old name breaks at once.
    Assert.True(Change.isPotentiallyDestructive (RenameTable(qn "sales" "a", qn "sales" "b")))
    Assert.True(Change.isPotentiallyDestructive (RenameColumn(qn "sales" "t", id' "a", id' "b")))

[<Fact>]
let ``a column rename names the table by its CURRENT name`` () =
    // Two reasons pointing the same way: the corpus records dependencies under
    // the name that exists today, so the gate can only find readers under it;
    // and column renames run BEFORE the table rename, so the table still
    // answers to it. Naming the desired table made the gate report "no
    // dependency found" for a column a query was plainly reading.
    let result =
        runWithRenames
            [ declaredRename (qn "sales" "orders") (Some(qn "sales" "legacy")) [ "amount", "total" ] ]
            (complete [ tbl Managed "sales" "orders" [ "id", false; "total", true ] ])
            (complete [ tbl Observed "sales" "legacy" [ "id", false; "amount", true ] ])

    Assert.Contains(result.Changes, fun c ->
        c = RenameColumn(qn "sales" "legacy", id' "amount", id' "total"))

[<Fact>]
let ``column renames are ordered before the table rename`` () =
    let result =
        runWithRenames
            [ declaredRename (qn "sales" "orders") (Some(qn "sales" "legacy")) [ "amount", "total" ] ]
            (complete [ tbl Managed "sales" "orders" [ "id", false; "total", true ] ])
            (complete [ tbl Observed "sales" "legacy" [ "id", false; "amount", true ] ])

    let tags = result.Changes |> List.map Change.tag
    Assert.True(
        List.findIndex ((=) "rename-column") tags < List.findIndex ((=) "rename-table") tags,
        "a column rename must run while the table still has its old name")


// ---- indexes as desired state ---------------------------------------------

let private tableWithIndexes name columns indexes =
    TableObject
        { Name = qn "sales" name
          Columns = columns |> List.mapi (fun i (n, nullable) -> col (i + 1) n nullable)
          PrimaryKey = None
          UniqueConstraints = []
          CheckConstraints = []
          ForeignKeys = []
          Indexes = indexes
          Triggers = []
          Scope = Managed }

let private index name columns unique =
    { Name = id' name; Columns = columns |> List.map id'; IsUnique = unique; Predicate = None }

/// A snapshot whose desired side DECLARES indexes, which is how a project takes
/// ownership of them.
let private declaringIndexes objects =
    { Objects = objects
      ServerVersion = None
      Completeness = Completeness.ofList [ "relations", Complete; "indexes", Complete ] }

[<Fact>]
let ``a declared index that does not exist is created`` () =
    let result =
        run true managed
            (declaringIndexes [ tableWithIndexes "orders" orders [ index "idx_total" [ "total" ] false ] ])
            (complete [ tbl Observed "sales" "orders" orders ])

    Assert.Contains(result.Changes, fun c -> c = CreateIndex(qn "sales" "orders", id' "idx_total"))

[<Fact>]
let ``an index the project does not declare is dropped once it declares any`` () =
    let result =
        run true managed
            (declaringIndexes [ tableWithIndexes "orders" orders [ index "idx_keep" [ "id" ] false ] ])
            (complete
                [ TableObject
                    { Name = qn "sales" "orders"
                      Columns = orders |> List.mapi (fun i (n, nullable) -> col (i + 1) n nullable)
                      PrimaryKey = None
                      UniqueConstraints = []
                      CheckConstraints = []
                      ForeignKeys = []
                      Indexes = [ index "idx_keep" [ "id" ] false; index "idx_gone" [ "total" ] false ]
                      Triggers = []
                      Scope = Observed } ])

    Assert.Contains(result.Changes, fun c -> c = DropIndex(qn "sales" "orders", id' "idx_gone"))
    Assert.DoesNotContain(result.Changes, fun c -> c = DropIndex(qn "sales" "orders", id' "idx_keep"))

[<Fact>]
let ``a project declaring NO index drops none and discloses instead`` () =
    // Declaring nothing is not the same as declaring emptiness. A project that
    // says nothing about indexes must not have every existing one proposed for
    // removal — that is NG-006 applied to a second object type.
    let result =
        run true managed
            (complete [ tbl Managed "sales" "orders" orders ])
            (complete
                [ TableObject
                    { Name = qn "sales" "orders"
                      Columns = orders |> List.mapi (fun i (n, nullable) -> col (i + 1) n nullable)
                      PrimaryKey = None
                      UniqueConstraints = []
                      CheckConstraints = []
                      ForeignKeys = []
                      Indexes = [ index "idx_total" [ "total" ] false ]
                      Triggers = []
                      Scope = Observed } ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Detail.Contains "declares none")

[<Fact>]
let ``an index redefined under the same name is reported`` () =
    let result =
        run true managed
            (declaringIndexes [ tableWithIndexes "orders" orders [ index "idx" [ "total" ] true ] ])
            (complete
                [ TableObject
                    { Name = qn "sales" "orders"
                      Columns = orders |> List.mapi (fun i (n, nullable) -> col (i + 1) n nullable)
                      PrimaryKey = None
                      UniqueConstraints = []
                      CheckConstraints = []
                      ForeignKeys = []
                      Indexes = [ index "idx" [ "id" ] false ]
                      Triggers = []
                      Scope = Observed } ])

    Assert.Contains(result.Changes, fun c ->
        match c with
        | UnclassifiedChange d -> d.Contains "index 'idx' covers"
        | _ -> false)

[<Fact>]
let ``creating an index is additive and dropping one needs approval`` () =
    // Dropping an index breaks nothing and reports nothing: queries keep
    // working and get slower, which no error surfaces.
    Assert.False(Change.isPotentiallyDestructive (CreateIndex(qn "sales" "t", id' "i")))
    Assert.True(Change.isPotentiallyDestructive (DropIndex(qn "sales" "t", id' "i")))


// ---- triggers as desired state ---------------------------------------------

let private tableWithTriggers scope name columns triggers =
    TableObject
        { Name = qn "sales" name
          Columns = columns |> List.mapi (fun i (n, nullable) -> col (i + 1) n nullable)
          PrimaryKey = None
          UniqueConstraints = []
          CheckConstraints = []
          ForeignKeys = []
          Indexes = []
          Triggers = triggers
          Scope = scope }

let private trigger name timing events level =
    { Name = id' name
      Timing = timing
      Events = events
      Level = level
      UpdateColumns = []
      Function = qn "sales" "touch"
      Arguments = []
      HasCondition = false }

/// A snapshot whose desired side DECLARES triggers, which is how a project
/// takes ownership of them.
let private declaringTriggers objects =
    { Objects = objects
      ServerVersion = None
      Completeness = Completeness.ofList [ "relations", Complete; "triggers", Complete ] }

[<Fact>]
let ``a declared trigger that does not exist is created`` () =
    let result =
        run true managed
            (declaringTriggers
                [ tableWithTriggers
                    Managed "orders" orders
                    [ trigger "touch_orders" TriggerTiming.Before [ "update" ] TriggerLevel.Row ] ])
            (complete [ tbl Observed "sales" "orders" orders ])

    Assert.Contains(result.Changes, fun c -> c = CreateTrigger(qn "sales" "orders", id' "touch_orders"))

[<Fact>]
let ``a trigger identical on both sides produces no change`` () =
    // The convergence case. If any field the two sides spell differently were
    // compared, every idempotent re-run would propose a replace forever.
    let same = trigger "touch_orders" TriggerTiming.After [ "insert"; "update" ] TriggerLevel.Row

    let result =
        run true managed
            (declaringTriggers [ tableWithTriggers Managed "orders" orders [ same ] ])
            (complete [ tableWithTriggers Observed "orders" orders [ same ] ])

    Assert.Empty result.Changes

[<Fact>]
let ``a trigger whose timing differs is a replace, not a create and a drop`` () =
    let result =
        run true managed
            (declaringTriggers
                [ tableWithTriggers
                    Managed "orders" orders
                    [ trigger "touch_orders" TriggerTiming.Before [ "update" ] TriggerLevel.Row ] ])
            (complete
                [ tableWithTriggers
                    Observed "orders" orders
                    [ trigger "touch_orders" TriggerTiming.After [ "update" ] TriggerLevel.Row ] ])

    Assert.Contains(result.Changes, fun c -> c = ReplaceTrigger(qn "sales" "orders", id' "touch_orders"))
    Assert.DoesNotContain(result.Changes, fun c -> c = DropTrigger(qn "sales" "orders", id' "touch_orders"))

[<Fact>]
let ``a trigger that fires on more events than declared is a replace`` () =
    let result =
        run true managed
            (declaringTriggers
                [ tableWithTriggers
                    Managed "orders" orders
                    [ trigger "touch_orders" TriggerTiming.After [ "update" ] TriggerLevel.Row ] ])
            (complete
                [ tableWithTriggers
                    Observed "orders" orders
                    [ trigger "touch_orders" TriggerTiming.After [ "insert"; "update" ] TriggerLevel.Row ] ])

    Assert.Contains(result.Changes, fun c -> c = ReplaceTrigger(qn "sales" "orders", id' "touch_orders"))

[<Fact>]
let ``a trigger the project does not declare is dropped once it declares any`` () =
    let result =
        run true managed
            (declaringTriggers
                [ tableWithTriggers
                    Managed "orders" orders
                    [ trigger "keep" TriggerTiming.After [ "insert" ] TriggerLevel.Row ] ])
            (complete
                [ tableWithTriggers
                    Observed "orders" orders
                    [ trigger "keep" TriggerTiming.After [ "insert" ] TriggerLevel.Row
                      trigger "gone" TriggerTiming.After [ "delete" ] TriggerLevel.Row ] ])

    Assert.Contains(result.Changes, fun c -> c = DropTrigger(qn "sales" "orders", id' "gone"))
    Assert.DoesNotContain(result.Changes, fun c -> c = DropTrigger(qn "sales" "orders", id' "keep"))

[<Fact>]
let ``a project declaring NO trigger drops none and discloses instead`` () =
    // NG-006 applied to a third object type. Without this, shipping triggers
    // would have proposed dropping every trigger in every existing database.
    let result =
        run true managed
            (complete [ tbl Managed "sales" "orders" orders ])
            (complete
                [ tableWithTriggers
                    Observed "orders" orders
                    [ trigger "audit" TriggerTiming.After [ "insert" ] TriggerLevel.Row ] ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Detail.Contains "trigger(s) exist in the database")

[<Fact>]
let ``a trigger drop is suppressed rather than proposed when drops are off`` () =
    let result =
        run false managed
            (declaringTriggers [ tableWithTriggers Managed "orders" orders [] ])
            (complete
                [ tableWithTriggers
                    Observed "orders" orders
                    [ trigger "audit" TriggerTiming.After [ "insert" ] TriggerLevel.Row ] ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = DropsNotEnabled)

[<Fact>]
let ``a WHEN clause present on both sides is disclosed as not compared`` () =
    // The condition text is unavailable on BOTH sides: a file's WHEN clause is
    // never deparsed, and pg_get_expr refuses tgqual outright. Two triggers
    // that agree on everything comparable may still differ, and saying so is
    // the difference between a bounded result and a false clean.
    let conditional =
        { trigger "touch_orders" TriggerTiming.Before [ "update" ] TriggerLevel.Row with HasCondition = true }

    let result =
        run true managed
            (declaringTriggers [ tableWithTriggers Managed "orders" orders [ conditional ] ])
            (complete [ tableWithTriggers Observed "orders" orders [ conditional ] ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = NotCompared && s.Detail.Contains "WHEN clause")

[<Fact>]
let ``a new table's declared triggers are created in the same plan`` () =
    // Trigger comparison runs only for tables on both sides, so without an
    // explicit pass for new tables a fresh table's triggers would need a
    // second apply — and the first run would report a change it had not made.
    let result =
        run true managed
            (declaringTriggers
                [ tableWithTriggers
                    Managed "orders" orders
                    [ trigger "touch_orders" TriggerTiming.Before [ "insert" ] TriggerLevel.Row ] ])
            (complete [])

    Assert.Contains(result.Changes, fun c -> c = CreateTrigger(qn "sales" "orders", id' "touch_orders"))

[<Fact>]
let ``a trigger is created after the routine it executes`` () =
    // PostgreSQL rejects a CREATE TRIGGER whose function does not exist yet,
    // and the whole plan runs in one transaction, so a wrong order does not
    // half-apply: it fails outright.
    let routine =
        RoutineObject
            { Name = qn "sales" "touch"
              Kind = Function
              ArgumentTypes = []
              ReturnType = Some "trigger"
              Language = "plpgsql"
              Body = None
              Scope = Managed }

    let result =
        run true managed
            (declaringTriggers
                [ routine
                  tableWithTriggers
                      Managed "orders" orders
                      [ trigger "touch_orders" TriggerTiming.Before [ "insert" ] TriggerLevel.Row ] ])
            (complete [])

    let tags = result.Changes |> List.map Change.tag
    Assert.True(
        List.findIndex ((=) "create-routine") tags < List.findIndex ((=) "create-trigger") tags,
        "the function must exist before the trigger that names it")


// ---- creation order follows the foreign keys -------------------------------

let private tableWithFks name columns fks =
    TableObject
        { Name = qn "sales" name
          Columns = columns |> List.mapi (fun i (n, nullable) -> col (i + 1) n nullable)
          PrimaryKey = None
          UniqueConstraints = []
          CheckConstraints = []
          ForeignKeys =
            fks
            |> List.map (fun (constraintName, column, target) ->
                { ConstraintName = Some(id' constraintName)
                  Columns = [ id' column ]
                  ReferencedTable = qn "sales" target
                  ReferencedColumns = [ id' "id" ] })
          Indexes = []
          Triggers = []
          Scope = Managed }

[<Fact>]
let ``a referenced table is created before the table referencing it`` () =
    // Found by a live round-trip, not by a unit test: every project here had
    // one table or two unrelated ones, so the rank-only order was never
    // exercised. A project declaring a child that sorts first could not be
    // applied AT ALL — 42P01, and the whole transaction rolls back.
    let result =
        run true managed
            (complete
                [ tableWithFks "audit" [ "id", false; "order_id", false ] [ "fk_audit", "order_id", "orders" ]
                  tableWithFks "orders" orders [] ])
            (complete [])

    let order =
        result.Changes
        |> List.choose (function CreateTable n -> Some(QualifiedName.display n) | _ -> None)

    Assert.True(
        List.findIndex ((=) "sales.orders") order < List.findIndex ((=) "sales.audit") order,
        sprintf "orders must be created before audit, got %A" order)

[<Fact>]
let ``a chain of three tables is created parent first`` () =
    let result =
        run true managed
            (complete
                [ tableWithFks "c" [ "id", false; "b_id", false ] [ "fk_c", "b_id", "b" ]
                  tableWithFks "b" [ "id", false; "a_id", false ] [ "fk_b", "a_id", "a" ]
                  tableWithFks "a" [ "id", false ] [] ])
            (complete [])

    let order =
        result.Changes
        |> List.choose (function CreateTable n -> Some(QualifiedName.display n) | _ -> None)

    Assert.Equal<string list>([ "sales.a"; "sales.b"; "sales.c" ], order)

[<Fact>]
let ``a self-referencing table imposes no order and is still created`` () =
    // PostgreSQL accepts a foreign key to the table being defined, so this is
    // not a cycle and must not be withheld as one.
    let result =
        run true managed
            (complete
                [ tableWithFks "tree" [ "id", false; "parent_id", true ] [ "fk_parent", "parent_id", "tree" ] ])
            (complete [])

    Assert.Contains(result.Changes, fun c -> c = CreateTable(qn "sales" "tree"))

[<Fact>]
let ``a reference to a table that already exists imposes no order`` () =
    let result =
        run true managed
            (complete
                [ tableWithFks "audit" [ "id", false; "order_id", false ] [ "fk_audit", "order_id", "orders" ]
                  tbl Managed "sales" "orders" orders ])
            (complete [ tbl Observed "sales" "orders" orders ])

    Assert.Contains(result.Changes, fun c -> c = CreateTable(qn "sales" "audit"))

[<Fact>]
let ``two tables referencing each other are withheld with a reason`` () =
    // No order of plain CREATE TABLE statements satisfies a cycle. Emitting one
    // anyway would produce a plan Strata knows cannot execute.
    let result =
        run true managed
            (complete
                [ tableWithFks "a" [ "id", false; "b_id", true ] [ "fk_a", "b_id", "b" ]
                  tableWithFks "b" [ "id", false; "a_id", true ] [ "fk_b", "a_id", "a" ] ])
            (complete [])

    Assert.DoesNotContain(result.Changes, fun c -> c = CreateTable(qn "sales" "a"))
    Assert.DoesNotContain(result.Changes, fun c -> c = CreateTable(qn "sales" "b"))
    Assert.Contains(result.Suppressed, fun s -> s.Detail.Contains "form a cycle")


// ---- a constraint the declaring file did not name --------------------------
//
// CREATE TABLE t (order_id bigint REFERENCES orders (id)) names nothing. The
// server assigns t_order_id_fkey at CREATE time. A previous version fabricated
// the name "foreign_key" on the declared side, so every re-plan proposed adding
// one constraint and dropping the other, forever — and no project written the
// ordinary way could ever converge. Found by a live round-trip.

let private unnamedFk cols target targetCols =
    { ConstraintName = None
      Columns = cols |> List.map id'
      ReferencedTable = qn "sales" target
      ReferencedColumns = targetCols |> List.map id' }

[<Fact>]
let ``an unnamed declared foreign key matches a deployed one by what it does`` () =
    let declared =
        tableWith "sales" "orders" orders None [] [] [ unnamedFk [ "id" ] "customers" [ "id" ] ]

    let deployed =
        tableWith "sales" "orders" orders None [] [] [ fk "orders_id_fkey" [ "id" ] "customers" [ "id" ] ]

    let result = run true managed (complete [ declared ]) (complete [ deployed ])

    Assert.Empty result.Changes

[<Fact>]
let ``an unnamed declared foreign key pointing somewhere else is still a difference`` () =
    // The point of matching on the definition is that the definition is what
    // is being matched. Two constraints that do different things must not be
    // treated as one because neither was named.
    let declared =
        tableWith "sales" "orders" orders None [] [] [ unnamedFk [ "id" ] "suppliers" [ "id" ] ]

    let deployed =
        tableWith "sales" "orders" orders None [] [] [ fk "orders_id_fkey" [ "id" ] "customers" [ "id" ] ]

    let result = run true managed (complete [ declared ]) (complete [ deployed ])

    Assert.Contains(
        result.Changes,
        fun c -> c = AddConstraint(qn "sales" "orders", None, ConstraintKind.ForeignKey, [ id' "id" ]))

    Assert.Contains(
        result.Changes,
        fun c -> c = DropConstraint(qn "sales" "orders", id' "orders_id_fkey", ConstraintKind.ForeignKey))

[<Fact>]
let ``a named declared constraint is still matched by its name`` () =
    // A project that named a constraint asked for that name. It must not be
    // satisfied by a differently-named one that happens to share its shape.
    let declared =
        tableWith "sales" "orders" orders None [] [] [ fk "fk_wanted" [ "id" ] "customers" [ "id" ] ]

    let deployed =
        tableWith "sales" "orders" orders None [] [] [ fk "fk_other" [ "id" ] "customers" [ "id" ] ]

    let result = run true managed (complete [ declared ]) (complete [ deployed ])

    Assert.Contains(result.Changes, fun c -> c = AddConstraint(qn "sales" "orders", Some(id' "fk_wanted"), ConstraintKind.ForeignKey, [ id' "id" ]))

[<Fact>]
let ``a name match wins over a definition match`` () =
    // Two deployed constraints, one sharing the declared name and one sharing
    // only the declared shape. The named declaration must claim the named one,
    // leaving the other as the difference.
    let declared =
        tableWith "sales" "orders" orders None [] []
            [ fk "fk_named" [ "id" ] "customers" [ "id" ]
              unnamedFk [ "id" ] "customers" [ "id" ] ]

    let deployed =
        tableWith "sales" "orders" orders None [] []
            [ fk "fk_named" [ "id" ] "customers" [ "id" ]
              fk "orders_id_fkey" [ "id" ] "customers" [ "id" ] ]

    let result = run true managed (complete [ declared ]) (complete [ deployed ])

    Assert.Empty result.Changes

[<Fact>]
let ``two identical unnamed declarations claim two deployed constraints`` () =
    // Matched one for one, not counted twice: a single deployed constraint
    // cannot satisfy two declarations that both ask for it.
    let declared =
        tableWith "sales" "orders" orders None [] []
            [ unnamedFk [ "id" ] "customers" [ "id" ]
              unnamedFk [ "id" ] "customers" [ "id" ] ]

    let deployed =
        tableWith "sales" "orders" orders None [] [] [ fk "orders_id_fkey" [ "id" ] "customers" [ "id" ] ]

    let result = run true managed (complete [ declared ]) (complete [ deployed ])

    Assert.Contains(result.Changes, fun c -> c = AddConstraint(qn "sales" "orders", None, ConstraintKind.ForeignKey, [ id' "id" ]))

[<Fact>]
let ``an unnamed check with no shadow rendering is not compared, in either direction`` () =
    // An unnamed check has no name AND no expression from the parse tree, so
    // without a rendering nothing can attribute a deployed check to it.
    // Reporting the deployed one as absent from desired state would be a
    // difference Strata invented.
    let declared =
        tableWith "sales" "orders" orders None []
            [ { ConstraintName = None; Expression = "" } ] []

    let deployed =
        tableWith "sales" "orders" orders None []
            [ { ConstraintName = Some(id' "orders_total_check"); Expression = "(total >= 0)" } ] []

    let result = run true managed (complete [ declared ]) (complete [ deployed ])

    Assert.DoesNotContain(result.Changes, fun c -> Change.tag c = "unclassified")
    Assert.Contains(result.Suppressed, fun s -> s.Detail.Contains "unnamed and the declared DDL could not be normalised")


// ---- reference data --------------------------------------------------------
//
// A lookup table's rows are part of the schema: an account cannot name an
// account type that does not exist. Rows are matched on the primary key, and
// compared on the server's rendering of the declared columns — never on a
// value Strata interpreted itself.

let private row key rendered literals : ResolvedRow =
    { Key = key; Rendered = rendered; Literals = literals }

let private resolved declaredRows deployedRows : ResolvedData =
    { Table = "sales.account_type"
      Columns = [ "id"; "label" ]
      KeyColumns = [ "id" ]
      Declared = declaredRows
      Deployed = deployedRows }

let private runWithData data failures desired actual =
    Strata.Application.SchemaDiff.run true managed [] [] None [] data failures [] [] [] desired actual

let private accountType = tbl Managed "sales" "account_type" [ "id", false; "label", false ]

[<Fact>]
let ``a declared row the table does not hold is inserted`` () =
    let result =
        runWithData
            [ resolved [ row "(1)" "(1,Checking)" [ "'1'"; "'Checking'" ] ] [] ]
            []
            (complete [ accountType ])
            (complete [ accountType ])

    Assert.Contains(result.Changes, fun c -> c = InsertRow(qn "sales" "account_type", "(1)"))

[<Fact>]
let ``a row that matches on the server's rendering produces no change`` () =
    // The convergence case, and the one that decides whether this feature is
    // usable at all. `1000.50` in a file and `1000.50` in a numeric(6,2) column
    // are the same value; if anything here compared the raw texts, every
    // idempotent re-run would propose rewriting every row forever.
    let result =
        runWithData
            [ resolved
                [ row "(1)" "(1,Checking)" [ "'1'"; "'Checking'" ] ]
                [ row "(1)" "(1,Checking)" [] ] ]
            []
            (complete [ accountType ])
            (complete [ accountType ])

    Assert.Empty result.Changes

[<Fact>]
let ``a row whose declared value differs is updated, not re-inserted`` () =
    let result =
        runWithData
            [ resolved
                [ row "(1)" "(1,Current)" [ "'1'"; "'Current'" ] ]
                [ row "(1)" "(1,Checking)" [] ] ]
            []
            (complete [ accountType ])
            (complete [ accountType ])

    Assert.Contains(result.Changes, fun c -> c = UpdateRow(qn "sales" "account_type", "(1)"))
    Assert.DoesNotContain(result.Changes, fun c -> c = InsertRow(qn "sales" "account_type", "(1)"))

[<Fact>]
let ``a row the project does not declare is reported and never deleted`` () =
    // NG-006 with higher stakes than an object. A reference row that user data
    // points at cannot be removed without failing the foreign key or cascading
    // into that data — so this holds even with drops enabled, which `run` has
    // set to true here.
    let result =
        runWithData
            [ resolved
                [ row "(1)" "(1,Checking)" [ "'1'"; "'Checking'" ] ]
                [ row "(1)" "(1,Checking)" []
                  row "(99)" "(99,Legacy)" [] ] ]
            []
            (complete [ accountType ])
            (complete [ accountType ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Detail.Contains "NOT proposed for deletion")

[<Fact>]
let ``a table whose rows could not be read is not a table with no rows`` () =
    // The distinction that stops the worst outcome available here: treating an
    // unreadable table as empty would propose inserting every declared row
    // into a table that already holds them.
    let result =
        runWithData
            []
            [ ({ Table = "sales.account_type"
                 Reason = "has no primary key" }: DataFailure) ]
            (complete [ accountType ])
            (complete [ accountType ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = NotCompared && s.Detail.Contains "no primary key")

[<Fact>]
let ``reference rows are written after the table that holds them`` () =
    let result =
        runWithData
            [ resolved [ row "(1)" "(1,Checking)" [ "'1'"; "'Checking'" ] ] [] ]
            []
            (complete [ accountType ])
            (complete [])

    let tags = result.Changes |> List.map Change.tag
    Assert.True(
        List.findIndex ((=) "create-table") tags < List.findIndex ((=) "insert-row") tags,
        sprintf "the table must exist before its rows, got %A" tags)

[<Fact>]
let ``an update never assigns the key it matches on`` () =
    let result =
        runWithData
            [ resolved
                [ row "(1)" "(1,Current)" [ "'1'"; "'Current'" ] ]
                [ row "(1)" "(1,Checking)" [] ] ]
            []
            (complete [ accountType ])
            (complete [ accountType ])

    let sql =
        result.Statements
        |> List.tryPick (fun s -> if Change.tag s.Change = "update-row" then s.Sql else None)

    match sql with
    | None -> failwith "expected DDL for the update"
    | Some text ->
        Assert.Contains("WHERE \"id\" = '1'", text)
        Assert.DoesNotContain("SET \"id\"", text)


// ---- schemas ---------------------------------------------------------------
//
// A project's first apply against an empty database has nothing to put its
// tables IN until the schema exists. Every round-trip during development
// worked around this with a hand-run CREATE SCHEMA, which is how it stayed
// invisible: the tool infers managed schemas from directory names, so it knew
// every schema's name and created none of them.

let private runWithSchemas existing declaredIn desired actual =
    Strata.Application.SchemaDiff.run
        true managed [] [] existing declaredIn [] [] [] [] [] desired actual

[<Fact>]
let ``a declared schema the database does not have is created`` () =
    let result = runWithSchemas (Some [ "public" ]) [ "sales" ] (complete []) (complete [])

    Assert.Contains(result.Changes, fun c -> c = CreateSchema(id' "sales"))

[<Fact>]
let ``a schema that already exists is not created again`` () =
    let result = runWithSchemas (Some [ "public"; "sales" ]) [ "sales" ] (complete []) (complete [])

    Assert.Empty result.Changes

[<Fact>]
let ``schema matching ignores case, as PostgreSQL folds unquoted names`` () =
    let result = runWithSchemas (Some [ "SALES" ]) [ "sales" ] (complete []) (complete [])

    Assert.Empty result.Changes

[<Fact>]
let ``a schema is created before the tables that live in it`` () =
    // Rank 0, alone. Everything else fails outright against a namespace that
    // does not exist yet, and the whole plan is one transaction.
    let result =
        runWithSchemas
            (Some [ "public" ])
            [ "sales" ]
            (complete [ tbl Managed "sales" "orders" orders ])
            (complete [])

    let tags = result.Changes |> List.map Change.tag
    Assert.True(
        List.findIndex ((=) "create-schema") tags < List.findIndex ((=) "create-table") tags,
        sprintf "the schema must exist before its tables, got %A" tags)

[<Fact>]
let ``an unreadable schema list proposes nothing and says so`` () =
    // None is not an empty list. A caller that cannot see the schemas must not
    // conclude one is missing and propose creating it.
    let result = runWithSchemas None [ "sales" ] (complete []) (complete [])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = NotCompared && s.Detail.Contains "could not be read")

[<Fact>]
let ``a schema the project declares nothing in is never dropped`` () =
    // There is no DropSchema case to find. A schema is a container: dropping
    // one takes everything inside it, including objects the project never
    // declared and so never claimed. `run` has drops ENABLED here.
    let result = runWithSchemas (Some [ "public"; "sales"; "legacy" ]) [ "sales" ] (complete []) (complete [])

    Assert.Empty result.Changes
    Assert.DoesNotContain(result.Changes, fun c -> (Change.tag c).Contains "schema" && (Change.tag c).Contains "drop")
