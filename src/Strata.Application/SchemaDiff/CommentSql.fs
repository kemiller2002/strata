namespace Strata.Application.SchemaDiff

open Strata.Semantic.Schema

/// The `COMMENT ON` statement for a comment target.
///
/// Its own module rather than more arms in `PostgresDdl`, which is at its
/// line budget; `PostgresDdl` still holds the one exhaustive match over
/// `Change` and delegates both comment cases here.
module CommentSql =

    open PostgresSql

    /// The object a `COMMENT ON` names, spelled the way PostgreSQL requires
    /// for its kind. A routine goes through `ROUTINE`, which covers functions
    /// and procedures alike, and a domain through `TYPE`, which covers both.
    let target (t: CommentTarget) =
        let arguments (types: string list) = String.concat ", " types

        match t with
        | CommentTarget.Schema s -> sprintf "SCHEMA %s" (quote s)
        | CommentTarget.Relation (kind, name) ->
            let keyword =
                match kind with
                | RelationKind.Table -> "TABLE"
                | RelationKind.View -> "VIEW"
                | RelationKind.MaterializedView -> "MATERIALIZED VIEW"
                | RelationKind.Sequence -> "SEQUENCE"
                | RelationKind.Index -> "INDEX"

            sprintf "%s %s" keyword (quoteName name)
        | CommentTarget.Column (relation, column) -> sprintf "COLUMN %s.%s" (quoteName relation) (quote column)
        | CommentTarget.Routine (name, types) -> sprintf "ROUTINE %s(%s)" (quoteName name) (arguments types)
        | CommentTarget.Type name -> sprintf "TYPE %s" (quoteName name)
        | CommentTarget.Constraint (table, name) -> sprintf "CONSTRAINT %s ON %s" (quote name) (quoteName table)
        | CommentTarget.Trigger (table, name) -> sprintf "TRIGGER %s ON %s" (quote name) (quoteName table)

    /// Sets the comment. The text is a quoted literal, never interpolated.
    let set (t: CommentTarget) (text: string) = sprintf "COMMENT ON %s IS %s" (target t) (literal text)

    /// Removes the comment: `IS NULL` is how PostgreSQL spells it.
    let remove (t: CommentTarget) = sprintf "COMMENT ON %s IS NULL" (target t)
