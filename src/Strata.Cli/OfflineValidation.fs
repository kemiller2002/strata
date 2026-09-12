/// Checking SQL against a compiled artifact, with no database.
///
/// Authority for: what `strata validate --artifact` can and cannot decide.
///
/// ## Why this is the largest single gain from compiling
///
/// `strata validate` against a live server needs credentials, a reachable
/// database, and a round trip. That puts it out of reach of the three places it
/// is worth the most: an editor, a pre-commit hook, and a pull request from a
/// fork. An artifact is a file, so all three become possible — and the check is
/// the SAME check, because the artifact carries the same semantic model
/// introspection produces (`DF-STRATA-2026-2F6B`).
///
/// ## It also puts the authority the right way round
///
/// Validating against a live database asks "does this query match what is
/// deployed?" Validating against an artifact asks "does this query match what
/// the project declares?" Those differ exactly when a server is behind, and the
/// second is the question worth answering: a query that is valid against the
/// artifact and fails against production has found a STALE SERVER, not a bad
/// query. The first framing would have the author change correct code.
///
/// ## What it cannot decide, and says so
///
/// A search path is a property of the connection that will run the query, and
/// `DF-STRATA-2026-9A41` keeps facts about a server out of an artifact — so the
/// artifact does not carry one and this cannot know it. Rather than resolve
/// unqualified names against nothing (which would report every unqualified name
/// as unresolved) or guess silently, the assumed path is stated in the output
/// and can be set with `--search-path`.
module Strata.Cli.OfflineValidation

open System.IO
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Application
open Strata.Host.Files

/// The search path to resolve unqualified names against.
///
/// The default is exactly the schemas the artifact MANAGES, and the exclusion of
/// `public` is the interesting part.
///
/// A real connection's `search_path` usually ends in `public`, so putting it in
/// the default looks obviously right. It is not. The artifact says nothing about
/// `public` — an unmanaged schema is one Strata has no snapshot of — so with
/// `public` on the path, `SELECT ... FROM product` is genuinely ambiguous
/// between `shop.product`, which the artifact declares, and `public.product`,
/// which may or may not exist. Strata says so, correctly, for every unqualified
/// name in every file. Measured: the same query is VALID against
/// `--search-path shop` and UNVERIFIABLE against `shop,public`.
///
/// Being uselessly right is still not useful. The default is therefore the
/// schemas the artifact can speak for, so the answer is decidable; a caller
/// whose real path reaches further passes `--search-path` and gets the honest
/// ambiguity back.
///
/// Either way the path used is stated in the output, because an assumption a
/// reader cannot see is one they cannot correct.
let searchPathFor (explicit: string option) (resolved: ResolvedDesiredState) =
    match explicit with
    | Some text ->
        text.Split(',')
        |> Array.toList
        |> List.map (fun s -> s.Trim())
        |> List.filter (fun s -> s <> "")
        |> List.map Identifier.unquoted
    | None -> Deploy.managedSchemas resolved |> List.map Identifier.unquoted

let run
    (parser: Strata.Analysis.DialectPort.IDialectParser)
    (artifactPath: string)
    (sqlPath: string)
    (explicitSearchPath: string option)
    (json: bool)
    : int =

    if not (File.Exists artifactPath) then
        eprintfn "error: no artifact at %s. Produce one with `strata compile --out %s`." artifactPath artifactPath
        2
    elif not (File.Exists sqlPath) then
        eprintfn "error: file not found: %s" sqlPath
        2
    else

    match Artifact.ofText (File.ReadAllText artifactPath) with
    | Microsoft.FSharp.Core.Error message ->
        eprintfn "error: %s" message
        2
    | Ok resolved ->
        let searchPath = searchPathFor explicitSearchPath resolved

        // On stderr, so `--json` output stays byte-identical (NFR-001) while the
        // assumption is still impossible to miss.
        eprintfn
            "validating against %s, not a live database. Unqualified names resolve against search_path %s%s."
            artifactPath
            (searchPath |> List.map (fun (i: Identifier) -> i.Text) |> String.concat ", ")
            (if explicitSearchPath.IsSome then
                 ""
             else
                 " (the schemas this artifact manages; pass --search-path to widen it)")

        // Carried forward for the same reason `deploy` carries them: a warning
        // recorded when the artifact was built is a warning nobody reads unless
        // something repeats it.
        for warning in resolved.Warnings do
            eprintfn "warning: recorded at compile time: %s" warning

        let report =
            Validation.validate parser resolved.Declared.Snapshot searchPath (File.ReadAllText sqlPath)

        if json then
            printfn "%s" (Validation.toJson sqlPath report)
        else
            printfn "%s" (Validation.toText sqlPath report)

        Validation.Report.exitCode report
