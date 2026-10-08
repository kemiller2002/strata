/// The Echelon repository lifecycle contract, v1 (echelon-registry
/// spec/repository-lifecycle-contract.md, capability
/// `echelon.repository-lifecycle`).
///
/// Authority for: how a Registry-driven installer such as Conditor establishes,
/// checks and upgrades Strata in a CONSUMER repository. It is not about a
/// database project: `strata.json` and the schema tree stay owned by the
/// project commands (`init --project`, `add`, `sync`).
///
/// What Strata owns in a consumer repository is one file,
/// `.echelon/strata.json`, which pins the Strata release the repository was
/// established with. `verify` then answers the question a pipeline needs before
/// it compiles or deploys anything: is the `strata` on this machine the release
/// this repository pinned? An artifact compiled by one release and checked by
/// another is the drift this exists to make visible.
///
/// The decisions are pure functions of the release identity and the observed
/// file; reading and writing happen in `execute` alone.
module Strata.Cli.Lifecycle

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions

[<Literal>]
let Capability = "echelon.repository-lifecycle"

[<Literal>]
let ContractVersion = 1

[<Literal>]
let SystemId = "strata"

[<Literal>]
let Repository = "kemiller2002/strata"

[<Literal>]
let Executable = "strata"

/// Repository-relative, forward-slashed: the one resource Strata owns.
[<Literal>]
let ManifestPath = ".echelon/strata.json"

/// Exit codes fixed by the contract.
module ExitCode =
    let success = 0
    let invalidInvocation = 2
    let unsatisfied = 3

/// The release this executable is. Both halves come from the assembly's
/// informational version, `<version>+<40-hex commit>`, which the build stamps
/// from `<Version>` and `SourceRevisionId`.
type ReleaseIdentity =
    { ReleaseVersion: string
      SourceCommit: string option }

let private commitPattern = Regex("^[0-9a-f]{40}$")

/// Pure: split an informational version into release version and source
/// commit. A suffix that is not a full commit is not a commit.
let releaseOf (informationalVersion: string) : ReleaseIdentity =
    match informationalVersion.Split('+', 2) with
    | [| version; commit |] when commitPattern.IsMatch commit ->
        { ReleaseVersion = version; SourceCommit = Some commit }
    | parts ->
        { ReleaseVersion = parts[0]; SourceCommit = None }

type Operation =
    | Status
    | Init
    | Verify
    | Doctor
    | Upgrade

module Operation =
    let name =
        function
        | Status -> "status"
        | Init -> "init"
        | Verify -> "verify"
        | Doctor -> "doctor"
        | Upgrade -> "upgrade"

type Request =
    | Version
    | RepositoryOperation of Operation * root: string

/// A lifecycle invocation that could not be understood (exit 2).
type InvocationError = InvocationError of string

/// Pure: recognise a lifecycle invocation.
///
/// `None` means "not a lifecycle invocation" and the ordinary commands handle
/// it. `init` is shared with the project command (`init --project`): it is the
/// lifecycle `init` exactly when `--root` is given, which is the only form the
/// contract ever uses. `status`, `verify`, `doctor`, `upgrade` and `version`
/// are not project commands, so they are always lifecycle invocations.
let tryParse (args: string list) : Result<Request, InvocationError> option =
    let repository operation rest =
        match rest with
        | [ "--root"; root ] when not (String.IsNullOrWhiteSpace root) -> Ok(RepositoryOperation(operation, root))
        | [] -> Error(InvocationError $"{Operation.name operation} needs --root <repository>.")
        | _ ->
            let given = String.Join(" ", rest)
            Error(InvocationError $"{Operation.name operation} takes only --root <repository>; got '{given}'.")

    match args with
    | [ "version" ] -> Some(Ok Version)
    | "version" :: rest ->
        let given = String.Join(" ", rest)
        Some(Error(InvocationError $"version takes no arguments; got '{given}'."))
    | "status" :: rest -> Some(repository Status rest)
    | "verify" :: rest -> Some(repository Verify rest)
    | "doctor" :: rest -> Some(repository Doctor rest)
    | "upgrade" :: rest -> Some(repository Upgrade rest)
    | "init" :: rest when List.contains "--root" rest -> Some(repository Init rest)
    | _ -> None

/// What `.echelon/strata.json` pins.
type Pin =
    { InstalledVersion: string
      SourceCommit: string option }

/// What was found at `.echelon/strata.json`.
type Observed =
    | Absent
    | Unreadable of reason: string
    /// The file names another tool: it is not Strata's to rewrite.
    | Foreign of tool: string
    | Pinned of Pin

let desiredPin (identity: ReleaseIdentity) =
    { InstalledVersion = identity.ReleaseVersion
      SourceCommit = identity.SourceCommit }

type FileOutcome =
    | Created
    | Updated
    | Unchanged

module FileOutcome =
    let name =
        function
        | Created -> "created"
        | Updated -> "updated"
        | Unchanged -> "unchanged"

