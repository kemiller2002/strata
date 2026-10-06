/// Conformance: what `.echelon/foundations.json` declares about the Echelon
/// foundations is true of the repository (DF-STRATA-FND-2026-0001).
///
/// Aegis applies, so each test about it fails if Aegis stops being consumed:
/// referenced at the pinned version, configured, used by the commands, and
/// declared in `aegis-boundaries.json` with exactly the codes the boundary
/// records. Forma, Folio and Limen are declared not applicable, so the tests
/// about them fail if that stops being true: a browser, document or
/// engine/kernel surface appears while the declaration still says otherwise.
module Strata.Tests.FoundationsConformanceTests

open System
open System.IO
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Xunit
open Strata.Cli

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private read (relative: string) = File.ReadAllText(Path.Combine(repoRoot, relative))

let private json (relative: string) = JsonNode.Parse(read relative)

let private str (node: JsonNode) = node.GetValue<string>()

let private capabilities () = (json ".echelon/foundations.json").["capabilities"]

let private required (name: string) = (capabilities ()).[name].["required"].GetValue<bool>()

let private decisionRecord =
    "research/decisions/DF-STRATA-FND-2026-0001--forma-folio-limen-not-applicable-to-a-command-line-tool.md"

// ---- what is declared --------------------------------------------------------

[<Fact>]
let ``foundations.json requires Aegis, Ordo and Praxis and keeps current baselines for the rest`` () =
    let capabilities = capabilities ()

    for name in [ "aegis"; "ordo"; "praxis" ] do
        Assert.True(required name, $"{name} must be required")

    for name, version in [ "aegis", "1.0.0"; "forma", "0.4.1"; "folio", "0.3.0"; "limen", "0.7.1" ] do
        Assert.Equal(version, str capabilities.[name].["version"])

    Assert.Equal("aegis-boundaries.json", str capabilities.["aegis"].["boundaryManifest"])
    // Folio 0.3.0 is a published release now; a source commit is not a pin.
    Assert.Null(capabilities.["folio"].["sourceCommit"])

[<Fact>]
let ``a foundation declared not applicable is justified by an accepted decision record`` () =
    let notApplicable = [ "forma"; "folio"; "limen" ] |> List.filter (required >> not)

    if not (List.isEmpty notApplicable) then
        let record = read decisionRecord
        Assert.Contains("id: DF-STRATA-FND-2026-0001", record)
        Assert.Contains("status: accepted", record)

        for name in notApplicable do
            let title = string (Char.ToUpperInvariant name[0]) + name.Substring 1
            Assert.Contains($"**{title}: not applicable**", record)

// ---- Aegis applies, and is consumed --------------------------------------------

[<Fact>]
let ``Aegis is referenced at the pinned version by the composition root`` () =
    Assert.Contains(
        "<PackageReference Include=\"EchelonFoundry.Aegis.Core\" Version=\"1.0.0\" />",
        read "src/Strata.Cli/Strata.Cli.fsproj"
    )

[<Fact>]
let ``Aegis is configured once and every command runs inside the boundary`` () =
    let program = read "src/Strata.Cli/Program.fs"
    Assert.Contains("Boundary.configure [ Sinks.standardError ]", program)

    // The entry point is guarded.
    Assert.Matches(@"let main argv =\s+Boundary\.attempt aegis ""Strata\.Cli""", program)

    // No command catches an exception for itself and prints it: that is the
    // path that bypassed Aegis and showed operators raw technology text.
    Assert.DoesNotContain("ex.Message", program)
    Assert.False(Regex.IsMatch(program, @"with\s+ex\s*->"), "Program.fs catches an exception outside the Aegis boundary")

    let guarded = Regex.Matches(program, @"Boundary\.attempt aegis ""(Strata\.Cli[\w.]*)""") |> Seq.map _.Groups[1].Value |> Set.ofSeq

    for operation in
        [ "Strata.Cli"
          "Strata.Cli.Keygen"
          "Strata.Cli.Sign"
          "Strata.Cli.ValidateOffline"
          "Strata.Cli.Drift"
          "Strata.Cli.Deploy"
          "Strata.Cli.Plan"
          "Strata.Cli.Validate"
          "Strata.Cli.Check"
          "Strata.Cli.Query" ] do
        Assert.True(guarded.Contains operation, $"{operation} is not run inside the Aegis boundary")

