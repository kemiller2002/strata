module Strata.Tests.SchemaDiffArchitectureTests

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open Xunit

/// Conformance rules that keep the schema diff decomposed (STRATA-QUAL-001).
///
/// `SchemaDiff.fs` reached 3694 lines in two days, with every change kind
/// matched in four distant places, because nothing objected. These tests are
/// the objection. They read source text, which is crude, and deliberately so:
/// each rule is one regular expression over files a reviewer can open.
///
///   - SIZE: every module has a line budget in
///     `tests/Strata.Tests/architecture/schemadiff-budget.json`. A module may
///     not exceed it, a budget may not sit more than `slack` lines above its
///     module (so budgets ratchet down as code shrinks), and no budget may
///     exceed `ceiling` without an owned, unexpired exception. That a budget
///     only ever DECREASES across commits is checked against the base branch
///     by `scripts/check-schemadiff-budget-ratchet.sh`.
///   - DIRECTION: model <- drop-safety policy <- family differs <- ordering |
///     PostgreSQL SQL <- orchestration <- rendering. SQL generation never sees
///     rendering; the model and the differs never see SQL generation.
///   - ONE MATCH PER CONCERN: an exhaustive match over `Change` lives only in
///     the SQL, ordering and rendering modules.

let private repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))
let private diffDirectory = Path.Combine(repoRoot, "src", "Strata.Application", "SchemaDiff")
let private budgetFile = Path.Combine(__SOURCE_DIRECTORY__, "architecture", "schemadiff-budget.json")
let private projectFile = Path.Combine(repoRoot, "src", "Strata.Application", "Strata.Application.fsproj")

/// The layer each module belongs to. Declaration order is the dependency
/// order: a layer may reference only what `mayReference` allows.
type Layer =
    | ModelLayer
    | PolicyLayer
    | DiffersLayer
    | OrderingLayer
    | SqlLayer
    | OrchestrationLayer
    | RenderingLayer

let private layers =
    Map.ofList
        [ "Resolved", ModelLayer
          "Model", ModelLayer
          "DropSafety", PolicyLayer
          "Schemas", DiffersLayer
          "Extensions", DiffersLayer
          "RowSecurity", DiffersLayer
          "Grants", DiffersLayer
          "Sequences", DiffersLayer
          "Enums", DiffersLayer
          "Domains", DiffersLayer
          "Tables", DiffersLayer
          "TableIndexes", DiffersLayer
          "TableConstraints", DiffersLayer
          "Objects", DiffersLayer
          "ReferenceRows", DiffersLayer
          "Ordering", OrderingLayer
          "PostgresSql", SqlLayer
          "PostgresDdl", SqlLayer
          "Plan", OrchestrationLayer
          "Render", RenderingLayer ]

/// Ordering and SQL generation are siblings: each may read the differs'
/// shared structures, neither may read the other. Rendering reads only the
/// model, so a change to how SQL is written can never change what a person
/// is told, and vice versa.
let private mayReference (from: Layer) (target: Layer) =
    match from, target with
    | _, ModelLayer -> true
    | PolicyLayer, PolicyLayer -> true
    | DiffersLayer, (PolicyLayer | DiffersLayer) -> true
    | OrderingLayer, (DiffersLayer | OrderingLayer) -> true
    | SqlLayer, SqlLayer -> true
    | OrchestrationLayer, (PolicyLayer | DiffersLayer | OrderingLayer | SqlLayer | OrchestrationLayer) -> true
    | RenderingLayer, RenderingLayer -> true
    | _ -> false

let private rank (layer: Layer) =
    match layer with
    | ModelLayer -> 0
    | PolicyLayer -> 1
    | DiffersLayer -> 2
    | OrderingLayer
    | SqlLayer -> 3
    | OrchestrationLayer -> 4
    | RenderingLayer -> 5

let private moduleFiles () =
    Directory.GetFiles(diffDirectory, "*.fs")
    |> Array.map Path.GetFileName
    |> Array.sort
    |> List.ofArray

/// Source text with whole-line comments removed, so prose that names another
/// module is not mistaken for a reference to it.
let private code (file: string) =
    File.ReadAllLines(Path.Combine(diffDirectory, file))
    |> Array.filter (fun line -> not (line.TrimStart().StartsWith "//"))
    |> String.concat "\n"

/// What a file makes visible to the others: its modules and its top-level
/// types. A reference is a qualified use of a module (`Tables.`), an `open`
/// of one, or any use of a top-level type's name.
let private exportsOf (file: string) =
    let text = File.ReadAllText(Path.Combine(diffDirectory, file))

    let modules =
        Regex.Matches(text, @"^module (\w+)", RegexOptions.Multiline) |> Seq.map (fun m -> m.Groups.[1].Value)

    let types =
        Regex.Matches(text, @"^type (\w+)", RegexOptions.Multiline) |> Seq.map (fun m -> m.Groups.[1].Value)

    (modules |> Seq.map (fun m -> m, true)) |> Seq.append (types |> Seq.map (fun t -> t, false)) |> List.ofSeq

