namespace Strata.Host.Files

open System
open System.Text.RegularExpressions

/// Reading rename intent out of a declared object file.
///
/// Authority for: what `-- strata:renamed_from` means and where it applies.
///
/// ## Why an annotation is required
///
/// Notebook §86 names this directly: a rename and a drop-plus-add produce
/// IDENTICAL desired states. `orders` gone and `customer_orders` present says
/// nothing about whether the data should move. `ER-010` — desired state alone
/// does not authorize every transition — makes guessing forbidden rather than
/// merely unwise: guessing wrong either destroys a table or silently keeps one
/// that should have gone.
///
/// So intent is declared, never inferred. `Q-004` asked how objects keep a
/// stable identity across a rename; this is the answer, and it is deliberately
/// the cheapest one that works.
///
/// ## The form
///
/// ```sql
/// -- strata:renamed_from sales.legacy_orders
/// CREATE TABLE sales.orders (
///     id      bigint NOT NULL,
///     total   numeric(12,2), -- strata:renamed_from amount
///     ...
/// );
/// ```
///
/// A comment BEFORE the column list renames the OBJECT. A comment on the same
/// line as a column definition renames that COLUMN. Same-line attribution is
/// chosen over preceding-line because it survives reformatting: a formatter
/// may move a standalone comment, but it keeps a trailing one with its line.
///
/// Comments are read from the RAW TEXT because libpg_query discards them —
/// they never reach the parse tree, so there is nothing to read there.
///
/// ## Deliberately not supported
///
/// A rename is read from the file that declares the NEW name only. There is no
/// mechanism for renaming an object out of a file that no longer exists, which
/// is correct: the annotation lives with the object that survives.
module RenameAnnotations =

    // A quoted identifier may appear in the old name, so `"` is part of the
    // character class — doubled here because this is a verbatim string.
    let private annotation =
        Regex(@"--\s*strata:renamed_from\s+([A-Za-z0-9_.""]+)", RegexOptions.Compiled)

    type Renames =
        { /// The object's previous name, if the file declares one.
          Object: string option
          /// Previous name -> current name, per column.
          Columns: (string * string) list }

    [<RequireQualifiedAccess>]
    module Renames =

        let none = { Object = None; Columns = [] }

    /// The column a definition line declares, if it looks like one.
    ///
    /// Deliberately shallow: the first identifier on the line. A line inside a
    /// column list that starts with a name IS a column definition, and one that
    /// starts with CONSTRAINT or a closing paren is not.
    let private columnOnLine (line: string) =
        let trimmed = line.Trim().TrimStart('(').Trim()

        let firstWord =
            trimmed.Split([| ' '; '\t'; '('; ',' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead

        match firstWord with
        | None -> None
        | Some word ->
            let bare = word.Trim('"')
            let upper = bare.ToUpperInvariant()

            if upper = "CONSTRAINT" || upper = "PRIMARY" || upper = "UNIQUE"
               || upper = "CHECK" || upper = "FOREIGN" || upper = "LIKE"
               || upper = "CREATE" || upper = ")" || bare = "" then
                None
            else
                Some bare

    /// Read every rename annotation from one object file.
    let read (contents: string) : Renames =
        // The column list starts at the first `(`. Anything before it belongs
        // to the object; anything after belongs to a column.
        let bodyStart =
            let index = contents.IndexOf '('
            if index < 0 then contents.Length else index

        let lines = contents.Replace("\r\n", "\n").Split('\n')

        let mutable offset = 0
        let objectRenames = ResizeArray<string>()
        let columnRenames = ResizeArray<string * string>()

        for line in lines do
            let lineStart = offset
            offset <- offset + line.Length + 1

            let matched = annotation.Match line

            if matched.Success then
                let oldName = matched.Groups.[1].Value

                if lineStart < bodyStart then objectRenames.Add oldName
                else
                    // Strip the comment before looking for the column, so the
                    // annotation's own text cannot be mistaken for one.
                    let beforeComment = line.Substring(0, line.IndexOf "--")

                    match columnOnLine beforeComment with
                    | Some column -> columnRenames.Add(oldName, column)
                    // An annotation inside the body on a line that declares no
                    // column has nothing to attach to. Ignored rather than
                    // guessed at; a rename Strata cannot place is one it must
                    // not act on.
                    | None -> ()

        { Object = Seq.tryHead objectRenames
          Columns = List.ofSeq columnRenames }
