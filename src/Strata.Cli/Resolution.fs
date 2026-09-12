/// Rendering the declared side through a server, once.
///
/// Authority for: turning `DesiredState.Loaded` into `ResolvedDesiredState`.
///
/// This is the compile step in everything but name. Every call here asks
/// PostgreSQL what the DECLARED side means — a default's rendering, a view's
/// stored form, a policy's expression, a declared row through the real column
/// types — and none of them consults the target. That is what makes this half
/// of a deployment compilable while the change list is not
/// (`DF-STRATA-2026-2F6B`).
///
/// ## Why it is here and not in Tier 3
///
/// It needs `ShadowNormalisation` and `ReferenceData`, which are Tier 4
/// adapters, and Tier 3 must not acquire a host dependency
/// (`DF-STRATA-2026-D3F8`). `WI-0082` described this as hoisting normalisation
/// OUT of the CLI; the tier rules say it cannot leave, so what it becomes
/// instead is a single named boundary — which is the part `strata compile`
/// actually needs.
///
/// ## Warnings are returned, not printed
///
/// A compiled artifact has to record the conditions it was built under, and a
/// line on stderr does not survive into a file. Each step therefore yields its
/// value AND what it could not render, and `resolve` concatenates. Nothing
/// short-circuits: a normalisation that fails is not an error, it is a
/// comparison that gets disclosed as not-compared rather than guessed at
/// (`ER-008`).
module Strata.Cli.Resolution

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Application
open Strata.Host.Postgres

/// The declaration text for an object, by name.
let private declarationOf (declared: DesiredState.Loaded) name =
    declared.Declarations
    |> List.tryPick (fun (declaredName, text) ->
        if QualifiedName.display declaredName = QualifiedName.display name then Some text else None)

/// Declared view DDL rendered the way the catalog renders it, so the two sides
/// can be compared at all. Executed in a transaction that is always rolled back
/// — the database is unchanged either way. If it cannot run (no CREATE
/// privilege, a read-only target), views fall back to being disclosed as
/// not-compared.
let private renderViews connectionString (declared: DesiredState.Loaded) =
    let declaredViews =
        declared.Snapshot.Objects
        |> List.choose (fun o ->
            match o with
            | ViewObject v when not v.IsMaterialized ->
                declarationOf declared v.Name
                |> Option.map (fun text -> QualifiedName.display v.Name, text)
            | _ -> None)

    match ShadowNormalisation.normaliseViews connectionString declaredViews with
    | Ok normalised -> normalised, []
    | Microsoft.FSharp.Core.Error message ->
        [],
        if List.isEmpty declaredViews then
            []
        else
            [ sprintf
                  "could not normalise declared views (%s); view definitions will be reported as not-compared."
                  message ]

/// Declared defaults and checks rendered the way the catalog renders them, by
/// the same rolled-back transaction.
let private renderTables connectionString (declared: DesiredState.Loaded) =
    let declaredTables =
        declared.Snapshot.Objects
        |> List.choose (fun o ->
            match o with
            | TableObject t ->
                declarationOf declared t.Name
                |> Option.map (fun text -> QualifiedName.display t.Name, text)
            | _ -> None)

    match ShadowNormalisation.normaliseTables connectionString declaredTables with
    | Ok normalised ->
        normalised
        |> List.map (fun n ->
            ({ Table = n.Table
               Defaults = n.Defaults
               Checks = n.Checks }: SchemaDiff.NormalisedTable)),
        []
    | Microsoft.FSharp.Core.Error message ->
        [],
        if List.isEmpty declaredTables then
            []
        else
            [ sprintf
                  "could not normalise declared tables (%s); defaults and check expressions will be reported as not-compared."
                  message ]

/// Declared policies, with their expressions rendered by the server. A file's
/// `tenant = 'x'` and the catalog's `(tenant = 'x'::text)` are the same policy,
/// and nothing but PostgreSQL can say so — the same reason check constraints go
/// through the shadow.
///
/// The table's own declared DDL comes along because a policy's expression is
/// over the table's columns: it cannot be created until the table exists.
let private renderPolicies connectionString (declared: DesiredState.Loaded) =
    let byTable =
        declared.Policies
        |> List.groupBy (fun (table, _) -> QualifiedName.display table)
        |> List.choose (fun (name, entries) ->
            match declarationOf declared (fst (List.head entries)) with
            | None -> None
            | Some tableDdl ->
                let policies =
                    entries
                    |> List.choose (fun (table, policy) ->
                        declared.PolicyDeclarations
                        |> List.tryPick (fun ((t, n), text) ->
                            if QualifiedName.display t = QualifiedName.display table
                               && Identifier.folded n = Identifier.folded policy.Name then
                                Some(policy.Name.Text, text)
                            else
                                None))

                if List.isEmpty policies then None else Some(name, tableDdl, policies))

    let rendered, warnings =
        match ShadowNormalisation.normalisePolicies connectionString byTable with
        | Ok normalised -> normalised, []
        | Microsoft.FSharp.Core.Error message ->
            [],
            if List.isEmpty byTable then
                []
            else
                [ sprintf
                      "could not normalise declared policies (%s); policy expressions will be reported as not-compared."
                      message ]

    // A policy the server did not render keeps its placeholder, so the diff
    // compares everything else about it and says nothing about the expression —
    // rather than claiming the expression matches, or that it differs.
    declared.Policies
    |> List.map (fun (table, policy) ->
        match
            rendered
            |> List.tryFind (fun n -> n.Table = QualifiedName.display table && n.Policy = policy.Name.Text)
        with
        | Some n -> table, { policy with Using = n.Using; WithCheck = n.WithCheck }
        | None -> table, policy),
    warnings

