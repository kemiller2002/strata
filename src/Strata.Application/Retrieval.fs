namespace Strata.Application

open Strata.Semantic.Identity
open Strata.Semantic.Evidence
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Semantic.Wire
open Strata.Analysis.Graph

/// Targeted retrieval.
///
/// Authority for: assembling a compact answer to one question, with its scope.
///
/// ER-017 and D-019: agent context is retrieved, not dumped. A 1,000-table
/// database must not produce a 1,000-table answer (§P-017). NG-010 is the trap
/// to avoid — replacing a giant SQL file with a giant JSON file is not
/// retrieval.
///
/// Every answer carries its `Scope`. That is not decoration: without it a
/// reader cannot tell whether "no readers" is a fact or a bound (§144.11).
module Retrieval =

    /// A question Strata can answer.
    type Query =
        | Inspect of QualifiedName
        | Relationships of QualifiedName
        | Path of from: QualifiedName * to': QualifiedName
        | Readers of QualifiedName
        | Writers of QualifiedName
        /// The analysis scope alone. Exists so a session can fetch the scope
        /// once and then use brief answers, rather than paying for it per query.
        | ScopeOnly

    /// The answer, always paired with what was analysed to produce it.
    type Answer =
        { Query: Query
          Result: Json
          Scope: Scope
          /// Why the answer might be incomplete. Empty is a claim in itself, so
          /// it is only empty when the scope genuinely supports the answer.
          Caveats: string list }

    let private queryTag (query: Query) =
        match query with
        | Inspect _ -> "inspect"
        | Relationships _ -> "relationships"
        | Path _ -> "path"
        | Readers _ -> "readers"
        | Writers _ -> "writers"
        | ScopeOnly -> "scope"

    let private queryTarget (query: Query) =
        match query with
        | Inspect n
        | Relationships n
        | Readers n
        | Writers n -> QualifiedName.display n
        | Path (a, b) -> QualifiedName.display a + " -> " + QualifiedName.display b
        | ScopeOnly -> "(analysis scope)"

    /// Render a table compactly: enough to act on, not the whole snapshot.
    let private tableSummary (t: Table) =
        JObject [ "name", JString(QualifiedName.display t.Name)
                  "kind", JString "table"
                  "scope", managementScope t.Scope
                  "columns",
                  JArray(
                      t.Columns
                      |> List.sortBy (fun c -> c.Position)
                      |> List.map (fun c ->
                          JObject [ "name", JString c.Name.Display
                                    "type", JString(QualifiedName.display c.Type.TypeName)
                                    "nullable", JBool c.Type.IsNullable ])
                  )
                  "primaryKey",
                  (match t.PrimaryKey with
                   | Some pk -> JArray(pk.Columns |> List.map (fun c -> JString c.Display))
                   | None -> JNull)
                  "foreignKeys",
                  JArray(
                      t.ForeignKeys
                      |> List.map (fun fk ->
                          JObject [ "columns", JArray(fk.Columns |> List.map (fun c -> JString c.Display))
                                    "references", JString(QualifiedName.display fk.ReferencedTable) ])
                  )
                  // Unique constraints, check constraints and indexes were
                  // omitted from this summary until EV-STRATA-2026-A2B8 showed
                  // the omission made three ordinary questions unanswerable
                  // from Strata while the raw DDL answered them: what values a
                  // column allows, whether a column is indexed, and whether it
                  // is unique. All three were already in the semantic model and
                  // populated by introspection — they were simply never
                  // rendered. A retrieval answer that is small because it is
                  // lossy is the inverse of NG-010, not a saving.
                  "uniqueConstraints",
                  JArray(
                      t.UniqueConstraints
                      |> List.map (fun uc ->
                          JObject [ "name", JString uc.ConstraintName.Display
                                    "columns", JArray(uc.Columns |> List.map (fun c -> JString c.Display)) ])
                  )
                  "checkConstraints",
                  JArray(
                      t.CheckConstraints
                      |> List.map (fun cc ->
                          JObject [ "name", JString cc.ConstraintName.Display
                                    // The expression text as the catalog renders
                                    // it. Strata does not parse it and does not
                                    // pretend to understand it.
                                    "expression", JString cc.Expression ])
                  )
                  "indexes",
                  JArray(
                      t.Indexes
                      |> List.map (fun idx ->
                          JObject [ "name", JString idx.Name.Display
                                    "columns", JArray(idx.Columns |> List.map (fun c -> JString c.Display))
                                    "unique", JBool idx.IsUnique
                                    "predicate",
                                    (match idx.Predicate with
                                     | Some predicate -> JString predicate
                                     | None -> JNull) ])
                  ) ]

    let private relationshipSummary (r: Relationship) =
        JObject [ "from", JString(QualifiedName.display r.FromTable)
                  "fromColumns", JArray(r.FromColumns |> List.map (fun c -> JString c.Display))
                  "to", JString(QualifiedName.display r.ToTable)
                  "toColumns", JArray(r.ToColumns |> List.map (fun c -> JString c.Display))
                  "kind", JString(RelationshipKind.tag r.Kind)
                  "certainty", certainty r.Certainty
                  "evidenceCount", JInt r.EvidenceCount
                  // The guard that stops an inferred edge being read as a
                  // constraint the database enforces (§9, RK-017).
                  "enforcedByDatabase", JBool(RelationshipKind.isEnforcedByDatabase r.Kind)
                  "evidence", JArray(r.Evidence |> List.map evidenceItem |> List.sortBy Json.render) ]

    /// A short, stable digest of a scope.
    ///
    /// Lets a brief answer name WHICH scope it was produced under, so a reader
    /// or agent can tell that two answers share a scope, and can fetch the full
    /// block once (`strata scope`) rather than re-reading it per answer.
    ///
    /// Not a security hash — it exists to identify, not to authenticate.
    let scopeDigest (s: Scope) =
        let rendered = Json.render (scope s)

        let mutable hash = 5381UL

        for ch in rendered do
            hash <- (hash * 33UL) ^^^ uint64 ch

        sprintf "%016x" hash

    /// Caveats an answer must carry, derived from scope rather than authored.
    ///
    /// Deriving them means a new gap in the scope automatically reaches every
    /// answer, instead of relying on each call site to remember.
    let private caveatsFor (scope: Scope) (isAbsenceClaim: bool) =
        [ if not scope.LiveDatabaseInspected then
              yield "No live database was inspected; structure is from a cached or supplied snapshot."

          for category, reason in Completeness.inaccessibleCategories scope.SchemaCompleteness do
              yield sprintf "Metadata category '%s' was inaccessible: %s" category reason

          match Completeness.stateOf "relation_access" scope.SchemaCompleteness with
          | Partial reason -> yield sprintf "Some objects are visible but unreadable: %s" reason
          | Complete
          | Inaccessible _
          | NotRequested -> ()

          match scope.DialectCompatibility with
          | Diverged (parserMajor, serverMajor) ->
              yield
                  sprintf
                      "Strata parsed with the PostgreSQL %d grammar but the target server is %d. A statement Strata reports as parsed may still be rejected by the target."
                      parserMajor
                      serverMajor
          | ServerVersionUnknown parserMajor ->
              yield
                  sprintf
                      "Strata parsed with the PostgreSQL %d grammar; the target server version was not established, so parse success is not evidence the target accepts a statement."
                      parserMajor
          | Matched _
          | NoParsingPerformed -> ()

          if scope.Corpus.ParseFailures > 0 then
              yield sprintf "%d SQL unit(s) failed to parse and contribute no dependencies." scope.Corpus.ParseFailures

          if scope.Corpus.ExtractionGaps > 0 then
              yield sprintf "%d SQL unit(s) were only partially analysed." scope.Corpus.ExtractionGaps

          if isAbsenceClaim && not (Scope.supportsAbsenceClaim scope) then
              yield
                  "This is a bounded result, not an absence claim: the analysed scope does not support concluding that nothing else exists."

          if not scope.ExternalConsumersIndexed then
              yield "External database consumers were not indexed; readers or writers outside the analysed corpus are not represented." ]

    /// Answer a query.
    let answer (snapshot: SchemaSnapshot) (graph: SemanticGraph) (scope': Scope) (query: Query) : Answer =
        let isAbsenceClaim =
            match query with
            | Readers _
            | Writers _
            | Relationships _ -> true
            | Inspect _
            | Path _
            | ScopeOnly -> false

        let result =
            match query with
            | Inspect name ->
                snapshot.Objects
                |> List.tryFind (fun o -> QualifiedName.display (SchemaObject.name o) = QualifiedName.display name)
                |> function
                    | Some (TableObject t) -> tableSummary t
                    | Some (ViewObject v) ->
                        JObject [ "name", JString(QualifiedName.display v.Name)
                                  "kind", JString(if v.IsMaterialized then "materialized-view" else "view")
                                  "scope", managementScope v.Scope
                                  "columns",
                                  JArray(
                                      v.Columns
                                      |> List.sortBy (fun c -> c.Position)
                                      |> List.map (fun c -> JString c.Name.Display)
                                  ) ]
                    | Some (RoutineObject r) ->
                        JObject [ "name", JString(QualifiedName.display r.Name)
                                  "kind", JString "routine"
                                  "language", JString r.Language
                                  "arguments", JArray(r.ArgumentTypes |> List.map JString) ]
                    | None ->
                        // Not in the snapshot is not "does not exist".
                        JObject [ "name", JString(QualifiedName.display name)
                                  "found", JBool false
                                  "note", JString "Not present in the analysed snapshot. This does not establish that the object does not exist." ]

            | Relationships name ->
                JArray(SemanticGraph.relationshipsFor name graph |> List.map relationshipSummary)

            | Path (a, b) ->
                match SemanticGraph.pathBetween a b graph with
                | Some edges ->
                    JObject [ "found", JBool true
                              "length", JInt(List.length edges)
                              "edges", JArray(edges |> List.map relationshipSummary)
                              // A path is only as good as its weakest edge.
                              "allEdgesDatabaseEnforced",
                              JBool(edges |> List.forall (fun e -> RelationshipKind.isEnforcedByDatabase e.Kind)) ]
                | None ->
                    JObject [ "found", JBool false
                              "note", JString "No path within the analysed relationships." ]

            | Readers name -> JArray(SemanticGraph.readersOf name graph |> List.map JString)

            | Writers name -> JArray(SemanticGraph.writersOf name graph |> List.map JString)

            | ScopeOnly ->
                // The scope itself travels in the answer's own scope field. The
                // result carries the digest that brief answers reference, so a
                // caller can confirm a brief answer belongs to THIS scope.
                JObject [ "scopeDigest", JString(scopeDigest scope') ]

        { Query = query
          Result = result
          Scope = scope'
          Caveats = caveatsFor scope' isAbsenceClaim }

    /// Caveats that change how a result is READ, as opposed to those that
    /// describe the analysis around it.
    ///
    /// These are never omitted, in any output mode. `EV-STRATA-2026-F4C6`
    /// showed the scope block costs more than the answer, but the fix for that
    /// must not reintroduce the failure the block exists to prevent: a reader
    /// seeing `[]` and concluding "nothing reads this". The bounded-result
    /// warning and the dialect-divergence warning both change the meaning of
    /// the result itself, so they travel with every answer regardless of mode.
    let private loadBearingCaveats (a: Answer) =
        a.Caveats
        |> List.filter (fun c ->
            c.Contains "not an absence claim"
            || c.Contains "may still be rejected by the target"
            || c.Contains "not evidence the target accepts")

    /// Render an answer as JSON. The machine-readable contract (PR-017).
    let toJson (a: Answer) =
        JObject [ "query", JString(queryTag a.Query)
                  "target", JString(queryTarget a.Query)
                  "result", a.Result
                  "scope", scope a.Scope
                  "caveats", JArray(a.Caveats |> List.map JString) ]
        |> Json.render

    /// Render an answer as JSON with the scope replaced by a digest.
    ///
    /// For a multi-query session: fetch the full scope once, then use this.
    /// It keeps the load-bearing caveats inline and reports how many were
    /// elided, so nothing is silently dropped — a reader can always see that
    /// more exists and where to get it.
    let toJsonBrief (a: Answer) =
        let loadBearing = loadBearingCaveats a

        JObject [ "query", JString(queryTag a.Query)
                  "target", JString(queryTarget a.Query)
                  "result", a.Result
                  "scopeDigest", JString(scopeDigest a.Scope)
                  // Retained even in brief mode: this one changes how the
                  // result itself must be read.
                  "supportsAbsenceClaim", JBool(Scope.supportsAbsenceClaim a.Scope)
                  "caveats", JArray(loadBearing |> List.map JString)
                  "caveatsElided", JInt(List.length a.Caveats - List.length loadBearing)
                  "fullScopeCommand", JString "strata scope" ]
        |> Json.render

    /// Render an answer for a terminal. Human-readable (PR-017, PR-020).
    ///
    /// Caveats are printed LAST and always, because a reader who stops early
    /// should still not have been misled about certainty — and a reader who
    /// reads to the end sees what was not analysed.
    let toText (a: Answer) =
        let lines = ResizeArray<string>()
        lines.Add(sprintf "%s: %s" (queryTag a.Query) (queryTarget a.Query))
        lines.Add ""
        lines.Add(Json.render a.Result)

        if not (List.isEmpty a.Caveats) then
            lines.Add ""
            lines.Add "Analysis scope caveats:"

            for caveat in a.Caveats do
                lines.Add("  - " + caveat)

        System.String.Join("\n", lines)
