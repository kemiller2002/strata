module Strata.Cli.Program

open System
open Strata.Semantic.Identity
open Strata.Semantic.AnalysisScope
open Strata.Analysis.Graph
open Strata.Application
open Strata.Host.Postgres

/// Composition root. Holds no semantic decisions — it parses arguments, calls
/// the adapters, and prints what the application tier returns.

let private usage =
    """strata — semantic infrastructure for relational databases

USAGE
  strata <command> [args] --connection <connection-string> [--json]

COMMANDS
  inspect <schema.object>          Describe an object
  relationships <schema.object>    Relationships touching an object
  path <schema.a> <schema.b>       Relationship path between two objects
  readers <schema.object>          Sources that read an object
  writers <schema.object>          Sources that write an object

OPTIONS
  --connection <s>   PostgreSQL connection string (or set STRATA_PG)
  --json             Machine-readable output (default is human-readable)

NOTES
  Every answer carries its analysis scope. A result is bounded by what was
  actually inspected; "no readers" is not the same claim as "nothing reads it".
"""

/// Parse `schema.object`. An unqualified name is accepted and stays
/// unqualified — resolution against search_path is the analysis tier's job,
/// not the CLI's (RK-006).
let private parseName (text: string) =
    match text.Split('.') with
    | [| schema; name |] -> QualifiedName.qualified (Identifier.unquoted schema) (Identifier.unquoted name)
    | _ -> QualifiedName.unqualified (Identifier.unquoted text)

let private valueOf (flag: string) (argv: string list) =
    argv
    |> List.pairwise
    |> List.tryPick (fun (a, b) -> if a = flag then Some b else None)

[<EntryPoint>]
let main argv =
    let args = List.ofArray argv

    if List.isEmpty args || List.contains "--help" args || List.contains "-h" args then
        printfn "%s" usage
        0
    else

    let connection =
        match valueOf "--connection" args with
        | Some c -> Some c
        | None ->
            match Environment.GetEnvironmentVariable "STRATA_PG" with
            | null | "" -> None
            | value -> Some value

    match connection with
    | None ->
        eprintfn "error: no connection string. Pass --connection or set STRATA_PG."
        2
    | Some connectionString ->

    let positional = args |> List.filter (fun a -> not (a.StartsWith "--")) 

    let query =
        match positional with
        | "inspect" :: name :: _ -> Ok(Retrieval.Inspect(parseName name))
        | "relationships" :: name :: _ -> Ok(Retrieval.Relationships(parseName name))
        | "path" :: a :: b :: _ -> Ok(Retrieval.Path(parseName a, parseName b))
        | "readers" :: name :: _ -> Ok(Retrieval.Readers(parseName name))
        | "writers" :: name :: _ -> Ok(Retrieval.Writers(parseName name))
        | command :: _ -> Error(sprintf "unknown or incomplete command: %s" command)
        | [] -> Error "no command given"

    match query with
    | Error message ->
        eprintfn "error: %s" message
        eprintfn ""
        eprintfn "%s" usage
        2
    | Ok query ->

    try
        let snapshot = CatalogIntrospection.introspect connectionString

        let graph =
            { SemanticGraph.empty with
                Relationships = SemanticGraph.declaredFrom snapshot
                // No corpus is indexed by this command, so there are no
                // observed relationships and no read/write dependencies. The
                // scope below says so rather than leaving the emptiness to be
                // misread as "nothing reads this" (§144.11).
                Dependencies = [] }

        let scope =
            { Scope.nothingAnalyzed with
                LiveDatabaseInspected = true
                SchemaCompleteness = snapshot.Completeness
                Corpus = CorpusScope.empty }

        let answer = Retrieval.answer snapshot graph scope query

        if List.contains "--json" args then
            printfn "%s" (Retrieval.toJson answer)
        else
            printfn "%s" (Retrieval.toText answer)

        0
    with ex ->
        // A host failure is reported as a failure, never as an empty result.
        eprintfn "error: %s" ex.Message
        1
