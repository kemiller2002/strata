module Strata.Tests.ProvenanceTests

open System
open System.IO
open System.Security.Cryptography
open System.Text.Json
open System.Text.Json.Nodes
open Xunit
open Strata.Semantic.Provenance
open Strata.Application
open Strata.Host.Files

/// Strata's provenance codec against Praxis's own conformance fixtures, and the
/// identity resolution `compile`, `sign` and `deploy --approve` use.
///
/// The fixtures are vendored under `fixtures/praxis-provenance-record/`, with
/// the Praxis commit and every file's SHA-256 in `SOURCE.json`. Strata has no
/// dependency on Praxis; these tests are how "the same contract" is checked
/// rather than asserted.

let private fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "praxis-provenance-record")

let private load (relative: string) = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, relative)))

let private manifest = lazy (JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "manifest.json"))).AsObject())

let private entries (name: string) =
    manifest.Value.[name].AsArray() |> Seq.map (fun n -> n.AsObject()) |> Seq.toList

let private text (node: JsonNode) (name: string) = node.[name].GetValue<string>()

// ---- the vendored fixtures --------------------------------------------------

[<Fact>]
let ``the vendored fixtures match the digests recorded with them`` () =
    let source = JsonNode.Parse(File.ReadAllText(Path.Combine(fixtures, "SOURCE.json"))).AsObject()
    Assert.Equal("kemiller2002/praxis", text source "repository")
    let files = source.["files"].AsObject() |> Seq.toList
    Assert.NotEmpty files

    for pair in files do
        let actual =
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixtures, pair.Key)))).ToLowerInvariant()

        Assert.True((actual = pair.Value.GetValue<string>()), sprintf "%s does not match its recorded digest" pair.Key)

    // Every file on disk is accounted for, so nothing unrecorded was added.
    let onDisk =
        Directory.GetFiles(fixtures, "*.json", SearchOption.AllDirectories)
        |> Array.map (fun p -> Path.GetRelativePath(fixtures, p).Replace('\\', '/'))
        |> Array.filter (fun p -> p <> "SOURCE.json")
        |> Array.sort

    Assert.Equal<string array>(files |> List.map (fun p -> p.Key) |> List.sort |> List.toArray, onDisk)

let caseData () : obj array seq =
    entries "cases" |> Seq.map (fun c -> [| box (text c "file"); box (text c "expect") |])

[<Theory>]
[<MemberData(nameof caseData)>]
let ``each conformance case reads as the manifest expects`` (file: string, expect: string) =
    let outcome =
        match ProvenanceJson.validate (load file), expect with
        | Ok (ProvenanceJson.Reading.Current _), "valid" -> None
        | Ok (ProvenanceJson.Reading.Unversioned _), "valid-unversioned" -> None
        | Ok (ProvenanceJson.Reading.Unsupported _), "unsupported-version" -> None
        | Error _, "invalid" -> None
        | other, _ -> Some(sprintf "%s: expected %s, got %A" file expect other)

    Assert.Equal(None, outcome)

let successorData () : obj array seq =
    entries "successors"
    |> Seq.map (fun c -> [| box (text c "before"); box (text c "after"); box (text c "expect") |])

[<Theory>]
[<MemberData(nameof successorData)>]
let ``each successor pair is judged as the manifest expects`` (before: string, after: string, expect: string) =
    let problems = ProvenanceJson.successorProblems ((load before).AsObject()) ((load after).AsObject())

    match expect with
    | "preserved" -> Assert.True(List.isEmpty problems, sprintf "%s -> %s: %A" before after problems)
    | "destructive" -> Assert.False(List.isEmpty problems, sprintf "%s -> %s was not detected as destructive" before after)
    | other -> failwithf "unknown expectation %s" other

[<Fact>]
let ``every end-to-end step is valid and each hop preserves its predecessor`` () =
    // The chain's cross-system expectations (`expected.chain`) need a lineage
    // walker Strata does not use; what Strata does need is covered: every step
    // reads, and every successor hop is non-destructive.
    let steps = manifest.Value.["e2e"].["steps"].AsArray() |> Seq.map (fun n -> n.AsObject()) |> Seq.toList
    Assert.NotEmpty steps

    for step in steps do
        let file = text step "file"

        match ProvenanceJson.validate (load file) with
        | Ok (ProvenanceJson.Reading.Current _) -> ()
        | other -> failwithf "%s: %A" file other

        match step.["successorOf"] with
        | null -> ()
        | previous ->
            let problems =
                ProvenanceJson.successorProblems ((load (previous.GetValue<string>())).AsObject()) ((load file).AsObject())

            Assert.True(List.isEmpty problems, sprintf "%s: %A" file problems)

