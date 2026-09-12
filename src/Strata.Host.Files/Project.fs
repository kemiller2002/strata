namespace Strata.Host.Files

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

/// Reading a Strata project from disk (PR-025, DF-STRATA-2026-A4D9).
///
/// Authority for: what a project declares and where its files are.
///
/// A project is `strata.json` plus a tree of per-object SQL files. This module
/// reads the manifest and enumerates the object files; turning their contents
/// into a schema is Tier 3's job, because deciding what a file MEANS is a
/// semantic question and this is a host adapter.
///
/// `System.Text.Json` is used for READING only. Everything Strata writes goes
/// through `Wire.fs`, whose hand-written output is what NFR-001 depends on.
/// Reading a config file has no determinism requirement; emitting one would.
module Project =

    /// The manifest, as declared.
    type Manifest =
        { /// Where desired-state object files live, relative to the project root.
          SchemaRoot: string
          /// Every object file in the project, by explicit path relative to the
          /// project root.
          ///
          /// REQUIRED, and exhaustive: a `.sql` file under the schema root that
          /// is not listed here fails the load, and a listed path that does not
          /// exist fails it too (`DF-STRATA-2026-7D14`). The redundancy with the
          /// directory tree is the point — because both exist and must agree,
          /// the DISAGREEMENT is detectable. A directory walk alone cannot
          /// notice that a file was added, because there is nothing for it to
          /// disagree with.
          ///
          /// No globs. `schema/**/*.sql` is a directory walk wearing a
          /// manifest's clothes: drop a file and the build takes it.
          Include: string list
          /// Where application SQL lives, for the dependency analysis that makes
          /// destructive changes safe. Kept separate from SchemaRoot because a
          /// table definition and a query that reads it are different inputs.
          CorpusRoots: string list }

    [<RequireQualifiedAccess>]
    module Manifest =

        let defaults =
            { SchemaRoot = "schema"
              Include = []
              CorpusRoots = [] }

    /// One desired-state file, located.
    type ObjectFile =
        { /// Path as given, for error messages a human can act on.
          Path: string
          /// Schema directory the file sat under, per the layout contract.
          Schema: string
          /// Object-type directory: "tables", "views", "functions", ...
          ObjectType: string
          Contents: string }

    type ProjectRead =
        { Root: string
          Manifest: Manifest
          /// The schemas this project manages, derived from the directory names
          /// under the schema root (`DF-STRATA-2026-C3A2`).
          ///
          /// Derived rather than declared, and the failure modes are why. As a
          /// manifest field, deleting a schema's files left it managed and
          /// EMPTY — which reads as "this schema should be empty", authority to
          /// drop everything in it. Derived from the tree, the same deletion
          /// leaves it UNMANAGED and Strata stops touching it.
          Schemas: string list
          Files: ObjectFile list
          /// Files that could not be read or did not fit the layout. Never
          /// silently skipped: a project reporting 17 of 20 objects as its
          /// desired state would have a diff propose dropping the other three.
          Failures: (string * string) list }

    let private stringList (element: JsonElement) (name: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.Array ->
            value.EnumerateArray()
            |> Seq.choose (fun v -> if v.ValueKind = JsonValueKind.String then Some(v.GetString()) else None)
            |> List.ofSeq
        | _ -> []

    let private stringValue (element: JsonElement) (name: string) (fallback: string) =
        match element.TryGetProperty name with
        | true, value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> fallback

    /// Read and parse `strata.json`.
    let readManifest (path: string) : Result<Manifest, string> =
        try
            use document = JsonDocument.Parse(File.ReadAllText path)
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                Error "strata.json must contain a JSON object"
            elif not (fst (root.TryGetProperty "include")) then
                // Absent and empty are different, and both are errors — but a
                // project that forgot the key needs a different sentence from
                // one that deliberately lists nothing.
                Error
                    "strata.json must declare \"include\": the project's object files, by explicit path. \
                     Run `strata init --from-tree` to write it from the current directory."
            else
                let included = stringList root "include"

                if List.isEmpty included then
                    Error "strata.json declares an empty \"include\": a project with no object files declares nothing"
                else

                let duplicates =
                    included
                    |> List.countBy (fun p -> p.Replace('\\', '/'))
                    |> List.filter (fun (_, n) -> n > 1)
                    |> List.map fst

                if not (List.isEmpty duplicates) then
                    Error(
                        sprintf
                            "strata.json lists the same path more than once in \"include\": %s"
                            (String.concat ", " (List.sort duplicates)))
                else

                Ok
                    { SchemaRoot = stringValue root "schemaRoot" Manifest.defaults.SchemaRoot
                      Include = included
                      CorpusRoots = stringList root "corpusRoots" }
        with
        | :? JsonException as ex -> Error(sprintf "strata.json is not valid JSON: %s" ex.Message)
        | ex -> Error ex.Message

    /// The layout contract: `<schemaRoot>/<schema>/<objectType>/<name>.sql`.
    ///
    /// A file that does not sit at that depth is a FAILURE, not a file to
    /// ignore. Silently skipping it would remove an object from desired state,
    /// and absence from desired state is what a diff reasons about.
    let private classify (schemaRoot: string) (path: string) =
        let relative =
            let full = Path.GetFullPath path
            let rootFull = Path.GetFullPath schemaRoot

            if full.StartsWith rootFull then
                full.Substring(rootFull.Length).TrimStart(Path.DirectorySeparatorChar, '/')
            else
                full

        match relative.Replace('\\', '/').Split('/') with
        | [| schema; objectType; _ |] -> Ok(schema, objectType)
        | segments ->
            Error(
                sprintf
                    "expected <schema>/<object-type>/<name>.sql under the schema root, found %d path segment(s)"
                    segments.Length)

    /// Directory names that cannot be schema names, and why.
    ///
    /// A directory name must equal `pg_namespace.nspname` BYTE FOR BYTE, so the
    /// name has to be one every filesystem stores unchanged. One pattern kills
    /// four hazards at once (`DF-STRATA-2026-C3A2`):
    ///
    ///   * case sensitivity — no uppercase, so `orders` and `Orders` cannot
    ///     collide on macOS or Windows;
    ///   * Unicode normalisation — ASCII only, so NFC and NFD cannot differ
    ///     between Linux and macOS;
    ///   * path-reserved characters — `/`, `\`, `:` and control characters;
    ///   * Windows device names — legal PostgreSQL schema names, illegal
    ///     directory names, and they need an explicit list on top of the
    ///     pattern.
    ///
    /// It is also exactly the set of identifiers needing no quoting in SQL, so
    /// the file content and the directory name are the same string with no
    /// folding to reason about.
    let private windowsDeviceNames =
        set [ "aux"; "con"; "prn"; "nul"
              "com1"; "com2"; "com3"; "com4"; "com5"; "com6"; "com7"; "com8"; "com9"
              "lpt1"; "lpt2"; "lpt3"; "lpt4"; "lpt5"; "lpt6"; "lpt7"; "lpt8"; "lpt9" ]

    let private portableSchemaName = Regex(@"^[a-z_][a-z0-9_]*$", RegexOptions.CultureInvariant)

    /// `None` when the name is usable, `Some reason` when it is not.
    let schemaNameProblem (name: string) =
        if not (portableSchemaName.IsMatch name) then
            Some(
                sprintf
                    "schema \"%s\" cannot be managed by Strata. Schema names must match [a-z_][a-z0-9_]* so that a \
                     directory name can equal the schema name on every filesystem. Uppercase, non-ASCII and \
                     path-reserved characters are excluded because Linux, macOS and Windows disagree about them."
                    name)
        elif windowsDeviceNames.Contains name then
            Some(
                sprintf
                    "schema \"%s\" cannot be managed by Strata: it is a reserved device name on Windows, so no \
                     directory of that name can exist there."
                    name)
        else
            None

    /// Where the object files are, given a project root.
    ///
    /// A `schema/` subdirectory if there is one, otherwise the root itself, so
    /// `strata plan --project ./schema` and `--project .` both work without
    /// configuration.
    let private locateSchemaRoot (root: string) (declared: string option) =
        match declared with
        | Some relative -> Path.Combine(root, relative)
        | None ->
            let conventional = Path.Combine(root, "schema")
            if Directory.Exists conventional then conventional else root

    let read (root: string) : Result<ProjectRead, string> =
        let manifestPath = Path.Combine(root, "strata.json")

        // The manifest is REQUIRED. It was optional while project membership was
        // a directory walk; once `include` decides membership there is nothing
        // for a project without one to mean (`DF-STRATA-2026-7D14`).
        if not (File.Exists manifestPath) then
            Error(
                sprintf
                    "no strata.json in %s. A Strata project declares its object files explicitly; run `strata init --from-tree` to write one."
                    root)
        else

        match readManifest manifestPath with
        | Error message -> Error message
        | Ok manifest ->

        let schemaRoot = Path.Combine(root, manifest.SchemaRoot)

        if not (Directory.Exists schemaRoot) then
            Error(sprintf "the schema root %s does not exist" schemaRoot)
        else

        let normalise (p: string) = p.Replace('\\', '/')

        // Both sides of the agreement, as paths relative to the project root.
        let declared = manifest.Include |> List.map normalise |> Set.ofList

        let onDisk =
            Directory.GetFiles(schemaRoot, "*.sql", SearchOption.AllDirectories)
            |> Array.map (fun full ->
                normalise (Path.GetRelativePath(root, full)))
            |> Set.ofArray

        // Scope is `*.sql` only. README.md, .gitignore and strata.json itself
        // are not Strata's business, or the first README breaks the build.
        // Merge and editor leftovers (customer.sql.orig, *.sql.bak) do not match
        // the extension and so do not trip this.
        let unlisted = Set.difference onDisk declared |> Set.toList |> List.sort
        let missing = Set.difference declared onDisk |> Set.toList |> List.sort

        // Errors, not warnings. An automated build ignoring warnings is what a
        // warning IS, and both of these have a cheap resolution: list the file,
        // or move it out of the directory.
        let membershipErrors =
            [ if not (List.isEmpty unlisted) then
                  sprintf
                      "%d file(s) under %s are not listed in \"include\". Strata owns this directory, so every .sql file in it must be declared — list them, or move them out:\n  %s"
                      (List.length unlisted)
                      manifest.SchemaRoot
                      (String.concat "\n  " unlisted)
              if not (List.isEmpty missing) then
                  sprintf
                      "%d path(s) in \"include\" do not exist:\n  %s"
                      (List.length missing)
                      (String.concat "\n  " missing) ]

        if not (List.isEmpty membershipErrors) then
            Error(String.concat "\n\n" membershipErrors)
        else

        let files = ResizeArray<ObjectFile>()
        let failures = ResizeArray<string * string>()

        // Sorted so a project reads identically on any host (NFR-001).
        for relative in List.sort (List.map normalise manifest.Include) do
            let full = Path.Combine(root, relative)

            match classify schemaRoot full with
            | Error reason -> failures.Add(relative, reason)
            | Ok (schema, objectType) ->
                try
                    files.Add
                        { Path = full
                          Schema = schema
                          ObjectType = objectType
                          Contents = File.ReadAllText full }
                with ex ->
                    failures.Add(relative, ex.Message)

        // Derived from the directories that actually hold included files, not
        // from every directory present. A directory holding nothing declares
        // nothing, and is reported below rather than read as "this schema
        // should be empty".
        let schemas =
            files
            |> Seq.map (fun f -> f.Schema)
            |> Seq.distinct
            |> Seq.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> List.ofSeq

        let nameErrors = schemas |> List.choose schemaNameProblem

        // An empty schema directory is an error, and this is what makes removing
        // `managedSchemas` safe: without it the "this schema should be empty"
        // hazard simply moves from the manifest to the filesystem.
        let emptyDirectories =
            Directory.GetDirectories schemaRoot
            |> Array.map Path.GetFileName
            |> Array.filter (fun d -> not (List.contains d schemas))
            |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
            |> List.ofArray

        let layoutErrors =
            nameErrors
            @ [ if not (List.isEmpty emptyDirectories) then
                    sprintf
                        "%d schema directory/directories declare no object files. A directory that declares nothing cannot be told from one that says its schema should be empty, so it is an error — remove it, or declare something in it:\n  %s"
                        (List.length emptyDirectories)
                        (String.concat "\n  " emptyDirectories) ]

        if not (List.isEmpty layoutErrors) then
            Error(String.concat "\n\n" layoutErrors)
        else

        Ok
            { Root = root
              Manifest = manifest
              Schemas = schemas
              Files = List.ofSeq files
              Failures = List.ofSeq failures }
