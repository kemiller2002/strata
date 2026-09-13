/// Shares the live database with CatalogIntrospectionTests — see the note there.
[<Xunit.Collection("live-database")>]
module Strata.Tests.AwkwardFormsTests

open System
open System.IO
open Xunit
open Npgsql
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Application
open Strata.Host.PgParser
open Strata.Host.Postgres

/// Holds Strata's reading of a declaration to the SERVER's reading of the same
/// text.
///
/// ## Why this exists, and why it is not more unit tests
///
/// Two defects shipped green in one afternoon. An `IDENTITY` column was read as
/// nullable, so any table with one could never converge. A column-level `GRANT`
/// was read as a table-wide one, so Strata would have handed out access to
/// columns the file withheld — and executed it. Both passed the suite, and both
/// passed a manual round-trip, because every round-trip I ran used the shapes I
/// had in mind while writing the code. That is exactly why they passed.
///
/// A unit test cannot close that gap on its own: an expectation written by the
/// author of the code can encode the same misunderstanding as the code. It
/// already did once — the completeness-category defect, where the fixture used
/// the same invented names the code did, so test and code agreed and both were
/// wrong. Only a live database found it.
///
/// So there are no hand-written expectations here. Each file under
/// `awkward/converges/` is executed against a real PostgreSQL, introspected
/// back, loaded through the parser, and diffed. The assertion is that the diff
/// is EMPTY: whatever the server made of the text, Strata read the same thing.
/// The server is the oracle.
///
/// Each file under `awkward/refused/` must produce a load FAILURE. That is the
/// other half, and it is the half the grant defect needed: a declaration Strata
/// does not model must be refused out loud, never quietly reinterpreted as
/// something it can model. "Loads, but means something else" is the bug class
/// this whole file exists to make impossible to ship.
module private Fixture =

    let private connectionString =
        match Environment.GetEnvironmentVariable "STRATA_TEST_PG" with
        | null | "" -> None
        | value -> Some value

    let connection = connectionString

    /// Where the corpus lives, relative to the test assembly.
    let directory (kind: string) =
        Path.Combine(AppContext.BaseDirectory, "awkward", kind)

    let cases (kind: string) : obj array seq =
        let dir = directory kind

        if Directory.Exists dir then
            let files = Directory.GetFiles(dir, "*.sql") |> Array.map Path.GetFileName
            let directories = Directory.GetDirectories dir |> Array.map Path.GetFileName

            Array.append files directories
            |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> Array.map (fun name -> [| box name |])
            |> Seq.ofArray
        else
            Seq.empty

    /// A case's declarations, in the order they must be applied.
    ///
    /// A case is either ONE `.sql` file or a DIRECTORY of them, and the
    /// difference is load-bearing rather than cosmetic. `DesiredState` records
    /// a file's verbatim text only when the file declares exactly one object —
    /// executing a two-table file to create one of them would create the other
    /// as a side effect — and normalisation needs that verbatim text. So a
    /// single-file case declaring a table AND a view records nothing, and the
    /// view is never normalised, never compared, and passes with an empty
    /// change list.
    ///
    /// That is not a hypothetical. It is why this corpus never once exercised
    /// view or policy normalisation, and why WI-0092 sat here undetected: a
    /// view needs a table, a table and a view cannot share a file, and the
    /// corpus had no way to express two files. Now it does, and a case that
    /// needs more than one object is a directory — which is how real projects
    /// are laid out anyway.
    let parts (kind: string) (name: string) : (string * string) list =
        let path = Path.Combine(directory kind, name)

        if Directory.Exists path then
            Directory.GetFiles(path, "*.sql")
            |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> Array.map (fun f -> Path.GetFileName f, File.ReadAllText f)
            |> Array.toList
        else
            [ name, File.ReadAllText path ]

    let read (kind: string) (name: string) = File.ReadAllText(Path.Combine(directory kind, name))

    /// A case may declare that some property of it CANNOT be compared, with the
    /// reason, by putting `-- NOT-COMPARED: <why>` in any of its parts.
    ///
    /// Some limits are real. A `serial` column's default renders as
    /// `nextval('<table>_<column>_seq')`, and the shadow table has a different
    /// name, so the two renderings can never match and the diff says so instead
    /// of churning forever. A fixture covering that shape must be allowed to
    /// leave a not-compared disclosure behind.
    ///
    /// It is an allowance, not an exemption: a case that declares one and then
    /// produces no such disclosure fails too, so the annotation cannot outlive
    /// the limit it documents.
    let notComparedReasons (parts: (string * string) list) =
        parts
        |> List.collect (fun (_, text) -> text.Split('\n') |> Array.toList)
        |> List.choose (fun line ->
            let trimmed = line.Trim()

            if trimmed.StartsWith "-- NOT-COMPARED:" then
                Some(trimmed.Substring("-- NOT-COMPARED:".Length).Trim())
            else
                None)

    /// The scratch schema every case is built in.
    ///
    /// A fixed name, not a unique one, so the fixture text can name it verbatim
    /// and the SAME characters go to the server and to the parser. Substituting
    /// a generated name into one path and not the other is exactly the kind of
    /// difference this file exists to catch.
    let schema = "awk"

    let exec (sql: string) =
        use c = new NpgsqlConnection(connectionString.Value)
        c.Open()
        use command = new NpgsqlCommand(sql, c)
        command.ExecuteNonQuery() |> ignore

    let reset () =
        exec (sprintf "DROP SCHEMA IF EXISTS %s CASCADE" schema)
        exec (sprintf "CREATE SCHEMA %s" schema)

    let drop () =
        try exec (sprintf "DROP SCHEMA IF EXISTS %s CASCADE" schema) with _ -> ()

    /// The grantee roles the grant cases need are created by
    /// `scripts/test-fixture.sh`, not here: creating a role needs a superuser,
    /// and the test role deliberately is not one — three other tests depend on
    /// it not being one.

