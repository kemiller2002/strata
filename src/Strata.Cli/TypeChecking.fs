/// Running the PREPARE pass, and reporting what it could not ask.
///
/// Authority for: how tier 3 (`DF-STRATA-2026-8B60`) reaches the CLI.
///
/// ## Why this is opt-in
///
/// The record warns that the two validation tiers "must not disagree silently".
/// The obvious way to obey that is to run the type check whenever a connection
/// is present — and it is wrong, for a reason worth writing down.
///
/// Most files people validate today are DDL, and `PREPARE` cannot take DDL at
/// all. Running automatically would turn every one of those from a clean exit 0
/// into "types unverifiable" (exit 2) overnight. Everyone would learn to ignore
/// exit 2, which costs more than the check gains — `unverifiable` only works as
/// a signal while it is rare.
///
/// So it is `--types`, and the silence is broken the other way: when a
/// connection IS available and `--types` was not given, the default path says so
/// in one line. A gap a reader can see is not a silent disagreement.
module Strata.Cli.TypeChecking

open Strata.Analysis.DialectPort
open Strata.Host.Postgres

/// Split a file into `(index, offset, text)` the way the parser sees it.
///
/// Through the parser rather than by splitting on semicolons, because a
/// semicolon inside a string literal or a dollar-quoted body is not a statement
/// boundary and `PREPARE` would be handed half a statement.
let statements (parser: IDialectParser) (text: string) =
    parser.ParseScript text
    |> List.indexed
    |> List.choose (fun (i, parsed) ->
        match parsed with
        // A statement that does not parse is Validation's finding to report, not
        // this pass's. Reporting it twice would read as two problems.
        | Failed _ -> None
        | Parsed (location, _) ->
            if location.Length > 0 && location.Offset + location.Length <= text.Length then
                Some(i + 1, location.Offset, text.Substring(location.Offset, location.Length))
            else
                None)

/// Report the pass, and return what it means for the exit code.
///
/// `0` clean, `1` the server rejected something, `2` nothing could be asked.
let report (result: TypeCheck.Result') : int =
    for finding in result.Findings do
        printfn ""
        printfn "  [type error] statement %d at offset %d" finding.Statement finding.Offset
        printfn "         %s" finding.Message
        printfn "         SQLSTATE %s" finding.SqlState

    match result.NotChecked with
    | [] -> ()
    | notChecked ->
        printfn ""
        printfn
            "  %d statement(s) could NOT be type-checked. This is not a pass:"
            (List.length notChecked)

        // Grouped by reason: a file of forty DDL statements should say so once,
        // not forty times. A reader who sees forty identical lines stops reading
        // at the second.
        for reason, group in notChecked |> List.groupBy (fun n -> n.Reason) do
            printfn
                "         %s (statement%s %s)"
                reason
                (if List.length group = 1 then "" else "s")
                (group |> List.map (fun n -> string n.Statement) |> String.concat ", ")

    if not (List.isEmpty result.Findings) then 1
    elif not (List.isEmpty result.NotChecked) then 2
    else
        printfn ""
        printfn "  Types check out: every statement was planned by the server without complaint."
        0
