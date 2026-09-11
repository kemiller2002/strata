namespace Strata.Semantic

open System
open System.Text
open Strata.Semantic.Identity
open Strata.Semantic.Evidence
open Strata.Semantic.Resolution
open Strata.Semantic.AnalysisScope

/// Deterministic wire rendering for Strata's public output.
///
/// Authority for: the stable machine-readable vocabulary (NFR-003) and the
/// determinism guarantee (NFR-001).
///
/// `.sde/architecture/BOUNDARY-PRESERVATION.md` requires that wire
/// representations be deliberately specified and never depend on incidental
/// host-language or framework serialization behaviour. So this module
/// hand-writes every rendering rather than reflecting over F# types: a renamed
/// union case must break a test here, not silently change Strata's output
/// contract.
///
/// Determinism rules, all load-bearing for NFR-001:
///   - object keys are emitted in a fixed declared order, never a hash order;
///   - collections are sorted by a declared key before rendering;
///   - no timestamps, GUIDs, machine names or culture-sensitive formatting.
module Wire =

    /// A minimal JSON value. Strata renders its own JSON rather than taking a
    /// serializer dependency, because Tier 1 may reference only FSharp.Core.
    type Json =
        | JNull
        | JBool of bool
        | JInt of int
        | JString of string
        /// Members are rendered in list order. Construction sites choose that
        /// order deliberately; nothing here sorts them for you.
        | JObject of (string * Json) list
        | JArray of Json list

    [<RequireQualifiedAccess>]
    module Json =

        let private escape (s: string) =
            let sb = StringBuilder(s.Length + 2)
            sb.Append('"') |> ignore

            for ch in s do
                match ch with
                | '"' -> sb.Append "\\\"" |> ignore
                | '\\' -> sb.Append "\\\\" |> ignore
                | '\b' -> sb.Append "\\b" |> ignore
                | '\f' -> sb.Append "\\f" |> ignore
                | '\n' -> sb.Append "\\n" |> ignore
                | '\r' -> sb.Append "\\r" |> ignore
                | '\t' -> sb.Append "\\t" |> ignore
                | c when c < ' ' -> sb.AppendFormat(Globalization.CultureInfo.InvariantCulture, "\\u{0:x4}", int c) |> ignore
                | c -> sb.Append c |> ignore

            sb.Append('"') |> ignore
            sb.ToString()

        /// Render compactly. Invariant culture throughout: a machine-readable
        /// contract must not change because the host locale did.
        let rec render (json: Json) : string =
            match json with
            | JNull -> "null"
            | JBool true -> "true"
            | JBool false -> "false"
            | JInt i -> i.ToString(Globalization.CultureInfo.InvariantCulture)
            | JString s -> escape s
            | JObject members ->
                members
                |> List.map (fun (key, value) -> escape key + ":" + render value)
                |> String.concat ","
                |> fun body -> "{" + body + "}"
            | JArray items -> items |> List.map render |> String.concat "," |> fun body -> "[" + body + "]"

    // ---- Identity -----------------------------------------------------------

    let identifier (id: Identifier) =
        JObject [ "text", JString id.Text
                  "quoted", JBool id.WasQuoted ]

    let qualifiedName (q: QualifiedName) =
        JObject [ "schema", (match q.Schema with Some s -> identifier s | None -> JNull)
                  "name", identifier q.Name
                  "display", JString(QualifiedName.display q) ]

    /// Hand-written tag. Renaming a case must fail a test, not alter output.
    let objectKind (kind: ObjectKind) =
        JString(
            match kind with
            | ObjectKind.Schema -> "schema"
            | ObjectKind.Table -> "table"
            | ObjectKind.View -> "view"
            | ObjectKind.MaterializedView -> "materialized-view"
            | ObjectKind.Column -> "column"
            | ObjectKind.Index -> "index"
            | ObjectKind.Sequence -> "sequence"
            | ObjectKind.Routine -> "routine"
            | ObjectKind.Trigger -> "trigger"
            | ObjectKind.Constraint -> "constraint")

    let managementScope (scope: ManagementScope) =
        JString(
            match scope with
            | Managed -> "managed"
            | Observed -> "observed"
            | ExtensionOwned -> "extension-owned"
            | SystemOwned -> "system-owned"
            | UnknownOwnership -> "unknown")

    // ---- Evidence -----------------------------------------------------------

    let certainty (c: Certainty) =
        JString(
            match c with
            | Certain -> "certain"
            | High -> "high"
            | Medium -> "medium"
            | Low -> "low")

    let evidenceSource (source: EvidenceSource) =
        match source with
        | Catalog relation -> JObject [ "kind", JString "catalog"; "relation", JString relation ]
        | ForeignKeyConstraint name -> JObject [ "kind", JString "foreign-key"; "constraint", JString name ]
        | ViewDefinition view -> JObject [ "kind", JString "view-definition"; "view", JString view ]
        | RoutineBody routine -> JObject [ "kind", JString "routine-body"; "routine", JString routine ]
        | SqlUnit sourceId -> JObject [ "kind", JString "sql-unit"; "source", JString sourceId ]
        | ManualDeclaration by -> JObject [ "kind", JString "manual"; "declaredBy", JString by ]

    let evidenceItem (item: EvidenceItem) =
        JObject [ "source", evidenceSource item.Source
                  "detail", JString item.Detail ]

    /// Evidence order is not semantically meaningful, so it is sorted by its
    /// rendered form to keep output byte-identical across runs (NFR-001).
    let fact (renderValue: 'T -> Json) (f: Fact<'T>) =
        JObject [ "value", renderValue (Fact.value f)
                  "certainty", certainty (Fact.certainty f)
                  "evidenceCount", JInt(Fact.evidenceCount f)
                  "evidence",
                  JArray(
                      Fact.evidence f
                      |> List.map evidenceItem
                      |> List.sortBy Json.render
                  ) ]

    // ---- Resolution ---------------------------------------------------------

    let resolutionGap (gap: ResolutionGap) =
        match gap with
        | SchemaNotQualified -> JObject [ "kind", JString "schema-not-qualified" ]
        | ColumnNotAttributable candidates ->
            JObject [ "kind", JString "column-not-attributable"
                      "candidates", JArray(candidates |> List.map qualifiedName |> List.sortBy Json.render) ]
        | WildcardNotExpanded -> JObject [ "kind", JString "wildcard-not-expanded" ]
        | MultipleCandidates candidates ->
            JObject [ "kind", JString "multiple-candidates"
                      "candidates", JArray(candidates |> List.map qualifiedName |> List.sortBy Json.render) ]
        | ConstructNotModelled construct ->
            JObject [ "kind", JString "construct-not-modelled"; "construct", JString construct ]
        | AnalyzabilityLimit reason ->
            JObject [ "kind", JString "analyzability-limit"; "reason", JString reason ]

    /// Renders the resolution state ALWAYS, including for `Resolved`.
    ///
    /// A consumer must never have to infer the state from the presence or
    /// absence of a field: that would be exactly the Representation Collapse
    /// that ER-008 forbids at the boundary.
    let resolution (renderValue: 'T -> Json) (r: Resolution<'T>) =
        JObject [ "state", JString(Resolution.tag r)
                  "value", (match Resolution.tryValue r with Some v -> renderValue v | None -> JNull)
                  "gap", (match Resolution.tryGap r with Some g -> resolutionGap g | None -> JNull)
                  "dependencyEdgeSafe", JBool(Resolution.isDependencyEdgeSafe r) ]

    // ---- Analysis scope -----------------------------------------------------

    let categoryState (state: CategoryState) =
        match state with
        | Complete -> JObject [ "state", JString "complete"; "reason", JNull ]
        | Partial reason -> JObject [ "state", JString "partial"; "reason", JString reason ]
        | Inaccessible reason -> JObject [ "state", JString "inaccessible"; "reason", JString reason ]
        | NotRequested -> JObject [ "state", JString "not-requested"; "reason", JNull ]

    /// Categories are sorted by name: the order they were discovered in is an
    /// accident of the introspection query, not information.
    let completeness (c: Completeness) =
        JObject [ "categories",
                  JArray(
                      c.Categories
                      |> List.sortBy fst
                      |> List.map (fun (name, state) ->
                          JObject [ "category", JString name; "state", categoryState state ])
                  )
                  "fullyComplete", JBool(Completeness.isFullyComplete c) ]

    let dialectCompatibility (c: DialectCompatibility) =
        match c with
        | Matched major ->
            JObject [ "state", JString "matched"; "parserMajor", JInt major; "serverMajor", JInt major ]
        | Diverged (parserMajor, serverMajor) ->
            JObject [ "state", JString "diverged"
                      "parserMajor", JInt parserMajor
                      "serverMajor", JInt serverMajor ]
        | ServerVersionUnknown parserMajor ->
            JObject [ "state", JString "server-version-unknown"
                      "parserMajor", JInt parserMajor
                      "serverMajor", JNull ]
        | NoParsingPerformed ->
            JObject [ "state", JString "no-parsing-performed"; "parserMajor", JNull; "serverMajor", JNull ]

    let corpusScope (s: CorpusScope) =
        JObject [ "indexedSources", JArray(s.IndexedSources |> List.sort |> List.map JString)
                  "parseFailures", JInt s.ParseFailures
                  "extractionGaps", JInt s.ExtractionGaps ]

    /// The scope bound that must accompany every Strata answer (PR-021).
    ///
    /// `supportsAbsenceClaim` is rendered so a consumer cannot read "0 readers"
    /// without also seeing whether that zero is a fact or a bound (§144.11).
    let scope (s: Scope) =
        JObject [ "liveDatabaseInspected", JBool s.LiveDatabaseInspected
                  "schemaCompleteness", completeness s.SchemaCompleteness
                  "corpus", corpusScope s.Corpus
                  "dialectCompatibility", dialectCompatibility s.DialectCompatibility
                  // Whether a successful parse is evidence the target accepts
                  // the statement. False whenever the grammars diverge.
                  "parseImpliesTargetAccepts",
                  JBool(DialectCompatibility.parseImpliesTargetAccepts s.DialectCompatibility)
                  "runtimeQueriesIndexed", JBool s.RuntimeQueriesIndexed
                  "externalConsumersIndexed", JBool s.ExternalConsumersIndexed
                  "ormMetadataIndexed", JBool s.OrmMetadataIndexed
                  "supportsAbsenceClaim", JBool(Scope.supportsAbsenceClaim s) ]
