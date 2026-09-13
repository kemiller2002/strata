namespace Strata.Host.Postgres

open System
open Npgsql
open Strata.Semantic.Identity
open Strata.Semantic.Evidence
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope

/// PostgreSQL catalog introspection.
///
/// Authority for: turning a live database's catalog into a `SchemaSnapshot`.
///
/// Two rules govern this module, both from P-009 and RK-004:
///
///   1. A category Strata could not read is reported `Inaccessible` with the
///      reason. It is never reported as an empty result, because "no rows" and
///      "no permission" are different answers and conflating them is how a
///      destructive operation gets approved against a database Strata could not
///      actually see.
///   2. A per-category failure does not abort the snapshot. A partial snapshot
///      that says which parts are missing is more useful, and more honest, than
///      no snapshot at all.
module CatalogIntrospection =

    /// Identifiers arrive from the catalog already in their stored form:
    /// PostgreSQL stored `"MixedCase"` as `MixedCase` and `orders` as `orders`.
    /// A name that is not already lower-case could only have been created
    /// quoted, so it must be quoted to be referenced again.
    let private identifierOf (text: string) =
        if text <> text.ToLowerInvariant() then Identifier.quoted text
        else Identifier.unquoted text

    let private qualified schema name =
        QualifiedName.qualified (identifierOf schema) (identifierOf name)

    /// Read a category, converting any failure into an `Inaccessible` state
    /// rather than letting it escape.
    ///
    /// Catching broadly is deliberate here and is confined to this one
    /// boundary: Tier 4's job is to report what actually happened, including
    /// "we could not find out", as a first-class outcome rather than an
    /// exception crossing into the domain.
    let private readCategory
        (connection: NpgsqlConnection)
        (categoryName: string)
        (sql: string)
        (readRow: NpgsqlDataReader -> 'T)
        : Result<'T list, string> =
        try
            use command = new NpgsqlCommand(sql, connection)
            use reader = command.ExecuteReader() :?> NpgsqlDataReader

            let rows = ResizeArray<'T>()

            while reader.Read() do
                rows.Add(readRow reader)

            Ok(List.ofSeq rows)
        with ex ->
            Error(sprintf "%s: %s" categoryName ex.Message)

    let private str (reader: NpgsqlDataReader) (name: string) =
        let i = reader.GetOrdinal name
        if reader.IsDBNull i then "" else reader.GetString i

    let private strOpt (reader: NpgsqlDataReader) (name: string) =
        let i = reader.GetOrdinal name
        if reader.IsDBNull i then None else Some(reader.GetString i)

    let private boolOf (reader: NpgsqlDataReader) (name: string) =
        let i = reader.GetOrdinal name
        not (reader.IsDBNull i) && reader.GetBoolean i

    let private intOf (reader: NpgsqlDataReader) (name: string) =
        let i = reader.GetOrdinal name
        if reader.IsDBNull i then 0 else int (reader.GetInt16 i)

    let private arrayOf (reader: NpgsqlDataReader) (name: string) =
        let i = reader.GetOrdinal name
        if reader.IsDBNull i then []
        else reader.GetFieldValue<string[]> i |> List.ofArray

    // ---- row shapes ---------------------------------------------------------

    type private RelationRow =
        { Schema: string
          Name: string
          Kind: char
          ExtensionOwned: bool
          /// Catalog visibility does not imply access. See CatalogQueries.relations.
          SchemaAccessible: bool
          Readable: bool }

    type private ColumnRow =
        { Schema: string
          Relation: string
          Column: Column }

    type private ConstraintRow =
        { Schema: string
          Relation: string
          Name: string
          Type: char
          Definition: string
          Columns: string list
          ReferencedSchema: string option
          ReferencedRelation: string option
          ReferencedColumns: string list }

    type private IndexRow =
        { Schema: string
          Relation: string
          Index: Index }

    type private TriggerRow =
        { Schema: string
          Relation: string
          /// `r`/`p` for a table, `v`/`m` for a view. Carried so a trigger on
          /// a view is visibly NOT attached to anything rather than quietly
          /// dropped: `Table` is the only place the model has for one.
          RelationKind: string
          Trigger: Trigger }

    type private RoutineRow =
        { Schema: string
          Name: string
          Kind: char
          ArgumentTypes: string list
          ReturnType: string
          Language: string
          Body: string
          ExtensionOwned: bool }

    /// Introspect a database into a snapshot.
    ///
    /// The connection is opened and closed here; this function performs the
    /// only I/O in the Strata pipeline that touches a customer database.
    let introspect (connectionString: string) : SchemaSnapshot =
        use connection = new NpgsqlConnection(connectionString)
        connection.Open()

        let failures = ResizeArray<string * string>()

        let categoryResult name result =
            match result with
            | Ok rows -> Some rows
            | Error reason ->
                failures.Add(name, reason)
                None

        let serverVersion =
            readCategory connection "server_version" CatalogQueries.serverVersion (fun r -> r.GetString 0)
            |> categoryResult "server_version"
            |> Option.bind List.tryHead
            |> Option.map (fun full ->
                let major =
                    match Int32.TryParse(full.Split('.').[0]) with
                    | true, m -> m
                    | false, _ -> 0

                Fact.declared (Catalog "pg_settings") "current_setting('server_version')" { Major = major; Full = full })

        let relations =
            readCategory connection "relations" CatalogQueries.relations (fun r ->
                { Schema = str r "schema_name"
                  Name = str r "relation_name"
                  Kind = (str r "relkind").[0]
                  ExtensionOwned = boolOf r "extension_owned"
                  SchemaAccessible = boolOf r "schema_accessible"
                  Readable = boolOf r "relation_readable" })
            |> categoryResult "relations"

        let columns =
            readCategory connection "columns" CatalogQueries.columns (fun r ->
                { Schema = str r "schema_name"
                  Relation = str r "relation_name"
                  Column =
                    { Name = identifierOf (str r "column_name")
                      Type =
                        { TypeName = QualifiedName.unqualified (Identifier.unquoted (str r "type_name"))
                          IsNullable = boolOf r "is_nullable" }
                      Position = intOf r "ordinal"
                      HasDefault = boolOf r "has_default"
                      DefaultExpression =
                        // Empty means no default, which `HasDefault` already
                        // says; the option carries only a real expression.
                        match str r "default_expression" with
                        | "" -> None
                        | expression -> Some expression
                      IsGenerated = boolOf r "is_generated"
                      IsIdentity = boolOf r "is_identity" } })
            |> categoryResult "columns"

        let constraints =
            readCategory connection "constraints" CatalogQueries.constraints (fun r ->
                { Schema = str r "schema_name"
                  Relation = str r "relation_name"
                  Name = str r "constraint_name"
                  Type = (str r "constraint_type").[0]
                  Definition = str r "definition"
                  Columns = arrayOf r "column_names"
                  ReferencedSchema = strOpt r "referenced_schema"
                  ReferencedRelation = strOpt r "referenced_relation"
                  ReferencedColumns = arrayOf r "referenced_columns" })
            |> categoryResult "constraints"

        let indexes =
            readCategory connection "indexes" CatalogQueries.indexes (fun r ->
                { Schema = str r "schema_name"
                  Relation = str r "relation_name"
                  Index =
                    { Name = identifierOf (str r "index_name")
                      Columns = arrayOf r "column_names" |> List.map identifierOf
                      IsUnique = boolOf r "is_unique"
                      Predicate = strOpt r "predicate" } })
            |> categoryResult "indexes"

        let triggers =
            readCategory connection "triggers" CatalogQueries.triggers (fun r ->
                { Schema = str r "schema_name"
                  Relation = str r "relation_name"
                  RelationKind = str r "relation_kind"
                  Trigger =
                    { Name = identifierOf (str r "trigger_name")
                      Timing =
                        match str r "timing" with
                        | "before" -> TriggerTiming.Before
                        | "instead" -> TriggerTiming.InsteadOf
                        | _ -> TriggerTiming.After
                      Events = arrayOf r "events"
                      Level =
                        match str r "level" with
                        | "row" -> TriggerLevel.Row
                        | _ -> TriggerLevel.Statement
                      UpdateColumns = arrayOf r "update_columns" |> List.map identifierOf
                      Function =
                        (match strOpt r "function_schema" with
                         | Some schema when schema <> "" ->
                             qualified schema (str r "function_name")
                         | _ -> QualifiedName.unqualified (identifierOf (str r "function_name")))
                      Arguments = arrayOf r "arguments"
                      HasCondition = boolOf r "has_condition" } })
            |> categoryResult "triggers"

        let sequences =
            readCategory connection "sequences" CatalogQueries.sequences (fun r ->
                SequenceObject
                    { Name = qualified (str r "schema_name") (str r "sequence_name")
                      DataType = str r "data_type"
                      Start = r.GetInt64(r.GetOrdinal "start_value")
                      Increment = r.GetInt64(r.GetOrdinal "increment_by")
                      MinValue = r.GetInt64(r.GetOrdinal "min_value")
                      MaxValue = r.GetInt64(r.GetOrdinal "max_value")
                      Cache = r.GetInt64(r.GetOrdinal "cache_size")
                      Cycle = boolOf r "is_cycled"
                      Scope = (if boolOf r "extension_owned" then ExtensionOwned else Observed) })
            |> categoryResult "sequences"

        let enumTypes =
            readCategory connection "enum_types" CatalogQueries.enumTypes (fun r ->
                EnumObject
                    { Name = qualified (str r "schema_name") (str r "type_name")
                      // In enumsortorder, which the query guarantees. Never
                      // sorted here: the order is the type.
                      Values = r.GetFieldValue<string array>(r.GetOrdinal "labels") |> Array.toList
                      Scope = (if boolOf r "extension_owned" then ExtensionOwned else Observed) })
            |> categoryResult "enum_types"

        // Read separately from the domains themselves because a domain has any
        // number of CHECKs and PostgreSQL keeps them in `pg_constraint`, not in
        // `pg_type`. Joined back below, in the catalog's own oid order — which
        // is the order a `CREATE DOMAIN` declared them in.
        let domainConstraints =
            readCategory connection "domain_constraints" CatalogQueries.domainConstraints (fun r ->
                (str r "schema_name", str r "type_name"),
                ({ Name = Some(identifierOf (str r "constraint_name"))
                   Definition = str r "definition"
                   IsValidated = boolOf r "validated" }: DomainConstraint))
            |> categoryResult "domain_constraints"

        // Read WHOLE, never as a closure over the reader: a lambda that reads
        // `r` after `readCategory` has disposed it throws
        // `Cannot access a disposed object`, and it does so only once the
        // constraints are joined on — long after this line.
        let domainTypes =
            readCategory connection "domain_types" CatalogQueries.domainTypes (fun r ->
                { Name = qualified (str r "schema_name") (str r "type_name")
                  BaseType = str r "base_type"
                  Collation = strOpt r "collation_name"
                  NotNull = boolOf r "not_null"
                  Default = strOpt r "default_expression"
                  Constraints = []
                  Scope = (if boolOf r "extension_owned" then ExtensionOwned else Observed) })
            |> categoryResult "domain_types"

        let domains =
            match domainTypes with
            // A domain whose constraints could not be read is NOT reported with
            // none: an empty constraint list reads as "this domain constrains
            // nothing", which a diff would answer by proposing every declared
            // CHECK be added, forever. The category is dropped whole instead,
            // and `completeness` says so (ER-008).
            | Some types when Option.isSome domainConstraints ->
                let byType = domainConstraints |> Option.defaultValue []

                Some(
                    types
                    |> List.map (fun d ->
                        let key =
                            (d.Name.Schema |> Option.map (fun s -> s.Text) |> Option.defaultValue ""), d.Name.Name.Text

                        DomainObject
                            { d with
                                Constraints = byType |> List.filter (fun (k, _) -> k = key) |> List.map snd })
                )
            | _ -> None

        let viewDefinitions =
            readCategory connection "view_definitions" CatalogQueries.viewDefinitions (fun r ->
                (str r "schema_name", str r "relation_name"), str r "definition")
            |> categoryResult "view_definitions"

        let routines =
            readCategory connection "routines" CatalogQueries.routines (fun r ->
                { Schema = str r "schema_name"
                  Name = str r "routine_name"
                  Kind = (str r "kind").[0]
                  ArgumentTypes = arrayOf r "argument_types"
                  ReturnType = str r "return_type"
                  Language = str r "language"
                  Body = str r "body"
                  ExtensionOwned = boolOf r "extension_owned" })
            |> categoryResult "routines"

        // ---- assemble ------------------------------------------------------

        let columnsFor schema relation =
            columns
            |> Option.defaultValue []
            |> List.filter (fun c -> c.Schema = schema && c.Relation = relation)
            |> List.map (fun c -> c.Column)
            |> List.sortBy (fun c -> c.Position)

        let constraintsFor schema relation =
            constraints
            |> Option.defaultValue []
            |> List.filter (fun c -> c.Schema = schema && c.Relation = relation)

        let indexesFor schema relation =
            indexes
            |> Option.defaultValue []
            |> List.filter (fun i -> i.Schema = schema && i.Relation = relation)
            |> List.map (fun i -> i.Index)

        let triggersFor schema relation =
            triggers
            |> Option.defaultValue []
            |> List.filter (fun t -> t.Schema = schema && t.Relation = relation)
            |> List.map (fun t -> t.Trigger)

        // Triggers on a view. `INSTEAD OF` triggers only exist on views, and
        // the model has no place for them, so they are counted and disclosed
        // in the completeness block rather than silently absent.
        let triggersOnNonTables =
            triggers
            |> Option.defaultValue []
            |> List.filter (fun t -> t.RelationKind <> "r" && t.RelationKind <> "p")

        let scopeOf extensionOwned =
            // An extension-owned object is observed, never managed (RK-008).
            if extensionOwned then ExtensionOwned else Observed

        // Objects present in the catalog that the connected role cannot read.
        // These are NOT dropped from the snapshot — dropping them would make
        // them look absent, and absence is the one thing Strata must never
        // fabricate (NG-006). They are reported, and the completeness block
        // says how many there are so no claim about them reads as complete.
        let unreadable =
            relations
            |> Option.defaultValue []
            |> List.filter (fun r -> not r.Readable || not r.SchemaAccessible)

        let objects =
            relations
            |> Option.defaultValue []
            |> List.map (fun rel ->
                let cols = columnsFor rel.Schema rel.Name
                let cons = constraintsFor rel.Schema rel.Name

                match rel.Kind with
                | 'v'
                | 'm' ->
                    ViewObject
                        { Name = qualified rel.Schema rel.Name
                          Columns = cols
                          IsMaterialized = rel.Kind = 'm'
                          Definition =
                            viewDefinitions
                            |> Option.defaultValue []
                            |> List.tryFind (fun ((s, n), _) -> s = rel.Schema && n = rel.Name)
                            |> Option.map snd
                            |> Option.defaultValue ""
                          Scope = scopeOf rel.ExtensionOwned }
                | _ ->
                    TableObject
                        { Name = qualified rel.Schema rel.Name
                          Columns = cols
                          PrimaryKey =
                            cons
                            |> List.tryFind (fun c -> c.Type = 'p')
                            |> Option.map (fun c ->
                                { ConstraintName = Some(identifierOf c.Name)
                                  Columns = c.Columns |> List.map identifierOf })
                          UniqueConstraints =
                            cons
                            |> List.filter (fun c -> c.Type = 'u')
                            |> List.map (fun c ->
                                { ConstraintName = Some(identifierOf c.Name)
                                  Columns = c.Columns |> List.map identifierOf })
                          CheckConstraints =
                            cons
                            |> List.filter (fun c -> c.Type = 'c')
                            |> List.map (fun c ->
                                { ConstraintName = Some(identifierOf c.Name)
                                  Expression = c.Definition })
                          ForeignKeys =
                            cons
                            |> List.filter (fun c -> c.Type = 'f')
                            |> List.choose (fun c ->
                                match c.ReferencedSchema, c.ReferencedRelation with
                                | Some refSchema, Some refRelation ->
                                    Some
                                        { ConstraintName = Some(identifierOf c.Name)
                                          Columns = c.Columns |> List.map identifierOf
                                          ReferencedTable = qualified refSchema refRelation
                                          ReferencedColumns = c.ReferencedColumns |> List.map identifierOf }
                                | _ -> None)
                          Indexes = indexesFor rel.Schema rel.Name
                          Triggers = triggersFor rel.Schema rel.Name
                          Scope = scopeOf rel.ExtensionOwned })

        let routineObjects =
            routines
            |> Option.defaultValue []
            |> List.map (fun r ->
                RoutineObject
                    { Name = qualified r.Schema r.Name
                      Kind = if r.Kind = 'p' then Procedure else Function
                      ArgumentTypes = r.ArgumentTypes
                      ReturnType = if String.IsNullOrWhiteSpace r.ReturnType then None else Some r.ReturnType
                      Language = r.Language
                      // Empty means the server holds no TEXT for this body — a
                      // BEGIN ATOMIC body is a parse tree — not that the body
                      // is empty. Nothing may compare on it.
                      Body = if String.IsNullOrEmpty r.Body then None else Some r.Body
                      Scope = scopeOf r.ExtensionOwned })

        // A category that failed is Inaccessible with its reason; one that
        // succeeded is Complete. Nothing is silently omitted.
        let stateFor name succeeded =
            match failures |> Seq.tryFind (fun (n, _) -> n = name) with
            | Some (_, reason) -> name, Inaccessible reason
            | None -> name, (if succeeded then Complete else NotRequested)

        let completeness =
            Completeness.ofList
                [ stateFor "server_version" (Option.isSome serverVersion)
                  stateFor "relations" (Option.isSome relations)
                  stateFor "columns" (Option.isSome columns)
                  stateFor "constraints" (Option.isSome constraints)
                  stateFor "indexes" (Option.isSome indexes)
                  stateFor "view_definitions" (Option.isSome viewDefinitions)
                  stateFor "routines" (Option.isSome routines)
                  // Categories Strata does not yet read at all. Stated rather
                  // than omitted, so their absence is visible (§6, §129).
                  (match stateFor "triggers" (Option.isSome triggers) with
                   // Read, but the ones on views have nowhere to live. Partial
                   // with a count, never Complete: a category that dropped
                   // rows must not claim it saw everything (ER-008).
                   | name, Complete when not (List.isEmpty triggersOnNonTables) ->
                       name,
                       Partial(
                           sprintf
                               "%d trigger(s) are defined on views, which Strata models nowhere: %s"
                               (List.length triggersOnNonTables)
                               (triggersOnNonTables
                                |> List.map (fun t -> t.Schema + "." + t.Relation + "." + t.Trigger.Name.Text)
                                |> List.sort
                                |> String.concat ", "))
                   | state -> state)
                  stateFor "sequences" (Option.isSome sequences)
                  stateFor "enum_types" (Option.isSome enumTypes)
                  stateFor "domain_types" (Option.isSome domains)
                  stateFor "domain_constraints" (Option.isSome domainConstraints)
                  "rls_policies", NotRequested
                  "extensions", NotRequested
                  "grants", NotRequested

                  // The distinction PostgreSQL forces on us: the catalog listed
                  // these objects, but the connected role cannot read them.
                  // Partial, never Complete — analysis of them is impossible
                  // even though their names are known (RK-004).
                  "relation_access",
                  (if List.isEmpty unreadable then
                       Complete
                   else
                       Partial(
                           sprintf
                               "%d relation(s) visible in the catalog but not readable by the connected role: %s"
                               (List.length unreadable)
                               (unreadable
                                |> List.map (fun r -> r.Schema + "." + r.Name)
                                |> List.sort
                                |> String.concat ", ")
                       )) ]

        { Objects =
            (objects
             @ routineObjects
             @ (sequences |> Option.defaultValue [])
             @ (enumTypes |> Option.defaultValue [])
             @ (domains |> Option.defaultValue []))
            // Deterministic order regardless of catalog return order (NFR-001).
            |> List.sortBy (fun o -> QualifiedName.display (SchemaObject.name o))
          ServerVersion = serverVersion
          Completeness = completeness }

    /// Privileges granted on relations, schemas, routines and columns.
    ///
    /// Returned as a `Result` for the same reason `readSchemas` is: a caller
    /// that could not read the ACLs must not conclude a privilege is absent
    /// and propose granting it, nor conclude one is undeclared and revoke it.
    ///
    /// All four catalogs are read in ONE connection and one `Result`. Reading
    /// them separately would allow a partial answer — relations read, routines
    /// not — and a partial answer here is the dangerous kind: it looks like a
    /// complete one in which nobody holds anything.
    let readGrants (connectionString: string) : Result<Grant list, string> =
        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()

            /// One ACL query's rows, keyed by target and grantee.
            ///
            /// `is_grantable` is carried rather than compared. A privilege held
            /// WITH GRANT OPTION is still that privilege, so it converges
            /// against a declared plain grant; the power to pass it on is
            /// something Strata sees and does not manage, and it is disclosed
            /// rather than dropped on the floor.
            let read (sql: string) (targetOf: NpgsqlDataReader -> GrantTarget) =
                use command = new NpgsqlCommand(sql, connection)
                use reader = command.ExecuteReader() :?> NpgsqlDataReader

                let rows =
                    [ while reader.Read() do
                        yield
                            (targetOf reader, str reader "grantee"),
                            (str reader "privilege_type", reader.GetBoolean(reader.GetOrdinal "is_grantable")) ]

                rows
                |> List.groupBy fst
                |> List.map (fun ((target, grantee), privileges) ->
                    { Target = target
                      Grantee = grantee
                      Privileges = privileges |> List.map (snd >> fst) |> List.distinct |> List.sort
                      Grantable =
                        privileges
                        |> List.filter (snd >> snd)
                        |> List.map (snd >> fst)
                        |> List.distinct
                        |> List.sort })

            let relations =
                read CatalogQueries.grants (fun r ->
                    GrantTarget.Relation(qualified (str r "schema_name") (str r "object_name")))

            let schemaLevel =
                read CatalogQueries.schemaGrants (fun r ->
                    GrantTarget.Schema(Identifier.unquoted (str r "schema_name")))

            let routineLevel =
                read CatalogQueries.routineGrants (fun r ->
                    let arguments =
                        let ordinal = r.GetOrdinal "argument_types"
                        if r.IsDBNull ordinal then []
                        else r.GetFieldValue<string array> ordinal |> List.ofArray

                    GrantTarget.Routine(qualified (str r "schema_name") (str r "object_name"), arguments))

            let columnLevel =
                read CatalogQueries.columnGrants (fun r ->
                    GrantTarget.RelationColumn(
                        qualified (str r "schema_name") (str r "object_name"),
                        Identifier.unquoted (str r "column_name")))

            Ok(relations @ schemaLevel @ routineLevel @ columnLevel)
        with ex ->
            Error ex.Message

    /// Row-level security state and policies, per table.
    ///
    /// A `Result` for the same reason the others are: a caller that could not
    /// read this must not conclude row-level security is off. Concluding that
    /// wrongly is the worst direction here — it reports a table as unprotected
    /// when it is locked down, or, worse, says nothing about one that is
    /// default-denying every row.
    ///
    /// Tables with neither the flag nor a policy do not appear at all: they
    /// have nothing to say, and listing every ordinary table would bury the
    /// three that do.
    let readRowLevelSecurity (connectionString: string) : Result<RowLevelSecurity list, string> =
        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()
            use command = new NpgsqlCommand(CatalogQueries.rowLevelSecurity, connection)
            use reader = command.ExecuteReader() :?> NpgsqlDataReader

            let optional (r: NpgsqlDataReader) (column: string) =
                let ordinal = r.GetOrdinal column
                if r.IsDBNull ordinal then None else Some(r.GetString ordinal)

            // `polcmd` is a single char. Anything else is a PostgreSQL version
            // that grew a command Strata has no case for, and guessing `All`
            // would report a policy as applying to every statement when it
            // applies to one. It is reported as unreadable instead.
            let commandOf (code: string) =
                match code with
                | "*" -> Some PolicyCommand.All
                | "r" -> Some PolicyCommand.Select
                | "a" -> Some PolicyCommand.Insert
                | "w" -> Some PolicyCommand.Update
                | "d" -> Some PolicyCommand.Delete
                | _ -> None

            let rows =
                [ while reader.Read() do
                    let table = qualified (str reader "schema_name") (str reader "object_name")
                    let enabled = reader.GetBoolean(reader.GetOrdinal "enabled")
                    let forced = reader.GetBoolean(reader.GetOrdinal "forced")

                    let policy =
                        match optional reader "policy_name", optional reader "command" with
                        | Some name, Some code ->
                            commandOf code
                            |> Option.map (fun cmd ->
                                { Name = Identifier.unquoted name
                                  Command = cmd
                                  IsPermissive = reader.GetBoolean(reader.GetOrdinal "permissive")
                                  Roles =
                                    (let ordinal = reader.GetOrdinal "roles"

                                     if reader.IsDBNull ordinal then []
                                     else reader.GetFieldValue<string array> ordinal |> List.ofArray)
                                  Using = optional reader "using_expr"
                                  WithCheck = optional reader "check_expr" })
                        // A row with no policy name is a table whose RLS flag is
                        // set and which has none — the default-deny case, and
                        // the whole reason the query left-joins.
                        | _ -> None

                    yield (QualifiedName.display table, table, enabled, forced), policy ]

            rows
            |> List.groupBy fst
            |> List.map (fun ((_, table, enabled, forced), group) ->
                { Table = table
                  Enabled = enabled
                  Forced = forced
                  Policies = group |> List.choose snd |> List.sortBy (fun p -> Identifier.folded p.Name) })
            |> Ok
        with ex ->
            Error ex.Message

    /// Extensions installed in the database.
    ///
    /// A `Result` for the usual reason: a caller that could not read this must
    /// not conclude an extension is absent and propose creating one, which on a
    /// database that already has it fails the whole plan.
    let readExtensions (connectionString: string) : Result<Extension list, string> =
        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()
            use command = new NpgsqlCommand(CatalogQueries.extensions, connection)
            use reader = command.ExecuteReader() :?> NpgsqlDataReader

            Ok
                [ while reader.Read() do
                    yield
                        { Name = Identifier.unquoted (str reader "name")
                          Schema = Some(Identifier.unquoted (str reader "schema_name"))
                          Version = Some(str reader "version")
                          IsRelocatable = reader.GetBoolean(reader.GetOrdinal "relocatable") } ]
        with ex ->
            Error ex.Message

    /// Schema names that exist in the database.
    ///
    /// Returned as a `Result` rather than folded into the snapshot, and the
    /// distinction is load-bearing: a caller that cannot read this must not
    /// conclude a schema is missing and propose creating one. An `Error` means
    /// "could not tell", which is not "absent".
    let readSchemas (connectionString: string) : Result<string list, string> =
        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()
            use command = new NpgsqlCommand(CatalogQueries.schemas, connection)
            use reader = command.ExecuteReader()

            Ok [ while reader.Read() do
                     if not (reader.IsDBNull 0) then
                         yield reader.GetString 0 ]
        with ex ->
            Error ex.Message

    /// The server's version, on its own.
    ///
    /// `introspect` already reads this, but reading it needs the whole of
    /// `introspect` — and `compile` must not introspect. A compiling server is a
    /// scratch server whose SCHEMA is nobody's business; only its VERSION
    /// matters, because it is the thing that rendered every expression in the
    /// artifact (`WI-0084`). So this asks the one question.
    let readServerVersion (connectionString: string) : Result<ServerVersion, string> =
        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()
            use command = new NpgsqlCommand(CatalogQueries.serverVersion, connection)

            match command.ExecuteScalar() with
            | :? string as full ->
                let major =
                    match Int32.TryParse(full.Split('.').[0]) with
                    | true, m -> m
                    // A version string PostgreSQL rendered that Strata cannot
                    // read is not version zero. Refused rather than guessed:
                    // deploy compares majors, and a wrong major compares wrong.
                    | false, _ -> failwithf "could not read a major version from '%s'" full

                Ok { Major = major; Full = full }
            | _ -> Error "the server returned no version"
        with ex ->
            Error ex.Message

    /// The effective search_path of a connection.
    ///
    /// Needed by Tier 2 to resolve unqualified names. Returned as data rather
    /// than applied here: resolution is a Tier 2 decision.
    let readSearchPath (connectionString: string) : Result<Identifier list, string> =
        try
            use connection = new NpgsqlConnection(connectionString)
            connection.Open()
            use command = new NpgsqlCommand(CatalogQueries.searchPath, connection)

            match command.ExecuteScalar() with
            | :? string as raw ->
                raw.Split(',')
                |> Array.map (fun s -> s.Trim().Trim('"'))
                // "$user" is resolved by the server per-connection; Strata cannot
                // expand it offline and must not guess a schema for it.
                |> Array.filter (fun s -> s <> "" && not (s.StartsWith "$"))
                |> Array.map identifierOf
                |> List.ofArray
                |> Ok
            | _ -> Error "search_path did not return a string"
        with ex ->
            Error ex.Message
