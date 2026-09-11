namespace Strata.Host.Files

open System
open System.IO
open Strata.Analysis.Corpus

/// Reading a SQL corpus from disk.
///
/// Authority for: which files become corpus sources.
///
/// Notebook §8: "Do not promiscuously scrape arbitrary strings from source code
/// initially." This module reads `.sql` files from a directory the caller
/// names. It does not search for SQL embedded in application source, and it
/// does not follow anything outside the given root.
module FileCorpus =

    /// A file that could not be read, kept as data.
    ///
    /// A corpus run must be able to say "I could not read 3 of these" rather
    /// than quietly indexing 17 of 20 files and reporting a clean result
    /// (ER-008, §144.11).
    type ReadFailure = { Path: string; Reason: string }

    type CorpusRead =
        { Sources: (SqlOrigin * string) list
          Failures: ReadFailure list }

    /// Classify a file by its location, so migrations are distinguishable from
    /// ordinary SQL in provenance output.
    let private originFor (root: string) (path: string) =
        let relative =
            let full = Path.GetFullPath path
            let rootFull = Path.GetFullPath root

            if full.StartsWith rootFull then
                full.Substring(rootFull.Length).TrimStart(Path.DirectorySeparatorChar, '/')
            else
                full

        // Normalise separators so a source identifier is the same on any host
        // (NFR-001: output must not vary by platform).
        let normalised = relative.Replace('\\', '/')

        let looksLikeMigration =
            normalised.Split('/')
            |> Array.exists (fun segment ->
                let lower = segment.ToLowerInvariant()
                lower = "migrations" || lower = "migration")

        if looksLikeMigration then MigrationScript normalised else SqlFile normalised

    /// Read every `.sql` file under `root`.
    ///
    /// Files are returned in sorted order so a corpus run is deterministic
    /// regardless of directory enumeration order (NFR-001).
    let read (root: string) : Result<CorpusRead, string> =
        if not (Directory.Exists root) then
            Error(sprintf "corpus directory not found: %s" root)
        else

        try
            let files =
                Directory.GetFiles(root, "*.sql", SearchOption.AllDirectories)
                |> Array.sortBy (fun p -> p.Replace('\\', '/'))

            let sources = ResizeArray<SqlOrigin * string>()
            let failures = ResizeArray<ReadFailure>()

            for path in files do
                try
                    sources.Add(originFor root path, File.ReadAllText path)
                with ex ->
                    failures.Add { Path = path; Reason = ex.Message }

            Ok
                { Sources = List.ofSeq sources
                  Failures = List.ofSeq failures }
        with ex ->
            Error ex.Message
