module Strata.Tests.ProjectTests

open System.IO
open Xunit
open Strata.Host.Files

/// Tests for the project loader — `DF-STRATA-2026-7D14` and
/// `DF-STRATA-2026-C3A2`.
///
/// Strata is given a directory and OWNS everything inside it. Membership is not
/// a directory walk: `include` lists every object file, and any disagreement
/// between the list and the tree fails the load. The redundancy is the check —
/// a directory walk alone cannot notice that a file was added, because there is
/// nothing for it to disagree with.
///
/// Errors, never warnings. An automated build ignoring warnings is what a
/// warning IS, and both failure directions have a cheap resolution: list the
/// file, or move it out.

let private scratch () =
    let dir = Path.Combine(Path.GetTempPath(), "strata-project-" + System.Guid.NewGuid().ToString("N").Substring(0, 12))
    Directory.CreateDirectory dir |> ignore
    dir

/// Writes a project and returns its root. `files` are paths relative to the
/// root; `included` is what the manifest declares, which is deliberately
/// separate so a test can make them disagree.
let private project (files: (string * string) list) (included: string list) =
    let root = scratch ()

    for relative, contents in files do
        let full = Path.Combine(root, relative)
        Directory.CreateDirectory(Path.GetDirectoryName full) |> ignore
        File.WriteAllText(full, contents)

    let quoted = included |> List.map (sprintf "\"%s\"") |> String.concat ", "
    File.WriteAllText(Path.Combine(root, "strata.json"), sprintf """{ "include": [%s] }""" quoted)
    root

let private table = "CREATE TABLE app.customer (id bigint NOT NULL);\n"

let private error result =
    match result with
    | Ok _ -> failwith "expected the load to fail"
    | Microsoft.FSharp.Core.Error (message: string) -> message

// ---- membership -------------------------------------------------------------

[<Fact>]
let ``a listed file is loaded`` () =
    let root = project [ "schema/app/tables/customer.sql", table ] [ "schema/app/tables/customer.sql" ]

    match Project.read root with
    | Microsoft.FSharp.Core.Error m -> failwithf "expected success, got %s" m
    | Ok p ->
        Assert.Single p.Files |> ignore
        Assert.Equal<string list>([ "app" ], p.Schemas)

[<Fact>]
let ``a file on disk that the manifest does not list fails the load`` () =
    // THE rule. A directory walk would have silently included this; that is
    // exactly what an agent dropping a file into the tree looks like.
    let root =
        project
            [ "schema/app/tables/customer.sql", table
              "schema/app/tables/sneaky.sql", "CREATE TABLE app.sneaky (id bigint);\n" ]
            [ "schema/app/tables/customer.sql" ]

    let message = error (Project.read root)
    Assert.Contains("not listed", message)
    Assert.Contains("sneaky.sql", message)

[<Fact>]
let ``a listed path that does not exist fails the load`` () =
    let root =
        project
            [ "schema/app/tables/customer.sql", table ]
            [ "schema/app/tables/customer.sql"; "schema/app/tables/ghost.sql" ]

    let message = error (Project.read root)
    Assert.Contains("do not exist", message)
    Assert.Contains("ghost.sql", message)

[<Fact>]
let ``both directions are reported together, not one at a time`` () =
    // A loader that stopped at the first problem would make adoption a series
    // of one-error-at-a-time rounds.
    let root =
        project
            [ "schema/app/tables/customer.sql", table
              "schema/app/tables/extra.sql", table ]
            [ "schema/app/tables/customer.sql"; "schema/app/tables/ghost.sql" ]

    let message = error (Project.read root)
    Assert.Contains("extra.sql", message)
    Assert.Contains("ghost.sql", message)

[<Fact>]
let ``a non-SQL file in the directory is not Strata's business`` () =
    // Scope is *.sql only, or the first README breaks the build. Merge and
    // editor leftovers do not match the extension either.
    let root =
        project
            [ "schema/app/tables/customer.sql", table
              "schema/README.md", "notes\n"
              "schema/app/tables/customer.sql.orig", "<<<<<<< HEAD\n" ]
            [ "schema/app/tables/customer.sql" ]

    match Project.read root with
    | Microsoft.FSharp.Core.Error m -> failwithf "expected success, got %s" m
    | Ok p -> Assert.Single p.Files |> ignore

