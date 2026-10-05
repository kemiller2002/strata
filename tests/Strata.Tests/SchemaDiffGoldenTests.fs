module Strata.Tests.SchemaDiffGoldenTests

open System
open System.IO
open Xunit
open Microsoft.FSharp.Reflection
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange
open Strata.Application
open Strata.Application.SchemaDiff

/// Golden characterization of `SchemaDiff.Plan.run` and its renderers (STRATA-QUAL-001).
///
/// `SchemaDiffTests` proves WHICH changes and suppressions a diff produces,
/// which is what guards the safety semantics. It barely looks at HOW a plan
/// renders: the SQL text of each statement, the order the statements execute
/// in, and the text and JSON a person or pipeline reads. A refactor that
/// changed a quote, a keyword, a rank or a sentence would have passed it.
///
/// These fixtures pin all four, byte for byte, for a corpus that reaches every
/// `Change` case `run` can produce. They are the safety net for decomposing
/// what was `SchemaDiff.fs`, and they stay as its regression gate.
///
/// ## Updating a golden
///
/// A golden differs only when output changed. If the change is intended, run
/// the tests with `STRATA_UPDATE_GOLDEN=1`, review the diff of
/// `tests/Strata.Tests/golden/schemadiff/*.golden` like any other code change,
/// and commit it. A missing golden is written and the test FAILS, so a new
/// fixture cannot pass silently on its first run.

let private id' = Identifier.unquoted
let private qn schema name = QualifiedName.qualified (id' schema) (id' name)

