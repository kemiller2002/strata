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
    Strata.Application.SchemaDiff.run allowDrops managedSchemas [] desired actual

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
let ``a view in the database is suppressed as not modelled, not proposed for dropping`` () =
    // Views do not load as desired state yet. Without this a view would be
    // absent from desired state for a reason that has nothing to do with
    // intent, and a diff would read that as "drop it".
    let view =
        ViewObject
            { Name = qn "sales" "v_open"
              Columns = []
              IsMaterialized = false
              Definition = "SELECT 1"
              Scope = Observed }

    let result = run true managed (complete []) (complete [ view ])

    Assert.Empty result.Changes
    Assert.Contains(result.Suppressed, fun s -> s.Reason = NotModelled)

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
