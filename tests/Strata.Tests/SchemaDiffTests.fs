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
          Scope = scope }

let private snapshot completeness objects =
    { Objects = objects
      ServerVersion = None
      Completeness = Completeness.ofList [ "relations", completeness ] }

let private complete = snapshot Complete
let private partial' = snapshot (Partial "a file did not parse")

let private managed = [ "sales" ]

/// `SchemaDiff.run` also takes the verbatim text that declared each object, so
/// a CREATE can execute the author's own DDL rather than a reconstruction.
/// These tests build snapshots directly and have no files, so they pass none
/// and exercise the reconstruction path deliberately.
let private run allowDrops managedSchemas desired actual =
    Strata.Application.SchemaDiff.run allowDrops managedSchemas [] [] [] [] desired actual

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
              PrimaryKey = Some { ConstraintName = id' "orders_pkey"; Columns = [ id' "id" ] }
              UniqueConstraints = []
              CheckConstraints = []
              ForeignKeys = []
              Indexes = [ { Name = id' "orders_pkey"; Columns = [ id' "id" ]; IsUnique = true; Predicate = None } ]
              Scope = Observed }

    let desired =
        TableObject
            { Name = qn "sales" "orders"
              Columns = [ col 1 "id" false ]
              PrimaryKey = Some { ConstraintName = id' "orders_pkey"; Columns = [ id' "id" ] }
              UniqueConstraints = []
              CheckConstraints = []
              ForeignKeys = []
              Indexes = []
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
              PrimaryKey = Some { ConstraintName = id' "t_pkey"; Columns = [ id' "id" ] }
              UniqueConstraints = []
              CheckConstraints = []
              ForeignKeys = []
              Indexes = []
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
              CheckConstraints = [ { ConstraintName = id' "ck"; Expression = "" } ]
              ForeignKeys = []
              Indexes = []
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
          Scope = Managed }

let private fk name cols target targetCols =
    { ConstraintName = id' name
      Columns = cols |> List.map id'
      ReferencedTable = qn "sales" target
      ReferencedColumns = targetCols |> List.map id' }

let private plain = tableWith "sales" "orders" orders None [] [] []

[<Fact>]
let ``a foreign key in the database and not in desired state is reported`` () =
    let withFk = tableWith "sales" "orders" orders None [] [] [ fk "fk_o_c" [ "id" ] "customers" [ "id" ] ]
    let result = run true managed (complete [ plain ]) (complete [ withFk ])

    Assert.Contains(result.Changes, fun c ->
        match c with
        | UnclassifiedChange d -> d.Contains "foreign key" && d.Contains "fk_o_c"
        | _ -> false)

[<Fact>]
let ``a foreign key in desired state and not in the database is added`` () =
    let withFk = tableWith "sales" "orders" orders None [] [] [ fk "fk_o_c" [ "id" ] "customers" [ "id" ] ]
    let result = run true managed (complete [ withFk ]) (complete [ plain ])

    Assert.Contains(result.Changes, fun c -> c = AddConstraint(qn "sales" "orders", id' "fk_o_c"))

[<Fact>]
let ``a check constraint present on one side only is reported`` () =
    let withCheck =
        tableWith "sales" "orders" orders None [] [ { ConstraintName = id' "ck_x"; Expression = "" } ] []

    let result = run true managed (complete [ plain ]) (complete [ withCheck ])

    Assert.Contains(result.Changes, fun c ->
        match c with
        | UnclassifiedChange d -> d.Contains "check constraint" && d.Contains "ck_x"
        | _ -> false)

[<Fact>]
let ``a primary key covering different columns is reported`` () =
    let pkOn cols =
        Some { PrimaryKey.ConstraintName = id' "pk"; Columns = cols |> List.map id' }

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

    Assert.Contains(result.Changes, fun c ->
        match c with
        | UnclassifiedChange d -> d.Contains "foreign key 'fk' covers"
        | _ -> false)

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
        tableWith "sales" "orders" orders None [] [ { ConstraintName = id' "ck"; Expression = "x > 0" } ] []

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
            (Some { PrimaryKey.ConstraintName = id' "pk"; Columns = [ id' "id" ] })
            [ { UniqueConstraint.ConstraintName = id' "uq"; Columns = [ id' "total" ] } ]
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
    Strata.Application.SchemaDiff.run true managed [] normalised [] [] desired actual

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
          Scope = Managed }

let private runWithTables normalisedTables desired actual =
    Strata.Application.SchemaDiff.run true managed [] [] normalisedTables [] desired actual

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
              CheckConstraints = [ { ConstraintName = id' "ck"; Expression = expression } ]
              ForeignKeys = []
              Indexes = []
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
    Strata.Application.SchemaDiff.run true managed [] [] [] renames desired actual

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
