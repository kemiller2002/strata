namespace Strata.Application

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort

/// Assembling desired state from declared object files (PR-025).
///
/// Authority for: turning a project's object files into a `SchemaSnapshot`.
///
/// The output is the SAME type catalog introspection produces, which is the
/// point: a diff compares two `SchemaSnapshot`s and neither side is privileged.
/// `DF-STRATA-2026-B1E7`.
///
/// ## Completeness means something different on this side
///
/// An introspected snapshot is incomplete when the server would not tell
/// Strata something. A DECLARED snapshot is incomplete when a file did not
/// parse, sat outside the layout, or declared an object type Strata does not
/// model yet. Both produce the same consequence — absence cannot be trusted —
/// which is exactly why the same `Completeness` carries it. A diff that read a
/// half-loaded desired state and proposed dropping the rest is the failure
/// `NG-006` and §1437 exist to prevent, and it is prevented here by refusing
/// to call the snapshot complete rather than by a check further downstream.
module DesiredState =

    /// A file that did not become a declared object.
    type LoadFailure =
        { Path: string
          Reason: string }

    /// Rows a reference table must contain, as one file declares them.
    ///
    /// A lookup table's rows are part of the schema: an `account` row cannot
    /// name an account type that does not exist, so the types are as much a
    /// precondition as the foreign key enforcing them.
    type DeclaredData =
        { Table: QualifiedName
          /// The columns the file names. Columns it does not name are columns
          /// it says nothing about, and nothing compares them.
          Columns: Identifier list
          /// Each row's values as re-emittable SQL literal TOKENS, in the same
          /// order as `Columns`. Never values Strata interpreted — see
          /// `DialectPort.DeclaredRows`.
          Rows: string list list
          Path: string }

    type Loaded =
        { Snapshot: SchemaSnapshot
          Failures: LoadFailure list
          /// The verbatim text that declared each object, when the file
          /// declared exactly one.
          ///
          /// Carried because CREATING an object from a reconstructed parse
          /// tree loses whatever the model does not carry — a column default,
          /// a check expression — and 96% of real tables have at least one
          /// (`EV-STRATA-2026-D3A8`). The file already holds exactly the DDL
          /// the author wrote, so executing it is both simpler and strictly
          /// more faithful than deparsing protobuf back into SQL.
          ///
          /// Only single-declaration files are recorded. Executing a file that
          /// declares two tables to create ONE of them would create the other
          /// as a side effect, which is a change nobody planned.
          Declarations: (QualifiedName * string) list

          /// The verbatim text that declared each trigger, keyed by the table
          /// it fires on and its own name.
          ///
          /// Kept apart from `Declarations` because a trigger name is not a
          /// `QualifiedName`: trigger names are scoped to a TABLE, not to a
          /// schema, so two tables may each carry a trigger called
          /// `set_updated_at` and neither is the other.
          ///
          /// Verbatim for a stronger reason than tables have. A trigger's
          /// `WHEN` clause and its `UPDATE OF` column list are not recoverable
          /// from the model, so a reconstruction would silently create a
          /// trigger that fires more often than the file asked for.
          TriggerDeclarations: ((QualifiedName * Identifier) * string) list

          /// Reference rows, one entry per declaring file.
          Data: DeclaredData list

          /// Privileges the project declares, one per object/grantee pair.
          Grants: Grant list

          /// Policies the project declares, with the table each is on.
          ///
          /// Their `Using` and `WithCheck` carry PRESENCE only — `Some ""` means
          /// the clause was written — because neither is comparable until the
          /// server has rendered it. `PolicyDeclarations` holds the text that
          /// does the rendering.
          Policies: (QualifiedName * Policy) list

          /// The verbatim text of each declared policy, keyed by table and
          /// policy name. Kept apart from `Declarations` for the same reason a
          /// trigger's is: a policy name is scoped to its table, not a schema.
          PolicyDeclarations: ((QualifiedName * Identifier) * string) list

          /// Row-level security settings the project declares.
          ///
          /// A list of settings rather than a pair of booleans per table,
          /// because a file that never mentions forcing has said NOTHING about
          /// it rather than "do not force".
          RowSecurity: (QualifiedName * RowSecuritySetting) list

          /// Extensions the project requires. Created and version-updated,
          /// never dropped.
          Extensions: Extension list }

    /// Declared schema names, from the objects actually loaded.
    let private declaredSchemas (objects: SchemaObject list) =
        objects
        |> List.choose (fun o -> (SchemaObject.name o).Schema)
        |> List.map Identifier.folded
        |> List.distinct

    /// Assemble a snapshot from already-read files.
    ///
    /// Takes `(path, contents)` rather than reading disk, so the whole
    /// assembly is testable without a filesystem — the same reason
    /// `CorpusPipeline.analyse` takes sources rather than a directory.
    let loadWithLayout (parser: IDialectParser) (files: (string * string option * string) list) : Loaded =
        let objects = ResizeArray<SchemaObject>()
        let failures = ResizeArray<LoadFailure>()
        let declarations' = ResizeArray<QualifiedName * string>()
        let indexes = ResizeArray<QualifiedName * Index>()
        let triggers = ResizeArray<QualifiedName * Trigger>()
        let triggerDeclarations = ResizeArray<(QualifiedName * Identifier) * string>()
        let data = ResizeArray<DeclaredData>()
        let grants = ResizeArray<Grant>()
        let policies = ResizeArray<QualifiedName * Policy>()
        let policyDeclarations = ResizeArray<(QualifiedName * Identifier) * string>()
        let rowSecurity = ResizeArray<QualifiedName * RowSecuritySetting>()
        let extensions = ResizeArray<Extension>()

        for path, expectedSchema, contents in files do
            let declarations = parser.ParseObjectDefinitions contents

            match declarations with
            | [] ->
                failures.Add
                    { Path = path
                      Reason = "file declares no statements" }
            | _ ->
                let declaredHere =
                    declarations
                    |> List.choose (function
                        | Declared o -> Some o
                        | DeclaredIndex _
                        | DeclaredTrigger _
                        | DeclaredPolicy _
                        | DeclaredRowSecurity _
                        | DeclaredExtension _
                        | DeclaredRows _
                        | DeclaredGrant _
                        | Unmodelled _
                        | DeclarationFailed _ -> None)

                for declaration in declarations do
                    match declaration with
                    | Declared object' ->
                        // The file's DIRECTORY names a schema; the declaration
                        // names one too, and they must agree. This is the
                        // fail-closed half of `DF-STRATA-2026-C3A2`: without it
                        // a project could assert an object in a schema that has
                        // no directory, which is a schema Strata cannot manage.
                        //
                        // Only checked when the declaration is qualified. An
                        // unqualified name says nothing about its schema, which
                        // is a different problem from saying the wrong one.
                        match expectedSchema, (SchemaObject.name object').Schema with
                        | Some expected, Some declared when Identifier.folded declared <> expected ->
                            failures.Add
                                { Path = path
                                  Reason =
                                    sprintf
                                        "declares %s, but the file sits under the %s schema directory. A declaration's schema must match the directory that holds it."
                                        (QualifiedName.display (SchemaObject.name object'))
                                        expected }
                        | _ -> objects.Add object'
                    | DeclaredIndex (table, index) -> indexes.Add(table, index)
                    | DeclaredTrigger (table, trigger) -> triggers.Add(table, trigger)
                    | DeclaredPolicy (table, policy) -> policies.Add(table, policy)
                    | DeclaredRowSecurity (table, setting) -> rowSecurity.Add(table, setting)
                    | DeclaredExtension extension -> extensions.Add extension
                    // Collected below, where the whole file can be judged at
                    // once: a row declaration only means something alongside
                    // the other statements in its file.
                    | DeclaredRows _ -> ()
                    | DeclaredGrant grant -> grants.Add grant
                    | Unmodelled detail -> failures.Add { Path = path; Reason = detail }
                    | DeclarationFailed error ->
                        failures.Add
                            { Path = path
                              Reason = sprintf "does not parse: %s" error.Message }

                match declaredHere with
                | [ single ] -> declarations'.Add(SchemaObject.name single, contents)
                // Two or more objects in one file: the text cannot be attributed
                // to either, so neither gets it and both fall back to
                // reconstruction.
                | _ -> ()

                // ---- reference rows -------------------------------------
                //
                // The whole file is executed against a shadow table, so the
                // file has to be about one table and one column list. Anything
                // else is refused rather than guessed at:
                //
                //   * two tables    - the shadow pass has no single table to
                //                     create, and executing the file would
                //                     write rows into a table it was not asked
                //                     about;
                //   * two column lists - a column one statement names and
                //                     another omits is compared for some rows
                //                     and not others, and "this row's label is
                //                     declared" would depend on which line it
                //                     was written on;
                //   * an object too - the CREATE would carry the INSERTs along
                //                     with it and load the data as a side
                //                     effect of creating the table, bypassing
                //                     the diff entirely.
                //
                // Each is a file the author can fix by splitting it.
                let rowDeclarations =
                    declarations
                    |> List.choose (function
                        | DeclaredRows (table, columns, rows) -> Some(table, columns, rows)
                        | _ -> None)

                match rowDeclarations with
                | [] -> ()
                | _ when not (List.isEmpty declaredHere) ->
                    failures.Add
                        { Path = path
                          Reason =
                            "declares both an object and rows; split them, or creating the object would load the rows as a side effect" }
                | (table, columns, _) :: _ ->
                    let sameTable =
                        rowDeclarations
                        |> List.forall (fun (t, _, _) ->
                            QualifiedName.display t = QualifiedName.display table)

                    let sameColumns =
                        rowDeclarations
                        |> List.forall (fun (_, c, _) ->
                            (c |> List.map Identifier.folded) = (columns |> List.map Identifier.folded))

                    if not sameTable then
                        failures.Add
                            { Path = path
                              Reason = "declares rows for more than one table; one file, one table" }
                    elif not sameColumns then
                        failures.Add
                            { Path = path
                              Reason =
                                "declares rows with differing column lists; a column named by one statement and omitted by another would be compared for some rows and not others" }
                    else
                        data.Add
                            { Table = table
                              Columns = columns
                              Rows = rowDeclarations |> List.collect (fun (_, _, r) -> r)
                              Path = path }

                // Same rule for a trigger file, and the same reason: a file
                // holding two CREATE TRIGGERs cannot have its text attributed
                // to either, so executing it to create one would create both.
                match
                    declarations
                    |> List.choose (function
                        | DeclaredTrigger (table, trigger) -> Some(table, trigger)
                        | _ -> None)
                with
                | [ (table, trigger) ] when List.isEmpty declaredHere ->
                    triggerDeclarations.Add((table, trigger.Name), contents)
                | _ -> ()

                // And again for a policy, for the third time and the same
                // reason: the text is executed verbatim to create the object, so
                // a file holding two CREATE POLICYs cannot have its text
                // attributed to either without creating both.
                match
                    declarations
                    |> List.choose (function
                        | DeclaredPolicy (table, policy) -> Some(table, policy)
                        | _ -> None)
                with
                | [ (table, policy) ] when List.isEmpty declaredHere ->
                    policyDeclarations.Add((table, policy.Name), contents)
                | _ -> ()

        // Indexes are declared in their own files but live on a table, so they
        // are attached once every file has been read.
        //
        // An index naming a table the project does not declare is a FAILURE,
        // not something to drop quietly: the project is asserting an index on
        // something it does not own, and acting on half of that is worse than
        // acting on none of it.
        let declaredIndexes = List.ofSeq indexes
        let declaredTriggers = List.ofSeq triggers

        let declaresTable (name: QualifiedName) =
            objects
            |> Seq.exists (fun o ->
                match o with
                | TableObject t -> QualifiedName.display t.Name = QualifiedName.display name
                | ViewObject _
                | RoutineObject _
                | SequenceObject _
                | EnumObject _ -> false)

        let orphanIndexes =
            declaredIndexes
            |> List.filter (fun (table, _) ->
                not (
                    objects
                    |> Seq.exists (fun o ->
                        QualifiedName.display (SchemaObject.name o) = QualifiedName.display table)))
            |> List.map (fun (table, index) ->
                { Path = QualifiedName.display table
                  Reason =
                    sprintf
                        "index '%s' is declared on a table this project does not declare"
                        index.Name.Text })

        // A trigger on an object the project does not declare AS A TABLE.
        // Triggers on views are legal PostgreSQL — `INSTEAD OF` triggers only
        // exist on views — and Strata models triggers on tables alone, so a
        // trigger on a declared view lands here too. That is the right place
        // for it: a failure the project can see, rather than a declaration
        // that quietly disappears.
        // Rows declared for a table the project does not declare. Same rule as
        // an orphan index, and a sharper reason: Strata resolves declared rows
        // by creating a shadow copy of the table from its declaring file, so
        // without that file there is nothing to create the shadow from and
        // nothing the rows could be compared against.
        let orphanData =
            List.ofSeq data
            |> List.filter (fun d -> not (declaresTable d.Table))
            |> List.map (fun d ->
                { Path = d.Path
                  Reason =
                    sprintf
                        "declares rows for '%s', which this project does not declare as a table"
                        (QualifiedName.display d.Table) })

        let orphanTriggers =
            declaredTriggers
            |> List.filter (fun (table, _) -> not (declaresTable table))
            |> List.map (fun (table, trigger) ->
                { Path = QualifiedName.display table
                  Reason =
                    sprintf
                        "trigger '%s' is declared on '%s', which this project does not declare as a table"
                        trigger.Name.Text
                        (QualifiedName.display table) })

        let withIndexes =
            objects
            |> Seq.map (fun o ->
                match o with
                | TableObject t ->
                    let attached =
                        declaredIndexes
                        |> List.filter (fun (table, _) ->
                            QualifiedName.display table = QualifiedName.display t.Name)
                        |> List.map snd

                    let attachedTriggers =
                        declaredTriggers
                        |> List.filter (fun (table, _) ->
                            QualifiedName.display table = QualifiedName.display t.Name)
                        |> List.map snd

                    if List.isEmpty attached && List.isEmpty attachedTriggers then
                        o
                    else
                        TableObject
                            { t with
                                Indexes = (if List.isEmpty attached then t.Indexes else attached)
                                Triggers =
                                    (if List.isEmpty attachedTriggers then t.Triggers else attachedTriggers) }
                // Nothing attaches to an enum type: an index or a trigger is on
                // a table, and a type has neither.
                | EnumObject _ -> o
                | ViewObject _
                | RoutineObject _
                | SequenceObject _ -> o)
            |> List.ofSeq

        let loaded = withIndexes
        let loadFailures = List.ofSeq failures

        // One object per file is the layout contract. More than one is not an
        // error, but it does mean "which file owns this object" has two
        // answers, so it is reported.
        // Keyed on the object's IDENTITY, not its name. PostgreSQL allows
        // overloads, so `f(integer)` and `f(text)` are two objects that share a
        // name — `SchemaDiff` has always known this and matched routines on name
        // plus argument types for the same reason.
        //
        // Keying on the name alone reported every overloaded pair as "declared
        // 2 times", which is a load failure, which marks desired state
        // incomplete, which suppresses EVERY drop in the project. A project
        // using a perfectly ordinary overload could not propose a removal at
        // all. Found by the awkward-forms corpus on its first run.
        let identityOf (o: SchemaObject) =
            match o with
            | RoutineObject r ->
                sprintf "routine:%s(%s)" (QualifiedName.display r.Name) (String.concat "," r.ArgumentTypes)
            | other -> QualifiedName.display (SchemaObject.name other)

        let duplicates =
            loaded
            |> List.countBy identityOf
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map (fun (identity, count) ->
                { Path = identity
                  Reason = sprintf "declared %d times across the project" count })

        let allFailures = loadFailures @ duplicates @ orphanIndexes @ orphanTriggers @ orphanData

        let state reason =
            if List.isEmpty allFailures then Complete else Partial reason

        { Snapshot =
            { Objects = loaded
              // A declared snapshot has no server, and saying `None` is the
              // honest answer. A diff needs it to compare dialect majors and
              // must get the version from the LIVE side.
              ServerVersion = None
              Completeness =
                Completeness.ofList
                    [ "relations",
                      state (sprintf "%d file(s) did not become a declared object" (List.length allFailures))
                      "columns",
                      state "column declarations are only as complete as the files that parsed"
                      "constraints",
                      state "constraint declarations are only as complete as the files that parsed"
                      // A project that declares NO index file is not saying
                      // "this schema has no indexes" — it is saying nothing
                      // about indexes at all, and the difference decides
                      // whether every existing index is a drop candidate.
                      // Declaring one index is how a project takes ownership
                      // of them, exactly as declaring one table does.
                      "indexes",
                      (if List.isEmpty declaredIndexes then NotRequested
                       else state "index declarations are only as complete as the files that parsed")
                      // Identical rule, identical reason. A project with no
                      // trigger file says nothing about triggers; declaring
                      // one takes ownership of all of them on the tables it
                      // declares.
                      "triggers",
                      (if List.isEmpty declaredTriggers then NotRequested
                       else state "trigger declarations are only as complete as the files that parsed")
                      // A project with no data file is not saying its reference
                      // tables should be empty. Same rule as indexes and
                      // triggers, and the consequence of getting it wrong is
                      // worse: a row nobody declared is a row user data may
                      // point at.
                      // A project with no GRANT file says nothing about
                      // privileges. Declaring one takes ownership of that
                      // grantee's privileges on that object — and of nothing
                      // else, which is finer than the rule for indexes and
                      // triggers, because a wrong revoke costs a person their
                      // access rather than an index rebuild.
                      "grants",
                      (if Seq.isEmpty grants then NotRequested
                       else state "grant declarations are only as complete as the files that parsed")
                      "reference_data",
                      (if Seq.isEmpty data then NotRequested
                       else state "declared rows are only as complete as the files that parsed")
                      "view_definitions", NotRequested
                      "routines", NotRequested ] }
          Failures = allFailures
          Declarations = List.ofSeq declarations'
          TriggerDeclarations = List.ofSeq triggerDeclarations
          Policies = List.ofSeq policies
          PolicyDeclarations = List.ofSeq policyDeclarations
          RowSecurity = List.ofSeq rowSecurity
          Extensions = List.ofSeq extensions
          Data = List.ofSeq data |> List.filter (fun d -> declaresTable d.Table)
          // Two declarations for the same object and grantee are merged rather
          // than treated as rivals: `GRANT SELECT` and `GRANT INSERT` written
          // on separate lines mean the grantee should hold both.
          Grants =
            List.ofSeq grants
            |> List.groupBy (fun g -> GrantTarget.key g.Target, g.Grantee.ToLowerInvariant())
            |> List.map (fun (_, gs) ->
                { (List.head gs) with
                    Privileges = gs |> List.collect (fun g -> g.Privileges) |> List.distinct |> List.sort }) }

    /// Assemble a snapshot from `(path, contents)` pairs, with no layout to
    /// check against.
    ///
    /// The common shape for tests and for any caller that has text without a
    /// directory. `loadWithLayout` is what a project uses.
    let load (parser: IDialectParser) (files: (string * string) list) : Loaded =
        loadWithLayout parser (files |> List.map (fun (path, contents) -> path, None, contents))

    /// Schemas the project actually declared objects in.
    ///
    /// Distinct from the manifest's `managedSchemas`: the manifest says what
    /// MAY be changed, this says what was DECLARED. A schema listed as managed
    /// with no declared objects is the dangerous case — desired state would
    /// appear to say "this schema should be empty" — so a caller needs both to
    /// tell an empty declaration from an absent one.
    let schemasDeclaredIn (loaded: Loaded) = declaredSchemas loaded.Snapshot.Objects