[<Fact>]
let ``aegis-boundaries.json declares exactly the codes the boundary records, and every boundary is guarded`` () =
    let manifest = json "aegis-boundaries.json"
    Assert.Equal("aegis/boundaries/v1", str manifest.["schema"])
    Assert.Equal("Strata", str manifest.["application"])

    let boundaries = manifest.["boundaries"].AsArray() |> Seq.toList

    for boundary in boundaries do
        let name = str boundary.["name"]
        Assert.True(boundary.["guarded"].GetValue<bool>(), $"{name} is not guarded")
        Assert.NotEmpty(boundary.["codes"].AsArray())

    let declared =
        boundaries |> Seq.collect (fun b -> b.["codes"].AsArray() |> Seq.map str) |> Set.ofSeq

    let used =
        Regex.Matches(read "src/Strata.Cli/Boundary.fs", "\"(STRATA\\.[A-Z_.]+)\"")
        |> Seq.map _.Groups[1].Value
        |> Set.ofSeq

    Assert.Equal<Set<string>>(Set.ofList Boundary.codes, used)
    Assert.Equal<Set<string>>(used, declared)

// ---- Forma, Folio and Limen are not applicable, and that stays true -------------

/// Every file in the repository that is not build output or version control.
let private repositoryFiles () =
    let excluded = [ ".git"; "bin"; "obj"; "node_modules" ]

    let rec walk (directory: string) =
        seq {
            for file in Directory.GetFiles directory do
                yield Path.GetRelativePath(repoRoot, file).Replace('\\', '/')

            for child in Directory.GetDirectories directory do
                if not (List.contains (Path.GetFileName child) excluded) then
                    yield! walk child
        }

    walk repoRoot |> Seq.toList

/// The one script in the repository: the ROS launcher, which is repository
/// tooling run by Node, not a browser surface.
let private tooling = set [ "tools/ros_fs_launcher.mjs" ]

let private browserExtensions = [ ".html"; ".htm"; ".css"; ".js"; ".mjs"; ".ts"; ".tsx"; ".jsx"; ".razor"; ".cshtml" ]

[<Fact>]
let ``while Forma or Limen is not applicable there is no browser surface to apply them to`` () =
    if not (required "forma") || not (required "limen") then
        let files = repositoryFiles ()

        let surface =
            files
            |> List.filter (fun path ->
                not (tooling.Contains path)
                && (Path.GetFileName path = "package.json"
                    || List.contains (Path.GetExtension(path).ToLowerInvariant()) browserExtensions))

        let listed = String.Join(", ", surface)

        Assert.True(
            List.isEmpty surface,
            $"A browser surface exists ({listed}). Adopt Forma and Limen and set them required, or supersede DF-STRATA-FND-2026-0001."
        )

        // No WebAssembly host either.
        for project in files |> List.filter (fun p -> p.EndsWith ".csproj" || p.EndsWith ".fsproj") do
            Assert.DoesNotContain("BlazorWebAssembly", read project)
            Assert.DoesNotContain("<RuntimeIdentifier>browser-wasm", read project)

[<Fact>]
let ``while Forma or Folio is not applicable no source composes their elements`` () =
    if not (required "forma") || not (required "folio") then
        let sources =
            repositoryFiles ()
            |> List.filter (fun p -> p.StartsWith "src/" && (p.EndsWith ".fs" || p.EndsWith ".cs"))

        Assert.NotEmpty sources

        for source in sources do
            let text = read source
            Assert.False(Regex.IsMatch(text, @"<ef-[a-z]"), $"{source} composes a Forma or Folio element")
            Assert.DoesNotContain("@echelon-foundry/design-system", text)
            Assert.DoesNotContain("@echelon-foundry/print-components", text)

[<Fact>]
let ``while Limen is not applicable its own configuration says so, with a reason`` () =
    if not (required "limen") then
        let boundary = (json "limen.config.json").["boundary"]
        let rationale = str boundary.["notApplicable"].["rationale"]
        Assert.Contains("command-line", rationale)
        Assert.Null(boundary.["engine"])
        Assert.Null(boundary.["kernel"])

        // Limen stays installed so `limen verify --strict` re-checks the claim.
        Assert.Equal("0.7.1", str (json ".echelon/limen.json").["installedVersion"])
        Assert.True(File.Exists(Path.Combine(repoRoot, ".github", "workflows", "limen-verify.yml")))
