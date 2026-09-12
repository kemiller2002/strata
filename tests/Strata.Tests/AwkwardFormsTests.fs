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
            Directory.GetFiles(dir, "*.sql")
            |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> Array.map (fun path -> [| box (Path.GetFileName path) |])
            |> Seq.ofArray
        else
            Seq.empty

    let read (kind: string) (name: string) = File.ReadAllText(Path.Combine(directory kind, name))

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
    let sql = Fixture.read "converges" name
    let connection = Fixture.connection.Value

    Fixture.reset ()

    try
        // The server's reading. `search_path` puts unqualified names in the
        // scratch schema, so the fixture text runs exactly as written.
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
        let declared = DesiredState.load parser [ name, sql ]

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

        let desired =
            { declared.Snapshot with Objects = declared.Snapshot.Objects |> List.map requalify }

        // Defaults and check expressions are only comparable once the server has
        // rendered them, exactly as `strata plan` does it.
        let normalisedTables =
            declared.Snapshot.Objects
            |> List.choose (fun o ->
                match o with
                | TableObject t ->
                    declared.Declarations
                    |> List.tryPick (fun (n, text) ->
                        if QualifiedName.display n = QualifiedName.display t.Name then
                            Some(QualifiedName.display (qualify t.Name), text)
                        else
                            None)
                | _ -> None)
            |> fun tables ->
                match ShadowNormalisation.normaliseTables connection tables with
                | Ok normalised ->
                    normalised
                    |> List.map (fun n ->
                        ({ Table = n.Table; Defaults = n.Defaults; Checks = n.Checks }: SchemaDiff.NormalisedTable))
                | Microsoft.FSharp.Core.Error _ -> []

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

        let result =
            SchemaDiff.run
                // Drops ENABLED. A declaration Strata reads as something the
                // server did not build shows up as a removal, and this must see
                // it rather than have it suppressed.
                true
                [ Fixture.schema ]
                []
                []
                (Some [ Fixture.schema ])
                [ Fixture.schema ]
                grants
                actualGrants
                []
                []
                []
                normalisedTables
                []
                desired
                (inScratch actual)

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

                if not ((Fixture.read kind name).StartsWith "-- CONTRACT:") then
                    yield sprintf "%s/%s" kind name ]

    Assert.Empty missing

[<RequiresPostgres>]
let ``the corpus is not empty`` () =
    // A directory that failed to copy to the output would make every theory
    // above vacuously green, which is the failure mode this whole file is about.
    Assert.NotEmpty(convergesCases ())
    Assert.NotEmpty(refusedCases ())
