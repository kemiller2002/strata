namespace Strata.Analysis

open Strata.Semantic.AnalysisScope

/// SQL corpus indexing with provenance.
///
/// Authority for: what Strata knows about a unit of SQL and where it came from.
///
/// Notebook §8 requires each analysed SQL unit to carry a source identifier,
/// location, statement boundaries, content hash, dialect, parser version, parse
/// status and extraction status. The point is that a corpus answer must be
/// attributable: "17 queries join these tables" is worthless if nobody can say
/// which 17 or where they live (ER-007, ER-020).
///
/// §8 also warns against promiscuously scraping arbitrary strings from source
/// code. This module indexes units it is GIVEN; it does not go looking.
module Corpus =

    /// Where a unit of SQL came from.
    type SqlOrigin =
        | SqlFile of path: string
        | MigrationScript of path: string
        | ViewDefinitionSource of view: string
        | RoutineBodySource of routine: string
        /// Supplied directly, e.g. a candidate statement being validated.
        | DirectInput of label: string

    [<RequireQualifiedAccess>]
    module SqlOrigin =

        /// Stable identifier for provenance. Hand-written, not reflected.
        let sourceId (origin: SqlOrigin) =
            match origin with
            | SqlFile path -> "file:" + path
            | MigrationScript path -> "migration:" + path
            | ViewDefinitionSource view -> "view:" + view
            | RoutineBodySource routine -> "routine:" + routine
            | DirectInput label -> "input:" + label

    /// How far analysis got on one statement.
    ///
    /// These are ordered by how much Strata knows. `ParseFailed` is NOT a
    /// statement with no dependencies, and `ExtractedWithGaps` is not the same
    /// as `Extracted` — ER-008 again, at corpus level.
    type UnitStatus =
        | Extracted
        | ExtractedWithGaps of gapCount: int
        | ParseFailed of message: string
        | NotAnalyzed of reason: string

    /// One statement, with everything needed to attribute a claim to it.
    type IndexedStatement =
        { SourceId: string
          Origin: SqlOrigin
          /// Offset of this statement within its source text.
          Offset: int
          Length: int
          /// Content hash of the statement text. Used for deduplication and to
          /// detect that a source changed under a cached analysis (NFR-002).
          ContentHash: string
          /// Literal-independent shape fingerprint, where the parser supplied
          /// one (§123). `None` when fingerprinting failed or was not run —
          /// distinct from a fingerprint of empty string.
          Fingerprint: string option
          Dialect: string
          ParserVersion: string
          Status: UnitStatus }

    /// An indexed corpus.
    type CorpusIndex =
        { Statements: IndexedStatement list }

    [<RequireQualifiedAccess>]
    module CorpusIndex =

        let empty = { Statements = [] }

        let add (statement: IndexedStatement) (index: CorpusIndex) =
            { index with Statements = statement :: index.Statements }

        let ofStatements (statements: IndexedStatement list) = { Statements = statements }

        let parseFailures (index: CorpusIndex) =
            index.Statements
            |> List.filter (fun s ->
                match s.Status with
                | ParseFailed _ -> true
                | Extracted
                | ExtractedWithGaps _
                | NotAnalyzed _ -> false)

        let extractionGaps (index: CorpusIndex) =
            index.Statements
            |> List.filter (fun s ->
                match s.Status with
                | ExtractedWithGaps _ -> true
                | Extracted
                | ParseFailed _
                | NotAnalyzed _ -> false)

        /// Distinct sources represented in the index.
        let indexedSources (index: CorpusIndex) =
            index.Statements |> List.map (fun s -> s.SourceId) |> List.distinct |> List.sort

        /// Statements sharing a fingerprint, i.e. the same query shape with
        /// different literals (§123). Returned smallest-key-first so the result
        /// is deterministic (NFR-001).
        let byFingerprint (index: CorpusIndex) =
            index.Statements
            |> List.choose (fun s -> s.Fingerprint |> Option.map (fun f -> f, s))
            |> List.groupBy fst
            |> List.map (fun (fingerprint, items) -> fingerprint, items |> List.map snd)
            |> List.sortBy fst

        /// The corpus half of an analysis scope.
        ///
        /// This is what makes a corpus claim bounded: it carries which sources
        /// were indexed and how many units Strata could not fully analyse, so a
        /// count derived from this index can never be presented as exhaustive
        /// without also presenting its gaps (PR-021, §144.11).
        let toScope (index: CorpusIndex) : CorpusScope =
            { IndexedSources = indexedSources index
              ParseFailures = List.length (parseFailures index)
              ExtractionGaps = List.length (extractionGaps index) }
