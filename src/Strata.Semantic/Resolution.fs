namespace Strata.Semantic

open Strata.Semantic.Identity

/// Resolution states for references extracted from SQL.
///
/// Authority for: how certain Strata is about what a name in SQL refers to.
///
/// This module exists because of EV-STRATA-2026-B9C4 and DF-STRATA-2026-9B2E.
/// A parse tree is not a bound tree. The states below are the notebook's P-008
/// and ER-008 made structural: because `Resolution` is a closed union, no
/// consumer can silently treat `Ambiguous` as `Unresolved`, or either as
/// "nothing found", without the compiler objecting.
module Resolution =

    /// Why a reference could not be fully resolved.
    ///
    /// Every case here was observed in the Spike B probe, not imagined.
    type ResolutionGap =
        /// The SQL did not schema-qualify the name and search_path has not been
        /// applied. Spike B: `SELECT * FROM orders`.
        | SchemaNotQualified
        /// An unqualified column in a query with more than one candidate
        /// relation. Spike B: `SELECT id, name, total FROM orders o JOIN customer c ...`.
        | ColumnNotAttributable of candidates: QualifiedName list
        /// `SELECT *` was used, so the column list is only knowable against a
        /// catalog snapshot. Reporting zero column references here is the
        /// RK-002 failure.
        | WildcardNotExpanded
        /// More than one catalog object matches under the effective search_path.
        | MultipleCandidates of candidates: QualifiedName list
        /// The construct is understood to exist but Strata does not model it.
        | ConstructNotModelled of construct: string
        /// Analysis could not proceed: dynamic SQL, an unparsed branch, or an
        /// inaccessible catalog. Notebook §11.4.
        | AnalyzabilityLimit of reason: string

    /// What Strata knows about a single reference.
    ///
    /// The five states are those required by the task's binding spike and by
    /// notebook §12. They are ordered from most to least knowledge.
    type Resolution<'T> =
        /// Fully resolved to a specific object. Safe to use as a dependency edge.
        | Resolved of 'T
        /// Resolved enough to name, but something material is missing.
        | PartiallyResolved of value: 'T * gap: ResolutionGap
        /// Several candidates and no basis to choose. NEVER pick one.
        | Ambiguous of gap: ResolutionGap
        /// Strata does not model this construct.
        | Unsupported of gap: ResolutionGap
        /// Strata tried and failed. Distinct from "the object is absent".
        | Unresolved of gap: ResolutionGap

    [<RequireQualifiedAccess>]
    module Resolution =

        /// The only predicate permitted to gate dependency-edge creation.
        ///
        /// DF-STRATA-2026-9B2E invariant: an unresolved or ambiguous reference
        /// must never appear as a declared dependency. Every caller that builds
        /// an edge goes through this.
        let isDependencyEdgeSafe (r: Resolution<'T>) =
            match r with
            | Resolved _ -> true
            | PartiallyResolved _
            | Ambiguous _
            | Unsupported _
            | Unresolved _ -> false

        /// The value, if one is known at all. A caller that wants to display
        /// something may use this; a caller building an edge may NOT.
        let tryValue (r: Resolution<'T>) =
            match r with
            | Resolved v -> Some v
            | PartiallyResolved (v, _) -> Some v
            | Ambiguous _
            | Unsupported _
            | Unresolved _ -> None

        let tryGap (r: Resolution<'T>) =
            match r with
            | Resolved _ -> None
            | PartiallyResolved (_, g) -> Some g
            | Ambiguous g
            | Unsupported g
            | Unresolved g -> Some g

        let map (f: 'T -> 'U) (r: Resolution<'T>) : Resolution<'U> =
            match r with
            | Resolved v -> Resolved(f v)
            | PartiallyResolved (v, g) -> PartiallyResolved(f v, g)
            | Ambiguous g -> Ambiguous g
            | Unsupported g -> Unsupported g
            | Unresolved g -> Unresolved g

        /// Stable machine-readable tag for the wire contract (NFR-003).
        /// Hand-written per Boundary Preservation: the wire vocabulary must not
        /// depend on incidental F# union-case reflection.
        let tag (r: Resolution<'T>) =
            match r with
            | Resolved _ -> "resolved"
            | PartiallyResolved _ -> "partially-resolved"
            | Ambiguous _ -> "ambiguous"
            | Unsupported _ -> "unsupported"
            | Unresolved _ -> "unresolved"
