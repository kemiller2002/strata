namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity

/// SQL that redefines an object which already exists: a view or routine
/// replaced from its declaring file, a CHECK constraint re-added, and a
/// routine dropped by its signature (strata#28).
module RedefinitionSql =

    open PostgresSql

    /// The index just past whitespace and comments starting at `i`. `--`
    /// runs to the end of the line; `/* */` nests, as PostgreSQL's does.
    let rec private skipInsignificant (text: string) (i: int) =
        let at k = if k < text.Length then text.[k] else '\000'

        let rec blockEnd k depth =
            if k >= text.Length then text.Length
            elif at k = '*' && at (k + 1) = '/' then (if depth = 1 then k + 2 else blockEnd (k + 2) (depth - 1))
            elif at k = '/' && at (k + 1) = '*' then blockEnd (k + 2) (depth + 1)
            else blockEnd (k + 1) depth

        if i < text.Length && Char.IsWhiteSpace text.[i] then skipInsignificant text (i + 1)
        elif at i = '-' && at (i + 1) = '-' then
            let newline = text.IndexOf('\n', i)
            skipInsignificant text (if newline < 0 then text.Length else newline + 1)
        elif at i = '/' && at (i + 1) = '*' then skipInsignificant text (blockEnd (i + 2) 1)
        else i

    /// The significant words from `i`, each with the index it starts at.
    /// Lazy, so a caller reading the first four never scans a whole body.
    let private words (text: string) (i: int) =
        Seq.unfold
            (fun position ->
                let start = skipInsignificant text position
                let stop = Seq.initInfinite ((+) start) |> Seq.find (fun k -> k >= text.Length || not (Char.IsLetter text.[k]))
                if stop = start then None else Some((text.Substring(start, stop - start).ToUpperInvariant(), start), stop))
            i

    /// The declaring text with its leading `CREATE [OR REPLACE] <kind>` made
    /// `CREATE OR REPLACE <kind>`, for one of `kinds`.
    ///
    /// Comments and line breaks before and between the keywords are allowed,
    /// and a file that already says OR REPLACE is used as it is. Before
    /// strata#28 only a text starting with exactly `CREATE FUNCTION` was
    /// recognised, so a routine file written `CREATE OR REPLACE FUNCTION`, or
    /// opening with a comment, could be created but never redefined. Only the
    /// keywords change; everything else, the body included, is the file's own
    /// text. A shape outside `kinds`, such as a materialized view, which
    /// cannot be replaced in place, yields None and stops the apply.
    let orReplace (kinds: string list) (text: string) : string option =
        match words text 0 |> Seq.truncate 4 |> List.ofSeq with
        | ("CREATE", create) :: ("OR", _) :: ("REPLACE", _) :: (kind, _) :: _
        | ("CREATE", create) :: (kind, _) :: _ when kinds |> List.contains kind ->
            let kindStart = words text create |> Seq.find (fun (word, _) -> word = kind) |> snd
            Some(text.Substring(0, create) + "CREATE OR REPLACE " + text.Substring kindStart)
        | _ -> None

    /// Drops a check and adds it back under its name in one statement, `NOT
    /// VALID`, then validates it, unless the declared clause is itself `NOT
    /// VALID`. VALIDATE is what finds a stored row the new expression
    /// excludes; it fails, and the whole plan with it, which is the gate a
    /// narrowing needs. A widening always passes.
    let replaceCheck (table: QualifiedName) (name: Identifier) (definition: string) =
        let readd = sprintf "ALTER TABLE %s DROP CONSTRAINT %s, ADD CONSTRAINT %s %s" (quoteName table) (quote name) (quote name) definition

        if definition.EndsWith("NOT VALID", StringComparison.OrdinalIgnoreCase) then readd
        else sprintf "%s NOT VALID;\nALTER TABLE %s VALIDATE CONSTRAINT %s" readd (quoteName table) (quote name)

    /// A routine's signature, `"schema"."name"(types)`, as DROP ROUTINE and
    /// `to_regprocedure` read it. The argument types name the overload; they
    /// are type names, not identifiers, so they are written as the catalog
    /// rendered them, unquoted.
    let signature (name: QualifiedName) (argumentTypes: string list) =
        sprintf "%s(%s)" (quoteName name) (String.concat ", " argumentTypes)

    /// `DROP ROUTINE` covers a function and a procedure alike.
    let dropRoutine (name: QualifiedName) (argumentTypes: string list) =
        "DROP ROUTINE " + signature name argumentTypes
