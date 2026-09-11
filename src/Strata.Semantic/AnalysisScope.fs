namespace Strata.Semantic

/// What was and was not analyzed.
///
/// Authority for: the bound on every claim Strata makes (PR-021).
///
/// Notebook §129 requires every answer to know what was inspected, and §144.11
/// states that "no dependency found" must always remain scoped to analyzed
/// evidence. Without this type, "0 readers" and "0 readers among the 3 files we
/// looked at" are indistinguishable — which is the RK-005 failure.
module AnalysisScope =

    /// Whether a category of metadata was obtained.
    ///
    /// The three failure cases are distinct on purpose. `Inaccessible` (the
    /// account cannot see it) is not `NotRequested` (we did not ask) and neither
    /// is `Complete` with an empty result. ER-008 forbids collapsing them, and
    /// P-009 requires failing closed specifically on `Inaccessible`.
    type CategoryState =
        | Complete
        | Partial of reason: string
        | Inaccessible of reason: string
        | NotRequested

    /// Per-category introspection completeness.
    ///
    /// Categories are named by string rather than a closed union because the set
    /// grows with each introspection increment, and a partial snapshot reporting
    /// an unmodelled category is more honest than one that cannot express it.
    type Completeness =
        { Categories: (string * CategoryState) list }

    [<RequireQualifiedAccess>]
    module Completeness =

        let empty = { Categories = [] }

        let ofList (categories: (string * CategoryState) list) = { Categories = categories }

        let stateOf (category: string) (c: Completeness) =
            c.Categories
            |> List.tryFind (fun (name, _) -> name = category)
            |> Option.map snd
            |> Option.defaultValue NotRequested

        /// True when every requested category came back complete.
        let isFullyComplete (c: Completeness) =
            c.Categories
            |> List.forall (fun (_, state) ->
                match state with
                | Complete
                | NotRequested -> true
                | Partial _
                | Inaccessible _ -> false)

        /// Categories the account could not see. P-009: a high-risk operation
        /// must not proceed while this is non-empty without explicit override.
        let inaccessibleCategories (c: Completeness) =
            c.Categories
            |> List.choose (fun (name, state) ->
                match state with
                | Inaccessible reason -> Some(name, reason)
                | Complete
                | Partial _
                | NotRequested -> None)

    /// Which SQL sources were indexed.
    type CorpusScope =
        { /// Paths or source identifiers actually indexed.
          IndexedSources: string list
          /// Units that failed to parse. Counted, never silently dropped.
          ParseFailures: int
          /// Units parsed but only partially understood.
          ExtractionGaps: int }

    [<RequireQualifiedAccess>]
    module CorpusScope =

        let empty =
            { IndexedSources = []
              ParseFailures = 0
              ExtractionGaps = 0 }

    /// The complete scope bound for a Strata answer.
    ///
    /// Notebook §129's checklist. `LiveDatabaseInspected = false` with a
    /// non-empty corpus is a perfectly normal state and must be reportable.
    type Scope =
        { LiveDatabaseInspected: bool
          SchemaCompleteness: Completeness
          Corpus: CorpusScope
          /// Sources the notebook lists that Strata does not yet index at all.
          /// Present so their absence is stated rather than implied (§129).
          RuntimeQueriesIndexed: bool
          ExternalConsumersIndexed: bool
          OrmMetadataIndexed: bool }

    [<RequireQualifiedAccess>]
    module Scope =

        /// A scope that has inspected nothing. Every field false/empty, which is
        /// the correct starting point: claims widen only as analysis happens.
        let nothingAnalyzed =
            { LiveDatabaseInspected = false
              SchemaCompleteness = Completeness.empty
              Corpus = CorpusScope.empty
              RuntimeQueriesIndexed = false
              ExternalConsumersIndexed = false
              OrmMetadataIndexed = false }

        /// Whether an absence claim ("no readers found") may be stated as fact.
        ///
        /// It may not, unless the live database was inspected AND schema
        /// metadata is complete AND at least one corpus source was indexed.
        /// Otherwise the claim is bounded and must be reported as such (§144.11).
        let supportsAbsenceClaim (s: Scope) =
            s.LiveDatabaseInspected
            && Completeness.isFullyComplete s.SchemaCompleteness
            && not (List.isEmpty s.Corpus.IndexedSources)
