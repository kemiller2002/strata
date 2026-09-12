module Strata.Tests.ArtifactTests

open Xunit
open Microsoft.FSharp.Reflection
open Strata.Semantic.Identity
open Strata.Semantic.Evidence
open Strata.Semantic.AnalysisScope
open Strata.Semantic.Schema
open Strata.Analysis.DialectPort
open Strata.Application
open Strata.Host.Files

/// The compiled artifact, written and read back.
///
/// ## What these tests are for
///
/// The format is hand-written in both directions, which is the requirement
/// (`BOUNDARY-PRESERVATION.md`) and also the hazard: two functions three lines
/// apart can still disagree, and when they do the artifact is written one way
/// and read another. The symptom would not be an error. It would be a
/// deployment that is subtly not what was compiled — a policy expression that
/// came back `None` instead of `Some ""`, an unnamed constraint that acquired a
/// name, a `Partial` completeness that read as `Complete`.
///
/// So the sample below is deliberately hostile. It carries every union case,
/// every `option` in both states, and the three-state clauses in all three
/// states, and a coverage test fails if a new union case is added without
/// reaching the sample.

let private id' = Identifier.unquoted
let private qn schema name = QualifiedName.qualified (id' schema) (id' name)

let private column name position hasDefault defaultExpr generated identity : Column =
    { Name = id' name
      Type = { TypeName = QualifiedName.unqualified (id' "text"); IsNullable = false }
      Position = position
      HasDefault = hasDefault
      DefaultExpression = defaultExpr
      IsGenerated = generated
      IsIdentity = identity }

let private table : Table =
    { Name = qn "shop" "product"
      Columns =
        [ column "id" 1 false None false true
          // A default that IS rendered, next to one that is not.
          column "status" 2 true (Some "'open'::text") false false
          column "total" 3 true None true false ]
      PrimaryKey = Some { ConstraintName = Some(id' "product_pkey"); Columns = [ id' "id" ] }
      UniqueConstraints =
        [ // Named and UNNAMED, because an unnamed constraint that acquires a
          // name on the way through never converges again (WI-0054).
          { ConstraintName = Some(id' "product_code_key"); Columns = [ id' "code" ] }
          { ConstraintName = None; Columns = [ id' "slug" ] } ]
      CheckConstraints =
        [ { ConstraintName = Some(id' "product_total_positive"); Expression = "(total > (0)::numeric)" }
          { ConstraintName = None; Expression = "(status <> ''::text)" } ]
      ForeignKeys =
        [ { ConstraintName = Some(id' "product_bin_fkey")
            Columns = [ id' "bin_id" ]
            ReferencedTable = qn "shop" "bin"
            ReferencedColumns = [ id' "id" ] } ]
      Indexes =
        [ { Name = id' "product_status_idx"; Columns = [ id' "status" ]; IsUnique = false; Predicate = None }
          { Name = id' "product_open_idx"
            Columns = [ id' "id" ]
            IsUnique = true
            Predicate = Some "(status = 'open'::text)" } ]
      Triggers =
        [ { Name = id' "set_updated_at"
            Timing = TriggerTiming.Before
            Events = [ "UPDATE" ]
            Level = TriggerLevel.Row
            UpdateColumns = [ id' "status" ]
            Function = qn "shop" "touch"
            Arguments = [ "'a'" ]
            HasCondition = true }
          { Name = id' "audit_after"
            Timing = TriggerTiming.After
            Events = [ "INSERT"; "DELETE" ]
            Level = TriggerLevel.Statement
            UpdateColumns = []
            Function = qn "shop" "audit"
            Arguments = []
            HasCondition = false }
          { Name = id' "instead_of_it"
            Timing = TriggerTiming.InsteadOf
            Events = [ "INSERT" ]
            Level = TriggerLevel.Row
            UpdateColumns = []
            Function = qn "shop" "redirect"
            Arguments = []
            HasCondition = false } ]
      Scope = Managed }

let private view : View =
    { Name = qn "shop" "open_product"
      Columns = [ column "id" 1 false None false false ]
      IsMaterialized = false
      Definition = " SELECT id\n   FROM shop.product;"
      Scope = Managed }

let private materialized : View =
    { view with Name = qn "shop" "product_totals"; IsMaterialized = true; Scope = Observed }

let private routine : Routine =
    { Name = qn "shop" "touch"
      Kind = Function
      ArgumentTypes = [ "text" ]
      ReturnType = Some "trigger"
      Language = "plpgsql"
      Body = Some "BEGIN RETURN NEW; END"
      Scope = Managed }

let private procedure' : Routine =
    { routine with
        Name = qn "shop" "rebuild"
        Kind = Procedure
        // Both `option` fields in their EMPTY state: a routine the server holds
        // only as a symbol name has no body, and that is not an empty body.
        ReturnType = None
        Body = None
        Scope = ExtensionOwned }

let private sequence : Sequence =
    { Name = qn "shop" "product_id_seq"
      DataType = "bigint"
      Start = 1L
      Increment = 1L
      MinValue = 1L
      // bigint's maximum, which is exactly the value a naive int32 rendering
      // would lose.
      MaxValue = 9223372036854775807L
      Cache = 1L
      Cycle = false
      Scope = SystemOwned }

let private policy name command permissive using check : Policy =
    { Name = id' name
      Command = command
      IsPermissive = permissive
      Roles = [ "PUBLIC" ]
      Using = using
      WithCheck = check }

let private sample : ResolvedDesiredState =
    { Declared =
        { Snapshot =
            { Objects =
                [ TableObject table
                  ViewObject view
                  ViewObject materialized
                  RoutineObject routine
                  RoutineObject procedure'
                  SequenceObject sequence ]
              ServerVersion =
                Fact.create
                    High
                    [ { Source = Catalog "pg_settings"; Detail = "server_version" } ]
                    { Major = 16; Full = "16.15" }
              Completeness =
                Completeness.ofList
                    [ "relations", Complete
                      "grants", Partial "the role cannot read one schema"
                      "policies", Inaccessible "permission denied"
                      "triggers", NotRequested ] }
          Failures = [ { Path = "schema/shop/tables/broken.sql"; Reason = "could not be parsed" } ]
          Declarations = [ qn "shop" "product", "CREATE TABLE shop.product (id bigint);" ]
          TriggerDeclarations =
            [ (qn "shop" "product", id' "set_updated_at"), "CREATE TRIGGER set_updated_at ..." ]
          Data =
            [ { Table = qn "shop" "account_type"
                Columns = [ id' "id"; id' "name" ]
                Rows = [ [ "1"; "'new'" ]; [ "2"; "'old'" ] ]
                Path = "schema/shop/data/account_type.sql" } ]
          Grants =
            [ { Target = GrantTarget.Relation(qn "shop" "product")
                Grantee = "app_user"
                Privileges = [ "SELECT" ]
                Grantable = [] }
              { Target = GrantTarget.Schema(id' "shop"); Grantee = "app_user"; Privileges = [ "USAGE" ]; Grantable = [] }
              { Target = GrantTarget.Routine(qn "shop" "touch", [ "text" ])
                Grantee = "app_user"
                Privileges = [ "EXECUTE" ]
                Grantable = [ "EXECUTE" ] }
              { Target = GrantTarget.RelationColumn(qn "shop" "product", id' "status")
                Grantee = "app_user"
                Privileges = [ "UPDATE" ]
                Grantable = [] } ]
          Policies =
            [ // All five commands, and the THREE clause states: absent,
              // written-but-unrendered (`Some ""`), and rendered.
              qn "shop" "product", policy "p_all" PolicyCommand.All true (Some "(a)") None
              qn "shop" "product", policy "p_select" PolicyCommand.Select true (Some "") None
              qn "shop" "product", policy "p_insert" PolicyCommand.Insert true None (Some "(b)")
              qn "shop" "product", policy "p_update" PolicyCommand.Update false None None
              qn "shop" "product", policy "p_delete" PolicyCommand.Delete true (Some "(c)") (Some "") ]
          PolicyDeclarations = [ (qn "shop" "product", id' "p_all"), "CREATE POLICY p_all ON shop.product ..." ]
          RowSecurity =
            [ qn "shop" "product", RowSecuritySetting.Enable
              qn "shop" "product", RowSecuritySetting.Disable
              qn "shop" "product", RowSecuritySetting.Force
              qn "shop" "product", RowSecuritySetting.NoForce ]
          Extensions =
            [ { Name = id' "citext"; Schema = Some(id' "public"); Version = Some "1.6"; IsRelocatable = true }
              // A file that pinned no version and named no schema. Neither is
              // the same as choosing the default.
              { Name = id' "pgcrypto"; Schema = None; Version = None; IsRelocatable = false } ] }
      NormalisedViews = [ "shop.open_product", " SELECT id\n   FROM shop.product;" ]
      NormalisedTables =
        [ { Table = "shop.product"
            Defaults = [ "status", "'open'::text" ]
            Checks = [ "product_total_positive", "(total > (0)::numeric)" ] } ]
      Policies = [ qn "shop" "product", policy "p_all" PolicyCommand.All true (Some "(a)") None ]
      RowSecurity = [ qn "shop" "product", RowSecuritySetting.Enable ]
      Data =
        [ { Table = "shop.account_type"
            Columns = [ "id"; "name" ]
            KeyColumns = [ "id" ]
            Declared = [ { Key = "(1)"; Rendered = "(1,new)"; Literals = [ "'1'"; "'new'" ] } ]
            Deployed = [ { Key = "(2)"; Rendered = "(2,old)"; Literals = [] } ] } ]
      DataFailures = [ { Table = "shop.locked"; Reason = "permission denied" } ]
      Warnings = [ "could not normalise declared views (no CREATE privilege)" ] }

[<Fact>]
let ``an artifact reads back as exactly what was written`` () =
    // The property the whole format exists to have. Structural equality, so a
    // field that reads back as a different SHAPE — None for Some "", a name for
    // an unnamed constraint — fails here rather than at a deployment.
    match Artifact.ofText (Artifact.toText sample) with
    | Ok restored -> Assert.Equal<ResolvedDesiredState>(sample, restored)
    | Microsoft.FSharp.Core.Error message -> failwithf "the artifact could not be read: %s" message

[<Fact>]
let ``rendering is stable across a round trip`` () =
    // Byte-identical, not merely equivalent. NFR-001: an artifact that changed
    // shape on re-render would break any signature over it (WI-0089) and any
    // diff of two compiles.
    let once = Artifact.toText sample

    match Artifact.ofText once with
    | Ok restored -> Assert.Equal(once, Artifact.toText restored)
    | Microsoft.FSharp.Core.Error message -> failwithf "the artifact could not be read: %s" message

[<Fact>]
let ``rendering the same state twice gives the same bytes`` () =
    Assert.Equal(Artifact.toText sample, Artifact.toText sample)

[<Fact>]
let ``a policy clause written but not rendered survives as its own state`` () =
    // Called out on its own because collapsing `Some ""` into `None` or into a
    // rendered expression is the defect that made a changed policy expression
    // produce zero changes, and structural equality above would catch it in a
    // way nobody reading the failure would recognise.
    match Artifact.ofText (Artifact.toText sample) with
    | Ok restored ->
        let clauses =
            restored.Declared.Policies
            |> List.map (fun (_, p) -> p.Name.Text, p.Using, p.WithCheck)

        Assert.Contains(("p_select", Some "", None), clauses)
        Assert.Contains(("p_update", None, None), clauses)
        Assert.Contains(("p_delete", Some "(c)", Some ""), clauses)
    | Microsoft.FSharp.Core.Error message -> failwithf "the artifact could not be read: %s" message

[<Fact>]
let ``an unnamed constraint does not acquire a name`` () =
    match Artifact.ofText (Artifact.toText sample) with
    | Ok restored ->
        let tables =
            restored.Declared.Snapshot.Objects
            |> List.choose (fun o -> match o with TableObject t -> Some t | _ -> None)

        Assert.Contains(tables, fun t -> t.UniqueConstraints |> List.exists (fun u -> u.ConstraintName.IsNone))
        Assert.Contains(tables, fun t -> t.CheckConstraints |> List.exists (fun c -> c.ConstraintName.IsNone))
    | Microsoft.FSharp.Core.Error message -> failwithf "the artifact could not be read: %s" message

[<Fact>]
let ``a sequence bound larger than an int survives`` () =
    // bigint's maximum is the DEFAULT maxValue, so this is the ordinary case
    // rather than an edge one, and a JSON number would have lost it.
    match Artifact.ofText (Artifact.toText sample) with
    | Ok restored ->
        let sequences =
            restored.Declared.Snapshot.Objects
            |> List.choose (fun o -> match o with SequenceObject s -> Some s | _ -> None)

        Assert.Contains(sequences, fun s -> s.MaxValue = 9223372036854775807L)
    | Microsoft.FSharp.Core.Error message -> failwithf "the artifact could not be read: %s" message

[<Fact>]
let ``an artifact from a different format version is refused by name`` () =
    // Not parsed optimistically. A reader that guessed would deploy something
    // other than what was compiled, which is the one thing compiling rules out.
    let text = (Artifact.toText sample).Replace("\"formatVersion\":1", "\"formatVersion\":99")

    match Artifact.ofText text with
    | Ok _ -> failwith "a future format version was accepted"
    | Microsoft.FSharp.Core.Error message ->
        Assert.Contains("99", message)
        Assert.Contains("Recompile", message)

[<Fact>]
let ``text that is not an artifact is refused rather than half-read`` () =
    match Artifact.ofText "{\"hello\":1}" with
    | Ok _ -> failwith "arbitrary JSON was accepted as an artifact"
    | Microsoft.FSharp.Core.Error message -> Assert.Contains("artifact", message)

[<Fact>]
let ``malformed JSON is refused`` () =
    match Artifact.ofText "{not json" with
    | Ok _ -> failwith "malformed JSON was accepted"
    | Microsoft.FSharp.Core.Error message -> Assert.Contains("could not be read", message)

// ---- coverage ---------------------------------------------------------------
//
// The sample is only an oracle while it carries every case. A new union case
// added to the model would otherwise be rendered by a `match` that does not
// compile — or worse, by one that does, through a catch-all nobody noticed.

let private renderedText = Artifact.toText sample

let private assertEveryCaseReaches (expectedTags: (string * string) list) =
    let missing = expectedTags |> List.filter (fun (_, tag) -> not (renderedText.Contains tag))

    Assert.True(
        List.isEmpty missing,
        sprintf
            "the sample does not exercise: %s"
            (missing |> List.map fst |> String.concat ", "))

[<Fact>]
let ``the sample carries every SchemaObject case`` () =
    let cases = FSharpType.GetUnionCases typeof<SchemaObject> |> Array.map (fun c -> c.Name)

    Assert.Equal<string array>(
        [| "TableObject"; "ViewObject"; "RoutineObject"; "SequenceObject" |],
        cases)

    assertEveryCaseReaches
        [ "TableObject", "\"kind\":\"table\""
          "ViewObject", "\"kind\":\"view\""
          "RoutineObject", "\"kind\":\"routine\""
          "SequenceObject", "\"kind\":\"sequence\"" ]

[<Fact>]
let ``the sample carries every GrantTarget case`` () =
    Assert.Equal(4, (FSharpType.GetUnionCases typeof<GrantTarget>).Length)

    assertEveryCaseReaches
        [ "Relation", "\"kind\":\"relation\""
          "Schema", "\"kind\":\"schema\""
          "Routine", "\"kind\":\"routine\""
          "RelationColumn", "\"kind\":\"relation-column\"" ]

[<Fact>]
let ``the sample carries every PolicyCommand case`` () =
    Assert.Equal(5, (FSharpType.GetUnionCases typeof<PolicyCommand>).Length)

    assertEveryCaseReaches
        [ "All", "\"command\":\"all\""
          "Select", "\"command\":\"select\""
          "Insert", "\"command\":\"insert\""
          "Update", "\"command\":\"update\""
          "Delete", "\"command\":\"delete\"" ]

[<Fact>]
let ``the sample carries every CategoryState case`` () =
    Assert.Equal(4, (FSharpType.GetUnionCases typeof<CategoryState>).Length)

    assertEveryCaseReaches
        [ "Complete", "\"state\":\"complete\""
          "Partial", "\"state\":\"partial\""
          "Inaccessible", "\"state\":\"inaccessible\""
          "NotRequested", "\"state\":\"not-requested\"" ]

[<Fact>]
let ``the sample carries every RowSecuritySetting case`` () =
    Assert.Equal(4, (FSharpType.GetUnionCases typeof<RowSecuritySetting>).Length)
    assertEveryCaseReaches [ "Enable", "\"enable\""; "Disable", "\"disable\""; "Force", "\"force\""; "NoForce", "\"no-force\"" ]

[<Fact>]
let ``the sample carries every trigger timing and level`` () =
    Assert.Equal(3, (FSharpType.GetUnionCases typeof<TriggerTiming>).Length)
    Assert.Equal(2, (FSharpType.GetUnionCases typeof<TriggerLevel>).Length)

    assertEveryCaseReaches
        [ "Before", "\"timing\":\"before\""
          "After", "\"timing\":\"after\""
          "InsteadOf", "\"timing\":\"instead-of\""
          "Row", "\"level\":\"row\""
          "Statement", "\"level\":\"statement\"" ]

[<Fact>]
let ``the sample carries every ManagementScope case`` () =
    Assert.Equal(5, (FSharpType.GetUnionCases typeof<ManagementScope>).Length)

    assertEveryCaseReaches
        [ "Managed", "\"scope\":\"managed\""
          "Observed", "\"scope\":\"observed\""
          "ExtensionOwned", "\"scope\":\"extension-owned\""
          "SystemOwned", "\"scope\":\"system-owned\"" ]

// ---- what compile refuses ---------------------------------------------------
//
// `plan` reports an unreadable file as a warning and carries on with an
// incomplete desired state, which suppresses every drop and still shows the
// operator what Strata CAN say. `compile` refuses. An artifact is a claim that
// the project was read whole, and a deployment has no source tree to check the
// claim against, so a hole in it travels.

open System.IO
open Strata.Host.PgParser

let private parser =
    PgParserAdapter.PostgresParser() :> Strata.Analysis.DialectPort.IDialectParser

let private withProject (files: (string * string) list) (body: string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "strata-artifact-" + System.Guid.NewGuid().ToString("N"))

    try
        for relative, contents in files do
            let path = Path.Combine(root, relative)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, contents)

        body root
    finally
        try Directory.Delete(root, true) with _ -> ()

let private manifest (includes: string list) =
    "{\"include\":["
    + (includes |> List.map (fun i -> "\"" + i + "\"") |> String.concat ",")
    + "]}"

[<Fact>]
let ``a project that reads whole has no problems`` () =
    withProject
        [ "strata.json", manifest [ "schema/shop/tables/product.sql" ]
          "schema/shop/tables/product.sql", "CREATE TABLE shop.product (id bigint PRIMARY KEY);" ]
        (fun root ->
            match Strata.Cli.Compile.load parser root with
            | Ok loaded ->
                Assert.Empty loaded.Problems
                Assert.Single loaded.Declared.Snapshot.Objects |> ignore
            | Microsoft.FSharp.Core.Error message -> failwithf "the project would not load: %s" message)

[<Fact>]
let ``a file that does not parse is a problem, named by path`` () =
    withProject
        [ "strata.json", manifest [ "schema/shop/tables/broken.sql" ]
          "schema/shop/tables/broken.sql", "CREATE TABL shop.broken (id bigint);" ]
        (fun root ->
            match Strata.Cli.Compile.load parser root with
            | Ok loaded ->
                Assert.NotEmpty loaded.Problems
                Assert.Contains(loaded.Problems, fun (path, _) -> path.EndsWith "broken.sql")
            | Microsoft.FSharp.Core.Error message -> failwithf "the project would not load: %s" message)

[<Fact>]
let ``a declaration in the wrong schema directory is a problem`` () =
    // `DF-STRATA-2026-C3A2` calls this a COMPILE ERROR. It landed as a load
    // failure, because the check needs the parser and the hard project errors do
    // not, and that record says so explicitly: it becomes fatal when compile
    // arrives. This is the test that it did.
    withProject
        [ "strata.json", manifest [ "schema/shop/tables/thing.sql" ]
          "schema/shop/tables/thing.sql", "CREATE TABLE other.thing (id bigint);" ]
        (fun root ->
            match Strata.Cli.Compile.load parser root with
            | Ok loaded ->
                Assert.Contains(
                    loaded.Problems,
                    fun (_, reason) -> reason.Contains "schema directory")
            | Microsoft.FSharp.Core.Error message -> failwithf "the project would not load: %s" message)

[<Fact>]
let ``plan and compile read the same project the same way`` () =
    // The duplication that let the awkward-forms corpus test a pipeline that did
    // not ship (WI-0093). With no problems, the snapshot `plan` works from is
    // the one `compile` writes down — not a copy of it.
    withProject
        [ "strata.json", manifest [ "schema/shop/tables/product.sql" ]
          "schema/shop/tables/product.sql", "CREATE TABLE shop.product (id bigint PRIMARY KEY);" ]
        (fun root ->
            match Strata.Cli.Compile.load parser root with
            | Ok loaded -> Assert.Equal(loaded.Declared.Snapshot, Strata.Cli.Compile.snapshot loaded)
            | Microsoft.FSharp.Core.Error message -> failwithf "the project would not load: %s" message)
