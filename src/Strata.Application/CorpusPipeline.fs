namespace Strata.Application

open System
open System.Security.Cryptography
open System.Text
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.StatementReferences
open Strata.Analysis.DialectPort
open Strata.Analysis.ScopeResolution
open Strata.Analysis.CatalogResolution
open Strata.Analysis.Corpus
open Strata.Analysis.Effects
open Strata.Analysis.Graph

/// Corpus indexing pipeline.
///
/// Authority for: turning SQL text into an index, dependencies and observed
/// relationships.
///
/// This tier orchestrates; it decides nothing. Parsing is the injected port's
/// job, resolution is Tier 2's, certainty is Tier 1's. What lives here is the
/// ORDER of those steps and the accounting of what failed — which is exactly
/// what Tier 3 is for.
///
/// The pipeline takes SQL as `(origin, text)` pairs rather than reading disk
/// itself, so it is fully testable without a filesystem and so §8's rule
/// against promiscuously scraping source stays enforceable by the caller.
module CorpusPipeline =

    /// Everything one corpus run produced.
    type CorpusAnalysis =
        { Index: CorpusIndex
          Dependencies: Dependency list
          /// Join predicates resolved to real relations, ready for
          /// `SemanticGraph.observedFrom`.
          ObservedJoins: (QualifiedName * Identifier * QualifiedName * Identifier * string) list
          /// Every gap encountered, so the caller can report them rather than
          /// presenting a clean-looking result (PR-021).
          Gaps: string list }

    let private sha256Hex (text: string) =
        use sha = SHA256.Create()

        sha.ComputeHash(Encoding.UTF8.GetBytes text)
        |> Array.map (fun b -> b.ToString "x2")
        |> String.concat ""

    /// Resolve one side of a join predicate to a concrete relation.
    ///
    /// A qualifier may be a table alias, a bare relation name, or a CTE name.
    /// Only the first two can yield a relationship; a CTE-qualified column
    /// refers to a query-local result, not a database object (RK-001).
    let private resolveJoinSide
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (extraction: StatementExtraction)
        (qualifier: Identifier option)
        (column: Identifier)
        : QualifiedName option =

        let chain = ScopeChain.ofExtraction extraction

        let candidateRelations =
            extraction.Relations
            |> List.filter (fun m -> m.Role = RelationReference)

        match qualifier with
        | Some q ->
            match ScopeChain.tryResolveLocally q 0 chain with
            | Some (AliasBinding (_, target, _)) ->
                match resolveRelationName snapshot searchPath target with
                | Strata.Semantic.Resolution.Resolved name -> Some name
                | _ -> None
            // A CTE or temp-relation qualifier is query-local. No edge.
            | Some (CteBinding _)
            | Some (TempRelationBinding _) -> None
            | None ->
                // Bare relation name used as a qualifier.
                candidateRelations
                |> List.tryFind (fun m -> Identifier.sameName m.Name.Name q)
                |> Option.bind (fun m ->
                    match resolveRelationName snapshot searchPath m.Name with
                    | Strata.Semantic.Resolution.Resolved name -> Some name
                    | _ -> None)
        | None ->
            // Unqualified column in a join predicate. Attributable only if
            // exactly one relation in scope declares it; otherwise no edge,
            // because guessing is what RK-001 forbids.
            let relations =
                candidateRelations
                |> List.choose (fun m ->
                    match resolveRelationName snapshot searchPath m.Name with
                    | Strata.Semantic.Resolution.Resolved name -> Some name
                    | _ -> None)

            match resolveUnqualifiedColumn snapshot searchPath relations column with
            | Strata.Semantic.Resolution.Resolved resolved -> Some resolved.Relation
            | _ -> None

    /// Analyse one statement.
    let private analyseStatement
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (origin: SqlOrigin)
        (parserVersion: string)
        (statementText: string)
        (location: StatementLocation)
        (extraction: StatementExtraction)
        (fingerprint: string option) =

        let sourceId = SqlOrigin.sourceId origin
        let relations = dependencyEdgeCandidates searchPath extraction
        let _, columnGaps = columnDependencies snapshot searchPath extraction

        // Reads versus writes, from the statement's own shape. A statement that
        // writes also reads its source relations, which is why INSERT ... SELECT
        // contributes both.
        let writeTargets, readTargets =
            match extraction.Shape with
            | SelectShape -> [], relations
            | InsertShape
            | UpdateShape
            | DeleteShape ->
                match relations with
                | target :: rest -> [ target ], rest
                | [] -> [], []
            | DdlShape _
            | UtilityShape _ -> relations, []
            | UnsupportedShape _ -> [], relations

        let effects =
            Effect.classify
                extraction.Shape
                writeTargets
                readTargets
                extraction.HasWherePredicate
                extraction.ContainsDynamicSql

        let dependencies =
            [ for target in readTargets ->
                { SourceId = sourceId
                  Target = target
                  Kind = Reads
                  Evidence = [ { Source = Strata.Semantic.Evidence.SqlUnit sourceId; Detail = "reads" } ] }
              for target in writeTargets ->
                { SourceId = sourceId
                  Target = target
                  Kind = Writes
                  Evidence = [ { Source = Strata.Semantic.Evidence.SqlUnit sourceId; Detail = "writes" } ] } ]

        let joins =
            extraction.JoinPredicates
            |> List.choose (fun predicate ->
                match
                    resolveJoinSide snapshot searchPath extraction predicate.LeftQualifier predicate.LeftColumn,
                    resolveJoinSide snapshot searchPath extraction predicate.RightQualifier predicate.RightColumn
                with
                | Some leftRelation, Some rightRelation when leftRelation <> rightRelation ->
                    // Order the pair deterministically so a.x=b.y and b.y=a.x
                    // become the same edge rather than two (NFR-001).
                    if QualifiedName.display leftRelation <= QualifiedName.display rightRelation then
                        Some(leftRelation, predicate.LeftColumn, rightRelation, predicate.RightColumn, sourceId)
                    else
                        Some(rightRelation, predicate.RightColumn, leftRelation, predicate.LeftColumn, sourceId)
                // A self-join, or a side that did not resolve. Neither is an
                // inter-table relationship.
                | _ -> None)

        let analyzabilityGaps = Effect.analyzabilityGaps effects
        let gapCount = List.length columnGaps + List.length analyzabilityGaps

        let status =
            if gapCount > 0 then ExtractedWithGaps gapCount else Extracted

        let indexed =
            { SourceId = sourceId
              Origin = origin
              Offset = location.Offset
              Length = location.Length
              ContentHash = sha256Hex statementText
              Fingerprint = fingerprint
              Dialect = "postgresql"
              ParserVersion = parserVersion
              Status = status }

        let gapMessages =
            [ for gap in columnGaps -> sprintf "%s: %A" sourceId gap
              for effect in analyzabilityGaps -> sprintf "%s: %A degrades analyzability" sourceId effect.Kind ]

        indexed, dependencies, joins, gapMessages

    /// Analyse a whole corpus.
    ///
    /// A statement that fails to parse is indexed with `ParseFailed` and
    /// contributes no dependencies. It is NOT skipped: a corpus that silently
    /// drops unparseable units would report a smaller, cleaner-looking
    /// dependency set than it actually established (ER-008, §144.11).
    let analyse
        (parser: IDialectParser)
        (snapshot: SchemaSnapshot)
        (searchPath: Identifier list)
        (sources: (SqlOrigin * string) list)
        : CorpusAnalysis =

        let parserVersion = parser.Identity.DialectVersion

        let mutable index = CorpusIndex.empty
        let dependencies = ResizeArray<Dependency>()
        let joins = ResizeArray<QualifiedName * Identifier * QualifiedName * Identifier * string>()
        let gaps = ResizeArray<string>()

        for origin, text in sources do
            let sourceId = SqlOrigin.sourceId origin

            for parsed in parser.ParseScript text do
                match parsed with
                | Failed (location, error) ->
                    index <-
                        CorpusIndex.add
                            { SourceId = sourceId
                              Origin = origin
                              Offset = location.Offset
                              Length = location.Length
                              ContentHash = sha256Hex text
                              Fingerprint = None
                              Dialect = "postgresql"
                              ParserVersion = parserVersion
                              Status = ParseFailed error.Message }
                            index

                    gaps.Add(sprintf "%s: parse failed at position %d: %s" sourceId error.CursorPosition error.Message)

                | Parsed (location, extraction) ->
                    let statementText =
                        if location.Length > 0 && location.Offset + location.Length <= text.Length then
                            text.Substring(location.Offset, location.Length)
                        else
                            text

                    let fingerprint =
                        match parser.Fingerprint statementText with
                        | Ok value -> Some value
                        | Error _ -> None

                    let indexed, statementDependencies, statementJoins, statementGaps =
                        analyseStatement snapshot searchPath origin parserVersion statementText location extraction fingerprint

                    index <- CorpusIndex.add indexed index
                    dependencies.AddRange statementDependencies
                    joins.AddRange statementJoins
                    gaps.AddRange statementGaps

        { Index = index
          Dependencies = List.ofSeq dependencies
          ObservedJoins = List.ofSeq joins
          Gaps = List.ofSeq gaps }

    /// Build the full graph from a snapshot plus a corpus analysis.
    let buildGraph (snapshot: SchemaSnapshot) (analysis: CorpusAnalysis) : SemanticGraph =
        { Relationships =
            SemanticGraph.combine
                (SemanticGraph.declaredFrom snapshot)
                (SemanticGraph.observedFrom analysis.ObservedJoins)
          Dependencies = analysis.Dependencies }

    /// The analysis scope a corpus run establishes.
    let toScope (snapshot: SchemaSnapshot) (analysis: CorpusAnalysis) : Scope =
        { Scope.nothingAnalyzed with
            LiveDatabaseInspected = true
            SchemaCompleteness = snapshot.Completeness
            Corpus = CorpusIndex.toScope analysis.Index }