/// Declared reference rows, resolved against the live tables.
///
/// PUBLIC, and the only part of resolution that is, because it is the only part
/// that is NOT compilable. Everything else here renders the declared side and
/// nothing else; this one reads the rows a table actually holds, so its answer
/// belongs to a TARGET rather than to a project. `strata deploy` calls it again
/// against its own target rather than trusting an artifact's copy — see the
/// note in `Artifact.render` for what happened when the artifact carried it.
///
/// Both sides come back rendered by the SERVER, through the real column types,
/// so `1.250` in a file and `1.25` in a `numeric(12,2)` column are recognised as
/// the same value rather than reported as a difference forever. Everything
/// happens in a transaction that is rolled back.
let resolveData connectionString (declared: DesiredState.Loaded) =
    let declaredData =
        declared.Data
        |> List.map (fun d ->
            QualifiedName.display d.Table,
            d.Columns |> List.map (fun c -> c.Text),
            d.Rows,
            // Only needed when the table does not exist yet, so the rows and
            // the table that holds them can arrive in one plan.
            declarationOf declared d.Table)

    match ReferenceData.resolve connectionString declaredData with
    | Ok (resolutions, failures) ->
        let row (r: ReferenceData.ResolvedRow) =
            ({ Key = r.Key
               Rendered = r.Rendered
               Literals = r.Literals }: SchemaDiff.ResolvedRow)

        (resolutions
         |> List.map (fun r ->
             ({ Table = r.Table
                Columns = r.Columns
                KeyColumns = r.KeyColumns
                Declared = r.Declared |> List.map row
                Deployed = r.Deployed |> List.map row }: SchemaDiff.ResolvedData)),
         failures
         |> List.map (fun f -> ({ Table = f.Table; Reason = f.Reason }: SchemaDiff.DataFailure))),
        []
    | Microsoft.FSharp.Core.Error message ->
        // Could not resolve ANY of them. Every declared table becomes a failure
        // rather than an empty result: a table Strata could not read is not a
        // table with no rows, and treating it as one would propose inserting
        // every declared row into a table that already has them.
        ([],
         declared.Data
         |> List.map (fun d ->
             ({ Table = QualifiedName.display d.Table
                Reason = message }: SchemaDiff.DataFailure))),
        if List.isEmpty declared.Data then
            []
        else
            [ sprintf "could not resolve declared reference rows (%s)." message ]

/// Render every part of the declared side that only a server can settle.
///
/// Each step is independent of the others and none of them reads the target's
/// schema, so the result is a function of the source and of *a* PostgreSQL.
let resolve (connectionString: string) (declared: DesiredState.Loaded) : ResolvedDesiredState =
    let views, viewWarnings = renderViews connectionString declared
    let tables, tableWarnings = renderTables connectionString declared
    let policies, policyWarnings = renderPolicies connectionString declared
    let (data, dataFailures), dataWarnings = resolveData connectionString declared

    // The version of the server that rendered all of the above. Read rather
    // than assumed, and left as `None` when it cannot be read: `deploy` refuses
    // a major-version mismatch, and it can only refuse what it knows.
    let compiledWith, versionWarnings =
        match CatalogIntrospection.readServerVersion connectionString with
        | Ok version -> Some version, []
        | Microsoft.FSharp.Core.Error message ->
            None,
            [ sprintf
                  "could not read the version of the server used to render declared expressions (%s); a deployment cannot then check that its target agrees."
                  message ]

    { Declared = declared
      NormalisedViews = views
      NormalisedTables = tables
      Policies = policies
      RowSecurity = declared.RowSecurity
      Data = data
      DataFailures = dataFailures
      Warnings =
        List.concat [ viewWarnings; tableWarnings; policyWarnings; dataWarnings; versionWarnings ]
      CompiledWith = compiledWith }