/// A mutating operation's decision.
type Change =
    | Write of Pin * FileOutcome
    | Keep
    | Refuse of reason: string

let private semver =
    Regex("^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:-([0-9A-Za-z.-]+))?$")

/// Pure: compare two release versions on major.minor.patch, then prerelease
/// (a release outranks its prereleases). `None` when either is not semver.
let compareVersions (a: string) (b: string) : int option =
    let parse (v: string) =
        let m = semver.Match v

        if m.Success then
            Some(int m.Groups[1].Value, int m.Groups[2].Value, int m.Groups[3].Value, m.Groups[4].Value)
        else
            None

    match parse a, parse b with
    | Some(a1, a2, a3, ap), Some(b1, b2, b3, bp) ->
        match compare (a1, a2, a3) (b1, b2, b3) with
        | 0 when ap = bp -> Some 0
        | 0 when ap = "" -> Some 1
        | 0 when bp = "" -> Some -1
        | 0 -> Some(String.CompareOrdinal(ap, bp) |> sign)
        | other -> Some(sign other)
    | _ -> None

let private ownershipRefusal =
    function
    | Unreadable reason ->
        Some $"{ManifestPath} exists but cannot be read as Strata's pin ({reason}); it is not overwritten."
    | Foreign tool ->
        Some $"{ManifestPath} belongs to '{tool}', not Strata; it is not overwritten."
    | _ -> None

/// Pure: `init` reaches the pinned state, or leaves an equal one alone. A
/// repository already pinned to another release is `upgrade`'s business, not
/// `init`'s: init never moves a pin.
let decideInit (identity: ReleaseIdentity) (observed: Observed) : Change =
    let desired = desiredPin identity

    match observed with
    | Absent -> Write(desired, Created)
    | Pinned pin when pin = desired -> Keep
    | Pinned pin ->
        Refuse
            $"{ManifestPath} pins Strata {pin.InstalledVersion}, not this release ({identity.ReleaseVersion}); run `strata upgrade --root <repository>` to move it."
    | other -> ownershipRefusal other |> Option.defaultValue "unreachable" |> Refuse

/// Pure: `upgrade` moves an existing pin to this release. It never installs
/// (that is `init`) and never moves a pin backwards.
let decideUpgrade (identity: ReleaseIdentity) (observed: Observed) : Change =
    let desired = desiredPin identity

    match observed with
    | Absent -> Refuse $"{ManifestPath} is missing: Strata is not installed in this repository; run `strata init --root <repository>`."
    | Pinned pin when pin = desired -> Keep
    | Pinned pin ->
        match compareVersions pin.InstalledVersion identity.ReleaseVersion with
        | Some c when c > 0 ->
            Refuse
                $"{ManifestPath} pins Strata {pin.InstalledVersion}, newer than this release ({identity.ReleaseVersion}); a downgrade is refused."
        | None ->
            Refuse $"{ManifestPath} pins '{pin.InstalledVersion}', which is not a release version."
        | Some _ -> Write(desired, Updated)
    | other -> ownershipRefusal other |> Option.defaultValue "unreachable" |> Refuse

/// Pure: everything that makes the installed state invalid for this release.
/// Empty means healthy.
let problems (identity: ReleaseIdentity) (observed: Observed) : string list =
    match observed with
    | Absent -> [ $"{ManifestPath} is missing: Strata is not installed in this repository." ]
    | Pinned pin ->
        [ if pin.InstalledVersion <> identity.ReleaseVersion then
              $"{ManifestPath} pins Strata {pin.InstalledVersion}, but this executable is {identity.ReleaseVersion}."
          elif pin.SourceCommit <> identity.SourceCommit then
              let show = Option.defaultValue "none"
              $"{ManifestPath} pins source commit {show pin.SourceCommit}, but this executable was built from {show identity.SourceCommit}." ]
    | other -> ownershipRefusal other |> Option.toList

/// Pure: findings `doctor` reports that do not make the state invalid.
let advisories (identity: ReleaseIdentity) : string list =
    [ if identity.SourceCommit.IsNone then
          "this executable carries no source commit: it is a development build, not a release." ]

/// Pure: the bytes Strata writes. Fixed property order and a trailing newline,
/// so writing the same pin twice produces the same file.
let render (pin: Pin) : string =
    let node = JsonObject()
    node["schemaVersion"] <- JsonValue.Create 1
    node["tool"] <- JsonValue.Create SystemId
    node["repository"] <- JsonValue.Create Repository
    node["installedVersion"] <- JsonValue.Create pin.InstalledVersion

    node["sourceCommit"] <-
        match pin.SourceCommit with
        | Some commit -> JsonValue.Create commit :> JsonNode
        | None -> null

    node.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n"