type RequiresPostgresAttribute() =
    inherit FactAttribute()
    do
        base.Skip <-
            match Fixture.connection with
            | Some _ -> null
            | None ->
                "STRATA_TEST_PG not set; live PostgreSQL integration test skipped. \
                 Build the fixture with scripts/test-fixture.sh and set the connection string it prints."

type RequiresPostgresTheoryAttribute() =
    inherit TheoryAttribute()
    do
        base.Skip <-
            match Fixture.connection with
            | Some _ -> null
            | None ->
                "STRATA_TEST_PG not set; live PostgreSQL integration test skipped. \
                 Build the fixture with scripts/test-fixture.sh and set the connection string it prints."

let private parser =
    PgParserAdapter.PostgresParser() :> Strata.Analysis.DialectPort.IDialectParser

let convergesCases () = Fixture.cases "converges"
let refusedCases () = Fixture.cases "refused"

[<RequiresPostgresTheory; MemberData(nameof convergesCases)>]
let ``Strata reads the declaration the way the server does`` (name: string) =
    let parts = Fixture.parts "converges" name

    // ONE connection string for both sides, and it carries the same
    // `search_path` the fixture text ran under.
    //
    // Both matter. Without the search path an unqualified view body does not
    // resolve at all and the view falls out of the comparison — silently, which
    // is the failure this case exists to catch. With it on only one side, the
    // server deparses `FROM shipment` for one and `FROM awk.shipment` for the
    // other and every view looks changed. A real project qualifies its names and
    // uses one connection for both; this reproduces that condition rather than
    // inventing a third one.
    let connection = sprintf "%s;Search Path=%s,public" Fixture.connection.Value Fixture.schema

    Fixture.reset ()

    try
        // The server's reading. `search_path` puts unqualified names in the
        // scratch schema, so the fixture text runs exactly as written. A
        // directory case applies its parts in filename order, so a view can
        // name the table declared before it.
        for _, sql in parts do
            Fixture.exec (sprintf "SET search_path TO %s; %s" Fixture.schema sql)

        let actual = CatalogIntrospection.introspect connection

        let inScratch (snapshot: SchemaSnapshot) =
            { snapshot with
                Objects =
                    snapshot.Objects
                    |> List.filter (fun o ->
                        match (SchemaObject.name o).Schema with
                        | Some s -> Identifier.folded s = Fixture.schema
                        | None -> false) }

        // Strata's reading of the same characters. The loader sees unqualified
        // names, so they are qualified with the scratch schema to line the two
        // sides up — the only transformation applied, and applied after parsing
        // rather than to the text.
        let declared = DesiredState.load parser parts

        Assert.Empty declared.Failures

        let qualify (n: QualifiedName) =
            match n.Schema with
            | Some _ -> n
            | None -> QualifiedName.qualified (Identifier.unquoted Fixture.schema) n.Name

        let requalify (o: SchemaObject) =
            match o with
            | TableObject t ->
                TableObject
                    { t with
                        Name = qualify t.Name
                        ForeignKeys = t.ForeignKeys |> List.map (fun f -> { f with ReferencedTable = qualify f.ReferencedTable })
                        Triggers = t.Triggers |> List.map (fun g -> { g with Function = qualify g.Function }) }
            | ViewObject v -> ViewObject { v with Name = qualify v.Name }
            | RoutineObject r -> RoutineObject { r with Name = qualify r.Name }
            | SequenceObject s -> SequenceObject { s with Name = qualify s.Name }
            | EnumObject e -> EnumObject { e with Name = qualify e.Name }

        let desired =
            { declared.Snapshot with Objects = declared.Snapshot.Objects |> List.map requalify }


        // The loader sees unqualified names, so a declared grant is qualified
        // with the scratch schema the same way an object is. A SCHEMA grant
        // already names its schema and needs no qualifying — qualifying it
        // would name a schema inside a schema, which does not exist.
        let grants =
            declared.Grants
            |> List.map (fun g ->
                match g.Target with
                | GrantTarget.Relation n -> { g with Target = GrantTarget.Relation(qualify n) }
                | GrantTarget.Routine (n, args) -> { g with Target = GrantTarget.Routine(qualify n, args) }
                | GrantTarget.RelationColumn (n, c) -> { g with Target = GrantTarget.RelationColumn(qualify n, c) }
                | GrantTarget.Schema _ -> g)

        let actualGrants =
            match CatalogIntrospection.readGrants connection with
            | Ok gs ->
                Some(
                    gs
                    |> List.filter (fun g ->
                        match GrantTarget.schema g.Target with
                        | Some s -> Identifier.folded s = Fixture.schema
                        | None -> false))
            | Microsoft.FSharp.Core.Error _ -> None

        // Everything the server has to render, rendered — through the SAME
        // function `strata plan` calls, not a copy of part of it.
        //
        // This harness used to hand-roll a subset: it normalised tables and
        // skipped views and policies entirely. That is how WI-0092 hid. A view
        // whose `AS` was followed by a newline was never compared, and the
        // corpus could not notice, because the corpus never compared views at
        // all. A test that reimplements the pipeline tests a pipeline that does
        // not ship.
        //
        let qualifiedDeclared =
            { declared with
                Snapshot = desired
                Declarations = declared.Declarations |> List.map (fun (n, text) -> qualify n, text)
                Policies = declared.Policies |> List.map (fun (table, policy) -> qualify table, policy)
                PolicyDeclarations =
                    declared.PolicyDeclarations
                    |> List.map (fun ((table, name), text) -> (qualify table, name), text)
                RowSecurity = declared.RowSecurity |> List.map (fun (table, setting) -> qualify table, setting)
                Data = declared.Data |> List.map (fun d -> { d with Table = qualify d.Table }) }

        let resolved = Strata.Cli.Resolution.resolve connection qualifiedDeclared

        // A fixture whose normalisation failed proves nothing about how Strata
        // reads it, so the failure is the test result rather than a footnote.
        Assert.Empty resolved.Warnings

        let actualRowLevelSecurity =
            match CatalogIntrospection.readRowLevelSecurity connection with
            | Ok entries ->
                Some(
                    Ok(
                        entries
                        |> List.filter (fun e ->
                            match e.Table.Schema with
                            | Some s -> Identifier.folded s = Fixture.schema
                            | None -> false)))
            | Microsoft.FSharp.Core.Error message -> Some(Microsoft.FSharp.Core.Error message)

        // Extensions are not scoped to a schema, so both sides pass through
        // whole. A fixture declaring one it already has must converge; one
        // declaring a version it does not have must not.
        let actualExtensions =
            match CatalogIntrospection.readExtensions connection with
            | Ok installed -> Some(Ok installed)
            | Microsoft.FSharp.Core.Error message -> Some(Microsoft.FSharp.Core.Error message)

        let result =
            SchemaDiff.run
                { SchemaDiff.Inputs.between desired (inScratch actual) with
                    DeclaredExtensions = declared.Extensions
                    ActualExtensions = actualExtensions
                    DeclaredPolicies = resolved.Policies
                    DeclaredRowSecurity = resolved.RowSecurity
                    ActualRowLevelSecurity = actualRowLevelSecurity
                    // Drops ENABLED. A declaration Strata reads as something the
                    // server did not build shows up as a removal, and this must
                    // see it rather than have it suppressed.
                    AllowDrops = true
                    ManagedSchemas = [ Fixture.schema ]
                    ExistingSchemas = Some [ Fixture.schema ]
                    DeclaredGrants = grants
                    ActualGrants = actualGrants
                    Data = resolved.Data
                    DataFailures = resolved.DataFailures
                    NormalisedViews = resolved.NormalisedViews
                    NormalisedTables = resolved.NormalisedTables }

        // Rendered so a failure names what diverged rather than just counting.
        // `SchemaDiff.describe` is private, and it stays that way: widening
        // production API for a test's error message is not a trade worth making.
        let rendered =
            result.Changes
            |> List.map (fun c ->
                let target =
                    Strata.Analysis.ProposedChange.Change.target c
                    |> Option.map QualifiedName.display
                    |> Option.defaultValue "-"

                sprintf "%s %s" (Strata.Analysis.ProposedChange.Change.tag c) target)

        Assert.Empty rendered

        // The other half of the oracle, and the half that was missing.
        //
        // "The change list is empty" is necessary and NOT sufficient. A
        // comparison that never happened produces an empty change list too, so
        // for six months this corpus could not tell a fixture Strata read
        // correctly from a fixture Strata did not read at all. WI-0092 was
        // exactly that: a view dropped from normalisation over a newline,
        // reported as not-compared, and passing here.
        //
        // So a fixture must also leave nothing it declares in the not-compared
        // list. `NotModelled` is a different statement — "this exists and the
        // project says nothing about it" — and is left alone.
        let declaredNames =
            desired.Objects
            |> List.map (fun o -> QualifiedName.display (SchemaObject.name o))
            |> Set.ofList

        let silentlySkipped =
            result.Suppressed
            |> List.filter (fun sup ->
                sup.Reason = SchemaDiff.SuppressionReason.NotCompared
                && declaredNames.Contains(QualifiedName.display sup.Object))
            |> List.map (fun sup -> sprintf "%s: %s" (QualifiedName.display sup.Object) sup.Detail)

        match Fixture.notComparedReasons parts with
        | [] ->
            Assert.True(
                List.isEmpty silentlySkipped,
                sprintf
                    "%s declares objects Strata did not compare, so an empty change list proves nothing about them:\n  %s\n\nIf one of these cannot be compared, say so in the fixture with a `-- NOT-COMPARED: <why>` line."
                    name
                    (String.concat "\n  " silentlySkipped))
        | reasons ->
            // The allowance must be used, or it is documenting a limit that no
            // longer exists and hiding the next one.
            Assert.True(
                not (List.isEmpty silentlySkipped),
                sprintf
                    "%s declares NOT-COMPARED (%s) but everything it declares was compared. Remove the line."
                    name
                    (String.concat "; " reasons))
    finally
        Fixture.drop ()