let private column position name typeName nullable =
    { Name = id' name
      Type = { TypeName = QualifiedName.unqualified (id' typeName); IsNullable = nullable }
      Position = position
      HasDefault = false
      DefaultExpression = None
      IsGenerated = false
      IsIdentity = false }

let private columns (spec: (string * string * bool) list) =
    spec |> List.mapi (fun i (n, t, nullable) -> column (i + 1) n t nullable)

let private table scope schema name spec : Table =
    { Name = qn schema name
      Columns = columns spec
      PrimaryKey = None
      UniqueConstraints = []
      CheckConstraints = []
      ForeignKeys = []
      Indexes = []
      Triggers = []
      Scope = scope }

let private everything =
    Completeness.ofList [ "relations", Complete; "indexes", Complete; "triggers", Complete ]

let private snapshotOf completeness objects =
    { Objects = objects
      ServerVersion = None
      Completeness = completeness }

let private complete = snapshotOf everything

let private managed = [ "sales" ]

let private baseInputs desired actual =
    { Inputs.between desired actual with
        AllowDrops = true
        ManagedSchemas = managed
        ExistingSchemas = Some managed }

let private trigger name : Trigger =
    { Name = id' name
      Timing = TriggerTiming.Before
      Events = [ "UPDATE" ]
      Level = TriggerLevel.Row
      UpdateColumns = []
      Function = QualifiedName.unqualified (id' "touch")
      Arguments = []
      HasCondition = false }

let private sequence scope name increment =
    SequenceObject
        { Name = qn "sales" name
          DataType = "bigint"
          Start = 1L
          Increment = increment
          MinValue = 1L
          MaxValue = 9223372036854775807L
          Cache = 1L
          Cycle = false
          Scope = scope }

let private enum' scope name values =
    EnumObject { Name = qn "sales" name; Values = values; Scope = scope }

let private domain scope name baseType notNull default' constraints =
    DomainObject
        { Name = qn "sales" name
          BaseType = baseType
          Collation = None
          NotNull = notNull
          Default = default'
          Constraints = constraints
          Scope = scope }

let private check name definition validated : DomainConstraint =
    { Name = name |> Option.map id'
      Definition = definition
      IsValidated = validated }

let private policy name command roles using' withCheck : Policy =
    { Name = id' name
      Command = command
      IsPermissive = true
      Roles = roles
      Using = using'
      WithCheck = withCheck }

let private grant target grantee privileges grantable : Grant =
    { Target = target
      Grantee = grantee
      Privileges = privileges
      Grantable = grantable }

let private extension name schema version relocatable : Extension =
    { Name = id' name
      Schema = schema |> Option.map id'
      Version = version
      IsRelocatable = relocatable }

// ---- fixtures --------------------------------------------------------------
//
// One per schema-object family, plus the ones that exist for a cross-cutting
// concern: ordering across every rank, suppression wording when drops are off
// or desired state is partial, and every input that could not be read.

/// Tables: declared-text and reconstructed creates, refusals, foreign-key
/// depth ordering, a creation cycle, new-table indexes and triggers, schema
/// creation, and the drop guards.
let private tablesFixture () =
    let customers =
        { table Managed "sales" "customers" [ "id", "integer", false; "email", "text", true ] with
            PrimaryKey = Some { ConstraintName = Some(id' "customers_pkey"); Columns = [ id' "id" ] }
            UniqueConstraints = [ { ConstraintName = None; Columns = [ id' "email" ] } ]
            Indexes =
              [ { Name = id' "customers_email_lower"; Columns = [ id' "email" ]; IsUnique = false; Predicate = None }
                { Name = id' "customers_active"; Columns = [ id' "id" ]; IsUnique = true; Predicate = Some "active" } ]
            Triggers = [ trigger "customers_touch"; trigger "customers_audit" ] }

    let orders =
        { table Managed "sales" "orders" [ "id", "integer", false; "customer_id", "integer", false ] with
            PrimaryKey = Some { ConstraintName = None; Columns = [ id' "id" ] }
            ForeignKeys =
              [ { ConstraintName = Some(id' "orders_customer_fk")
                  Columns = [ id' "customer_id" ]
                  ReferencedTable = qn "sales" "customers"
                  ReferencedColumns = [ id' "id" ] } ] }

    let audit =
        { table Managed "sales" "order_audit" [ "order_id", "integer", false ] with
            ForeignKeys =
              [ { ConstraintName = None
                  Columns = [ id' "order_id" ]
                  ReferencedTable = qn "sales" "orders"
                  ReferencedColumns = [ id' "id" ] } ] }

    let checked' =
        { table Managed "sales" "checked" [ "n", "integer", true ] with
            CheckConstraints = [ { ConstraintName = Some(id' "n_positive"); Expression = "" } ] }

    let defaulted =
        { table Managed "sales" "defaulted" [ "n", "integer", true ] with
            Columns = [ { column 1 "n" "integer" true with HasDefault = true } ] }

    let cycleA =
        { table Managed "sales" "cycle_a" [ "b_id", "integer", true ] with
            ForeignKeys =
              [ { ConstraintName = None
                  Columns = [ id' "b_id" ]
                  ReferencedTable = qn "sales" "cycle_b"
                  ReferencedColumns = [ id' "id" ] } ] }

    let cycleB =
        { table Managed "sales" "cycle_b" [ "a_id", "integer", true ] with
            ForeignKeys =
              [ { ConstraintName = None
                  Columns = [ id' "a_id" ]
                  ReferencedTable = qn "sales" "cycle_a"
                  ReferencedColumns = [ id' "id" ] } ] }

    let declared =
        { table Managed "sales" "declared" [ "x", "integer", true ] with
            CheckConstraints = [ { ConstraintName = None; Expression = "" } ] }

    let mixedCase =
        { table Managed "sales" "Mixed\"Case" [ "Weird Name", "text", true ] with
            Name = QualifiedName.qualified (id' "sales") (Identifier.quoted "Mixed\"Case") }

    { baseInputs
          (complete
              [ TableObject audit
                TableObject orders
                TableObject customers
                TableObject checked'
                TableObject defaulted
                TableObject cycleA
                TableObject cycleB
                TableObject declared
                TableObject mixedCase ])
          (complete
              [ TableObject(table Observed "sales" "legacy" [ "id", "integer", false ])
                TableObject(table Observed "other" "unmanaged" [ "id", "integer", false ])
                TableObject(table ExtensionOwned "sales" "ext_owned" [ "id", "integer", false ]) ]) with
        ExistingSchemas = Some []
        Declarations =
          [ qn "sales" "declared", "CREATE TABLE sales.declared (x integer CHECK (x > 0));" ]
        TriggerDeclarations =
          [ (qn "sales" "customers", id' "customers_touch"),
            "CREATE TRIGGER customers_touch BEFORE UPDATE ON sales.customers FOR EACH ROW EXECUTE FUNCTION touch();" ] }

/// Columns and declared renames, with identifiers that need quoting.
let private columnsFixture () =
    let desiredOrders =
        { table Managed "sales" "orders" [ "id", "integer", false; "total", "numeric", true; "note", "text", true; "Shipped At", "timestamptz", true; "flag", "boolean", false ] with
            Columns =
              columns [ "id", "integer", false; "total", "numeric", true; "note", "text", true; "Shipped At", "timestamptz", true; "flag", "boolean", false ]
              @ [ { column 6 "stamped" "timestamptz" true with HasDefault = true } ] }

    let actualOrders =
        table Observed "sales" "orders" [ "id", "integer", false; "total", "integer", true; "obsolete", "text", true; "flag", "boolean", true ]

    let desiredRenamed = table Managed "sales" "invoices" [ "id", "integer", false; "amount", "numeric", true ]
    let actualRenamed = table Observed "sales" "bills" [ "id", "integer", false; "amt", "numeric", true ]

    { baseInputs (complete [ TableObject desiredOrders; TableObject desiredRenamed ]) (complete [ TableObject actualOrders; TableObject actualRenamed ]) with
        Renames =
          [ { Object = qn "sales" "invoices"
              RenamedFrom = Some(qn "sales" "bills")
              Columns = [ "amt", "amount" ] } ] }

/// Constraints, defaults, indexes and triggers on a table present on both sides.
let private constraintsFixture () =
    let fk name cols target refs : ForeignKey =
        { ConstraintName = name |> Option.map id'
          Columns = cols |> List.map id'
          ReferencedTable = target
          ReferencedColumns = refs |> List.map id' }

    let index name cols unique : Index =
        { Name = id' name; Columns = cols |> List.map id'; IsUnique = unique; Predicate = None }

    let desired =
        { table Managed "sales" "orders" [ "id", "integer", false; "customer_id", "integer", true; "code", "text", true; "status", "text", true; "qty", "integer", true ] with
            Columns =
              columns [ "id", "integer", false; "customer_id", "integer", true; "code", "text", true ]
              @ [ { column 4 "status" "text" true with HasDefault = true }
                  { column 5 "qty" "integer" true with HasDefault = true } ]
            PrimaryKey = Some { ConstraintName = Some(id' "orders_pkey"); Columns = [ id' "id" ] }
            UniqueConstraints =
              [ { ConstraintName = Some(id' "orders_code_key"); Columns = [ id' "code" ] }
                { ConstraintName = None; Columns = [ id' "customer_id"; id' "code" ] }
                { ConstraintName = Some(id' "orders_status_key"); Columns = [ id' "status"; id' "code" ] } ]
            ForeignKeys =
              [ fk (Some "orders_customer_fk") [ "customer_id" ] (qn "sales" "customers") [ "id" ]
                fk None [ "code" ] (qn "sales" "codes") [ "code" ] ]
            CheckConstraints =
              [ { ConstraintName = Some(id' "qty_positive"); Expression = "" }
                { ConstraintName = Some(id' "status_known"); Expression = "" }
                { ConstraintName = Some(id' "code_upper"); Expression = "" }
                { ConstraintName = Some(id' "unrendered_check"); Expression = "" } ]
            Indexes = [ index "orders_code_idx" [ "code" ] false; index "orders_status_idx" [ "status" ] false ]
            Triggers =
              [ trigger "orders_touch"
                { trigger "orders_audit" with Timing = TriggerTiming.After }
                trigger "orders_new" ] }

    let actual =
        { table Observed "sales" "orders" [ "id", "integer", false; "customer_id", "integer", true; "code", "text", true; "status", "text", true; "qty", "integer", true ] with
            Columns =
              columns [ "id", "integer", false; "customer_id", "integer", true; "code", "text", true ]
              @ [ { column 4 "status" "text" true with HasDefault = true; DefaultExpression = Some "'old'::text" }
                  { column 5 "qty" "integer" true with HasDefault = false } ]
            PrimaryKey = None
            UniqueConstraints =
              [ { ConstraintName = Some(id' "orders_status_key"); Columns = [ id' "status" ] }
                { ConstraintName = Some(id' "orders_legacy_key"); Columns = [ id' "id" ] } ]
            ForeignKeys = [ fk (Some "orders_old_fk") [ "customer_id" ] (qn "sales" "people") [ "id" ] ]
            CheckConstraints =
              [ { ConstraintName = Some(id' "status_known"); Expression = "CHECK ((status = 'x'::text))" }
                { ConstraintName = Some(id' "code_upper"); Expression = "CHECK ((code = upper(code)))" }
                { ConstraintName = Some(id' "unrendered_check"); Expression = "CHECK (true)" }
                { ConstraintName = Some(id' "stale_check"); Expression = "CHECK (false)" } ]
            Indexes =
              [ { Name = id' "orders_status_key"; Columns = [ id' "status" ]; IsUnique = true; Predicate = None }
                index "orders_code_idx" [ "code"; "id" ] false
                index "orders_stale_idx" [ "id" ] false ]
            Triggers =
              [ trigger "orders_touch"
                trigger "orders_audit"
                trigger "orders_stale" ] }

    { baseInputs (complete [ TableObject desired ]) (complete [ TableObject actual ]) with
        NormalisedTables =
          [ { Table = "sales.orders"
              Defaults = [ "status", "'new'::text"; "qty", "1" ]
              Checks =
                [ "qty_positive", "CHECK ((qty > 0))"
                  "status_known", "CHECK ((status = ANY (ARRAY['new'::text, 'old'::text])))"
                  "code_upper", "CHECK ((code = upper(code)))" ] } ]
        TriggerDeclarations =
          [ (qn "sales" "orders", id' "orders_audit"),
            "CREATE TRIGGER orders_audit AFTER UPDATE ON sales.orders FOR EACH ROW EXECUTE FUNCTION touch();" ] }

/// Sequences, enums and domains.
let private typesFixture () =
    let desired =
        [ sequence Managed "order_seq" 1L
          sequence Managed "invoice_seq" 10L
          enum' Managed "status" [ "draft"; "new"; "paid"; "shipped" ]
          enum' Managed "priority" [ "low"; "high" ]
          enum' Managed "colour" [ "red"; "it's" ]
          enum' Managed "single" [ "only" ]
          domain Managed "email" "text" true (Some "'x'::text") [ check (Some "email_at") "" true; check None "" true ]
          domain Managed "work_email" "sales.email" false None []
          domain Managed "code" "text" false None [ check (Some "code_len") "" true; check (Some "code_validated") "" true ]
          domain Managed "unrendered" "text" false None []
          domain Managed "rebased" "varchar(10)" false None []
          domain Managed "renamed_default" "text" true None [] ]

    let actual =
        [ sequence Observed "invoice_seq" 1L
          sequence Observed "stale_seq" 1L
          sequence ExtensionOwned "ext_seq" 1L
          enum' Observed "status" [ "new"; "paid" ]
          enum' Observed "priority" [ "high"; "low" ]
          enum' Observed "legacy" [ "a" ]
          enum' Observed "single" []
          domain Observed "code" "text" true (Some "'c'::text") [ check (Some "code_len") "CHECK ((length(VALUE) > 1))" true; check (Some "code_stale") "CHECK (true)" true; check (Some "code_validated") "CHECK ((VALUE <> ''::text))" false ]
          domain Observed "unrendered" "text" false None []
          domain Observed "rebased" "text" false None []
          domain Observed "renamed_default" "text" false (Some "'gone'::text") []
          domain Observed "legacy_domain" "text" false None [] ]

    { baseInputs (complete desired) (complete actual) with
        Declarations =
          [ qn "sales" "email", "CREATE DOMAIN sales.email AS text NOT NULL DEFAULT 'x' CHECK (VALUE ~ '@') CONSTRAINT email_at CHECK (VALUE <> '');"
            qn "sales" "work_email", "CREATE DOMAIN sales.work_email AS sales.email;" ]
        NormalisedDomains =
          [ { Domain = "sales.email"
              BaseType = "text"
              Collation = None
              NotNull = true
              Default = Some "'x'::text"
              Constraints = [ "CHECK ((VALUE <> ''::text))", true; "CHECK ((VALUE ~ '@'::text))", true ] }
            { Domain = "sales.work_email"
              BaseType = "sales.email"
              Collation = None
              NotNull = false
              Default = None
              Constraints = [] }
            { Domain = "sales.code"
              BaseType = "text"
              Collation = None
              NotNull = false
              Default = Some "'d'::text"
              Constraints = [ "CHECK ((length(VALUE) > 2))", true; "CHECK ((VALUE <> ''::text))", true ] }
            { Domain = "sales.rebased"
              BaseType = "character varying(10)"
              Collation = Some "C"
              NotNull = false
              Default = None
              Constraints = [] }
            { Domain = "sales.renamed_default"
              BaseType = "text"
              Collation = None
              NotNull = true
              Default = None
              Constraints = [] } ] }

/// Grants on every target kind, PUBLIC, revokes, unclaimed grantees and the
/// grant option.
let private grantsFixture () =
    let orders = GrantTarget.Relation(qn "sales" "orders")
    let schema = GrantTarget.Schema(id' "sales")
    let routine = GrantTarget.Routine(qn "sales" "total", [ "integer"; "character varying" ])
    let column = GrantTarget.RelationColumn(qn "sales" "orders", id' "email")

    { baseInputs (complete []) (complete []) with
        DeclaredGrants =
          [ grant orders "app_user" [ "SELECT"; "INSERT" ] []
            grant schema "app_user" [ "USAGE" ] []
            grant routine "PUBLIC" [ "EXECUTE" ] []
            grant column "Reporting" [ "SELECT"; "UPDATE" ] [] ]
        ActualGrants =
          Some
              [ grant orders "app_user" [ "SELECT"; "DELETE" ] [ "SELECT" ]
                grant (GrantTarget.Relation(qn "sales" "orders")) "reporting" [ "SELECT" ] []
                grant orders "replication" [ "SELECT" ] []
                grant (GrantTarget.Relation(qn "other" "things")) "app_user" [ "SELECT" ] [] ] }

/// Extensions, policies and row-level security.
let private securityFixture () =
    let orders = qn "sales" "orders"
    let invoices = qn "sales" "invoices"
    let ledger = qn "sales" "ledger"

    { baseInputs (complete []) (complete []) with
        DeclaredExtensions =
          [ extension "citext" None None false
            extension "pgcrypto" None (Some "1.3") false
            extension "hstore" (Some "sales") None true
            extension "plpgsql" (Some "sales") None false ]
        ActualExtensions =
          Some(
              Ok
                  [ extension "pgcrypto" (Some "public") (Some "1.2") true
                    extension "hstore" (Some "public") (Some "1.8") true
                    extension "plpgsql" (Some "pg_catalog") (Some "1.0") false
                    extension "postgis" (Some "public") (Some "3.4") false ]
          )
        DeclaredPolicies =
          [ orders, policy "tenant_read" PolicyCommand.Select [ "app_user" ] (Some "(tenant = 'x'::text)") None
            orders, policy "tenant_write" PolicyCommand.Insert [ "app_user" ] None (Some "(tenant = 'x'::text)")
            orders, policy "owner_only" PolicyCommand.All [ "app_user" ] (Some "") None
            invoices, policy "new_policy" PolicyCommand.Select [ "app_user" ] (Some "true") None ]
        DeclaredRowSecurity =
          [ orders, RowSecuritySetting.Enable
            orders, RowSecuritySetting.Force
            invoices, RowSecuritySetting.Disable
            invoices, RowSecuritySetting.NoForce ]
        PolicyDeclarations =
          [ (orders, id' "tenant_write"),
            "CREATE POLICY tenant_write ON sales.orders FOR INSERT TO app_user WITH CHECK (tenant = 'x');"
            (invoices, id' "new_policy"), "CREATE POLICY new_policy ON sales.invoices FOR SELECT TO app_user USING (true);" ]
        ActualRowLevelSecurity =
          Some(
              Ok
                  [ { Table = orders
                      Enabled = false
                      Forced = false
                      Policies =
                        [ policy "tenant_read" PolicyCommand.Select [ "APP_USER" ] (Some "(tenant = 'x'::text)") None
                          policy "tenant_write" PolicyCommand.Update [ "app_user" ] None (Some "(tenant = 'y'::text)")
                          policy "owner_only" PolicyCommand.All [ "app_user" ] (Some "(owner = CURRENT_USER)") None
                          policy "undeclared" PolicyCommand.Delete [ "admin" ] (Some "true") None ] }
                    { Table = invoices
                      Enabled = true
                      Forced = true
                      Policies = [] }
                    { Table = ledger
                      Enabled = true
                      Forced = false
                      Policies = [ { policy "ledger_read" PolicyCommand.Select [ "auditor"; "app_user" ] (Some "true") None with IsPermissive = false } ] }
                    { Table = qn "sales" "vault"
                      Enabled = true
                      Forced = false
                      Policies = [] }
                    { Table = qn "sales" "loose"
                      Enabled = false
                      Forced = false
                      Policies = [ policy "loose_read" PolicyCommand.Select [ "app_user" ] (Some "true") None ] }
                    { Table = qn "sales" "forced_only"
                      Enabled = false
                      Forced = true
                      Policies = [] }
                    { Table = qn "other" "elsewhere"
                      Enabled = true
                      Forced = false
                      Policies = [] } ]
          ) }

/// Views and routines, compared by presence and, where the server rendered
/// them, by definition.
let private objectsFixture () =
    let view scope name materialized definition =
        ViewObject
            { Name = qn "sales" name
              Columns = []
              IsMaterialized = materialized
              Definition = definition
              Scope = scope }

    let routine scope name kind args body =
        RoutineObject
            { Name = qn "sales" name
              Kind = kind
              ArgumentTypes = args
              ReturnType = None
              Language = "sql"
              Body = body
              Scope = scope }

    { baseInputs
          (complete
              [ view Managed "new_view" false ""
                view Managed "undeclared_text" false ""
                view Managed "changed_view" false ""
                view Managed "same_view" false ""
                view Managed "unrendered_view" false ""
                view Managed "summary" true ""
                routine Managed "new_fn" Function [ "integer" ] (Some "select 1")
                routine Managed "changed_fn" Function [ "integer" ] (Some "select 2")
                routine Managed "changed_proc" Procedure [] (Some "begin end")
                routine Managed "atomic_fn" Function [] None ])
          (complete
              [ view Observed "changed_view" false " SELECT 1 AS x;"
                view Observed "same_view" false " SELECT 2 AS y;"
                view Observed "unrendered_view" false " SELECT 3;"
                view Observed "summary" true " SELECT 4;"
                view Observed "stale_view" false " SELECT 5;"
                view ExtensionOwned "ext_view" false " SELECT 6;"
                routine Observed "changed_fn" Function [ "integer" ] (Some "select 1")
                routine Observed "changed_proc" Procedure [] (Some "begin null; end")
                routine Observed "atomic_fn" Function [] (Some "select 3")
                routine Observed "stale_fn" Function [ "text" ] (Some "select 'x'") ]) with
        Declarations =
          [ qn "sales" "new_view", "CREATE VIEW sales.new_view AS SELECT 1 AS x;"
            qn "sales" "changed_view", "  create view sales.changed_view AS SELECT 10 AS x;"
            qn "sales" "new_fn", "CREATE FUNCTION sales.new_fn(integer) RETURNS integer LANGUAGE sql AS $$select 1$$;"
            qn "sales" "changed_fn", "CREATE FUNCTION sales.changed_fn(integer) RETURNS integer LANGUAGE sql AS $$select 2$$;"
            qn "sales" "changed_proc", "CREATE PROCEDURE sales.changed_proc() LANGUAGE plpgsql AS $$begin end$$;" ]
        NormalisedViews =
          [ "sales.changed_view", " SELECT 10 AS x;"
            "sales.same_view", " SELECT 2 AS y;"
            "sales.summary", " SELECT 4;" ] }

/// Reference data rows, including the enum two-step disclosure.
let private dataFixture () =
    let row key rendered literals =
        { Key = key; Rendered = rendered; Literals = literals }

    { baseInputs
          (complete [ enum' Managed "status" [ "new"; "paid" ] ])
          (complete [ enum' Observed "status" [ "new" ] ]) with
        Data =
          [ { Table = "sales.statuses"
              Columns = [ "code"; "Label" ]
              KeyColumns = [ "code" ]
              Declared = [ row "new" "new|New" [ "'new'"; "'New'" ]; row "paid" "paid|Paid!" [ "'paid'"; "'Paid!'" ]; row "o'k" "o'k|OK" [ "'o''k'"; "'OK'" ] ]
              Deployed = [ row "paid" "paid|Paid" []; row "void" "void|Void" []; row "draft" "draft|Draft" [] ] }
            { Table = "sales.flags"
              Columns = [ "flag" ]
              KeyColumns = [ "flag" ]
              Declared = [ row "on" "on" [ "'on'" ]; row "off" "off" [ "'off'" ] ]
              Deployed = [ row "on" "ON" [] ] } ]
        DataFailures =
          [ { Table = "sales.orders_by_status"; Reason = "invalid input value for enum sales.status: \"paid\"" }
            { Table = "sales.broken"; Reason = "permission denied" } ] }

/// Every removal suppressed: drops not enabled, then desired state partial.
let private dropsDisabledFixture () =
    let inputs = constraintsFixture ()

    { inputs with
        AllowDrops = false
        Desired =
          { inputs.Desired with
              Objects =
                inputs.Desired.Objects
                @ (typesFixture ()).Desired.Objects
                @ (objectsFixture ()).Desired.Objects }
        Actual =
          { inputs.Actual with
              Objects =
                inputs.Actual.Objects
                @ [ TableObject(table Observed "sales" "legacy" [ "id", "integer", false ]) ]
                @ (typesFixture ()).Actual.Objects
                @ (objectsFixture ()).Actual.Objects }
        NormalisedDomains = (typesFixture ()).NormalisedDomains
        DeclaredGrants = (grantsFixture ()).DeclaredGrants
        ActualGrants = (grantsFixture ()).ActualGrants }

let private partialDesiredFixture () =
    let inputs = dropsDisabledFixture ()

    { inputs with
        AllowDrops = true
        Desired =
          { inputs.Desired with
              Completeness =
                Completeness.ofList
                    [ "relations", Partial "sales/broken.sql did not parse"
                      "indexes", Complete
                      "triggers", Complete ] } }

/// Every input that could not be read, so every "NOT compared" disclosure.
let private unreadableFixture () =
    { baseInputs
          (complete [ TableObject(table Managed "sales" "orders" [ "id", "integer", false ]) ])
          (complete []) with
        ExistingSchemas = None
        DeclaredGrants = [ grant (GrantTarget.Relation(qn "sales" "orders")) "app_user" [ "SELECT" ] [] ]
        ActualGrants = None
        DeclaredExtensions = [ extension "citext" None None false ]
        ActualExtensions = Some(Error "permission denied for pg_extension")
        DeclaredPolicies = [ qn "sales" "orders", policy "p" PolicyCommand.All [ "app_user" ] (Some "true") None ]
        ActualRowLevelSecurity = Some(Error "permission denied for pg_policy") }

/// Nothing differs. The rendering of an empty plan is output too.
let private identicalFixture () =
    let t = table Managed "sales" "orders" [ "id", "integer", false ]
    baseInputs (complete [ TableObject t ]) (complete [ TableObject { t with Scope = Observed } ])

let private fixtures: (string * (unit -> Inputs)) list =
    [ "tables", tablesFixture
      "columns", columnsFixture
      "constraints", constraintsFixture
      "types", typesFixture
      "grants", grantsFixture
      "security", securityFixture
      "objects", objectsFixture
      "data", dataFixture
      "drops-disabled", dropsDisabledFixture
      "partial-desired", partialDesiredFixture
      "unreadable", unreadableFixture
      "identical", identicalFixture ]

// ---- rendering ---------------------------------------------------------------

let private caseName (change: Change) =
    (FSharpValue.GetUnionFields(change, typeof<Change>) |> fst).Name

/// Everything observable about one plan, as text: each statement in execution
/// order with the exact SQL it would run (or the refusal), then the human and
/// machine renderings exactly as the CLI prints them.
let private render (inputs: Inputs) =
    let result = SchemaDiff.Plan.run inputs
    let gate = DeploymentGate.run Strata.Analysis.Graph.SemanticGraph.empty Scope.nothingAnalyzed result.Changes

    let statements =
        result.Statements
        |> List.mapi (fun i s ->
            let sql =
                match s.Sql with
                | Some text -> text.Split('\n') |> Array.map (fun l -> "     | " + l) |> String.concat "\n"
                | None -> "     (no SQL: Strata refuses to write this statement)"

            sprintf "[%02d] %s (%s)\n%s" (i + 1) (Change.tag s.Change) (caseName s.Change) sql)

    String.concat
        "\n"
        [ sprintf "## statements (%d)" (List.length result.Statements)
          yield! statements
          ""
          "## text"
          SchemaDiff.Render.toText result gate
          ""
          "## json"
          SchemaDiff.Render.toJson result gate
          "" ]

let private goldenDirectory =
    Path.Combine(__SOURCE_DIRECTORY__, "golden", "schemadiff")

let private updating =
    Environment.GetEnvironmentVariable "STRATA_UPDATE_GOLDEN" = "1"

let fixtureNames: obj[] seq = fixtures |> Seq.map (fun (name, _) -> [| box name |])

[<Theory>]
[<MemberData("fixtureNames")>]
let ``plan SQL, statement order, text and JSON match the golden`` (name: string) =
    let inputs = (fixtures |> List.find (fun (n, _) -> n = name) |> snd) ()
    let actual = render inputs
    let path = Path.Combine(goldenDirectory, name + ".golden")

    if updating || not (File.Exists path) then
        Directory.CreateDirectory goldenDirectory |> ignore
        File.WriteAllText(path, actual)

        if not updating then
            failwithf "golden %s did not exist and has been written; review it and re-run" path
    else
        let expected = File.ReadAllText(path).Replace("\r\n", "\n")
        Assert.Equal(expected, actual)

[<Theory>]
[<MemberData("fixtureNames")>]
let ``the same input renders byte-identically twice`` (name: string) =
    let fixture = fixtures |> List.find (fun (n, _) -> n = name) |> snd
    // Two independently constructed, structurally equal inputs: nothing in
    // the output may depend on identity, iteration order of a hash, or time.
    Assert.Equal(render (fixture ()), render (fixture ()))

/// `Change` cases no fixture can reach through `run`, each with the reason.
///
/// Adding a case here is a claim that `SchemaDiff.Plan.run` cannot produce it; the
/// test below fails the other way if a fixture ever does.
let private unreachableFromRun =
    Map.ofList
        [ "TruncateTable",
          "proposed only by ProposedChange.ofStatement for a parsed migration; no differ emits it" ]

[<Fact>]
let ``the golden corpus reaches every Change case run can produce`` () =
    let all =
        FSharpType.GetUnionCases(typeof<Change>) |> Array.map (fun c -> c.Name) |> Set.ofArray

    let reached =
        fixtures
        |> List.collect (fun (_, f) -> (SchemaDiff.Plan.run (f ())).Changes |> List.map caseName)
        |> Set.ofList

    let missing = all - reached - (unreachableFromRun |> Map.keys |> Set.ofSeq)
    let wronglyExcluded = Set.intersect reached (unreachableFromRun |> Map.keys |> Set.ofSeq)

    Assert.True(Set.isEmpty missing, sprintf "no golden fixture reaches: %s" (String.Join(", ", missing)))
    Assert.True(Set.isEmpty wronglyExcluded, sprintf "listed unreachable but reached: %s" (String.Join(", ", wronglyExcluded)))

/// The one case no fixture can reach still has its SQL and phase pinned,
/// through the modules that own them.
[<Fact>]
let ``TruncateTable, which no differ produces, has pinned SQL and phase`` () =
    let sources = SchemaDiff.PostgresDdl.sources (baseInputs (complete []) (complete []))
    let change = TruncateTable(qn "sales" "orders")

    Assert.Equal(Some "TRUNCATE TABLE \"sales\".\"orders\"", SchemaDiff.PostgresDdl.statement sources change)
    Assert.Equal(SchemaDiff.Phase.Truncations, SchemaDiff.Ordering.phase change)

// ---- removal conformance (STRATA-QUAL-002) -----------------------------------

/// Every `Change` case that REMOVES something the database has.
///
/// `DropSafety` is the single removal authority: each of these may be proposed
/// only when desired state loaded completely and `--allow-drops` is on. A
/// domain constraint was the one exception until STRATA-QUAL-002, and nothing
/// noticed, because nothing asked. These tests ask.
let removalCases =
    set
        [ "DropColumn"
          "DropTable"
          "RevokePrivileges"
          "DropSequence"
          "DropEnumType"
          "DropDomainType"
          "DropDomainConstraint"
          "DropIndex"
          "DropTrigger"
          "DropConstraint" ]

/// Cases whose names read like a removal but are not one, each with the reason.
/// Adding a case here is a claim that it may be proposed without `--allow-drops`.
let notRemovals =
    Map.ofList
        [ "DropDomainDefault",
          "an attribute of a declared domain converging on the file; nothing the database holds is lost"
          "DropDomainNotNull",
          "loosens a declared domain to match the file; nothing the database holds is lost" ]

[<Fact>]
let ``every Change case that reads like a removal is classified`` () =
    let looksLikeRemoval (name: string) =
        name.StartsWith "Drop" || name.StartsWith "Revoke" || name.StartsWith "Truncate"

    let unclassified =
        FSharpType.GetUnionCases(typeof<Change>)
        |> Array.map (fun c -> c.Name)
        |> Array.filter looksLikeRemoval
        |> Array.filter (fun n ->
            not (Set.contains n removalCases)
            && not (Map.containsKey n notRemovals)
            && not (Map.containsKey n unreachableFromRun))

    Assert.True(
        Array.isEmpty unclassified,
        sprintf "classify as a removal or a non-removal: %s" (String.Join(", ", unclassified)))

let private removalsIn (inputs: Inputs) =
    (SchemaDiff.Plan.run inputs).Changes
    |> List.map caseName
    |> List.filter (fun n -> Set.contains n removalCases)

[<Fact>]
let ``the golden corpus proposes every removal case when drops are allowed`` () =
    // Without this the two tests below could pass vacuously.
    let reached =
        fixtures
        |> List.collect (fun (_, f) -> removalsIn { f () with AllowDrops = true })
        |> Set.ofList

    let missing = removalCases - reached
    Assert.True(Set.isEmpty missing, sprintf "no fixture proposes: %s" (String.Join(", ", missing)))

[<Fact>]
let ``no removal is proposed without --allow-drops`` () =
    let offenders =
        fixtures
        |> List.collect (fun (name, f) ->
            removalsIn { f () with AllowDrops = false } |> List.map (fun c -> sprintf "%s: %s" name c))

    Assert.True(List.isEmpty offenders, String.Join("\n", offenders))

[<Fact>]
let ``no removal is proposed when desired state did not load completely`` () =
    let incomplete (inputs: Inputs) =
        { inputs with
            AllowDrops = true
            Desired =
              { inputs.Desired with
                  Completeness =
                    Completeness.ofList (
                        ("relations", Partial "a file did not parse")
                        :: (inputs.Desired.Completeness.Categories |> List.filter (fun (c, _) -> c <> "relations"))
                    ) } }

    let offenders =
        fixtures
        |> List.collect (fun (name, f) -> removalsIn (incomplete (f ())) |> List.map (fun c -> sprintf "%s: %s" name c))

    Assert.True(List.isEmpty offenders, String.Join("\n", offenders))
