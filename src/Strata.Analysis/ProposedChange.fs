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
        /// Renames a relation, preserving its data.
        ///
        /// NOT inferable from two snapshots — a rename and a drop-plus-add look
        /// identical (§86) — so this only ever comes from an explicit
        /// annotation. It is destructive in the way that matters: every reader
        /// of the OLD name breaks, and the data survives, so the gate weighs
        /// dependents on the name being retired.
        | RenameTable of from: QualifiedName * to': QualifiedName
        /// Renames a column, preserving its data. Same reasoning.
        | RenameColumn of table: QualifiedName * from: Identifier * to': Identifier
        /// Creates a relation. Additive.
        | CreateTable of table: QualifiedName
        /// Creates an index. Additive to READERS — nothing can depend on an
        /// index that does not exist — though building one takes a lock, which
        /// is an operational cost rather than a correctness one.
        | CreateIndex of table: QualifiedName * index: Identifier
        /// Removes an index. Destructive: queries keep working and get slower,
        /// which no error reports.
        | DropIndex of table: QualifiedName * index: Identifier
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
        /// Redefines an existing function or procedure.
        ///
        /// NOT additive. Every caller gets the new behaviour immediately, and
        /// a body change that compiles reports nothing.
        | ReplaceRoutine of routine: QualifiedName
        /// Creates a function or procedure. Additive for the same reason.
        ///
        /// This does NOT mean the routine's BODY is safe — its body may read
        /// objects that a later change removes. That is a question about the
        /// body, which `Validation` answers, not about the creation.
        | CreateRoutine of routine: QualifiedName
        /// Creates a trigger.
        ///
        /// NOT additive, and this is where a trigger parts company with an
        /// index. A new index changes how a query runs; a new trigger changes
        /// what a write DOES — it can raise, rewrite the row, or cascade into
        /// another table — and every existing writer gets that behaviour the
        /// instant it exists. The object is new; the behaviour it overwrites
        /// is not.
        | CreateTrigger of table: QualifiedName * trigger: Identifier
        /// Removes a trigger. Every write that relied on its effect — an audit
        /// row, a maintained timestamp, a denormalised total — silently stops
        /// getting it, and nothing errors.
        | DropTrigger of table: QualifiedName * trigger: Identifier
        /// Redefines a trigger. Both of the above at once.
        | ReplaceTrigger of table: QualifiedName * trigger: Identifier
        /// Inserts a row a reference table must contain.
        ///
        /// Additive: nothing can already depend on a row that does not exist,
        /// and a lookup row is a precondition for the rows that will reference
        /// it, not a change to any of them.
        ///
        /// The row is named by its KEY, rendered as the server renders it, so
        /// a plan reads `insert-row ref.account_type (3)` rather than repeating
        /// the whole row.
        | InsertRow of table: QualifiedName * key: string
        /// Changes a row a reference table already contains.
        ///
        /// NOT additive. A reference row is read by everything that joins to
        /// the table, so changing a label or a rate changes what every one of
        /// those queries returns, immediately and with no error.
        | UpdateRow of table: QualifiedName * key: string
        /// Adds a constraint. Can fail against existing data, but breaks no
        /// reader.
        ///
        /// `None` for a constraint the declaring file did not name: the server
        /// assigns the name, so there is none to report yet. It is not a
        /// constraint called nothing, and it must never be given a placeholder
        /// — see `Schema.ConstraintName`.
        | AddConstraint of table: QualifiedName * constraintName: Identifier option
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
            | RenameTable _ -> "rename-table"
            | RenameColumn _ -> "rename-column"
            | CreateIndex _ -> "create-index"
            | DropIndex _ -> "drop-index"
            | InsertRow _ -> "insert-row"
            | UpdateRow _ -> "update-row"
            | CreateTrigger _ -> "create-trigger"
            | DropTrigger _ -> "drop-trigger"
            | ReplaceTrigger _ -> "replace-trigger"
            | CreateView _ -> "create-view"
            | ReplaceView _ -> "replace-view"
            | ReplaceRoutine _ -> "replace-routine"
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
            | RenameTable (table, _)
            | RenameColumn (table, _, _)
            | CreateView table
            | ReplaceView table
            | ReplaceRoutine table
            | CreateRoutine table
            | CreateIndex (table, _)
            | DropIndex (table, _)
            | InsertRow (table, _)
            | UpdateRow (table, _)
            | CreateTrigger (table, _)
            | DropTrigger (table, _)
            | ReplaceTrigger (table, _)
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
            | DropIndex _
            // A trigger is the one CREATE in this list that is not additive.
            // See the case's own comment: creating one changes what every
            // existing write does.
            | CreateTrigger _
            | DropTrigger _
            | ReplaceTrigger _
            | DropTable _
            | ReplaceView _
            | ReplaceRoutine _
            | RenameTable _
            | RenameColumn _
            | AlterColumnType _
            | TruncateTable _
            // Every query joining the reference table sees the new value at
            // once, and none of them errors.
            | UpdateRow _
            | UnclassifiedChange _ -> true
            | AddColumn _
            | CreateTable _
            | InsertRow _
            | CreateIndex _
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
                            | "add-constraint", name -> AddConstraint(table, name)
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
