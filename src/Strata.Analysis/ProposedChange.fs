namespace Strata.Analysis

open Strata.Semantic.Identity
open Strata.Analysis.StatementReferences

/// What a proposed DDL statement would change.
///
/// Authority for: reading a migration's INTENT out of its statements.
///
/// This is the input to a deployment gate. It deliberately models only the
/// changes whose consequences Strata can actually assess today. A statement it
/// cannot classify becomes `UnclassifiedChange` and the gate treats that as a
/// reason to stop, not as permission to proceed (`ER-008`, `P-009`).
module ProposedChange =

    /// A single change a migration proposes.
    type Change =
        /// Removes a column. The destructive case the notebook's §130 example
        /// turns on.
        | DropColumn of table: QualifiedName * column: Identifier
        /// Removes a whole relation.
        | DropTable of table: QualifiedName
        /// Changes a column's type. Not destructive in itself, but every reader
        /// of the column may now receive a different type.
        | AlterColumnType of table: QualifiedName * column: Identifier * newType: string
        /// Adds a column. Additive.
        | AddColumn of table: QualifiedName * column: Identifier
        /// Creates a relation. Additive.
        | CreateTable of table: QualifiedName
        /// Creates a view. Additive: nothing can already depend on an object
        /// that does not yet exist.
        | CreateView of view: QualifiedName
        /// Redefines an existing view.
        ///
        /// NOT additive. `CREATE OR REPLACE VIEW` fails outright if the column
        /// list changes, and succeeds while breaking readers if the SEMANTICS
        /// change — a filter narrowed, a join changed — which no error reports.
        /// It is judged for dependents like any other destructive change.
        | ReplaceView of view: QualifiedName
        /// Creates a function or procedure. Additive for the same reason.
        ///
        /// This does NOT mean the routine's BODY is safe — its body may read
        /// objects that a later change removes. That is a question about the
        /// body, which `Validation` answers, not about the creation.
        | CreateRoutine of routine: QualifiedName
        /// Adds a constraint. Can fail against existing data, but breaks no
        /// reader.
        | AddConstraint of table: QualifiedName * constraintName: Identifier
        /// Removes every row. No schema change, total data loss.
        | TruncateTable of table: QualifiedName
        /// Strata parsed the statement but does not model its consequences.
        /// NOT the same as "harmless".
        | UnclassifiedChange of detail: string

    [<RequireQualifiedAccess>]
    module Change =

        /// Wire tag, hand-written per Boundary Preservation.
        let tag (change: Change) =
            match change with
            | DropColumn _ -> "drop-column"
            | DropTable _ -> "drop-table"
            | AlterColumnType _ -> "alter-column-type"
            | AddColumn _ -> "add-column"
            | CreateTable _ -> "create-table"
            | CreateView _ -> "create-view"
            | ReplaceView _ -> "replace-view"
            | CreateRoutine _ -> "create-routine"
            | AddConstraint _ -> "add-constraint"
            | TruncateTable _ -> "truncate-table"
            | UnclassifiedChange _ -> "unclassified"

        /// The object this change acts on, when there is one.
        let target (change: Change) =
            match change with
            | DropColumn (table, _)
            | DropTable table
            | AlterColumnType (table, _, _)
            | AddColumn (table, _)
            | CreateTable table
            | CreateView table
            | ReplaceView table
            | CreateRoutine table
            | AddConstraint (table, _)
            | TruncateTable table -> Some table
            | UnclassifiedChange _ -> None

        /// Does this change remove or overwrite something that already exists?
        ///
        /// Additive changes cannot break an existing reader; destructive ones
        /// can. This is the distinction the whole gate rests on, and it is
        /// deliberately conservative: `UnclassifiedChange` counts as
        /// destructive because Strata does not know that it isn't.
        let isPotentiallyDestructive (change: Change) =
            match change with
            | DropColumn _
            | DropTable _
            | ReplaceView _
            | AlterColumnType _
            | TruncateTable _
            | UnclassifiedChange _ -> true
            | AddColumn _
            | CreateTable _
            | CreateView _
            | CreateRoutine _
            | AddConstraint _ -> false

    /// Read the proposed changes out of a parsed statement.
    ///
    /// `extraction` supplies the relations; `ddlDetail` is the operation text
    /// the adapter recorded (e.g. "ALTER TABLE", "DROP"). Column-level detail
    /// comes from the statement's own column mentions, which for DDL are the
    /// columns being added or dropped.
    let ofStatement (extraction: StatementExtraction) : Change list =
        let targets =
            extraction.Relations
            |> List.filter (fun m -> m.Role = RelationReference)
            |> List.map (fun m -> m.Name)

        match extraction.Shape with
        | DdlShape operation ->
            let op = operation.ToUpperInvariant()

            match targets with
            | [] -> [ UnclassifiedChange(sprintf "%s with no resolvable target" operation) ]
            | table :: _ ->
                if op.Contains "ALTER" then
                    // Driven by the parse tree's own subcommand types, not by
                    // matching words in the statement text.
                    match extraction.AlterActions with
                    | [] -> [ UnclassifiedChange(sprintf "%s with no recognised action" operation) ]
                    | actions ->
                        actions
                        |> List.map (fun action ->
                            match action.Kind, action.Name with
                            | "add-column", Some column -> AddColumn(table, column)
                            | "drop-column", Some column -> DropColumn(table, column)
                            | "alter-column-type", Some column -> AlterColumnType(table, column, "changed")
                            | "add-constraint", name ->
                                AddConstraint(table, defaultArg name (Identifier.unquoted "unnamed"))
                            | kind, _ ->
                                // A subcommand Strata does not model. Explicitly
                                // unclassified, never silently ignored.
                                UnclassifiedChange(
                                    sprintf "ALTER TABLE %s: %s" (QualifiedName.display table) kind))
                elif op.Contains "DROP" then
                    [ DropTable table ]
                elif op.Contains "CREATE" then
                    [ CreateTable table ]
                else
                    [ UnclassifiedChange operation ]

        | UtilityShape "TRUNCATE" ->
            match targets with
            | [] -> [ UnclassifiedChange "TRUNCATE with no resolvable target" ]
            | tables -> tables |> List.map TruncateTable

        | SelectShape
        | InsertShape
        | UpdateShape
        | DeleteShape -> []

        | UtilityShape other -> [ UnclassifiedChange other ]
        | UnsupportedShape detail -> [ UnclassifiedChange detail ]

    /// Previously refined an ALTER by matching words in the statement text.
    /// Removed: `ofStatement` now reads the parse tree's own subcommand types,
    /// which is both correct and immune to a column named "drop_column".
    let refineWithText (_statementText: string) (change: Change) : Change = change
