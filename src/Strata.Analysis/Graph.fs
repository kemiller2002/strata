namespace Strata.Analysis

open Strata.Semantic.Identity
open Strata.Semantic.Evidence
open Strata.Semantic.Schema

/// Dependency and relationship graph.
///
/// Authority for: how objects relate, and on what evidence.
///
/// Two rules dominate this module, both from notebook §9 and §144.6:
///
///   1. A **declared** relationship (a foreign key) and an **observed** one
///      (a repeated join) are different kinds of fact and never merge. An
///      observed relationship must never be convertible into a declared one.
///   2. Inference improves navigation; it does not create database truth.
///      Every observed edge carries its evidence count and sources so a reader
///      can judge it, and certainty stays categorical because Q-022
///      (calibration) is unanswered (RK-017).
module Graph =

    /// How Strata came to believe two objects are related.
    type RelationshipKind =
        /// A foreign key. The database asserts this.
        | Declared
        /// Repeated join predicates in the corpus. Strata inferred this.
        | Observed
        /// A human said so.
        | Manual
        /// Declared and observed evidence disagree. Kept as its own state
        /// rather than resolved, because resolving it silently would discard
        /// the disagreement (§9).
        | Conflicting

    [<RequireQualifiedAccess>]
    module RelationshipKind =

        /// Wire tag, hand-written per Boundary Preservation.
        let tag (kind: RelationshipKind) =
            match kind with
            | Declared -> "declared"
            | Observed -> "observed"
            | Manual -> "manual"
            | Conflicting -> "conflicting"

        /// May this edge be treated as a constraint the database enforces?
        ///
        /// Only a declared edge. This is the guard that stops an inferred
        /// relationship being reported as a foreign key (§9, RK-017).
        let isEnforcedByDatabase (kind: RelationshipKind) =
            match kind with
            | Declared -> true
            | Observed
            | Manual
            | Conflicting -> false

    /// A column-to-column relationship.
    type Relationship =
        { FromTable: QualifiedName
          FromColumns: Identifier list
          ToTable: QualifiedName
          ToColumns: Identifier list
          Kind: RelationshipKind
          Certainty: Certainty
          /// How many distinct pieces of evidence support this edge. For a
          /// declared edge this is 1 (the constraint); for an observed edge it
          /// is the number of distinct query shapes that joined this way.
          EvidenceCount: int
          Evidence: EvidenceItem list }

    /// A directional dependency: who reads or writes what.
    type DependencyKind =
        | Reads
        | Writes

    type Dependency =
        { SourceId: string
          Target: QualifiedName
          Kind: DependencyKind
          Evidence: EvidenceItem list }

    /// A dependency on a specific COLUMN.
    ///
    /// Table-level dependencies answer "who touches this table". They cannot
    /// answer "what breaks if I drop this column", which `EV-STRATA-2026-B6F3`
    /// found is the seam question Strata was unable to answer while a plain
    /// text search could. That is the notebook's own flagship example (§130,
    /// "Drop column blocked because 8 readers found").
    ///
    /// `ViaWildcard` records that the dependency came from expanding `SELECT *`
    /// rather than from an explicit mention. Both are real dependencies — a
    /// `SELECT *` reader of a dropped column does break — but a human reviewing
    /// an impact report needs to tell them apart, because a wildcard reader may
    /// tolerate the change while an explicit one certainly will not.
    type ColumnDependency =
        { SourceId: string
          Table: QualifiedName
          Column: Identifier
          Kind: DependencyKind
          ViaWildcard: bool }

    /// The graph.
    type SemanticGraph =
        { Relationships: Relationship list
          Dependencies: Dependency list
          ColumnDependencies: ColumnDependency list }

    [<RequireQualifiedAccess>]
    module SemanticGraph =

        let empty =
            { Relationships = []
              Dependencies = []
              ColumnDependencies = [] }

        /// Declared relationships, straight from the catalog's foreign keys.
        let declaredFrom (snapshot: SchemaSnapshot) : Relationship list =
            snapshot.Objects
            |> List.collect (fun o ->
                match o with
                | TableObject t ->
                    t.ForeignKeys
                    |> List.map (fun fk ->
                        { FromTable = t.Name
                          FromColumns = fk.Columns
                          ToTable = fk.ReferencedTable
                          ToColumns = fk.ReferencedColumns
                          Kind = Declared
                          Certainty = Certain
                          EvidenceCount = 1
                          Evidence =
                            [ { Source =
                                  ForeignKeyConstraint(
                                      // An unnamed declared constraint has no
                                      // name to cite, and saying so beats
                                      // citing one that does not exist.
                                      match fk.ConstraintName with
                                      | Some name -> name.Text
                                      | None -> "(unnamed)")
                                Detail =
                                  sprintf
                                      "%s (%s) references %s (%s)"
                                      (QualifiedName.display t.Name)
                                      (fk.Columns |> List.map (fun c -> c.Text) |> String.concat ", ")
                                      (QualifiedName.display fk.ReferencedTable)
                                      (fk.ReferencedColumns |> List.map (fun c -> c.Text) |> String.concat ", ") } ] })
                | ViewObject _
                | RoutineObject _ -> [])
            // Deterministic order (NFR-001).
            |> List.sortBy (fun r ->
                QualifiedName.display r.FromTable, QualifiedName.display r.ToTable)

        /// Certainty for an observed edge, from how much evidence supports it.
        ///
        /// Categorical, with thresholds stated here rather than hidden in a
        /// score. These thresholds are a starting heuristic, NOT a calibrated
        /// measure — Q-022 asks how calibration would work and is unanswered,
        /// so nothing downstream may treat these as probabilities (RK-017).
        let certaintyForObserved (evidenceCount: int) =
            if evidenceCount >= 10 then High
            elif evidenceCount >= 3 then Medium
            else Low

        /// Build observed relationships from join predicates seen in the corpus.
        ///
        /// `joins` are (fromTable, fromColumn, toTable, toColumn, sourceId)
        /// tuples already resolved by Tier 2 — an unresolved reference never
        /// reaches here, so an observed edge cannot be built on a guess.
        ///
        /// Edges are keyed on the column pair, and evidence counts DISTINCT
        /// sources: the same query counted twice would inflate certainty.
        let observedFrom (joins: (QualifiedName * Identifier * QualifiedName * Identifier * string) list) =
            joins
            |> List.groupBy (fun (fromTable, fromColumn, toTable, toColumn, _) ->
                QualifiedName.display fromTable,
                Identifier.folded fromColumn,
                QualifiedName.display toTable,
                Identifier.folded toColumn)
            |> List.map (fun (_, group) ->
                let (fromTable, fromColumn, toTable, toColumn, _) = List.head group
                let sources = group |> List.map (fun (_, _, _, _, s) -> s) |> List.distinct |> List.sort

                { FromTable = fromTable
                  FromColumns = [ fromColumn ]
                  ToTable = toTable
                  ToColumns = [ toColumn ]
                  Kind = Observed
                  Certainty = certaintyForObserved (List.length sources)
                  EvidenceCount = List.length sources
                  Evidence =
                    sources
                    |> List.map (fun s ->
                        { Source = SqlUnit s
                          Detail =
                            sprintf
                                "join %s.%s = %s.%s"
                                (QualifiedName.display fromTable)
                                fromColumn.Text
                                (QualifiedName.display toTable)
                                toColumn.Text }) })
            |> List.sortBy (fun r -> QualifiedName.display r.FromTable, QualifiedName.display r.ToTable)

        /// Combine declared and observed edges.
        ///
        /// Where both exist for the same column pair, the declared edge wins and
        /// the observed evidence is folded into it as supporting detail — the
        /// database's own assertion is not weakened by also having been seen.
        /// The observed edge is NOT kept separately, which would double-count
        /// the same relationship.
        let combine (declared: Relationship list) (observed: Relationship list) =
            // The key is DIRECTION-INDEPENDENT. A foreign key points from the
            // referencing table to the referenced one, but a join predicate is
            // symmetric: `a.x = b.y` and `b.y = a.x` are the same fact. Keying
            // on the written direction would leave an observed edge sitting
            // beside the declared edge for the identical relationship, showing
            // a Low-certainty duplicate next to a Certain one — the
            // double-counting §9 exists to prevent. The endpoints are therefore
            // sorted before comparison.
            let key (r: Relationship) =
                let endpointA = QualifiedName.display r.FromTable, r.FromColumns |> List.map Identifier.folded
                let endpointB = QualifiedName.display r.ToTable, r.ToColumns |> List.map Identifier.folded

                if endpointA <= endpointB then endpointA, endpointB else endpointB, endpointA

            let declaredKeys = declared |> List.map key |> Set.ofList

            let merged =
                declared
                |> List.map (fun d ->
                    match observed |> List.tryFind (fun o -> key o = key d) with
                    | Some o ->
                        { d with
                            EvidenceCount = d.EvidenceCount + o.EvidenceCount
                            Evidence = d.Evidence @ o.Evidence }
                    | None -> d)

            let observedOnly = observed |> List.filter (fun o -> not (declaredKeys.Contains(key o)))

            (merged @ observedOnly)
            |> List.sortBy (fun r -> QualifiedName.display r.FromTable, QualifiedName.display r.ToTable)

        /// Edges touching an object, in either direction.
        let relationshipsFor (name: QualifiedName) (graph: SemanticGraph) =
            let target = QualifiedName.display name

            graph.Relationships
            |> List.filter (fun r ->
                QualifiedName.display r.FromTable = target || QualifiedName.display r.ToTable = target)

        /// Sources that read an object.
        let readersOf (name: QualifiedName) (graph: SemanticGraph) =
            let target = QualifiedName.display name

            graph.Dependencies
            |> List.filter (fun d -> d.Kind = Reads && QualifiedName.display d.Target = target)
            |> List.map (fun d -> d.SourceId)
            |> List.distinct
            |> List.sort

        /// Sources that write an object.
        let writersOf (name: QualifiedName) (graph: SemanticGraph) =
            let target = QualifiedName.display name

            graph.Dependencies
            |> List.filter (fun d -> d.Kind = Writes && QualifiedName.display d.Target = target)
            |> List.map (fun d -> d.SourceId)
            |> List.distinct
            |> List.sort

        /// Sources that read a specific column.
        ///
        /// This is the impact query for a proposed column drop. Wildcard-derived
        /// dependencies are included, because a `SELECT *` reader genuinely does
        /// break — omitting them is the `RK-002` failure.
        let columnReadersOf (table: QualifiedName) (column: Identifier) (graph: SemanticGraph) =
            let target = QualifiedName.display table

            graph.ColumnDependencies
            |> List.filter (fun d ->
                d.Kind = Reads
                && QualifiedName.display d.Table = target
                && Identifier.sameName d.Column column)
            |> List.map (fun d -> d.SourceId, d.ViaWildcard)
            |> List.distinct
            |> List.sortBy fst

        /// Sources that write a specific column.
        let columnWritersOf (table: QualifiedName) (column: Identifier) (graph: SemanticGraph) =
            let target = QualifiedName.display table

            graph.ColumnDependencies
            |> List.filter (fun d ->
                d.Kind = Writes
                && QualifiedName.display d.Table = target
                && Identifier.sameName d.Column column)
            |> List.map (fun d -> d.SourceId)
            |> List.distinct
            |> List.sort

        /// Shortest relationship path between two objects, breadth-first.
        ///
        /// Returns every edge on the path so a caller can see WHICH edges were
        /// traversed and how well evidenced each is. A path through a Low
        /// certainty observed edge is not the same answer as one through
        /// foreign keys, and collapsing them to a bare node list would hide
        /// that (§9 path ranking, ER-020).
        let pathBetween (fromName: QualifiedName) (toName: QualifiedName) (graph: SemanticGraph) =
            let start = QualifiedName.display fromName
            let goal = QualifiedName.display toName

            let neighbours node =
                graph.Relationships
                |> List.choose (fun r ->
                    let a = QualifiedName.display r.FromTable
                    let b = QualifiedName.display r.ToTable

                    if a = node then Some(b, r)
                    elif b = node then Some(a, r)
                    else None)
                // Deterministic traversal order, so the same graph always yields
                // the same shortest path when several are equally short.
                |> List.sortBy (fun (n, r) -> n, QualifiedName.display r.FromTable)

            let rec search (frontier: (string * Relationship list) list) (visited: Set<string>) =
                match frontier with
                | [] -> None
                | (node, pathSoFar) :: rest ->
                    if node = goal then Some(List.rev pathSoFar)
                    else
                        let next =
                            neighbours node
                            |> List.filter (fun (n, _) -> not (visited.Contains n))
                            |> List.map (fun (n, r) -> n, r :: pathSoFar)

                        let visited' =
                            next |> List.fold (fun (acc: Set<string>) (n, _) -> acc.Add n) visited

                        search (rest @ next) visited'

            if start = goal then Some []
            else search [ start, [] ] (Set.ofList [ start ])
