/// Deploying a compiled artifact to a target.
///
/// Authority for: what `strata deploy` reads, what it refuses, and why it never
/// opens the project directory.
///
/// ## The guarantee
///
/// One artifact goes to staging and then to production. Because it is the same
/// bytes, "what we tested is what we shipped" is a fact about the input rather
/// than a claim about a process — nobody can edit a file between the two
/// deployments and have it take effect, because deploy does not read files.
///
/// It follows that deploy must not fall back to the source tree for ANYTHING.
/// A single field quietly re-derived from a project directory would make the
/// artifact a partial record and the guarantee a half-truth, and the failure
/// would be invisible: two deployments of the same artifact behaving
/// differently, with nothing in either output saying why.
module Strata.Cli.Deploy

open System.IO
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Application
open Strata.Host.Files
open Strata.Host.Postgres

/// The schemas an artifact manages.
///
/// Derived from the objects it declares rather than carried separately, and the
/// two are the same set by construction: a declaration's schema must match its
/// directory and an empty schema directory does not compile
/// (`DF-STRATA-2026-C3A2`), so every managed schema holds at least one object.
///
/// Deriving rather than carrying means there is no second copy to disagree with
/// the first.
let managedSchemas (resolved: ResolvedDesiredState) =
    resolved.Declared.Snapshot.Objects
    |> List.choose (fun o -> (SchemaObject.name o).Schema)
    |> List.map Identifier.folded
    |> List.distinct
    |> List.sort

/// Whether an artifact may be deployed to a server of this version.
///
/// The check is on the MAJOR version, and it is a refusal rather than a warning.
///
/// Every normalised expression in an artifact is the compiling server's
/// rendering: `DEFAULT 'open'` is in there as `'open'::text` because PostgreSQL
/// 16's deparser said so. Deparsing changes between major versions. Deploying to
/// a different major would compare the artifact's renderings against renderings
/// that server would never produce, and the result is not an error — it is a
/// plan full of changes to objects nobody touched, which is indistinguishable
/// from real drift and which `--approve` would happily wave through.
///
/// Minor versions are not checked. PostgreSQL does not change catalog output in
/// a minor release; treating 16.2 and 16.15 as incompatible would refuse every
/// ordinary deployment and teach everyone to bypass the check.
let versionProblem (compiledWith: ServerVersion option) (target: Result<ServerVersion, string>) =
    match compiledWith, target with
    | None, _ ->
        Some
            "the artifact does not record which server rendered its expressions, so there is nothing to \
             check this target against. Recompile with a connection Strata can read a version from."
    | _, Microsoft.FSharp.Core.Error message ->
        Some(
            sprintf
                "the target's version could not be read (%s), so it cannot be shown to agree with the server that compiled this artifact."
                message)
    | Some compiled, Ok target when compiled.Major <> target.Major ->
        Some(
            sprintf
                "this artifact was compiled against PostgreSQL %s and the target is %s. Expressions in an artifact are the COMPILING server's rendering, and deparsing changes between major versions, so every default, check and policy would compare unequal and the plan would propose changes to objects nobody touched. Recompile against PostgreSQL %d."
                compiled.Full
                target.Full
                target.Major)
    | Some _, Ok _ -> None

/// Read an artifact and deploy it.
///
/// Exit codes match the rest of the CLI: 0 allow or converged, 1 block, 2
/// requires-approval or a refusal.
let run
    (parser: Strata.Analysis.DialectPort.IDialectParser)
    (connectionString: string)
    (artifactPath: string)
    (options: Deployment.Options)
    : int =

    if not (File.Exists artifactPath) then
        eprintfn "error: no artifact at %s. Produce one with `strata compile --out %s`." artifactPath artifactPath
        2
    else

    match Artifact.ofText (File.ReadAllText artifactPath) with
    | Microsoft.FSharp.Core.Error message ->
        eprintfn "error: %s" message
        2
    | Ok resolved ->

    match versionProblem resolved.CompiledWith (CatalogIntrospection.readServerVersion connectionString) with
    | Some problem ->
        eprintfn "REFUSED: %s" problem
        2
    | None ->

    // Carried forward so the operator sees what the artifact was built under.
    // A warning recorded at compile time and swallowed at deploy time is a
    // warning nobody ever reads — the compile may have happened in CI weeks ago.
    for warning in resolved.Warnings do
        eprintfn "warning: recorded at compile time: %s" warning

    for failure in resolved.DataFailures do
        eprintfn "warning: recorded at compile time: %s: %s" failure.Table failure.Reason

    // Reference rows are resolved HERE, against this target, not read from the
    // artifact. An artifact cannot carry them: half of the answer is what the
    // table already holds, which is a fact about one server. Carrying it meant
    // an artifact compiled against a populated database deployed to an empty one
    // and inserted nothing — four statements instead of six, exit 0, and a
    // lookup table with no rows. See the note in `Artifact.render`.
    let (data, dataFailures), dataWarnings = Resolution.resolveData connectionString resolved.Declared

    for warning in dataWarnings do
        eprintfn "warning: %s" warning

    for failure in dataFailures do
        eprintfn "warning: %s: %s" failure.Table failure.Reason

    // The snapshot is used as-is. `compile` refuses a project it could not read
    // whole, so an artifact's desired state is complete by construction — there
    // is no incomplete case to fold in the way `plan` has to.
    Deployment.run
        parser
        connectionString
        options
        (managedSchemas resolved)
        resolved.Declared.Snapshot
        { resolved with Data = data; DataFailures = dataFailures }