let validFiles () : obj array seq =
    entries "cases"
    |> Seq.filter (fun c -> (text c "expect").StartsWith "valid" || text c "expect" = "unsupported-version")
    |> Seq.map (fun c -> [| box (text c "file") |])

[<Theory>]
[<MemberData(nameof validFiles)>]
let ``a readable record is written back unchanged`` (file: string) =
    let original = load file

    match ProvenanceJson.read original with
    | Ok reading ->
        let written = JsonNode.Parse(ProvenanceJson.toText (ProvenanceJson.raw reading))
        Assert.True(JsonNode.DeepEquals(original, written), file)
    | Error problems -> failwithf "%s: %A" file problems

let private contribution key kind operation at : Contribution =
    { Key = key
      Operations = [ operation ]
      At = at
      Last = None
      Actor =
        { Kind = kind
          Id = "openai/codex"
          Provider = Some "openai"
          Model = Some "unknown"
          Runtime = Some "codex" }
      Reason = None
      Evidence = [] }

[<Fact>]
let ``appending keeps every field this build does not model`` () =
    let before = (load "valid/unknown-fields-preserved.json").AsObject()

    match ProvenanceJson.append (contribution "EXE-strata.run-1" ActorKind.Agent Operation.Approved "2026-09-26T11:00:00.000Z") before with
    | Ok after ->
        Assert.Empty(ProvenanceJson.successorProblems before after)
        Assert.NotNull(after.["x-future-envelope"])
        Assert.NotNull(after.["contributions"].["EXE-20260926T080000000Z-a1a1a1a1"].["attestation"])
        Assert.Equal("platform", after.["contributions"].["EXE-20260926T080000000Z-a1a1a1a1"].["actor"].["x-team"].GetValue<string>())
    | Error problems -> failwithf "%A" problems

[<Fact>]
let ``appending refuses an unsupported major, a second creation, and re-attribution`` () =
    let future = (load "unsupported/future-major.json").AsObject()
    Assert.True(Result.isError (ProvenanceJson.append (contribution "EXE-strata.run-1" ActorKind.Agent Operation.Approved "2026-09-26T11:00:00.000Z") future))

    let minimal = (load "valid/minimal-agent.json").AsObject()
    Assert.True(Result.isError (ProvenanceJson.append (contribution "EXE-strata.run-1" ActorKind.Agent Operation.Created "2026-09-26T11:00:00.000Z") minimal))

    // The same run key claimed by a different actor.
    Assert.True(
        Result.isError (
            ProvenanceJson.append (contribution "EXE-20260926T080000000Z-a1a1a1a1" ActorKind.Agent Operation.Modified "2026-09-26T11:00:00.000Z") minimal
        )
    )

[<Fact>]
let ``a derived record carries its source verbatim and never adopts its contributors`` () =
    let source = (load "valid/minimal-agent.json").AsObject()
    let creator = contribution "EXE-strata.run-1" ActorKind.Agent Operation.Created "2026-09-26T11:00:00.000Z"

    match ProvenanceJson.derive "strata:artifact/sha256:ab" creator [ "strata:project/p@sha256:cd" ] [ "praxis:RQ-APP-2026-A007", source ] with
    | Ok derived ->
        let contributions = derived.["contributions"].AsObject() |> Seq.map (fun p -> p.Key) |> Seq.toList
        Assert.Equal<string list>([ "EXE-strata.run-1" ], contributions)
        Assert.True(JsonNode.DeepEquals(source, derived.["sources"].["praxis:RQ-APP-2026-A007"]))

        let lineage = derived.["derivedFrom"].AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> Seq.toList
        Assert.Contains("strata:project/p@sha256:cd", lineage)
        Assert.Contains("praxis:RQ-APP-2026-A007", lineage)
        Assert.True(Result.isOk (ProvenanceJson.validate derived))
    | Error problems -> failwithf "%A" problems

// ---- who is acting ----------------------------------------------------------

let private resolveWith (declared: Attribution.Declared) (variables: (string * string) list) =
    let lookup name = variables |> List.tryFind (fst >> (=) name) |> Option.map snd

    match Attribution.resolve declared lookup "20260926T100000000Z-0a0b0c0d" with
    | Ok context -> context
    | Error message -> failwithf "resolution failed: %s" message

