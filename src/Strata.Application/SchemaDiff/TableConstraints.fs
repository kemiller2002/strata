namespace Strata.Application.SchemaDiff

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

module TableConstraints =

    /// Constraint and default differences for a table present on both sides.
    ///
    /// These were not compared at all until `EV-STRATA-2026-D3A8`, which found
    /// `plan` reporting "already matches desired state" for a table whose
    /// foreign key, check constraint and column default all differed. The
    /// model carried every one of those facts on both sides; nothing looked at
    /// them. A difference nobody compared rendered identically to no
    /// difference, which is the exact collapse `ER-008` exists to forbid.
    ///
    /// What can be compared faithfully is compared. What cannot is reported as
    /// `NotCompared` — never passed over.
    let private forTable
        (policy: RemovalPolicy)
        (indexesDeclared: bool)
        (triggersDeclared: bool)
        (normalised: NormalisedTable list)
        (desired: Table)
        (actual: Table)
        =
        let rendered =
            normalised |> List.tryFind (fun n -> n.Table = QualifiedName.display desired.Name)

        // A serial column's default names its SEQUENCE, and the shadow table's
        // sequence is named after the shadow table — so the two renderings can
        // never match however identical the declarations are. The type already
        // carries what serial means, so these are excluded from comparison
        // rather than reported as a permanent difference.
        let isSequenceDefault (expression: string) =
            expression.TrimStart().StartsWith("nextval(", StringComparison.OrdinalIgnoreCase)
        let columnList (columns: Identifier list) =
            columns |> List.map (fun c -> Identifier.folded c) |> String.concat ","

        let named (name: Identifier) = Identifier.folded name

        /// One constraint, reduced to the two things a comparison can use.
        ///
        /// `Name` is `None` for a constraint the declaring file did not name.
        /// `Definition` is what it does — its columns, or its target — which is
        /// the only handle an unnamed one has.
        let entry (name: ConstraintName) (definition: string) (columns: Identifier list) =
            (name |> Option.map named), definition, columns

        /// Compare two sets of constraints.
        ///
        /// A NAMED declared constraint is matched by name, because a name is
        /// the identity the catalog and the file agree on and the project asked
        /// for that specific name. An UNNAMED one is matched by definition: the
        /// file asked for a constraint that does this, and did not care what
        /// the server calls it. Matching an unnamed one by a fabricated name is
        /// what made every project using `REFERENCES u (id)` inline
        /// unconvergeable — the declared side said `foreign_key` forever and
        /// the catalog said `t_a_fkey` forever.
        ///
        /// Names are claimed first so a name match always wins over a
        /// definition match: a project that named a constraint gets that
        /// constraint, not whichever one happens to share its shape.
        let compareSet
            (label: string)
            (kind: ConstraintKind)
            (desiredEntries: (string option * string * Identifier list) list)
            (actualEntries: (string option * string * Identifier list) list)
            =
            let byName =
                desiredEntries
                |> List.choose (fun (n, d, c) -> n |> Option.map (fun n -> n, d, c))

            let claimedNames =
                byName
                |> List.filter (fun (n, _, _) -> actualEntries |> List.exists (fun (m, _, _) -> m = Some n))
                |> List.map (fun (n, _, _) -> n)

            // Definitions still free after the name matches, matched one for
            // one so two identical unnamed constraints do not both match the
            // same deployed one.
            let unmatchedActual =
                actualEntries
                |> List.filter (fun (n, _, _) ->
                    match n with
                    | Some n -> not (claimedNames |> List.contains n)
                    | None -> true)

            let unnamedDesired = desiredEntries |> List.filter (fun (n, _, _) -> Option.isNone n)

            // Each unnamed declaration claims the FIRST deployed constraint
            // with its definition: two declarations that do the same thing
            // need two deployed constraints, not one counted twice.
            let unnamedAdded, leftoverActual =
                unnamedDesired
                |> List.fold
                    (fun (added, pool) (_, definition, columns) ->
                        match Lists.removeFirst (fun (_, d, _) -> d = definition) pool with
                        | Some (_, rest) -> added, rest
                        | None -> added @ [ columns ], pool)
                    ([], unmatchedActual)

            let added =
                (byName
                 |> List.filter (fun (n, _, _) -> not (actualEntries |> List.exists (fun (m, _, _) -> m = Some n)))
                 |> List.map (fun (n, _, c) ->
                     Ok(AddConstraint(desired.Name, Some(Identifier.unquoted n), kind, c))))
                @ (unnamedAdded |> List.map (fun c -> Ok(AddConstraint(desired.Name, None, kind, c))))

            let removed =
                leftoverActual
                |> List.map (fun (n, _, _) ->
                    match n with
                    | Some name ->
                        DropSafety.removal
                            policy
                            desired.Name
                            [ DesiredStateLoaded(
                                  sprintf
                                      "%s '%s' is absent from desired state, but desired state did not load completely"
                                      label
                                      name
                              )
                              DropsEnabled(sprintf "%s '%s' would be dropped; pass --allow-drops" label name) ]
                            (DropConstraint(desired.Name, Identifier.unquoted name, kind))
                    // The catalog has no nameless constraints, so this cannot
                    // happen. Saying so beats dropping it silently.
                    | None ->
                        Microsoft.FSharp.Core.Error
                            { Object = desired.Name
                              Reason = NotModelled
                              Detail = sprintf "an unnamed %s was reported by the catalog, which should not occur" label })

            // Same name on both sides, different membership.
            //
            // PostgreSQL has no ALTER CONSTRAINT that changes what one covers,
            // so the only way to get there is to drop it and add it back. Both
            // halves are emitted, and `orderKey` ranks drops one step ahead of
            // adds so the name is free when the add runs.
            //
            // That makes it a REMOVAL, and it is gated like every other one: a
            // redefinition Strata cannot drop is a redefinition it cannot make,
            // and emitting the add alone would fail on the name that is still
            // taken. So when drops are not enabled — or desired state did not
            // load completely — neither half is proposed and the difference is
            // reported instead.
            let redefined =
                byName
                |> List.collect (fun (n, dcols, dcolumns) ->
                    actualEntries
                    |> List.tryFind (fun (m, _, _) -> m = Some n)
                    |> Option.map (fun (_, acols, _) ->
                        if dcols = acols then
                            []
                        else
                            let needs requirement =
                                sprintf
                                    "%s '%s' covers (%s) in desired state and (%s) in the database. Changing it means dropping and re-adding it, so it needs %s."
                                    label
                                    n
                                    dcols
                                    acols
                                    requirement

                            match
                                DropSafety.removal
                                    policy
                                    desired.Name
                                    [ DesiredStateLoaded(needs "a desired state that loaded completely")
                                      DropsEnabled(needs "--allow-drops") ]
                                    (DropConstraint(desired.Name, Identifier.unquoted n, kind))
                            with
                            | Ok drop -> [ Ok drop; Ok(AddConstraint(desired.Name, Some(Identifier.unquoted n), kind, dcolumns)) ]
                            | Error refused -> [ Error refused ])
                    |> Option.defaultValue [])

            added @ removed @ redefined

        let primaryKey =
            match desired.PrimaryKey, actual.PrimaryKey with
            | Some d, Some a when columnList d.Columns <> columnList a.Columns ->
                [ Ok(
                    UnclassifiedChange(
                        sprintf
                            "%s: primary key covers (%s) in desired state and (%s) in the database"
                            (QualifiedName.display desired.Name)
                            (columnList d.Columns)
                            (columnList a.Columns))) ]
            | Some d, None ->
                [ Ok(AddConstraint(desired.Name, d.ConstraintName, ConstraintKind.PrimaryKey, d.Columns)) ]
            | None, Some a ->
                [ Ok(
                    UnclassifiedChange(
                        sprintf
                            "%s: primary key '%s' exists in the database and not in desired state"
                            (QualifiedName.display desired.Name)
                            (match a.ConstraintName with Some n -> n.Text | None -> "(unnamed)"))) ]
            | Some _, Some _
            | None, None -> []

        let uniques =
            compareSet
                "unique constraint"
                ConstraintKind.Unique
                (desired.UniqueConstraints
                 |> List.map (fun u -> entry u.ConstraintName (columnList u.Columns) u.Columns))
                (actual.UniqueConstraints
                 |> List.map (fun u -> entry u.ConstraintName (columnList u.Columns) u.Columns))

        let foreignKeys =
            let describe (f: ForeignKey) =
                sprintf
                    "%s -> %s(%s)"
                    (columnList f.Columns)
                    (QualifiedName.display f.ReferencedTable)
                    (columnList f.ReferencedColumns)

            compareSet
                "foreign key"
                ConstraintKind.ForeignKey
                (desired.ForeignKeys |> List.map (fun f -> entry f.ConstraintName (describe f) f.Columns))
                (actual.ForeignKeys |> List.map (fun f -> entry f.ConstraintName (describe f) f.Columns))

        // A check's PRESENCE is comparable by name. Its EXPRESSION is not: the
        // declared side does not carry one, and the catalog reports its own
        // normalised rendering. Presence is therefore compared and equality of
        // expression is explicitly not claimed, below.
        // An unnamed check has neither a name to match on nor, from the parse
        // tree, an expression — so unlike an unnamed foreign key it has no
        // shape of its own to compare. The shadow rendering supplies one: the
        // same DDL executed in a rolled-back transaction, which keeps an
        // explicit constraint name and lets the server invent one for a check
        // the file did not name. A rendering whose name the file never used is
        // therefore the rendering of an unnamed declaration, and its EXPRESSION
        // is directly comparable with the deployed one.
        let namedChecks (t: Table) =
            t.CheckConstraints |> List.filter (fun c -> Option.isSome c.ConstraintName)

        let unnamedDeclaredChecks =
            desired.CheckConstraints |> List.filter (fun c -> Option.isNone c.ConstraintName)

        let declaredCheckNames =
            namedChecks desired |> List.choose (fun c -> c.ConstraintName |> Option.map named)

        let unnamedRenderings =
            match rendered with
            | None -> []
            | Some n ->
                n.Checks
                |> List.filter (fun (name, _) ->
                    not (declaredCheckNames |> List.contains (named (Identifier.unquoted name))))
                |> List.map snd

        /// Deployed checks explained by an unnamed declaration, removed one for
        /// one so two identical declarations claim two deployed constraints.
        let unmatchedUnnamedChecks, deployedChecksToCompare =
            unnamedRenderings
            |> List.fold
                (fun (unmatched, pool) (expression: string) ->
                    match
                        Lists.removeFirst (fun (c: CheckConstraint) -> c.Expression.Trim() = expression.Trim()) pool
                    with
                    | Some (_, rest) -> unmatched, rest
                    | None -> expression :: unmatched, pool)
                ([], actual.CheckConstraints)

        // No shadow rendering and an unnamed declaration: Strata holds nothing
        // that could attribute a deployed check to it, so it compares none of
        // them rather than reporting every one as absent from desired state.
        let checksUnattributable =
            List.isEmpty unnamedRenderings && not (List.isEmpty unnamedDeclaredChecks)

        let checks =
            if checksUnattributable then
                []
            else
                compareSet
                    "check constraint"
                    ConstraintKind.Check
                    (namedChecks desired |> List.map (fun c -> entry c.ConstraintName "" []))
                    (deployedChecksToCompare |> List.map (fun c -> entry c.ConstraintName "" []))
                @ (unmatchedUnnamedChecks
                   |> List.map (fun _ -> Ok(AddConstraint(desired.Name, None, ConstraintKind.Check, []))))

        let defaults =
            desired.Columns
            |> List.choose (fun d ->
                actual.Columns
                |> List.tryFind (fun a -> Identifier.sameName a.Name d.Name)
                |> Option.bind (fun a ->
                    let declaredExpression =
                        rendered
                        |> Option.bind (fun n ->
                            n.Defaults |> List.tryPick (fun (c, e) -> if Identifier.sameName (Identifier.unquoted c) d.Name then Some e else None))

                    match declaredExpression, a.DefaultExpression with
                    | Some declared, Some deployed when
                        not (isSequenceDefault declared)
                        && not (isSequenceDefault deployed)
                        && declared.Trim() <> deployed.Trim() ->
                        Some(
                            Ok(
                                UnclassifiedChange(
                                    sprintf
                                        "%s.%s: default is %s in desired state and %s in the database"
                                        (QualifiedName.display desired.Name)
                                        d.Name.Text
                                        declared
                                        deployed)))
                    | _ ->

                    if d.HasDefault <> a.HasDefault then
                        Some(
                            Ok(
                                UnclassifiedChange(
                                    sprintf
                                        "%s.%s: default %s in desired state and %s in the database"
                                        (QualifiedName.display desired.Name)
                                        d.Name.Text
                                        (if d.HasDefault then "present" else "absent")
                                        (if a.HasDefault then "present" else "absent"))))
                    else
                        None))

        let checkRedefinitions =
            match rendered with
            | None -> []
            | Some n ->
                actual.CheckConstraints
                |> List.choose (fun a ->
                    n.Checks
                    |> List.tryPick (fun (name, definition) ->
                        match a.ConstraintName with
                        | Some actualName when named (Identifier.unquoted name) = named actualName ->
                            Some definition
                        | _ -> None)
                    |> Option.bind (fun declared ->
                        if declared.Trim() <> a.Expression.Trim() then
                            Some(
                                Ok(
                                    UnclassifiedChange(
                                        sprintf
                                            "%s: check constraint '%s' is %s in desired state and %s in the database"
                                            (QualifiedName.display desired.Name)
                                            (match a.ConstraintName with
                                             | Some n -> n.Text
                                             | None -> "(unnamed)")
                                            declared
                                            a.Expression)))
                        else
                            None))

        let indexChanges, indexDisclosure = TableIndexes.indexes policy indexesDeclared desired actual

        let triggerChanges, triggerDisclosure, conditionDisclosure =
            TableIndexes.triggers policy triggersDeclared desired actual

        // Everything above establishes PRESENCE. Two expressions Strata cannot
        // read might still differ, and saying so is the difference between a
        // bounded result and a false clean.
        let notCompared =
            let comparedCheck (name: Identifier) =
                match rendered with
                | None -> false
                | Some n -> n.Checks |> List.exists (fun (c, _) -> named (Identifier.unquoted c) = named name)

            let sharedChecks =
                namedChecks desired
                |> List.filter (fun d ->
                    actual.CheckConstraints
                    |> List.exists (fun a -> a.ConstraintName |> Option.map named = (d.ConstraintName |> Option.map named))
                    && not (d.ConstraintName |> Option.map comparedCheck |> Option.defaultValue false))

            let sharedDefaults =
                desired.Columns
                |> List.filter (fun d ->
                    d.HasDefault
                    && actual.Columns
                       |> List.exists (fun a -> Identifier.sameName a.Name d.Name && a.HasDefault)
                    && (match rendered with
                        | None -> true
                        | Some n ->
                            // Compared, unless the rendering is a sequence
                            // default that can never match across schemas.
                            n.Defaults
                            |> List.tryPick (fun (c, e) ->
                                if Identifier.sameName (Identifier.unquoted c) d.Name then Some e else None)
                            |> function
                               | Some e -> isSequenceDefault e
                               | None -> true))

            [ if not (List.isEmpty sharedChecks) then
                  { Object = desired.Name
                    Reason = NotCompared
                    Detail =
                      sprintf
                          "%d check constraint(s) exist on both sides; their expressions were NOT compared, so they may differ"
                          (List.length sharedChecks) }
              if not (List.isEmpty sharedDefaults) then
                  { Object = desired.Name
                    Reason = NotCompared
                    Detail =
                      sprintf
                          "%d column default(s) exist on both sides; their expressions were NOT compared, so they may differ"
                          (List.length sharedDefaults) }
              ]
            @ Option.toList indexDisclosure
            @ Option.toList triggerDisclosure
            @ [ if checksUnattributable then
                  { Object = desired.Name
                    Reason = NotCompared
                    Detail =
                      sprintf
                          "%d check constraint(s) in the declaring file are unnamed and the declared DDL could not be normalised through the server, so no deployed check could be attributed to them and NONE were compared. Name them, or restore normalisation, to have them compared."
                          (List.length unnamedDeclaredChecks) } ]
            @ Option.toList conditionDisclosure

        primaryKey
        @ uniques
        @ foreignKeys
        @ checks
        @ defaults
        @ checkRedefinitions
        @ indexChanges
        @ triggerChanges,
        notCompared

    /// Constraint, default, index and trigger differences for every table
    /// present on both sides. A renamed table is not compared here: its
    /// constraints are read under its new name only once the rename applies.
    let compare (policy: RemovalPolicy) (inputs: Inputs) (m: TableMatching) : FamilyDiff =
        let indexesDeclared = DropSafety.claimsCategory "indexes" inputs.Desired
        let triggersDeclared = DropSafety.claimsCategory "triggers" inputs.Desired

        let perTable =
            m.Shared
            |> List.map (fun (d, a) ->
                forTable policy indexesDeclared triggersDeclared inputs.NormalisedTables d a)

        { Differences = perTable |> List.collect fst
          Disclosures = perTable |> List.collect snd }
