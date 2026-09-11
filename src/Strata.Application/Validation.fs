namespace Strata.Application

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.Resolution
open Strata.Semantic.AnalysisScope
open Strata.Semantic.Wire
open Strata.Analysis.StatementReferences
open Strata.Analysis.DialectPort
open Strata.Analysis.ScopeResolution
open Strata.Analysis.CatalogResolution

/// Validation of candidate SQL against a schema (PR-018).
///
/// Authority for: deciding whether a statement's references exist.
///
/// This is the compile-time equivalent for SQL. An agent that has read a schema
/// can still write `custmer_id` three hundred lines later; reasoning does not
/// catch that, a symbol table does. Tests `HY-STRATA-2026-D5B8`.
///
/// ## The distinction this module exists to preserve
///
/// A validator that reports "does not exist" when it means "I could not tell"
/// is worse than no validator: the author will change working SQL to satisfy
/// it. `ER-008` forbids that collapse and this is the sharpest place it bites,
/// so `Invalid` and `Unverifiable` are separate and never merged.
///
/// A reference is `Invalid` ONLY when Strata can prove absence:
///
///   * every relation the statement brings into scope resolved, AND
///   * the catalog snapshot is complete for the categories consulted, AND
///   * the object or column is still not found.
///
/// If any of those fails, the finding is `Unverifiable` — which is not a pass.
/// This is `ER-009` (fail closed when visibility is incomplete) applied to
/// authoring rather than to deployment.
module Validation =

    /// Why a finding was raised, kept apart per ER-008.
    type Severity =
        /// Strata can prove the reference does not exist.
        | Invalid
        /// Strata could not determine whether it exists. NOT a pass.
        | Unverifiable

    type Finding =
        { Severity: Severity
          /// Index of the statement within the script, 0-based.
          StatementIndex: int
          /// Byte offset of the statement, from the parser.
          Offset: int
          Length: int
          /// The identifier at issue, as written.
          Subject: string
          Message: string }

    /// The whole result for one script.
    type Report =
        { Findings: Finding list
          StatementCount: int
          Scope: Scope }

    [<RequireQualifiedAccess>]
    module Report =

        let invalidFindings (r: Report) =
            r.Findings |> List.filter (fun f -> f.Severity = Invalid)

        let unverifiableFindings (r: Report) =
            r.Findings |> List.filter (fun f -> f.Severity = Unverifiable)

        /// Process exit code, matching the deployment gate's convention so a
        /// pipeline treats "cannot verify" the same way in both tools:
        /// 0 clean, 1 proven wrong, 2 needs a human.
        let exitCode (r: Report) =
            if not (List.isEmpty (invalidFindings r)) then 1
            elif not (List.isEmpty (unverifiableFindings r)) then 2
            else 0

    /// Can absence be proven at all against this snapshot?
    ///
    /// Relations and columns are different categories, so a snapshot that read
    /// relations but not columns can prove a missing TABLE and must not claim a
    /// missing COLUMN.
    ///
    /// The category names must match what `CatalogIntrospection` actually
    /// reports — `relations` and `columns`. An earlier version of this module
    /// asked for `tables` and `views`, which the introspector never emits, so
    /// `stateOf` returned `NotRequested`, nothing was ever provable, and every
    /// finding degraded to `Unverifiable`. The unit tests did not catch it
    /// because their fixture used the same invented names; running against a
    /// live database did.
    let private canProveAbsence (categories: string list) (snapshot: SchemaSnapshot) =
        categories
        |> List.forall (fun category ->
            match Completeness.stateOf category snapshot.Completeness with
            | Complete -> true
            | Partial _
            | Inaccessible _
            | NotRequested -> false)

    let private findingFor severity index (location: StatementLocation) subject message =
        { Severity = severity
          StatementIndex = index
          Offset = location.Offset
          Length = location.Length
          Subject = subject
          Message = message }

    /// Every relation mention that is a genuine catalog question, paired with
    /// what the CATALOG says about it.
    ///
    /// Two resolvers run, in this order, and the order is load-bearing.
    ///
    /// `ScopeResolution.resolveRelations` runs FIRST and is catalog-BLIND: for a
    /// schema-qualified name it returns `Resolved` unconditionally, because its
    /// job is to say whether a name is a database object at all rather than a
    /// CTE, an alias or a temp relation (RK-001). Its `Resolved` means "resolved
    /// to a NAME", not "exists".
    ///
    /// `CatalogResolution.resolveRelationName` then answers existence. Skipping
    /// the first resolver would condemn every CTE as a missing table; skipping
    /// the second would pass every misspelled table as valid. An earlier draft
    /// of this module used only the first, and the test for a statement mixing
    /// a real table with a missing one caught it.
    let private catalogOutcomes
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (extraction: StatementExtraction)
        =
        resolveRelations searchPath extraction
        |> List.choose (fun (mention, outcome) ->
            match outcome with
            // Bound by the statement itself. Never a catalog question.
            | LocallyBound _
            | BindingSite _ -> None
            | DatabaseObject scopeResolution ->
                match scopeResolution with
                // Scope resolution already knows this cannot be pinned to one
                // name; the catalog is not consulted for a name we do not have.
                | Ambiguous gap
                | Unsupported gap
                | PartiallyResolved (_, gap) -> Some(mention, Microsoft.FSharp.Core.Error gap)
                | Unresolved gap -> Some(mention, Microsoft.FSharp.Core.Error gap)
                | Resolved name -> Some(mention, Ok(resolveRelationName snapshot searchPath name)))

    /// Validate one statement's relation references.
    let private validateRelations
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (index: int)
        (location: StatementLocation)
        (extraction: StatementExtraction)
        =
        let relationsProvable = canProveAbsence [ "relations" ] snapshot

        catalogOutcomes snapshot searchPath extraction
        |> List.choose (fun (mention, outcome) ->
            let subject = QualifiedName.display mention.Name

            match outcome with
            // Scope resolution could not name it. Never an existence claim.
            | Microsoft.FSharp.Core.Error gap ->
                Some(
                    findingFor Unverifiable index location subject
                        (sprintf "relation '%s': %s" subject (ResolutionGap.describe gap)))

            | Ok catalogResolution ->
                match catalogResolution with
                | Resolved _ -> None

                // Tier 2 deliberately refuses to call this absence, because a
                // snapshot may be incomplete. Tier 3 holds the completeness
                // evidence that licenses the stronger claim, and only here.
                | Unresolved _ when relationsProvable ->
                    Some(
                        findingFor Invalid index location subject
                            (sprintf "relation '%s' does not exist" subject))

                | Unresolved gap ->
                    Some(
                        findingFor Unverifiable index location subject
                            (sprintf
                                "relation '%s' did not resolve, and the catalog snapshot is not complete enough to conclude it is absent: %s"
                                subject
                                (ResolutionGap.describe gap)))

                | Ambiguous gap ->
                    Some(
                        findingFor Unverifiable index location subject
                            (sprintf "relation '%s' is ambiguous: %s" subject (ResolutionGap.describe gap)))

                | PartiallyResolved (_, gap)
                | Unsupported gap ->
                    Some(
                        findingFor Unverifiable index location subject
                            (sprintf "relation '%s': %s" subject (ResolutionGap.describe gap))))

    /// Validate one statement's column references.
    ///
    /// Walks the mentions directly rather than reading `columnDependencies`'
    /// gap list, because that list is flat: it says a column did not resolve
    /// but not WHICH column. "a referenced column does not exist" is not an
    /// actionable finding; "column 'amnout' does not exist" is.
    ///
    /// Absence of a column can only be proven when EVERY relation the statement
    /// brings into scope resolved. The column resolvers search only the
    /// relations they could resolve, so with an unresolved relation in scope
    /// "not found" is indistinguishable from "it lives in the table I could not
    /// find". Reporting that as invalid would be a fabricated error.
    let private validateColumns
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (bound: Identifier list)
        (index: int)
        (location: StatementLocation)
        (extraction: StatementExtraction)
        =
        let outcomes = catalogOutcomes snapshot searchPath extraction

        let everyRelationResolved =
            outcomes
            |> List.forall (fun (_, outcome) ->
                match outcome with
                | Ok (Resolved _) -> true
                | Ok _
                | Microsoft.FSharp.Core.Error _ -> false)

        let relations =
            outcomes
            |> List.choose (fun (_, outcome) ->
                match outcome with
                | Ok (Resolved name) -> Some name
                | Ok _
                | Microsoft.FSharp.Core.Error _ -> None)

        let provable =
            everyRelationResolved && canProveAbsence [ "relations"; "columns" ] snapshot

        extraction.Columns
        |> List.choose (fun mention ->
            // A wildcard names no column, so there is no identifier to check.
            // Whether it EXPANDS is a dependency-analysis question, not a
            // validity one.
            if mention.IsWildcard then None
            else
                match mention.Column with
                | None -> None
                // A name bound by an enclosing routine's signature is a
                // parameter, not a column. Only an UNQUALIFIED name can be one:
                // `p.id` names a relation called `p`, whatever the signature
                // says.
                | Some column when
                    mention.Qualifier.IsNone
                    && bound |> List.exists (fun b -> Identifier.sameName b column) -> None
                | Some column ->
                    let subject =
                        match mention.Qualifier with
                        | Some q -> sprintf "%s.%s" q.Text column.Text
                        | None -> column.Text

                    let resolution =
                        match mention.Qualifier with
                        | Some qualifier ->
                            resolveQualifiedColumn snapshot searchPath extraction relations qualifier column
                        | None -> resolveUnqualifiedColumn snapshot searchPath relations column

                    match resolution with
                    | Resolved _ -> None

                    | Unresolved _ when provable ->
                        Some(
                            findingFor Invalid index location subject
                                (sprintf "column '%s' does not exist in any relation in scope" subject))

                    | Unresolved gap ->
                        Some(
                            findingFor Unverifiable index location subject
                                (sprintf
                                    "column '%s' did not resolve, and the scope is not complete enough to conclude it is absent: %s"
                                    subject
                                    (ResolutionGap.describe gap)))

                    | Ambiguous gap ->
                        Some(
                            findingFor Unverifiable index location subject
                                (sprintf "column '%s' is ambiguous: %s" subject (ResolutionGap.describe gap)))

                    | PartiallyResolved (_, gap)
                    | Unsupported gap ->
                        Some(
                            findingFor Unverifiable index location subject
                                (sprintf "column '%s': %s" subject (ResolutionGap.describe gap))))

    /// Validate a whole SQL script.
    ///
    /// Recurses into routine bodies. A `CREATE FUNCTION` body is a string
    /// literal in the grammar, so a body selecting a column that does not exist
    /// extracts as a statement with no columns and no relations — and reporting
    /// VALID for it is a false pass, the one outcome a validator must never
    /// produce. The body is re-parsed as SQL in its own right and validated
    /// against the same catalog.
    ///
    /// Depth is bounded because a routine body cannot itself contain a
    /// `CREATE FUNCTION` with a body in PostgreSQL, but the bound is explicit
    /// rather than assumed: a grammar change should degrade to
    /// `Unverifiable`, not recurse forever.
    let rec private validateScript
        (parser: IDialectParser)
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (bound: Identifier list)
        (depth: int)
        (sql: string)
        : Finding list =

        let parsed = parser.ParseScript sql

        let findings =
            parsed
            |> List.mapi (fun index statement ->
                match statement with
                // A statement that does not parse is not a statement with no
                // problems. It is invalid by inspection, and saying so needs no
                // catalog.
                | Failed (location, error) ->
                    [ findingFor Invalid index location "" (sprintf "does not parse: %s" error.Message) ]

                | Parsed (location, extraction) ->
                    // Dynamic SQL is not analysable, and silence here would read
                    // as a pass (§11.4).
                    let dynamic =
                        if extraction.ContainsDynamicSql then
                            [ findingFor Unverifiable index location ""
                                "executes dynamically constructed SQL, which Strata cannot validate" ]
                        else []

                    let unmodelled =
                        extraction.UnmodelledConstructs
                        |> List.map (fun construct -> findingFor Unverifiable index location "" construct)

                    // A routine body is a STRING LITERAL to the parser, so
                    // nothing inside it is extracted and every reference in it
                    // is invisible here. Without this the validator reports
                    // VALID for a function whose body selects a column that
                    // does not exist — a false pass, which is the one outcome a
                    // validator must never produce. Reporting it as
                    // unverifiable is the honest floor; validating the body is
                    // the next step and is tracked separately.
                    let routineBody =
                        match extraction.RoutineBody with
                        | None ->
                            match extraction.Shape with
                            // Defines a routine, but no body came back — an
                            // `AS 'file', 'symbol'` form, or a shape the
                            // adapter does not read. Never a pass.
                            | DdlShape kind when kind = "CREATE FUNCTION" ->
                                [ findingFor Unverifiable index location ""
                                    "defines a routine whose body Strata could not read, so references inside it were not checked" ]
                            | DdlShape _
                            | SelectShape | InsertShape | UpdateShape | DeleteShape
                            | UtilityShape _ | UnsupportedShape _ -> []

                        | Some routine when depth >= 1 ->
                            [ findingFor Unverifiable index location ""
                                (sprintf
                                    "routine body in '%s' nests deeper than Strata validates, so it was not checked"
                                    routine.Language) ]

                        | Some routine ->
                            // Only languages whose bodies ARE SQL can be parsed
                            // as SQL. plpgsql has its own grammar and the port
                            // exposes a separate check for it; anything else is
                            // not Strata's to interpret, and guessing would
                            // produce findings about a language it cannot read.
                            match routine.Language.ToLowerInvariant() with
                            | "sql" ->
                                validateScript
                                    parser
                                    snapshot
                                    searchPath
                                    (bound @ routine.Parameters)
                                    (depth + 1)
                                    routine.Body
                                |> List.map (fun f ->
                                    { f with
                                        // Re-anchor to the defining statement:
                                        // offsets inside the body do not exist
                                        // in the file the caller passed.
                                        StatementIndex = index
                                        Offset = location.Offset
                                        Length = location.Length
                                        Message = sprintf "in routine body: %s" f.Message })
                            | "plpgsql" ->
                                // `ParseRoutineBody` wraps libpg_query's plpgsql
                                // parser, which expects the WHOLE
                                // `CREATE FUNCTION` statement rather than the
                                // bare body — passing just the body makes every
                                // valid function look like a parse error, which
                                // is a false INVALID and worse than no check.
                                let statementText =
                                    if location.Length > 0 && location.Offset + location.Length <= sql.Length then
                                        sql.Substring(location.Offset, location.Length)
                                    else
                                        sql

                                match parser.ParseRoutineBody statementText with
                                | Ok () ->
                                    [ findingFor Unverifiable index location ""
                                        "routine body parses as plpgsql, but Strata does not resolve references inside plpgsql bodies" ]
                                | Microsoft.FSharp.Core.Error error ->
                                    [ findingFor Invalid index location ""
                                        (sprintf "routine body does not parse as plpgsql: %s" error.Message) ]
                            | other ->
                                [ findingFor Unverifiable index location ""
                                    (sprintf "routine body is written in '%s', which Strata does not read" other) ]

                    validateRelations snapshot searchPath index location extraction
                    @ validateColumns snapshot searchPath bound index location extraction
                    @ dynamic
                    @ routineBody
                    @ unmodelled)
            |> List.concat

        findings

    let validate
        (parser: IDialectParser)
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (sql: string)
        : Report =

        let findings = validateScript parser snapshot searchPath [] 0 sql

        { Findings = findings
          StatementCount = List.length (parser.ParseScript sql)
          Scope =
            { Scope.nothingAnalyzed with
                LiveDatabaseInspected = true
                SchemaCompleteness = snapshot.Completeness
                DialectCompatibility =
                    DialectCompatibility.compare
                        parser.Identity.DialectMajor
                        (snapshot.ServerVersion
                         |> Option.map (fun fact -> (Strata.Semantic.Evidence.Fact.value fact).Major)) } }

    // ---- output -----------------------------------------------------------

    /// Wire tag, hand-written per Boundary Preservation. `unverifiable` is a
    /// first-class outcome and must never be rendered as a warning-flavoured
    /// pass; a consumer branching on this string has to handle three cases.
    let private severityTag (severity: Severity) =
        match severity with
        | Invalid -> "invalid"
        | Unverifiable -> "unverifiable"

    let private findingJson (f: Finding) =
        JObject [ "severity", JString(severityTag f.Severity)
                  "statementIndex", JInt f.StatementIndex
                  "offset", JInt f.Offset
                  "length", JInt f.Length
                  "subject", (if f.Subject = "" then JNull else JString f.Subject)
                  "message", JString f.Message ]

    let toJson (path: string) (report: Report) =
        JObject [ "file", JString path
                  "outcome",
                  JString(
                      if not (List.isEmpty (Report.invalidFindings report)) then "invalid"
                      elif not (List.isEmpty (Report.unverifiableFindings report)) then "unverifiable"
                      else "valid")
                  "exitCode", JInt(Report.exitCode report)
                  "statementCount", JInt report.StatementCount
                  "findings", JArray(report.Findings |> List.map findingJson)
                  "scope", scope report.Scope ]
        |> Json.render

    let toText (path: string) (report: Report) =
        let lines = ResizeArray<string>()
        let invalid = Report.invalidFindings report
        let unverifiable = Report.unverifiableFindings report

        let outcome =
            if not (List.isEmpty invalid) then "INVALID"
            elif not (List.isEmpty unverifiable) then "UNVERIFIABLE"
            else "VALID"

        lines.Add(sprintf "%s: %s (%d statement(s))" outcome path report.StatementCount)
        lines.Add ""

        for f in report.Findings do
            lines.Add(
                sprintf
                    "  [%s] statement %d at offset %d"
                    (severityTag f.Severity)
                    (f.StatementIndex + 1)
                    f.Offset)

            lines.Add(sprintf "         %s" f.Message)
            lines.Add ""

        if not (List.isEmpty unverifiable) then
            lines.Add "NOTE: 'unverifiable' is NOT a pass. Strata could not determine whether these"
            lines.Add "      references are correct, and says so rather than guessing either way."

        System.String.Join("\n", lines)
