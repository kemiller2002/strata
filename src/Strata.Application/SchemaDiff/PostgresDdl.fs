namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

/// The PostgreSQL statement for each change.
///
/// The dialect provider for the plan: everything here is PostgreSQL text, and
/// nothing here decides WHETHER a change happens or in what order. One
/// exhaustive match over `Change`, so a new change kind fails to compile here
/// until someone decides how — or whether — to write it.
module PostgresDdl =

    open PostgresSql

    /// Everything a statement may be written from, taken once from the inputs.
    [<NoComparison>]
    type Sources =
        { /// The verbatim text that declared each object.
          Declarations: (QualifiedName * string) list
          TriggerDeclarations: ((QualifiedName * Identifier) * string) list
          IndexDeclarations: ((QualifiedName * Identifier) * string) list
          PolicyDeclarations: ((QualifiedName * Identifier) * string) list
          Data: ResolvedData list
          DesiredSequences: Sequence list
          DesiredEnums: EnumType list
          /// Declared check expressions as the SERVER renders them. A check's
          /// expression is not recoverable from the parse tree, so this is
          /// the only source for one.
          NormalisedTables: NormalisedTable list
          DesiredTables: Table list
          /// The extensions the project declares, with the schema and version
          /// each file named, if it named them.
          DeclaredExtensions: Extension list }

    let sources (inputs: Inputs) =
        { Declarations = inputs.Declarations
          TriggerDeclarations = inputs.TriggerDeclarations
          IndexDeclarations = inputs.IndexDeclarations
          PolicyDeclarations = inputs.PolicyDeclarations
          Data = inputs.Data
          DesiredSequences = SnapshotObjects.sequences inputs.Desired
          DesiredEnums = SnapshotObjects.enums inputs.Desired
          NormalisedTables = inputs.NormalisedTables
          DesiredTables = SnapshotObjects.tables inputs.Desired
          DeclaredExtensions = inputs.DeclaredExtensions }

    /// DDL for one change, or `None` when Strata cannot write it faithfully.
    ///
    /// Every `None` here is a deliberate refusal, and each is a gap in the
    /// semantic model rather than an oversight: the model carries that a
    /// default or a check constraint EXISTS but not its expression, because
    /// the catalog reports those already normalised and storing a
    /// half-understood expression would be worse than storing none. Emitting a
    /// table without its checks would create an object that differs from what
    /// the project declared while reporting success.
    let statement (sources: Sources) (change: Change) : string option =
        let declarations = sources.Declarations
        let triggerDeclarations = sources.TriggerDeclarations
        let policyDeclarations = sources.PolicyDeclarations
        let data = sources.Data
        let desiredSequences = sources.DesiredSequences
        let desiredEnums = sources.DesiredEnums
        let normalised = sources.NormalisedTables
        let desired = sources.DesiredTables

        let desiredTable name = desired |> List.tryFind (fun t -> Names.same t.Name name)

        let declaredText name =
            declarations
            |> List.tryPick (fun (declared, text) -> if Names.same declared name then Some text else None)

        let declaredTriggerText table trigger =
            triggerDeclarations
            |> List.tryPick (fun ((t, n), text) ->
                if Names.same t table && Identifier.sameName n trigger then Some text else None)

        let declaredPolicyText table policy =
            policyDeclarations
            |> List.tryPick (fun ((t, n), text) ->
                if Names.same t table && Identifier.sameName n policy then Some text else None)

        let desiredSequence name =
            desiredSequences |> List.tryFind (fun (sq: Sequence) -> Names.same sq.Name name)

        let desiredEnum name =
            desiredEnums |> List.tryFind (fun (e: EnumType) -> Names.same e.Name name)

        // A reference row is written from the literals the SERVER produced for
        // the declared row, via quote_nullable. Strata does not re-render a
        // value it never interpreted: what goes into the database is what came
        // back out of the shadow table.
        let declaredRow table key =
            data
            |> List.tryFind (fun d -> d.Table = QualifiedName.display table)
            |> Option.bind (fun d ->
                d.Declared
                |> List.tryFind (fun r -> r.Key = key)
                |> Option.map (fun r -> d, r))

        let columnOf (d: ResolvedData) (row: ResolvedRow) (column: string) =
            d.Columns
            |> List.tryFindIndex (fun c -> c.ToLowerInvariant() = column.ToLowerInvariant())
            |> Option.bind (fun i -> row.Literals |> List.tryItem i)

        match change with
        // An unclassified change describes a difference, not an object, so
        // there is nothing it names that DDL could be written for.
        | UnclassifiedChange _ -> None

        // A view or routine is created by executing the file that declares it.
        // There is nothing to reconstruct: the definition text is not
        // recoverable from the parse tree, so a project without the file cannot
        // create one, and says so by emitting nothing.
        | CreateView name
        | CreateRoutine name -> declaredText name

        // The declaring file, with only its leading keywords made CREATE OR
        // REPLACE, so the author's body is kept byte-for-byte.
        | ReplaceView name -> declaredText name |> Option.bind (RedefinitionSql.orReplace [ "VIEW" ])
        | ReplaceRoutine name -> declaredText name |> Option.bind (RedefinitionSql.orReplace [ "FUNCTION"; "PROCEDURE" ])
        | DropRoutine (name, arguments) -> Some(RedefinitionSql.dropRoutine name arguments)

        // The declaring file holds exactly the DDL the author wrote, defaults
        // and check expressions included. Reconstruction below is the fallback
        // for a snapshot built without files, and it still refuses whatever it
        // cannot render faithfully.
        // Bare CREATE SCHEMA, with no AUTHORIZATION: the connected role owns
        // it, and that is the same role that will create everything in it.
        | CreateSchema schema -> Some(sprintf "CREATE SCHEMA %s" (quote schema))

        | CreateEnumType name ->
            desiredEnum name
            |> Option.map (fun e ->
                sprintf
                    "CREATE TYPE %s AS ENUM (%s)"
                    (quoteName name)
                    (e.Values |> List.map literal |> String.concat ", "))

        | AddEnumValue (name, value, after) ->
            // `AFTER` rather than `BEFORE` throughout: the diff walks the
            // declared order and always knows the label a new one follows, and
            // one form is easier to reason about than two. A value with no
            // predecessor is the first in the type, which is `BEFORE` the
            // current first.
            Some(
                match after with
                | Some predecessor ->
                    sprintf "ALTER TYPE %s ADD VALUE %s AFTER %s" (quoteName name) (literal value) (literal predecessor)
                | None ->
                    match desiredEnum name with
                    | Some e ->
                        match e.Values |> List.tryItem 1 with
                        | Some successor ->
                            sprintf
                                "ALTER TYPE %s ADD VALUE %s BEFORE %s"
                                (quoteName name)
                                (literal value)
                                (literal successor)
                        | None -> sprintf "ALTER TYPE %s ADD VALUE %s" (quoteName name) (literal value)
                    | None -> sprintf "ALTER TYPE %s ADD VALUE %s" (quoteName name) (literal value))

        | DropEnumType name -> Some(sprintf "DROP TYPE %s" (quoteName name))

        // From the declaring file, verbatim, like a view or a routine: a
        // domain's DEFAULT and its CHECK predicates are expressions, and the
        // model carries only the server's rendering of them. Writing the file's
        // own text is the one way to create the domain the project declared
        // rather than a reconstruction of it.
        | CreateDomainType name -> declaredText name

        | DropDomainType name -> Some(sprintf "DROP DOMAIN %s" (quoteName name))

        // The expressions below are the CATALOG's rendering, not the file's, and
        // that is not a shortcut: an ALTER carries one expression and the model
        // has exactly one form of it. `'x'::text` is what the server itself
        // deparsed and is accepted straight back.
        | SetDomainDefault (name, expression, _) ->
            Some(sprintf "ALTER DOMAIN %s SET DEFAULT %s" (quoteName name) expression)

        | DropDomainDefault name -> Some(sprintf "ALTER DOMAIN %s DROP DEFAULT" (quoteName name))
        | SetDomainNotNull name -> Some(sprintf "ALTER DOMAIN %s SET NOT NULL" (quoteName name))
        | DropDomainNotNull name -> Some(sprintf "ALTER DOMAIN %s DROP NOT NULL" (quoteName name))

        | AddDomainConstraint (name, constraintName, definition) ->
            // `pg_get_constraintdef` renders a domain check as `CHECK ((...))`,
            // which is already the whole clause an ADD takes. An unnamed one is
            // written WITHOUT a CONSTRAINT clause, so the server assigns the
            // name — the declaring file said it did not care, and inventing one
            // here is what made unnamed constraints unconvergeable (WI-0054).
            Some(
                match constraintName with
                | Some n -> sprintf "ALTER DOMAIN %s ADD CONSTRAINT %s %s" (quoteName name) (quote n) definition
                | None -> sprintf "ALTER DOMAIN %s ADD %s" (quoteName name) definition
            )

        // Only a NAMED constraint can be dropped: `ALTER DOMAIN ... DROP
        // CONSTRAINT` takes a name and there is no form that takes a predicate.
        // A deployed constraint always has one, so `None` here never arises
        // from introspection — and refusing beats inventing a name to drop.
        | DropDomainConstraint (name, constraintName) ->
            constraintName
            |> Option.map (fun n -> sprintf "ALTER DOMAIN %s DROP CONSTRAINT %s" (quoteName name) (quote n))

        | ValidateDomainConstraint (name, constraintName) ->
            Some(sprintf "ALTER DOMAIN %s VALIDATE CONSTRAINT %s" (quoteName name) (quote constraintName))

        // Written from the model rather than the declaring file, because every
        // property is carried and none of them is an expression. START is what
        // the file asked for; where the sequence already exists, ALTER leaves
        // the current value alone, which is the point — RESTART would hand out
        // a number twice.
        | CreateSequence name
        | AlterSequence name ->
            desiredSequence name
            |> Option.map (fun sq ->
                let body =
                    sprintf
                        "AS %s INCREMENT BY %d MINVALUE %d MAXVALUE %d CACHE %d%s"
                        sq.DataType
                        sq.Increment
                        sq.MinValue
                        sq.MaxValue
                        sq.Cache
                        (if sq.Cycle then " CYCLE" else " NO CYCLE")

                match change with
                | CreateSequence _ ->
                    sprintf "CREATE SEQUENCE %s %s START WITH %d" (quoteName name) body sq.Start
                | _ -> sprintf "ALTER SEQUENCE %s %s" (quoteName name) body)

        | DropSequence name -> Some(sprintf "DROP SEQUENCE %s" (quoteName name))

        // Privileges are keywords and are already uppercase; the grantee is
        // spelled by `granteeSql`, which never quotes PUBLIC.
        | GrantPrivileges (target, grantee, privileges) ->
            Some(
                sprintf
                    "GRANT %s ON %s TO %s"
                    (privilegesSql target privileges)
                    (grantTargetSql target)
                    (granteeSql grantee))

        | RevokePrivileges (target, grantee, privileges) ->
            Some(
                sprintf
                    "REVOKE %s ON %s FROM %s"
                    (privilegesSql target privileges)
                    (grantTargetSql target)
                    (granteeSql grantee))

        | SetComment (target, text) -> Some(CommentSql.set target text)
        | RemoveComment target -> Some(CommentSql.remove target)

        | CreateTable name when (declaredText name).IsSome -> declaredText name

        | CreateTable name ->
            match desiredTable name with
            | None -> None
            | Some table when not (List.isEmpty table.CheckConstraints) -> None
            | Some table when table.Columns |> List.exists (fun c -> c.HasDefault) -> None
            | Some table ->
                let columns = table.Columns |> List.map columnDdl

                let primaryKey =
                    match table.PrimaryKey with
                    | Some pk ->
                        [ sprintf
                            "%sPRIMARY KEY (%s)"
                            (constraintClause pk.ConstraintName)
                            (quoteList pk.Columns) ]
                    | None -> []

                let uniques =
                    table.UniqueConstraints
                    |> List.map (fun u ->
                        sprintf
                            "%sUNIQUE (%s)"
                            (constraintClause u.ConstraintName)
                            (quoteList u.Columns))

                let foreignKeys =
                    table.ForeignKeys
                    |> List.map (fun f ->
                        sprintf
                            "%sFOREIGN KEY (%s) REFERENCES %s (%s)"
                            (constraintClause f.ConstraintName)
                            (quoteList f.Columns)
                            (quoteName f.ReferencedTable)
                            (quoteList f.ReferencedColumns))

                Some(
                    sprintf
                        "CREATE TABLE %s (\n    %s\n)"
                        (quoteName name)
                        (columns @ primaryKey @ uniques @ foreignKeys |> String.concat ",\n    "))

        | RenameTable (from, to') ->
            Some(sprintf "ALTER TABLE %s RENAME TO %s" (quoteName from) (quote to'.Name))

        | RenameColumn (table, from, to') ->
            Some(sprintf "ALTER TABLE %s RENAME COLUMN %s TO %s" (quoteName table) (quote from) (quote to'))

        // The declaring file's own text when there is one: it is the only
        // source of a predicate, a sort order, an expression or an access
        // method. Reconstruction is the fallback, and it refuses any index
        // carrying something the model does not hold — building it without
        // that would index every row, or the wrong thing, and report success.
        | CreateIndex (table, index) ->
            match
                sources.IndexDeclarations
                |> List.tryFind (fun ((t, n), _) -> Names.same t table && Identifier.sameName n index)
            with
            | Some (_, text) -> Some text
            | None ->
            desiredTable table
            |> Option.bind (fun t -> t.Indexes |> List.tryFind (fun i -> Identifier.sameName i.Name index))
            |> Option.filter (fun i -> i.Predicate.IsNone && List.isEmpty i.Unmodelled)
            |> Option.map (fun i ->
                sprintf
                    "CREATE %sINDEX %s ON %s (%s)"
                    (if i.IsUnique then "UNIQUE " else "")
                    (quote i.Name)
                    (quoteName table)
                    (quoteList i.Columns))

        | DropIndex (table, index) ->
            // An index is dropped by name in its schema, not via its table.
            Some(
                sprintf
                    "DROP INDEX %s"
                    (match table.Schema with
                     | Some schema -> sprintf "%s.%s" (quote schema) (quote index)
                     | None -> quote index))

        | AddColumn (table, column) ->
            desiredTable table
            |> Option.bind (fun t -> t.Columns |> List.tryFind (fun c -> Identifier.sameName c.Name column))
            // A column whose default expression Strata does not carry cannot be
            // added faithfully: emitting it without the default would populate
            // existing rows with NULL instead of the declared value.
            |> Option.filter (fun c -> not c.HasDefault)
            |> Option.map (fun c -> sprintf "ALTER TABLE %s ADD COLUMN %s" (quoteName table) (columnDdl c))

        | AlterColumnType (table, column, newType) ->
            Some(
                sprintf
                    "ALTER TABLE %s ALTER COLUMN %s TYPE %s"
                    (quoteName table)
                    (quote column)
                    newType)

        | DropColumn (table, column) ->
            Some(sprintf "ALTER TABLE %s DROP COLUMN %s" (quoteName table) (quote column))

        | DropTable table -> Some(sprintf "DROP TABLE %s" (quoteName table))

        | TruncateTable table -> Some(sprintf "TRUNCATE TABLE %s" (quoteName table))

        | InsertRow (table, key) ->
            declaredRow table key
            |> Option.map (fun (d, row) ->
                sprintf
                    "INSERT INTO %s (%s) VALUES (%s)"
                    (quoteName table)
                    (d.Columns |> List.map (fun c -> quote (Identifier.unquoted c)) |> String.concat ", ")
                    (row.Literals |> String.concat ", "))

        | UpdateRow (table, key) ->
            declaredRow table key
            |> Option.bind (fun (d, row) ->
                // The key identifies the row and is never assigned: an UPDATE
                // that rewrote its own WHERE clause would be a different row.
                let isKey (c: string) =
                    d.KeyColumns |> List.exists (fun k -> k.ToLowerInvariant() = c.ToLowerInvariant())

                let assignments =
                    d.Columns
                    |> List.filter (fun c -> not (isKey c))
                    |> List.choose (fun c ->
                        columnOf d row c
                        |> Option.map (fun v -> sprintf "%s = %s" (quote (Identifier.unquoted c)) v))

                let predicate =
                    d.KeyColumns
                    |> List.map (fun k ->
                        columnOf d row k
                        |> Option.map (fun v -> sprintf "%s = %s" (quote (Identifier.unquoted k)) v))

                // Every declared column is a key column, so there is nothing to
                // set. That is a row that either exists or does not; there is
                // no update it could need, and emitting `SET` with no
                // assignments would not parse.
                if List.isEmpty assignments || predicate |> List.exists Option.isNone then
                    None
                else
                    Some(
                        sprintf
                            "UPDATE %s SET %s WHERE %s"
                            (quoteName table)
                            (assignments |> String.concat ", ")
                            (predicate |> List.choose id |> String.concat " AND ")))

        // A trigger is created by executing the file that declares it, for a
        // stronger reason than a view is. A `WHEN` clause and an `UPDATE OF`
        // column list are not in the model, so a reconstruction would create a
        // trigger that fires MORE OFTEN than the file asked for — and it would
        // report success while doing it.
        | CreateTrigger (table, trigger) -> declaredTriggerText table trigger

        // Drop and recreate, not CREATE OR REPLACE TRIGGER.
        //
        // Both reach the same state, but `CREATE OR REPLACE TRIGGER` needs
        // PostgreSQL 14, and this form needs nothing. The two statements are
        // atomic regardless: `Execution.apply` runs the whole plan in one
        // transaction, and PostgreSQL rolls DDL back like anything else.
        | ReplaceTrigger (table, trigger) ->
            declaredTriggerText table trigger
            |> Option.map (fun text ->
                sprintf "DROP TRIGGER %s ON %s;\n%s" (quote trigger) (quoteName table) text)

        // A policy is created by executing the file that declares it, for the
        // same reason a trigger is: its `USING` and `WITH CHECK` expressions are
        // not in the model — only whether each clause was written — so a
        // reconstruction would create a policy that admits DIFFERENT ROWS from
        // the one the file asked for, and report success.
        | CreatePolicy (table, policy) -> declaredPolicyText table policy

        // Drop and recreate. `ALTER POLICY` exists but can only change the
        // expressions and the roles, not the command it applies to or whether
        // it is permissive — so a policy that changed either would be altered
        // into something that still does not match the file, and the next plan
        // would propose the same change again forever.
        | ReplacePolicy (table, policy) ->
            declaredPolicyText table policy
            |> Option.map (fun text ->
                sprintf "DROP POLICY %s ON %s;\n%s" (quote policy) (quoteName table) text)

        // No IF NOT EXISTS. The diff already established it is absent, and
        // adding the guard would hide a disagreement between what Strata read
        // and what the server holds rather than letting it fail loudly.
        | CreateExtension extension ->
            sources.DeclaredExtensions
            |> List.tryFind (fun d -> Identifier.sameName d.Name extension)
            |> createExtension extension
            |> Some
        | UpdateExtension (extension, version) ->
            Some(sprintf "ALTER EXTENSION %s UPDATE TO %s" (quote extension) (literal version))
        | SetExtensionSchema (extension, schema) ->
            Some(sprintf "ALTER EXTENSION %s SET SCHEMA %s" (quote extension) (quote schema))

        | EnableRowLevelSecurity table ->
            Some(sprintf "ALTER TABLE %s ENABLE ROW LEVEL SECURITY" (quoteName table))
        | DisableRowLevelSecurity table ->
            Some(sprintf "ALTER TABLE %s DISABLE ROW LEVEL SECURITY" (quoteName table))
        | ForceRowLevelSecurity table ->
            Some(sprintf "ALTER TABLE %s FORCE ROW LEVEL SECURITY" (quoteName table))
        | NoForceRowLevelSecurity table ->
            Some(sprintf "ALTER TABLE %s NO FORCE ROW LEVEL SECURITY" (quoteName table))

        // A trigger is dropped by naming it AND its table: trigger names are
        // scoped to the table, not to the schema.
        | DropTrigger (table, trigger) ->
            Some(sprintf "DROP TRIGGER %s ON %s" (quote trigger) (quoteName table))

        | AddConstraint (table, name, kind, columns) ->
            let named = constraintClause name
            let columnList = quoteList columns

            let body =
                match kind with
                | ConstraintKind.PrimaryKey -> Some(sprintf "PRIMARY KEY (%s)" columnList)
                | ConstraintKind.Unique -> Some(sprintf "UNIQUE (%s)" columnList)
                | ConstraintKind.ForeignKey ->
                    // The referenced table and columns are not on the change,
                    // so they come from the declared table — matched on the
                    // columns, which is what identifies an unnamed one.
                    desiredTable table
                    |> Option.bind (fun t ->
                        t.ForeignKeys
                        |> List.tryFind (fun f ->
                            let folded (cs: Identifier list) = cs |> List.map Identifier.folded
                            folded f.Columns = folded columns))
                    |> Option.map (fun f ->
                        sprintf
                            "FOREIGN KEY (%s) REFERENCES %s (%s)"
                            columnList
                            (quoteName f.ReferencedTable)
                            (quoteList f.ReferencedColumns))
                | ConstraintKind.Check ->
                    // A check's expression exists only as the server's
                    // rendering of the declared DDL. Without that rendering
                    // there is nothing to write, and writing the constraint
                    // without its expression is not an option.
                    match name with
                    | None -> None
                    | Some n ->
                        normalised
                        |> List.tryFind (fun x -> x.Table = QualifiedName.display table)
                        |> Option.bind (fun x ->
                            x.Checks
                            |> List.tryPick (fun (checkName, definition) ->
                                if Identifier.sameName (Identifier.unquoted checkName) n then Some definition
                                else None))
                        // pg_get_constraintdef already renders the full
                        // `CHECK (...)`, so it is used as the body rather than
                        // wrapped again.
                        |> Option.map (fun definition -> definition.Trim())

            body |> Option.map (fun b -> sprintf "ALTER TABLE %s ADD %s%s" (quoteName table) named b)

        // Dropped by name, which is the only handle a constraint has. The
        // matching index goes with a primary key or unique constraint
        // automatically; PostgreSQL will not let one be dropped separately.
        | DropConstraint (table, name, _) ->
            Some(sprintf "ALTER TABLE %s DROP CONSTRAINT %s" (quoteName table) (quote name))

        | ReplaceCheckConstraint (table, name, definition) -> Some(RedefinitionSql.replaceCheck table name definition)
