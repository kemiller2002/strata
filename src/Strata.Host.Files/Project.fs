namespace Strata.Host.Files

open System
open System.IO
open System.Text.Json

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
          /// The EXPLICIT managed scope. §1437 requires this: an object outside
          /// these schemas is never dropped for being absent from desired
          /// state, no matter what a diff computes. An empty list means nothing
          /// is managed, which is a safe default rather than a permissive one.
          ManagedSchemas: string list
          /// Where application SQL lives, for the dependency analysis that makes
          /// destructive changes safe. Kept separate from SchemaRoot because a
          /// table definition and a query that reads it are different inputs.
          CorpusRoots: string list }

    [<RequireQualifiedAccess>]
    module Manifest =

        let defaults =
            { SchemaRoot = "schema"
              ManagedSchemas = []
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
            else
                Ok
                    { SchemaRoot = stringValue root "schemaRoot" Manifest.defaults.SchemaRoot
                      ManagedSchemas = stringList root "managedSchemas"
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

    /// Schemas inferred from the directory tree.
    ///
    /// The layout already names them — `schema/<schema>/tables/x.sql` — so
    /// requiring a manifest to repeat them would be asking for the same fact
    /// twice and creating a way for the two to disagree.
    let private inferSchemas (schemaRoot: string) =
        Directory.GetDirectories schemaRoot
        |> Array.map Path.GetFileName
        |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))
        |> List.ofArray

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

        // The manifest is OPTIONAL. Pointing at a directory is the normal case
        // and a project that needs no configuration should need no file: the
        // directory tree already says which schemas exist and which files
        // declare objects. A manifest exists only to say something the tree
        // cannot.
        let declaredManifest =
            if File.Exists manifestPath then
                match readManifest manifestPath with
                | Ok manifest -> Ok(Some manifest)
                | Error message -> Error message
            else
                Ok None

        match declaredManifest with
        | Error message -> Error message
        | Ok manifest ->
            let schemaRoot =
                locateSchemaRoot root (manifest |> Option.map (fun m -> m.SchemaRoot))

            if not (Directory.Exists schemaRoot) then
                Error(sprintf "no schema directory found under %s" root)
            else
                let files = ResizeArray<ObjectFile>()
                let failures = ResizeArray<string * string>()

                // Sorted so a project reads identically on any host (NFR-001).
                let paths =
                    Directory.GetFiles(schemaRoot, "*.sql", SearchOption.AllDirectories)
                    |> Array.sortWith (fun a b -> String.CompareOrdinal(a, b))

                for path in paths do
                    match classify schemaRoot path with
                    | Error reason -> failures.Add(path, reason)
                    | Ok (schema, objectType) ->
                        try
                            files.Add
                                { Path = path
                                  Schema = schema
                                  ObjectType = objectType
                                  Contents = File.ReadAllText path }
                        with ex ->
                            failures.Add(path, ex.Message)

                let resolved =
                    match manifest with
                    | Some m when not (List.isEmpty m.ManagedSchemas) -> m
                    | Some m -> { m with ManagedSchemas = inferSchemas schemaRoot }
                    | None ->
                        { SchemaRoot = Path.GetFileName schemaRoot
                          ManagedSchemas = inferSchemas schemaRoot
                          CorpusRoots = [] }

                Ok
                    { Root = root
                      Manifest = resolved
                      Files = List.ofSeq files
                      Failures = List.ofSeq failures }
