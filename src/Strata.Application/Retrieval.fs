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

    let private queryTarget (query: Query) =
        match query with
        | Inspect n
        | Relationships n
        | Readers n
        | Writers n -> QualifiedName.display n
        | Path (a, b) -> QualifiedName.display a + " -> " + QualifiedName.display b

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
    let answer (snapshot: SchemaSnapshot) (graph: SemanticGraph) (scope: Scope) (query: Query) : Answer =
        let isAbsenceClaim =
            match query with
            | Readers _
            | Writers _
            | Relationships _ -> true
            | Inspect _
            | Path _ -> false

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

        { Query = query
          Result = result
          Scope = scope
          Caveats = caveatsFor scope isAbsenceClaim }

    /// Render an answer as JSON. The machine-readable contract (PR-017).
    let toJson (a: Answer) =
        JObject [ "query", JString(queryTag a.Query)
                  "target", JString(queryTarget a.Query)
                  "result", a.Result
                  "scope", scope a.Scope
                  "caveats", JArray(a.Caveats |> List.map JString) ]
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