[<RequiresPostgresTheory; MemberData(nameof refusedCases)>]
let ``a form Strata does not model is refused, never reinterpreted`` (name: string) =
    // The half the grant defect needed. `GRANT SELECT (id) ON t` loaded happily
    // as a table-wide grant; nothing failed, so nothing complained. A form
    // Strata cannot represent has to say so.
    let sql = Fixture.read "refused" name
    let declared = DesiredState.load parser [ name, sql ]

    Assert.NotEmpty declared.Failures

    // And the reason has to say something. An empty or generic reason is how a
    // refusal becomes indistinguishable from a parse error.
    Assert.All(
        declared.Failures,
        fun f ->
            Assert.False(String.IsNullOrWhiteSpace f.Reason, "a refusal must carry a reason")
            Assert.True(f.Reason.Length > 20, sprintf "the reason is too vague to act on: %s" f.Reason))

[<Fact>]
let ``every fixture states its contract in the file`` () =
    // The directory name is otherwise the only thing saying what a case
    // asserts, and someone adding a case reads the file rather than the
    // harness. Checked rather than documented so the header cannot rot: a new
    // fixture without one fails here instead of quietly asserting whatever its
    // directory happens to mean.
    //
    // Not a `RequiresPostgres` fact — this needs no database, and skipping it
    // when there is none would leave the corpus unchecked in exactly the
    // environment where nobody notices.
    let missing =
        [ for kind in [ "converges"; "refused" ] do
            for case in Fixture.cases kind do
                let name = string (Array.head case)

                // A directory case states its contract in its first part, the
                // one that declares the object the rest hang off.
                match Fixture.parts kind name with
                | [] -> yield sprintf "%s/%s (no .sql parts)" kind name
                | (_, text) :: _ when not (text.StartsWith "-- CONTRACT:") -> yield sprintf "%s/%s" kind name
                | _ -> () ]

    Assert.Empty missing

[<RequiresPostgres>]
let ``the corpus is not empty`` () =
    // A directory that failed to copy to the output would make every theory
    // above vacuously green, which is the failure mode this whole file is about.
    Assert.NotEmpty(convergesCases ())
    Assert.NotEmpty(refusedCases ())
