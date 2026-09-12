/// `strata init`, `strata add` and `strata sync`.
///
/// Authority for: how a closed project's manifest is written and kept current.
///
/// These need no database. A manifest is a statement about the FILES, and the
/// commands that maintain it should work in a checkout with no credentials —
/// which is where someone adding a table actually is.
module Strata.Cli.ProjectCommands

open System.IO
open System.Text.Json
open Strata.Host.Files

let private manifestPath (root: string) = Path.Combine(root, "strata.json")

let private schemaRootOf (root: string) (manifest: Project.Manifest option) =
    match manifest with
    | Some m -> Path.Combine(root, m.SchemaRoot)
    | None ->
        let conventional = Path.Combine(root, "schema")
        if Directory.Exists conventional then conventional else root

/// Read the manifest as raw JSON, so `init` and `sync` can rewrite `include`
/// while leaving every other setting exactly as the author wrote it.
///
/// Round-tripping through a parsed record would quietly drop a key this build
/// does not know — which is how a project loses a setting a newer Strata added.
let private rawManifest (root: string) =
    let path = manifestPath root

    if not (File.Exists path) then
        None
    else
        try
            let document = JsonDocument.Parse(File.ReadAllText path)

            if document.RootElement.ValueKind = JsonValueKind.Object then
                Some document.RootElement
            else
                None
        with _ ->
            None

let private declaredIn (root: string) =
    match Project.read root with
    | Ok project -> project.Manifest.Include
    | Error _ ->
        // The manifest may be unreadable precisely BECAUSE it disagrees with the
        // tree, which is the situation `sync` exists for. So the include list is
        // read directly rather than through the loader's agreement check.
        match rawManifest root with
        | None -> []
        | Some element ->
            match element.TryGetProperty "include" with
            | true, array when array.ValueKind = JsonValueKind.Array ->
                array.EnumerateArray() |> Seq.map (fun e -> e.GetString()) |> List.ofSeq
            | _ -> []

let private currentSchemaRoot (root: string) =
    match rawManifest root with
    | Some element ->
        match element.TryGetProperty "schemaRoot" with
        | true, v when v.ValueKind = JsonValueKind.String -> Path.Combine(root, v.GetString())
        | _ -> schemaRootOf root None
    | None -> schemaRootOf root None

/// Write a manifest from what is on disk.
///
/// Refuses to overwrite an existing one without `--force`: the include list is a
/// decision someone made, and `init` is for projects that have not made it yet.
/// `sync` is the command for one that has.
let init (root: string) (force: bool) : int =
    let path = manifestPath root
    let schemaRoot = currentSchemaRoot root

    if File.Exists path && not force then
        eprintfn "error: %s already exists." path
        eprintfn "       `strata sync` reconciles an existing manifest with the tree and shows what it"
        eprintfn "       would change. Pass --force to overwrite this one instead."
        2
    elif not (Directory.Exists schemaRoot) then
        eprintfn "error: no schema directory at %s. A project is a tree of object files." schemaRoot
        2
    else

    let files = Manifests.objectFiles root schemaRoot

    if List.isEmpty files then
        eprintfn "error: no .sql files under %s, so there is nothing to declare." schemaRoot
        2
    else

    File.WriteAllText(path, Manifests.render (rawManifest root) files)

    printfn "Wrote %s with %d object file(s):" path (List.length files)

    for file in files do
        printfn "  %s" file

    printfn ""
    printfn "Read it. From here on a .sql file this does not list FAILS the build, which is"
    printfn "the point: a directory walk cannot notice that a file was added."
    0

/// Append paths to `include`.
///
/// Each must exist and sit under the schema root — a manifest line for a file
/// that is not there is one of the two errors `Project.read` raises, so writing
/// one would be adding a known failure.
let add (root: string) (paths: string list) : int =
    if List.isEmpty paths then
        eprintfn "error: add needs at least one path, relative to the project root."
        2
    elif not (File.Exists(manifestPath root)) then
        eprintfn "error: no strata.json in %s. Run `strata init --from-tree` first." root
        2
    else

    let schemaRoot = Path.GetFullPath(currentSchemaRoot root)
    let declared = declaredIn root

    let normalise (p: string) = p.Replace('\\', '/')

    let problems =
        paths
        |> List.choose (fun p ->
            let full = Path.GetFullPath(Path.Combine(root, p))

            if not (File.Exists full) then Some(p, "no such file")
            elif not (full.StartsWith schemaRoot) then Some(p, sprintf "is not under the schema root %s" schemaRoot)
            elif Path.GetExtension(full).ToLowerInvariant() <> ".sql" then Some(p, "is not a .sql file")
            elif declared |> List.map normalise |> List.contains (normalise p) then Some(p, "is already listed")
            else None)

    if not (List.isEmpty problems) then
        eprintfn "error: %d path(s) cannot be added:" (List.length problems)

        for path, reason in problems do
            eprintfn "  %s: %s" path reason

        2
    else

    let updated = declared @ (paths |> List.map normalise)
    File.WriteAllText(manifestPath root, Manifests.render (rawManifest root) updated)

    printfn "Added %d path(s) to %s." (List.length paths) (manifestPath root)

    for path in paths |> List.map normalise |> List.sort do
        printfn "  %s" path

    0

/// Reconcile the manifest with the tree, showing every change first.
///
/// Nothing is written without `--confirm`. A sync that silently adopts whatever
/// is on disk IS the directory walk `DF-STRATA-2026-7D14` removed: a file
/// dropped into the tree by anyone, or anything, would join the project on the
/// next build.
let sync (root: string) (confirm: bool) : int =
    if not (File.Exists(manifestPath root)) then
        eprintfn "error: no strata.json in %s. Run `strata init --from-tree` first." root
        2
    else

    let schemaRoot = currentSchemaRoot root
    let result = Manifests.reconcile (declaredIn root) (Manifests.objectFiles root schemaRoot)

    if Manifests.isClean result then
        printfn "%s already matches the tree; nothing to reconcile." (manifestPath root)
        0
    else

    if not (List.isEmpty result.ToAdd) then
        printfn "Would ADD %d file(s) that are on disk and not declared:" (List.length result.ToAdd)

        for path in result.ToAdd do
            printfn "  + %s" path

    if not (List.isEmpty result.ToRemove) then
        printfn ""
        printfn "Would REMOVE %d entry(ies) that name files not on disk:" (List.length result.ToRemove)

        for path in result.ToRemove do
            printfn "  - %s" path

    printfn ""

    if not confirm then
        printfn "Nothing was written. Review the list above, then re-run with --confirm."
        printfn ""
        printfn "It is shown rather than applied because a sync that adopts whatever is on disk"
        printfn "is the directory walk again: a file dropped into the tree would join the project"
        printfn "on the next build, and nobody would have decided that."
        2
    else

    let updated =
        Manifests.reconcile [] (Manifests.objectFiles root schemaRoot) |> fun r -> r.ToAdd

    File.WriteAllText(manifestPath root, Manifests.render (rawManifest root) updated)
    printfn "Reconciled %s: %d added, %d removed." (manifestPath root) (List.length result.ToAdd) (List.length result.ToRemove)
    0