/// Pure: read the pin from file text.
let parse (text: string) : Observed =
    try
        use document = JsonDocument.Parse text
        let root = document.RootElement

        let str (name: string) =
            match root.TryGetProperty name with
            | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
            | _ -> None

        if root.ValueKind <> JsonValueKind.Object then
            Unreadable "not a JSON object"
        else
            match str "tool", str "installedVersion" with
            | Some tool, _ when tool <> SystemId -> Foreign tool
            | Some _, Some version ->
                let commit =
                    match root.TryGetProperty "sourceCommit" with
                    | true, value when value.ValueKind = JsonValueKind.String -> Some(value.GetString())
                    | _ -> None

                Pinned
                    { InstalledVersion = version
                      SourceCommit = commit }
            | None, _ -> Unreadable "no \"tool\" property"
            | Some _, None -> Unreadable "no \"installedVersion\" property"
    with :? JsonException as ex ->
        Unreadable $"not valid JSON: {ex.Message}"

/// The JSON document a lifecycle operation writes to standard output.
let private report
    (operation: Operation)
    (root: string)
    (identity: ReleaseIdentity)
    (healthy: bool)
    (found: string list)
    (notes: string list)
    (file: FileOutcome option)
    =
    let node = JsonObject()
    node["contract"] <- JsonValue.Create Capability
    node["contractVersion"] <- JsonValue.Create ContractVersion
    node["operation"] <- JsonValue.Create(Operation.name operation)
    node["systemId"] <- JsonValue.Create SystemId
    node["releaseVersion"] <- JsonValue.Create identity.ReleaseVersion
    node["root"] <- JsonValue.Create root
    node["healthy"] <- JsonValue.Create healthy
    let toArray (items: string list) = JsonArray(items |> List.map (fun s -> JsonValue.Create s :> JsonNode) |> List.toArray)
    node["problems"] <- toArray found
    node["advisories"] <- toArray notes

    let files = JsonArray()

    match file with
    | Some outcome ->
        let entry = JsonObject()
        entry["path"] <- JsonValue.Create ManifestPath
        entry["outcome"] <- JsonValue.Create(FileOutcome.name outcome)
        files.Add entry
    | None -> ()

    node["files"] <- files
    node.ToJsonString(JsonSerializerOptions(WriteIndented = true))

/// The `version` identity document.
let versionDocument (identity: ReleaseIdentity) : string =
    let node = JsonObject()
    node["systemId"] <- JsonValue.Create SystemId
    node["repository"] <- JsonValue.Create Repository
    node["executable"] <- JsonValue.Create Executable
    node["releaseVersion"] <- JsonValue.Create identity.ReleaseVersion

    node["sourceCommit"] <-
        match identity.SourceCommit with
        | Some commit -> JsonValue.Create commit :> JsonNode
        | None -> null

    let contract = JsonObject()
    contract["id"] <- JsonValue.Create Capability
    contract["contractVersion"] <- JsonValue.Create ContractVersion
    node["provides"] <- JsonArray(contract)
    node.ToJsonString(JsonSerializerOptions(WriteIndented = true))

/// Effect: observe the pin under `root`.
let private observe (root: string) : Observed =
    let path = Path.Combine(root, ".echelon", "strata.json")

    if File.Exists path then parse (File.ReadAllText path)
    elif Directory.Exists path then Unreadable "it is a directory"
    else Absent

/// Effect: run one lifecycle request. Standard output carries exactly one JSON
/// document; diagnostics go to standard error.
let execute (identity: ReleaseIdentity) (request: Result<Request, InvocationError>) : int =
    match request with
    | Error(InvocationError message) ->
        eprintfn "error: %s" message
        ExitCode.invalidInvocation
    | Ok Version ->
        printfn "%s" (versionDocument identity)
        ExitCode.success
    | Ok(RepositoryOperation(operation, given)) ->
        let root = Path.GetFullPath given

        if not (Directory.Exists root) then
            eprintfn "error: --root %s is not an existing directory." given
            ExitCode.invalidInvocation
        else

        let observed = observe root

        let emit healthy found notes file =
            printfn "%s" (report operation root identity healthy found notes file)

            for problem in found do
                eprintfn "strata %s: %s" (Operation.name operation) problem

            if healthy then ExitCode.success else ExitCode.unsatisfied

        let apply change =
            match change with
            | Refuse reason -> emit false [ reason ] [] None
            | Keep -> emit true [] [] (Some Unchanged)
            | Write(pin, outcome) ->
                Directory.CreateDirectory(Path.Combine(root, ".echelon")) |> ignore
                File.WriteAllText(Path.Combine(root, ".echelon", "strata.json"), render pin)
                // The written state is verified, not assumed.
                match problems identity (observe root) with
                | [] -> emit true [] [] (Some outcome)
                | found -> emit false found [] (Some outcome)

        match operation with
        | Init -> apply (decideInit identity observed)
        | Upgrade -> apply (decideUpgrade identity observed)
        | Status
        | Verify ->
            let found = problems identity observed
            emit found.IsEmpty found [] None
        | Doctor ->
            let found = problems identity observed
            emit found.IsEmpty found (advisories identity) None