let private references (source: string) (name: string, isModule: bool) =
    if isModule then
        Regex.IsMatch(source, sprintf @"\b%s\.|\bopen %s\b" name name)
    else
        Regex.IsMatch(source, sprintf @"\b%s\b" name)

let private stem (file: string) = Path.GetFileNameWithoutExtension file

[<Fact>]
let ``every SchemaDiff module is assigned a layer`` () =
    let unassigned = moduleFiles () |> List.filter (fun f -> not (Map.containsKey (stem f) layers))
    let stale = layers |> Map.toList |> List.map fst |> List.filter (fun m -> not (File.Exists(Path.Combine(diffDirectory, m + ".fs"))))

    Assert.True(List.isEmpty unassigned, sprintf "assign a layer in SchemaDiffArchitectureTests: %s" (String.Join(", ", unassigned)))
    Assert.True(List.isEmpty stale, sprintf "layer map names modules that no longer exist: %s" (String.Join(", ", stale)))

[<Fact>]
let ``no SchemaDiff module references a layer it may not depend on`` () =
    let files = moduleFiles ()

    let violations =
        [ for file in files do
              let source = code file
              let fromLayer = layers.[stem file]

              for other in files do
                  if other <> file then
                      let toLayer = layers.[stem other]

                      if not (mayReference fromLayer toLayer) then
                          for export in exportsOf other do
                              if references source export then
                                  yield sprintf "%s (%A) references %s from %s (%A)" file fromLayer (fst export) other toLayer ]

    Assert.True(List.isEmpty violations, String.Join("\n", violations))

[<Fact>]
let ``only rendering knows about the deployment gate`` () =
    let offenders =
        moduleFiles ()
        |> List.filter (fun f -> layers.[stem f] <> RenderingLayer)
        |> List.filter (fun f -> Regex.IsMatch(code f, @"\bDeploymentGate\."))

    Assert.True(List.isEmpty offenders, sprintf "DeploymentGate referenced outside rendering: %s" (String.Join(", ", offenders)))

[<Fact>]
let ``compile order follows the layer order`` () =
    let compiled =
        Regex.Matches(File.ReadAllText projectFile, @"<Compile Include=""SchemaDiff/(\w+)\.fs""")
        |> Seq.map (fun m -> m.Groups.[1].Value)
        |> List.ofSeq

    let ranks = compiled |> List.map (fun m -> m, rank layers.[m])

    let outOfOrder =
        ranks
        |> List.pairwise
        |> List.filter (fun ((_, a), (_, b)) -> b < a)
        |> List.map (fun ((m, _), (n, _)) -> sprintf "%s is compiled before %s" m n)

    Assert.Equal(List.length (moduleFiles ()), List.length compiled)
    Assert.True(List.isEmpty outOfOrder, String.Join("\n", outOfOrder))

/// No differ produces `TruncateTable`, so a match arm on it appears only in
/// a function that matches EVERY change kind. Each concern gets one.
[<Fact>]
let ``exhaustive matches over Change live only in SQL, ordering and rendering`` () =
    let allowed = set [ "PostgresDdl.fs"; "Ordering.fs"; "Render.fs" ]

    let holders =
        moduleFiles ()
        |> List.filter (fun f -> Regex.IsMatch(code f, @"^\s*\|\s*TruncateTable\b", RegexOptions.Multiline))
        |> Set.ofList

    Assert.Equal<Set<string>>(allowed, holders)

/// Every removal goes through `DropSafety` (STRATA-QUAL-002).
///
/// Outside the SQL, ordering and rendering modules — which name every case to
/// spell, rank or describe it — a removal case may appear only as the CHANGE a
/// `DropSafety.removal` or `DropSafety.objectRemoval` call decides: on its own
/// line, parenthesised, with that call opening a few lines above. Crude, like
/// every rule here, and the behaviour is pinned separately in
/// `SchemaDiffGoldenTests` (no removal without `--allow-drops`). This rule
/// names the line a reviewer should look at when someone constructs a drop
/// directly.
[<Fact>]
let ``a removal Change is constructed only as the argument of a DropSafety decision`` () =
    let describers = set [ "PostgresDdl.fs"; "Ordering.fs"; "Render.fs"; "PostgresSql.fs" ]
    let removal = String.Join("|", Strata.Tests.SchemaDiffGoldenTests.removalCases)
    let mention = Regex(sprintf @"\b(%s)\b" removal)
    let asArgument = Regex(sprintf @"^\s*\((%s)\b" removal)
    let decision = Regex(@"\bDropSafety\.(removal|objectRemoval)\b")

    let violations =
        [ for file in moduleFiles () do
              if not (Set.contains file describers) then
                  let lines = (code file).Split('\n')

                  for i in 0 .. lines.Length - 1 do
                      if mention.IsMatch lines.[i] then
                          let opened =
                              lines.[max 0 (i - 25) .. i - 1] |> Array.exists (fun l -> decision.IsMatch l)

                          if file = "DropSafety.fs" || not (asArgument.IsMatch lines.[i]) || not opened then
                              yield sprintf "%s: %s" file (lines.[i].Trim()) ]

    Assert.True(List.isEmpty violations, "removal constructed outside DropSafety:\n" + String.Join("\n", violations))