// ---- the manifest itself ----------------------------------------------------

[<Fact>]
let ``a project with no strata.json is refused`` () =
    let root = scratch ()
    Directory.CreateDirectory(Path.Combine(root, "schema", "app", "tables")) |> ignore
    File.WriteAllText(Path.Combine(root, "schema", "app", "tables", "c.sql"), table)

    Assert.Contains("no strata.json", error (Project.read root))

[<Fact>]
let ``a manifest without an include key is refused, and says what to run`` () =
    let root = scratch ()
    File.WriteAllText(Path.Combine(root, "strata.json"), """{ "schemaRoot": "schema" }""")

    let message = error (Project.read root)
    Assert.Contains("must declare", message)
    Assert.Contains("init --from-tree", message)

[<Fact>]
let ``an empty include is refused with its own message`` () =
    // Absent and empty are different states. Both are errors, and a project that
    // forgot the key needs a different sentence from one that listed nothing.
    let root = scratch ()
    File.WriteAllText(Path.Combine(root, "strata.json"), """{ "include": [] }""")

    Assert.Contains("empty", error (Project.read root))

[<Fact>]
let ``a path listed twice is refused`` () =
    let root =
        project
            [ "schema/app/tables/customer.sql", table ]
            [ "schema/app/tables/customer.sql"; "schema/app/tables/customer.sql" ]

    Assert.Contains("more than once", error (Project.read root))

// ---- schemas ----------------------------------------------------------------

[<Fact>]
let ``the schema set is derived from the directories that hold included files`` () =
    let root =
        project
            [ "schema/app/tables/customer.sql", table
              "schema/ref/tables/kind.sql", "CREATE TABLE ref.kind (id bigint);\n" ]
            [ "schema/app/tables/customer.sql"; "schema/ref/tables/kind.sql" ]

    match Project.read root with
    | Microsoft.FSharp.Core.Error m -> failwithf "expected success, got %s" m
    | Ok p -> Assert.Equal<string list>([ "app"; "ref" ], p.Schemas)

[<Fact>]
let ``an empty schema directory is refused`` () =
    // This is what makes removing `managedSchemas` safe. Without it, the
    // "this schema should be empty" hazard just moves from the manifest to the
    // filesystem — and an empty managed schema is authority to drop everything
    // in it.
    let root = project [ "schema/app/tables/customer.sql", table ] [ "schema/app/tables/customer.sql" ]
    Directory.CreateDirectory(Path.Combine(root, "schema", "leftover")) |> ignore

    let message = error (Project.read root)
    Assert.Contains("declare no object files", message)
    Assert.Contains("leftover", message)

[<Theory>]
[<InlineData("Orders")>]      // uppercase: collides with `orders` on macOS
[<InlineData("café")>]        // non-ASCII: NFC and NFD differ across platforms
[<InlineData("1st")>]         // not a valid unquoted identifier
[<InlineData("my-schema")>]   // hyphen is outside the portable set
let ``a schema directory name outside the portable set is refused`` (name: string) =
    let root =
        project
            [ sprintf "schema/%s/tables/t.sql" name, sprintf "CREATE TABLE \"%s\".t (id bigint);\n" name ]
            [ sprintf "schema/%s/tables/t.sql" name ]

    Assert.Contains("cannot be managed by Strata", error (Project.read root))

[<Fact>]
let ``a Windows device name is refused even though PostgreSQL allows it`` () =
    // `aux` is a legal schema name and an illegal directory name on Windows.
    // The pattern alone does not catch it, so it needs its own list.
    let root =
        project
            [ "schema/aux/tables/t.sql", "CREATE TABLE aux.t (id bigint);\n" ]
            [ "schema/aux/tables/t.sql" ]

    Assert.Contains("reserved device name", error (Project.read root))