[<Fact>]
let ``nothing declared resolves to unknown, keyed by this strata run`` () =
    let context = resolveWith Attribution.nothingDeclared []
    Assert.Equal(Actor.unknown, context.Actor)
    Assert.False context.Identified
    Assert.Equal("EXE-strata.20260926T100000000Z-0a0b0c0d", context.Execution)

[<Fact>]
let ``a declared agent is an agent, with unknown attributes spelled unknown`` () =
    let context =
        resolveWith
            Attribution.nothingDeclared
            [ "ROS_ACTOR_KIND", "agent"; "ROS_TELEMETRY_PROVIDER", "openai"; "ROS_TELEMETRY_RUNTIME", "codex" ]

    Assert.Equal(ActorKind.Agent, context.Actor.Kind)
    Assert.Equal("openai/codex", context.Actor.Id)
    Assert.Equal(Some "unknown", context.Actor.Model)
    Assert.True context.Identified

[<Fact>]
let ``a known agent runtime is detected without recording its session id`` () =
    let context = resolveWith Attribution.nothingDeclared [ "CLAUDE_CODE_SESSION_ID", "session-secretish-123" ]
    Assert.Equal(ActorKind.Agent, context.Actor.Kind)
    Assert.Equal("anthropic/claude-code", context.Actor.Id)
    Assert.DoesNotContain("session-secretish-123", sprintf "%A" context)

[<Fact>]
let ``github actions with nothing declared is automation keyed by its run`` () =
    let context =
        resolveWith Attribution.nothingDeclared [ "GITHUB_ACTIONS", "true"; "GITHUB_RUN_ID", "9001"; "GITHUB_RUN_ATTEMPT", "2" ]

    Assert.Equal(
        ({ Kind = ActorKind.Automation
           Id = "github/github-actions"
           Provider = Some "github"
           Model = Some "unknown"
           Runtime = Some "github-actions" }: Actor),
        context.Actor)

    Assert.Equal("EXE-strata.gh-9001-2", context.Execution)

[<Fact>]
let ``a propagated praxis execution keys the contribution, and flags beat the environment`` () =
    let context =
        resolveWith
            { Attribution.nothingDeclared with Kind = Some "human"; Actor = Some "kevin" }
            [ "ROS_ACTOR_KIND", "agent"; "ROS_EXECUTION_ID", "EXE-20260926T080000000Z-a1a1a1a1" ]

    Assert.Equal(({ Kind = ActorKind.Human; Id = "kevin"; Provider = None; Model = None; Runtime = None }: Actor), context.Actor)
    Assert.Equal("EXE-20260926T080000000Z-a1a1a1a1", context.Execution)

[<Fact>]
let ``an unknown kind or a malformed execution is refused, not coerced`` () =
    let lookup (variables: (string * string) list) name = variables |> List.tryFind (fst >> (=) name) |> Option.map snd
    Assert.True(Result.isError (Attribution.resolve Attribution.nothingDeclared (lookup [ "ROS_ACTOR_KIND", "robot" ]) "r"))
    Assert.True(Result.isError (Attribution.resolve Attribution.nothingDeclared (lookup [ "ROS_EXECUTION_ID", "not an id" ]) "r"))
    Assert.True(Result.isError (Attribution.resolve Attribution.nothingDeclared (lookup [ "ROS_TELEMETRY_MODEL", "sk-ant-api03-AAAAAAAAAAAAAAAAAAAAAAAA" ]) "r"))

[<Fact>]
let ``approval by an agent or an unknown actor is reported, and refused when a human is required`` () =
    let agent = resolveWith Attribution.nothingDeclared [ "ROS_ACTOR_KIND", "agent" ]
    let unknown = resolveWith Attribution.nothingDeclared []
    let human = resolveWith { Attribution.nothingDeclared with Kind = Some "human"; Actor = Some "kevin" } []

    // A declared human inside a detected agent runtime is a contradiction.
    let humanInsideAgent =
        resolveWith { Attribution.nothingDeclared with Kind = Some "human"; Actor = Some "kevin" } [ "CODEX_THREAD_ID", "t" ]

    for context in [ agent; unknown; humanInsideAgent ] do
        match Attribution.checkApproval false context with
        | Attribution.AcceptedWithWarning _ -> ()
        | other -> failwithf "expected a warning for %A, got %A" context.Actor other

        match Attribution.checkApproval true context with
        | Attribution.Refused _ -> ()
        | other -> failwithf "expected a refusal for %A, got %A" context.Actor other

    Assert.Equal(Attribution.Accepted, Attribution.checkApproval true human)
