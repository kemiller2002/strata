namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

module PostgresSql =

    // ---- DDL emission -------------------------------------------------------

    /// Render an identifier for execution.
    ///
    /// Always quoted. A name from the catalog is already case-folded, so
    /// quoting it changes nothing; a name that was quoted in the file keeps the
    /// case it needs. Emitting unquoted would silently fold a mixed-case
    /// identifier into a different object.
    let quote (identifier: Identifier) =
        "\"" + identifier.Text.Replace("\"", "\"\"") + "\""

    /// Render a string as a SQL literal.
    ///
    /// An enum label is DATA, not an identifier: `CREATE TYPE t AS ENUM ('a')`
    /// takes single quotes, and a label containing one has to double it. Kept
    /// separate from `quote` so the two cannot be confused at a call site — they
    /// look alike and produce different statements.
    let literal (value: string) =
        "'" + value.Replace("'", "''") + "'"

    let quoteName (name: QualifiedName) =
        match name.Schema with
        | Some schema -> sprintf "%s.%s" (quote schema) (quote name.Name)
        | None -> quote name.Name

    /// How a grant's object is SPELLED in `GRANT` and `REVOKE`.
    ///
    /// Not `quoteName`: the three kinds take three different statements.
    /// `GRANT USAGE ON app` grants on a TABLE called `app`, which is a
    /// different object from the schema of that name and may not exist at all;
    /// `GRANT EXECUTE ON f` without a signature resolves against whatever is
    /// deployed rather than what the file named.
    ///
    /// Argument types are emitted unquoted because they are type names rather
    /// than identifiers — `integer`, `character varying`, `timestamp without
    /// time zone` — and quoting one would name a type that does not exist.
    let grantTargetSql (target: GrantTarget) =
        match target with
        | GrantTarget.Relation name -> quoteName name
        | GrantTarget.Schema schema -> sprintf "SCHEMA %s" (quote schema)
        | GrantTarget.Routine (name, arguments) ->
            sprintf "ROUTINE %s(%s)" (quoteName name) (String.concat ", " arguments)
        // The columns go on the PRIVILEGE, not the object: the SQL is
        // `GRANT SELECT (id) ON t`, not `GRANT SELECT ON t (id)`. So a column
        // target renders as the table here and the caller places the column
        // list — the one target whose SQL is not a substitution of this string.
        | GrantTarget.RelationColumn (name, _) -> quoteName name

    /// The privilege list for a `GRANT` or `REVOKE`.
    ///
    /// A column restriction attaches to each PRIVILEGE, not to the object:
    /// `GRANT SELECT (id), UPDATE (id) ON t`. Writing the column once after the
    /// table is not valid SQL, and writing it after only the first privilege
    /// would silently grant the rest table-wide — more access than asked for,
    /// which is the defect this whole area exists to prevent.
    let privilegesSql (target: GrantTarget) (privileges: string list) =
        match target with
        | GrantTarget.RelationColumn (_, column) ->
            privileges |> List.map (fun p -> sprintf "%s (%s)" p (quote column)) |> String.concat ", "
        | _ -> String.concat ", " privileges

    /// A column as it appears in DDL.
    ///
    /// The default EXPRESSION is not carried by the semantic model — only the
    /// fact that one exists — so a column with a default cannot be emitted
    /// without inventing its value. Callers handle that by refusing to emit,
    /// not by dropping the default.
    let columnDdl (column: Column) =
        sprintf
            "%s %s%s"
            (quote column.Name)
            (QualifiedName.display column.Type.TypeName)
            (if column.Type.IsNullable then "" else " NOT NULL")

    /// A grantee as it appears in `GRANT ... TO` and `REVOKE ... FROM`.
    ///
    /// PUBLIC is a keyword, not a role name, so it is never quoted — quoting
    /// it would name a role called "PUBLIC" that almost certainly does not
    /// exist.
    let granteeSql (grantee: string) =
        if grantee.ToUpperInvariant() = "PUBLIC" then "PUBLIC"
        else quote (Identifier.unquoted grantee)

    /// A comma-separated list of quoted identifiers, as in a column list.
    let quoteList (identifiers: Identifier list) =
        identifiers |> List.map quote |> String.concat ", "

    /// The `CONSTRAINT name ` prefix of a table constraint, or nothing.
    ///
    /// An unnamed constraint is written WITHOUT a CONSTRAINT clause, so the
    /// server assigns the name it would have assigned had the declaring file
    /// been executed directly. Inventing one would create an object the
    /// project did not ask for.
    let constraintClause (name: Identifier option) =
        match name with
        | Some n -> sprintf "CONSTRAINT %s " (quote n)
        | None -> ""

    /// `CREATE EXTENSION`, with the schema and version the FILE named, if it
    /// named them.
    ///
    /// Dropping `WITH SCHEMA app` put the extension wherever the session's
    /// search_path pointed — usually `public` — so every default written as
    /// `app.uuid_generate_v4()` failed with 42883 later in the same plan, and
    /// the whole apply rolled back. A file that named neither gets neither:
    /// PostgreSQL's own default is what it asked for.
    let createExtension (extension: Identifier) (declared: Extension option) =
        let schema =
            declared
            |> Option.bind (fun d -> d.Schema)
            |> Option.map (fun s -> sprintf " WITH SCHEMA %s" (quote s))
            |> Option.defaultValue ""

        let version =
            declared
            |> Option.bind (fun d -> d.Version)
            |> Option.map (fun v -> sprintf " VERSION %s" (literal v))
            |> Option.defaultValue ""

        sprintf "CREATE EXTENSION %s%s%s" (quote extension) schema version
