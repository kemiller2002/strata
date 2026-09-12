module Strata.Tests.ManifestTests

open System.IO
open System.Text.Json
open Xunit
open Strata.Host.Files

/// Writing and reconciling `strata.json`.
///
/// `DF-STRATA-2026-7D14` made the project directory closed: every `.sql` file
/// under the schema root must be listed, and every listed path must exist. The
/// cost is that every new object needs a manifest line, with zero leniency —
/// and without a way to write that line, the pressure to add a glob or a lenient
/// mode becomes irresistible, and both give the property straight back. These
/// commands are part of that decision rather than a convenience on top of it.

// ---- reconciling ------------------------------------------------------------

[<Fact>]
let ``a manifest that matches the tree needs nothing`` () =
    let result = Manifests.reconcile [ "a.sql"; "b.sql" ] [ "a.sql"; "b.sql" ]
    Assert.True(Manifests.isClean result)

[<Fact>]
let ``a file on disk and not listed would be added`` () =
    let result = Manifests.reconcile [ "a.sql" ] [ "a.sql"; "b.sql" ]
    Assert.Equal<string list>([ "b.sql" ], result.ToAdd)
    Assert.Empty result.ToRemove

[<Fact>]
let ``an entry naming a file that is gone would be removed`` () =
    // The other direction, and it is not symmetric in meaning: the file has
    // already left, so this removes a line that refers to nothing.
    let result = Manifests.reconcile [ "a.sql"; "b.sql" ] [ "a.sql" ]
    Assert.Equal<string list>([ "b.sql" ], result.ToRemove)
    Assert.Empty result.ToAdd

[<Fact>]
let ``both directions are reported in one pass`` () =
    // A reconciliation that reported one direction at a time would make the
    // author run it twice to see the whole picture.
    let result = Manifests.reconcile [ "a.sql"; "gone.sql" ] [ "a.sql"; "new.sql" ]
    Assert.Equal<string list>([ "new.sql" ], result.ToAdd)
    Assert.Equal<string list>([ "gone.sql" ], result.ToRemove)

[<Fact>]
let ``separators are normalised before comparing`` () =
    // A manifest written on Windows and read on Linux must not report every
    // file as both added and removed.
    Assert.True(Manifests.isClean (Manifests.reconcile [ "schema\\a\\b.sql" ] [ "schema/a/b.sql" ]))

// ---- rendering --------------------------------------------------------------

let private parse (text: string) = JsonDocument.Parse(text).RootElement

[<Fact>]
let ``a rendered manifest is valid JSON with sorted includes`` () =
    // Sorted so two people adding different files do not conflict on one line.
    let rendered = Manifests.render None [ "z.sql"; "a.sql"; "m.sql" ]
    let root = parse rendered

    Assert.Equal<string list>(
        [ "a.sql"; "m.sql"; "z.sql" ],
        (root.GetProperty "include").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq)

[<Fact>]
let ``rendering preserves every other setting verbatim`` () =
    // Round-tripping through a parsed record would quietly drop a key this build
    // does not know, which is how a project loses a setting a newer Strata added.
    let existing = parse """{"include":["old.sql"],"schemaRoot":"db","corpusRoots":["../app"],"somethingNewer":{"a":1}}"""
    let rendered = Manifests.render (Some existing) [ "new.sql" ]
    let root = parse rendered

    Assert.Equal("db", (root.GetProperty "schemaRoot").GetString())
    Assert.Equal(1, (root.GetProperty "somethingNewer").GetProperty("a").GetInt32())

    Assert.Equal<string list>(
        [ "new.sql" ],
        (root.GetProperty "include").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq)

[<Fact>]
let ``a path with a quote or a backslash survives rendering`` () =
    // Hand-written JSON, so escaping is this module's problem.
    let awkward = "schema/a/\"odd\" name.sql"
    let root = parse (Manifests.render None [ awkward ])

    Assert.Equal<string list>(
        [ awkward ],
        (root.GetProperty "include").EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq)

// ---- listing the tree -------------------------------------------------------

let private withTree (files: string list) (body: string -> string -> unit) =
    let root = Path.Combine(Path.GetTempPath(), "strata-manifest-" + System.Guid.NewGuid().ToString("N"))

    try
        for relative in files do
            let path = Path.Combine(root, relative)
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
            File.WriteAllText(path, "-- fixture")

        body root (Path.Combine(root, "schema"))
    finally
        try Directory.Delete(root, true) with _ -> ()

[<Fact>]
let ``only sql files are listed`` () =
    // A README must not break a build, and `customer.sql.orig` left by a merge
    // is not an object. Same scope the loader enforces, so `init` cannot write a
    // manifest the loader then rejects.
    withTree
        [ "schema/shop/tables/a.sql"
          "schema/shop/README.md"
          "schema/shop/tables/a.sql.orig" ]
        (fun root schemaRoot ->
            Assert.Equal<string list>([ "schema/shop/tables/a.sql" ], Manifests.objectFiles root schemaRoot))

[<Fact>]
let ``files are listed from every depth, sorted`` () =
    withTree
        [ "schema/shop/tables/b.sql"; "schema/shop/views/a.sql"; "schema/other/tables/c.sql" ]
        (fun root schemaRoot ->
            Assert.Equal<string list>(
                [ "schema/other/tables/c.sql"; "schema/shop/tables/b.sql"; "schema/shop/views/a.sql" ],
                Manifests.objectFiles root schemaRoot))

[<Fact>]
let ``a missing schema root lists nothing rather than throwing`` () =
    Assert.Empty(Manifests.objectFiles "/nonexistent" "/nonexistent/schema")