// ---- size budget -------------------------------------------------------------

type private Exception' =
    { Module: string
      Budget: int
      Owner: string
      Reason: string
      Expires: DateTime }

let private readBudget () =
    use document = JsonDocument.Parse(File.ReadAllText budgetFile)
    let root = document.RootElement

    let modules =
        root.GetProperty("modules").EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.GetInt32())
        |> Map.ofSeq

    let exceptions =
        root.GetProperty("exceptions").EnumerateArray()
        |> Seq.map (fun e ->
            { Module = e.GetProperty("module").GetString()
              Budget = e.GetProperty("budget").GetInt32()
              Owner = e.GetProperty("owner").GetString()
              Reason = e.GetProperty("reason").GetString()
              Expires = DateTime.Parse(e.GetProperty("expires").GetString(), Globalization.CultureInfo.InvariantCulture) })
        |> List.ofSeq

    root.GetProperty("ceiling").GetInt32(), root.GetProperty("slack").GetInt32(), modules, exceptions

let private lineCount (file: string) =
    File.ReadAllLines(Path.Combine(diffDirectory, file)).Length

[<Fact>]
let ``every SchemaDiff module has a line budget and no budget is stale`` () =
    let _, _, modules, _ = readBudget ()
    let files = moduleFiles () |> Set.ofList
    let budgeted = modules |> Map.toSeq |> Seq.map fst |> Set.ofSeq

    Assert.True(Set.isEmpty (files - budgeted), sprintf "unbudgeted: %s" (String.Join(", ", files - budgeted)))
    Assert.True(Set.isEmpty (budgeted - files), sprintf "budgeted but absent: %s" (String.Join(", ", budgeted - files)))

[<Fact>]
let ``no SchemaDiff module exceeds its line budget`` () =
    let _, _, modules, _ = readBudget ()

    let over =
        moduleFiles ()
        |> List.choose (fun f ->
            let lines = lineCount f

            match Map.tryFind f modules with
            | Some budget when lines > budget -> Some(sprintf "%s has %d lines, budget %d. Split it, or see the budget file." f lines budget)
            | _ -> None)

    Assert.True(List.isEmpty over, String.Join("\n", over))

[<Fact>]
let ``budgets ratchet down when a module shrinks`` () =
    let _, slack, modules, _ = readBudget ()

    let loose =
        moduleFiles ()
        |> List.choose (fun f ->
            let lines = lineCount f

            match Map.tryFind f modules with
            | Some budget when budget - lines > slack ->
                Some(sprintf "%s has %d lines and a budget of %d; lower the budget to at most %d" f lines budget (lines + slack))
            | _ -> None)

    Assert.True(List.isEmpty loose, String.Join("\n", loose))

[<Fact>]
let ``a budget above the ceiling needs an owned, unexpired exception`` () =
    let ceiling, _, modules, exceptions = readBudget ()
    let today = DateTime.UtcNow.Date

    let malformed =
        exceptions
        |> List.filter (fun e -> String.IsNullOrWhiteSpace e.Owner || String.IsNullOrWhiteSpace e.Reason)
        |> List.map (fun e -> sprintf "exception for %s needs an owner and a reason" e.Module)

    let expired =
        exceptions
        |> List.filter (fun e -> e.Expires < today)
        |> List.map (fun e -> sprintf "exception for %s (owner %s) expired on %s" e.Module e.Owner (e.Expires.ToString "yyyy-MM-dd"))

    let uncovered =
        modules
        |> Map.toList
        |> List.filter (fun (m, budget) ->
            budget > ceiling
            && not (exceptions |> List.exists (fun e -> e.Module = m && e.Expires >= today && e.Budget >= budget)))
        |> List.map (fun (m, budget) -> sprintf "%s has budget %d above the ceiling %d with no covering exception" m budget ceiling)

    let problems = malformed @ expired @ uncovered
    Assert.True(List.isEmpty problems, String.Join("\n", problems))
