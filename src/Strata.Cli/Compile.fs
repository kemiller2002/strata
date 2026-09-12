/// Turning a project directory into a compiled artifact.
///
/// Authority for: what `strata compile` does, and what it refuses.
///
/// ## The halves of a deployment
///
/// `DF-STRATA-2026-2F6B` splits deployment in two. Source into desired state
/// depends on the source and on *a* PostgreSQL, so it compiles. Desired state
/// into a change list depends on what the TARGET holds at the moment of
/// deployment, so it does not. This module is the first half, run once, written
/// out.
///
/// ## Compile is strict where plan is lenient
///
/// `plan` reports a file it could not read or parse as a warning and carries on
/// with an incomplete desired state — which is correct for it, because an
/// incomplete desired state suppresses every drop and the operator still gets to
/// see what Strata CAN say.
///
/// `compile` refuses. An artifact is a claim that the project was read, and a
/// claim with a hole in it is worth less than no claim: the hole travels to a
/// deployment that has no source tree to check it against. `DF-STRATA-2026-C3A2`
/// anticipated this when it landed the schema/directory check as a load failure
/// — "when WI-0083 lands compile, load failures become compile failures
/// uniformly". This is that.
module Strata.Cli.Compile

open System.IO
open Strata.Semantic.AnalysisScope
open Strata.Application
open Strata.Host.Files

/// A project read off disk and loaded into the model, with everything that went
/// wrong on the way kept separate.
type Loaded =
    { Project: Project.ProjectRead
      Declared: DesiredState.Loaded
      /// Every file-level and declaration-level problem, as
      /// `(path, reason)`. Empty means the project was read whole.
      Problems: (string * string) list }

/// Read a project directory into the model.
///
/// Shared with `plan` deliberately. The two commands disagree about what to DO
/// with a problem and must not disagree about what the project says — the
/// awkward-forms corpus spent its whole life testing a pipeline that did not
/// ship, for exactly this reason (`WI-0093`).
let load (parser: Strata.Analysis.DialectPort.IDialectParser) (projectRoot: string) : Result<Loaded, string> =
    match Project.read projectRoot with
    | Microsoft.FSharp.Core.Error message -> Microsoft.FSharp.Core.Error message
    | Ok project ->
        let declared =
            DesiredState.loadWithLayout
                parser
                (project.Files |> List.map (fun f -> f.Path, Some f.Schema, f.Contents))

        Ok
            { Project = project
              Declared = declared
              Problems =
                project.Failures
                @ (declared.Failures |> List.map (fun f -> f.Path, f.Reason)) }

/// The desired state as `plan` must see it: incomplete when a file could not be
/// read, so that absence is never mistaken for a decision to remove (NG-006).
///
/// `compile` never reaches this — it refuses first — but the two share it so
/// that a project with no problems produces the SAME snapshot either way.
let snapshot (loaded: Loaded) =
    if List.isEmpty loaded.Project.Failures then
        loaded.Declared.Snapshot
    else
        { loaded.Declared.Snapshot with
            Completeness =
                Completeness.ofList
                    (loaded.Declared.Snapshot.Completeness.Categories
                     |> List.map (fun (name, state) ->
                         if name = "relations" then
                             name,
                             Partial(
                                 sprintf "%d project file(s) could not be read" (List.length loaded.Project.Failures))
                         else
                             name, state)) }

/// Compile a project to an artifact file.
///
/// Exit codes match the rest of the CLI: 0 compiled, 1 the project is wrong, 2
/// the project could not be read at all.
let run
    (parser: Strata.Analysis.DialectPort.IDialectParser)
    (connectionString: string)
    (projectRoot: string)
    (outputPath: string)
    : int =

    match load parser projectRoot with
    | Microsoft.FSharp.Core.Error message ->
        eprintfn "error: %s" message
        2
    | Ok loaded when not (List.isEmpty loaded.Problems) ->
        // Named individually. An artifact is refused because of specific files,
        // and an agent told only "compile failed" has to go looking for them.
        eprintfn "error: the project does not compile. %d problem(s):" (List.length loaded.Problems)

        for path, reason in loaded.Problems |> List.sortBy fst do
            eprintfn "  %s: %s" path reason

        eprintfn ""
        eprintfn "Nothing was written. An artifact is a claim that the project was read whole,"
        eprintfn "and a deployment has no source tree to check that claim against."
        1
    | Ok loaded ->
        // Every expression the server has to settle, settled once. Nothing here
        // reads the target's schema, which is what makes this half compilable.
        let resolved = Resolution.resolve connectionString loaded.Declared

        for warning in resolved.Warnings do
            eprintfn "warning: %s" warning

        for failure in resolved.DataFailures do
            eprintfn "warning: %s: %s" failure.Table failure.Reason

        let directory = Path.GetDirectoryName(Path.GetFullPath outputPath)

        if not (Directory.Exists directory) then
            Directory.CreateDirectory directory |> ignore

        // Written with an integrity digest, always. It costs nothing and it
        // catches the ordinary failure — a file edited by hand, truncated by a
        // bad copy, merged badly. It proves nothing about WHO produced the
        // artifact; that is what `strata sign` is for, and the two are reported
        // separately because reporting them as one thing would be the more
        // convenient lie.
        File.WriteAllText(
            outputPath,
            Attestation.renderFile { Attestation.none with Digest = Some(Attestation.digestOf resolved) } resolved)

        printfn "Compiled %d object(s) from %s to %s."
            (List.length loaded.Declared.Snapshot.Objects)
            loaded.Project.Root
            outputPath

        0
