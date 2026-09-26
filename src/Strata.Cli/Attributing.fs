/// Reading who is acting from this process, and building the provenance
/// records `compile`, `sign` and `deploy --approve` write.
///
/// Authority for: the host half of DF-STRATA-2026-E4B7 — which environment is
/// read, when the clock is read, how this run is named, and which lineage a
/// compiled artifact names. The rules themselves live in Tier 1
/// (`Strata.Semantic.Provenance`) and Tier 3 (`Attribution`).
///
/// ## Authorship is not inferred
///
/// The actor compiling a project is recorded as the CREATOR OF THE ARTIFACT,
/// never as the author of the SQL in it. The project is named as lineage
/// (`derivedFrom`), and a provenance record for the source change is carried
/// verbatim only when someone supplies one (`--source-provenance`). Git
/// history, file owners and comments are never read for identity.
module Strata.Cli.Attributing

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Strata.Semantic.Provenance
open Strata.Application
open Strata.Host.Files

/// What the command line said about provenance.
type Request =
    { Declared: Attribution.Declared
      /// A provenance record for the source change, carried as lineage.
      SourceProvenance: string option
      /// `--no-provenance`: write none, and extend none.
      Suppressed: bool }

let private valueOf (flag: string) (argv: string list) =
    argv |> List.pairwise |> List.tryPick (fun (a, b) -> if a = flag then Some b else None)

/// The flags that take a value, for the positional-argument parser.
let valueFlags =
    [ "--actor-kind"; "--actor"; "--provider"; "--model"; "--runtime"; "--execution"; "--source-provenance" ]

let request (args: string list) : Request =
    { Declared =
        { Kind = valueOf "--actor-kind" args
          Actor = valueOf "--actor" args
          Provider = valueOf "--provider" args
          Model = valueOf "--model" args
          Runtime = valueOf "--runtime" args
          Execution = valueOf "--execution" args }
      SourceProvenance = valueOf "--source-provenance" args
      Suppressed = List.contains "--no-provenance" args }

/// Only the whitelisted variables are visible to resolution.
let environment (name: string) =
    if List.contains name Attribution.environmentVariables then
        match Environment.GetEnvironmentVariable name with
        | null -> None
        | value -> Some value
    else
        None

/// ISO-8601 UTC, millisecond precision. Read here, in the host, and only for
/// the wrapper: the artifact body never sees a clock (NFR-001).
let now () =
    DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", Globalization.CultureInfo.InvariantCulture)

/// This process's run, for `EXE-strata.<run>` when nothing is propagated.
let private localRun () =
    let stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", Globalization.CultureInfo.InvariantCulture)
    stamp + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes 4).ToLowerInvariant()

let resolve (request: Request) : Result<Attribution.Context, string> =
    Attribution.resolve request.Declared environment (localRun ())

let describe (context: Attribution.Context) =
    sprintf "%s in %s (%s; self-reported)" (Actor.describe context.Actor) context.Execution context.Mechanism

/// `strata:project/<name>@sha256:<digest of the files compiled>`. Lineage, not
/// authorship: it names what the artifact was derived from.
let projectReference (project: Project.ProjectRead) =
    let name =
        let raw = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath project.Root))

        match String(raw |> Seq.map (fun c -> if Char.IsWhiteSpace c || c = '@' then '-' else c) |> Seq.toArray) with
        | "" -> "project"
        | cleaned -> cleaned

    let content =
        project.Files
        |> List.map (fun f -> Path.GetRelativePath(project.Root, f.Path).Replace('\\', '/'), f.Contents)
        |> List.sortBy fst
        |> List.map (fun (path, contents) -> path + "\u0000" + contents + "\u0000")
        |> String.concat ""

    let digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes content)).ToLowerInvariant()
    sprintf "strata:project/%s@sha256:%s" name digest

