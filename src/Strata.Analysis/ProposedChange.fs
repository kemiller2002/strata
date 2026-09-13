namespace Strata.Analysis

open Strata.Semantic.Identity
open Strata.Semantic.Schema
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

    /// Which kind of constraint a change acts on.
    ///
    /// Carried on the change because the DDL for each is different, and the
    /// diff knows which it is at the moment it decides: recovering it later by
    /// searching a table for a matching name loses the answer for an unnamed
    /// constraint, which has no name to search by.
    ///
    /// Qualified access: `Check` and `Unique` are ordinary words, and
    /// `ForeignKey` already names an evidence source elsewhere.
    [<RequireQualifiedAccess>]
    type ConstraintKind =
        | PrimaryKey
        | Unique
        | ForeignKey
        | Check

    [<RequireQualifiedAccess>]
    module ConstraintKind =

        /// Wire tag, hand-written per Boundary Preservation.
        let tag (kind: ConstraintKind) =
            match kind with
            | ConstraintKind.PrimaryKey -> "primary-key"
            | ConstraintKind.Unique -> "unique"
            | ConstraintKind.ForeignKey -> "foreign-key"
            | ConstraintKind.Check -> "check"

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
        /// Creates a schema. Additive, and the precondition for everything
        /// else: a project's first apply against an empty database has nothing
        /// to put its tables IN until this runs.
        ///
        /// There is deliberately no DropSchema. A schema is a container, and
        /// dropping one takes everything inside it — including objects the
        /// project never declared and therefore never claimed. Absence from
        /// desired state is not authority to remove it (`NG-006`), and here the
        /// blast radius is the whole namespace rather than one object.
        | CreateSchema of schema: Identifier
        /// Grants privileges to one grantee on one object.
        ///
        /// Additive in the sense that nothing breaks — but it widens who can
        /// reach the data, which is a decision rather than a mechanical
        /// consequence, and a grant to PUBLIC is a different decision again.
        /// The target is a `GrantTarget` and not a name, because the three
        /// kinds take three different `GRANT` spellings. Rendering a schema
        /// grant with a relation's spelling would grant on a table that
        /// happens to share the name, or fail — never the intended thing.
        | GrantPrivileges of target: GrantTarget * grantee: string * privileges: string list
        /// Takes privileges away from one grantee on one object.
        ///
        /// Destructive, and invisible until it bites: whatever ran as that role
        /// starts failing on its next statement. Strata cannot say what that
        /// is, because the corpus records SQL and not the role that runs it.
        | RevokePrivileges of target: GrantTarget * grantee: string * privileges: string list
        /// Creates a sequence. Additive: nothing can already draw from one that
        /// does not exist.
        /// Installs an extension.
        ///
        /// Additive, and not small: an extension brings its own types,
        /// functions and operators, and `citext` alone installs 88 catalog
        /// objects. Nothing that works today stops working, but the database
        /// gains a lot of surface Strata does not manage.
        | CreateExtension of extension: Identifier
        /// Updates an installed extension to a new version.
        ///
        /// What changes is the extension's own objects, not the project's — and
        /// an extension's upgrade script can alter or drop them. Strata cannot
        /// read that script, so it cannot say what a version change does.
        | UpdateExtension of extension: Identifier * version: string
        /// Moves a relocatable extension's objects to another schema.
        ///
        /// Every unqualified reference to one of them stops resolving unless the
        /// new schema is on the search path.
        | SetExtensionSchema of extension: Identifier * schema: Identifier
        /// Creates a row-level security policy.
        ///
        /// Additive in the narrow sense that nothing that works today stops
        /// working — but a PERMISSIVE policy on a table where row-level
        /// security is off changes nothing, and the same policy once it is on
        /// may hide rows from everyone. What a policy does depends on state
        /// outside itself, so the gate never treats one as routine.
        | CreatePolicy of table: QualifiedName * policy: Identifier
        /// Replaces a policy whose definition differs, as a drop and a create.
        ///
        /// Not `ALTER POLICY`: that can change the expressions and the roles
        /// but NOT the command it applies to or whether it is permissive, so a
        /// policy that changed either would be altered into something that
        /// still does not match the file.
        | ReplacePolicy of table: QualifiedName * policy: Identifier
        /// Switches row-level security on for a table.
        ///
        /// The single most consequential change in the vocabulary. With no
        /// policies it hides every row from every role; with them it hides
        /// whatever they do not admit. Either way, queries that worked start
        /// returning fewer rows and NOTHING errors.
        | EnableRowLevelSecurity of table: QualifiedName
        /// Switches row-level security off.
        ///
        /// Exposes every row the policies were hiding. Nothing errors here
        /// either, which is what makes it dangerous.
        | DisableRowLevelSecurity of table: QualifiedName
        /// Makes row-level security apply to the table's owner too.
        | ForceRowLevelSecurity of table: QualifiedName
        /// Stops row-level security applying to the table's owner.
        | NoForceRowLevelSecurity of table: QualifiedName
        | CreateSequence of sequence: QualifiedName
        /// Removes a sequence.
        ///
        /// Destructive in a way that hides: a column DEFAULT naming it keeps
        /// its text, and every INSERT that relies on that default starts
        /// failing. Strata cannot see which defaults name it, because a default
        /// expression is text the catalog renders rather than a dependency it
        /// models.
        | DropSequence of sequence: QualifiedName
        /// Redefines a sequence's increment, bounds, cache or cycle.
        ///
        /// Not additive. The numbers it hands out change, and nothing errors.
        | AlterSequence of sequence: QualifiedName
        /// Creates an enumerated type. Additive: nothing can depend on a type
        /// that does not exist.
        | CreateEnumType of enumType: QualifiedName
        /// Adds a label to an existing enumerated type.
        ///
        /// Additive to the TYPE, and carries a position because an enum's order
        /// is semantic — `'a' < 'b'` is decided by `enumsortorder`, so where the
        /// label goes is part of what is being asked for. `None` means append.
        ///
        /// NOT additive to a transaction, which is the trap. PostgreSQL refuses
        /// to let a value added in a transaction be USED in that same
        /// transaction: "unsafe use of new value". Strata applies a whole plan
        /// in one transaction, so a plan that adds a label AND writes a
        /// reference row using it would fail as a unit. A freshly CREATED type
        /// is exempt — the restriction is about altering a type that already
        /// existed when the transaction began.
        | AddEnumValue of enumType: QualifiedName * value: string * after: string option
        /// Removes an enumerated type.
        | DropEnumType of enumType: QualifiedName
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
        ///
        /// The columns are carried because they are what identifies an UNNAMED
        /// constraint, and because they are most of the DDL that adds one.
        | AddConstraint of
            table: QualifiedName *
            constraintName: Identifier option *
            kind: ConstraintKind *
            columns: Identifier list
        /// Removes a constraint.
        ///
        /// Destructive, though not in the way a dropped column is: every query
        /// keeps working and keeps returning rows. What goes is the GUARANTEE —
        /// the database stops refusing the data the constraint excluded, and
        /// the first anyone hears of it is a row that should not exist.
        ///
        /// Always named: a constraint in the catalog always has one.
        | DropConstraint of table: QualifiedName * constraintName: Identifier * kind: ConstraintKind
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
            | CreateSchema _ -> "create-schema"
            | GrantPrivileges _ -> "grant"
            | RevokePrivileges _ -> "revoke"
            | CreateExtension _ -> "create-extension"
            | UpdateExtension _ -> "update-extension"
            | SetExtensionSchema _ -> "set-extension-schema"
            | CreatePolicy _ -> "create-policy"
            | ReplacePolicy _ -> "replace-policy"
            | EnableRowLevelSecurity _ -> "enable-row-level-security"
            | DisableRowLevelSecurity _ -> "disable-row-level-security"
            | ForceRowLevelSecurity _ -> "force-row-level-security"
            | NoForceRowLevelSecurity _ -> "no-force-row-level-security"
            | CreateEnumType _ -> "create-enum-type"
            | AddEnumValue _ -> "add-enum-value"
            | DropEnumType _ -> "drop-enum-type"
            | CreateSequence _ -> "create-sequence"
            | DropSequence _ -> "drop-sequence"
            | AlterSequence _ -> "alter-sequence"
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
            | DropConstraint _ -> "drop-constraint"
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
            | CreateEnumType table
            | DropEnumType table
            | AddEnumValue (table, _, _)
            | CreateSequence table
            | DropSequence table
            | AlterSequence table
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
            | AddConstraint (table, _, _, _)
            | DropConstraint (table, _, _)
            | CreatePolicy (table, _)
            | ReplacePolicy (table, _)
            | EnableRowLevelSecurity table
            | DisableRowLevelSecurity table
            | ForceRowLevelSecurity table
            | NoForceRowLevelSecurity table
            | TruncateTable table -> Some table
            // Reported through `GrantTarget.name`, which is lossy: a routine's
            // arguments are dropped and a schema comes back unqualified. That
            // is fine for a report and wrong for DDL, which is why the SQL
            // writer matches on the target itself rather than calling this.
            | GrantPrivileges (target, _, _)
            | RevokePrivileges (target, _, _) -> Some(GrantTarget.name target)
            // A schema is not an object IN a schema, so there is no qualified
            // target to report. Saying None beats inventing one.
            | CreateSchema _
            // An extension is not an object in a schema either. Its NAME is not
            // a qualified name, and inventing one would put it in a schema the
            // project may not manage.
            | CreateExtension _
            | UpdateExtension _
            | SetExtensionSchema _
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
            // A default naming it keeps its text and every insert relying on
            // that default starts failing; changing one silently changes the
            // numbers it hands out.
            | DropSequence _
            | AlterSequence _
            // Dropping a type takes every column declared with it. There is no
            // quieter version of this: the columns go with it.
            | DropEnumType _
            // Whatever ran as that role starts failing on its next statement.
            | RevokePrivileges _
            // Rows stop being visible, and nothing errors. Replacing a policy
            // is destructive for the window between the drop and the create,
            // and destructive in the ordinary sense if the new one admits less.
            | EnableRowLevelSecurity _
            | ForceRowLevelSecurity _
            | ReplacePolicy _
            // And the other direction: rows the policies were hiding become
            // visible to everyone. Not "destructive" in the sense of losing
            // data, but it overwrites a decision someone made, which is what
            // this predicate is for.
            | DisableRowLevelSecurity _
            | NoForceRowLevelSecurity _
            // An upgrade script can alter or drop the extension's own objects,
            // and Strata cannot read it. Moving one breaks every unqualified
            // reference to its functions and types.
            | UpdateExtension _
            | SetExtensionSchema _
            // Every query joining the reference table sees the new value at
            // once, and none of them errors.
            | UpdateRow _
            // Nothing breaks and nothing errors. What is lost is the promise
            // that the excluded data cannot arrive.
            | DropConstraint _
            | UnclassifiedChange _ -> true
            | AddColumn _
            | CreateSchema _
            // Additive to the type. The transaction-level hazard is real and is
            // reported by the diff as a disclosure, but this predicate is about
            // whether the CHANGE destroys something, and adding a label does
            // not: no existing value stops being valid.
            | AddEnumValue _
            | CreateEnumType _
            | CreateSequence _
            | CreateExtension _
            | CreatePolicy _
            | GrantPrivileges _
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
                            | "add-constraint", name ->
                                // A parsed ALTER does not say which kind, and
                                // the subcommand carries no column list. Both
                                // are unknown rather than guessed; the DIFF
                                // path, which does know, supplies them.
                                AddConstraint(table, name, ConstraintKind.Check, [])
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