let private readSource (path: string) : Result<string * JsonObject, string> =
    try
        match ProvenanceJson.parse (File.ReadAllText path) with
        | Error problems -> Error(sprintf "--source-provenance %s is not a valid provenance record (%s)" path (ProvenanceJson.describeProblems problems))
        | Ok reading ->
            let raw = ProvenanceJson.raw reading

            match ProvenanceJson.validate raw with
            | Error problems -> Error(sprintf "--source-provenance %s is malformed (%s)" path (ProvenanceJson.describeProblems problems))
            | Ok _ ->
                let subject =
                    match raw.["subject"] with
                    | :? JsonValue as v ->
                        match v.TryGetValue<string>() with
                        | true, s -> Some s
                        | _ -> None
                    | _ -> None

                Ok(subject |> Option.defaultValue "", raw)
    with ex ->
        Error(sprintf "could not read --source-provenance %s (%s)" path ex.Message)

/// The provenance a freshly compiled artifact carries, or `None` when there is
/// nothing to say: nobody declared or was detected, and no source record was
/// supplied. An all-unknown record would add a clock and nothing else, and
/// would cost the file its byte-for-byte reproducibility.
let forCompile
    (request: Request)
    (project: Project.ProjectRead)
    (digest: string)
    : Result<(ProvenanceJson.Reading * Attribution.Context) option, string> =
    if request.Suppressed then
        Ok None
    else
        match resolve request with
        | Error message -> Error message
        | Ok context when not context.Identified && request.SourceProvenance.IsNone -> Ok None
        | Ok context ->
            let projectRef = projectReference project

            let source =
                match request.SourceProvenance with
                | None -> Ok []
                | Some path ->
                    readSource path
                    // A source record with no subject describes the project
                    // change itself, so it is keyed by the project reference.
                    |> Result.map (fun (subject, raw) -> [ (if subject = "" then projectRef else subject), raw ])

            match source with
            | Error message -> Error message
            | Ok sources ->
                let creator = Attribution.contribution context Operation.Created (now ()) None []

                match ProvenanceJson.derive (Attestation.subjectOf digest) creator [ projectRef ] sources with
                | Error problems -> Error(ProvenanceJson.describeProblems problems)
                | Ok raw ->
                    match ProvenanceJson.read raw with
                    | Ok reading -> Ok(Some(reading, context))
                    | Error problems -> Error(ProvenanceJson.describeProblems problems)

/// `strata:key/sha256:<SPKI digest>`: which key signed, never the key.
let keyReference (privateKeyPem: string) =
    use key = ECDsa.Create()
    key.ImportFromPem(privateKeyPem.ToCharArray())
    let spki = key.ExportSubjectPublicKeyInfo()
    "strata:key/sha256:" + Convert.ToHexString(SHA256.HashData spki).ToLowerInvariant()

/// Adds this run's contribution to whatever the wrapper carries. An
/// unsupported major is carried verbatim and not extended; absent provenance
/// starts a record holding only this contribution (no invented history).
let extend
    (existing: ProvenanceJson.Reading option)
    (subject: string)
    (contribution: Contribution)
    : Result<ProvenanceJson.Reading option * string option, string> =
    let reread raw =
        match ProvenanceJson.read raw with
        | Ok reading -> Ok(Some reading, None)
        | Error problems -> Error(ProvenanceJson.describeProblems problems)

    match existing with
    | Some (ProvenanceJson.Reading.Unsupported (version, _)) ->
        Ok(existing, Some(sprintf "the artifact's provenance is version %s, which this build carries but does not extend" version))
    | Some reading ->
        match ProvenanceJson.append contribution (ProvenanceJson.raw reading) with
        | Ok raw -> reread raw
        | Error problems -> Error(ProvenanceJson.describeProblems problems)
    | None ->
        match ProvenanceJson.create (Some subject) contribution with
        | Ok raw -> reread raw
        | Error problems -> Error(ProvenanceJson.describeProblems problems)
