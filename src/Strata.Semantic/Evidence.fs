namespace Strata.Semantic

/// Provenance and certainty for derived facts.
///
/// Authority for: why Strata believes something (P-007, ER-007, D-017).
/// Every derived fact carries evidence; a fact with no evidence cannot be
/// constructed, because `Fact` has no case that omits it.
module Evidence =

    /// Where a fact came from.
    ///
    /// Notebook §9's evidence list, narrowed to sources a current requirement
    /// can actually produce. `ManualDeclaration` is present because an operator
    /// override must be distinguishable from something Strata worked out.
    type EvidenceSource =
        | Catalog of catalogRelation: string
        | ForeignKeyConstraint of constraintName: string
        | ViewDefinition of view: string
        | RoutineBody of routine: string
        | SqlUnit of sourceId: string
        | ManualDeclaration of declaredBy: string

    /// One piece of support for a fact.
    type EvidenceItem =
        { Source: EvidenceSource
          /// Free-text detail. Deliberately not parsed: it is for humans and for
          /// display, never for decisions.
          Detail: string }

    /// Categorical certainty.
    ///
    /// Notebook §144 and the ROS confidence standard both warn against decimal
    /// scores that have not been calibrated; Q-022 asks how calibration would
    /// even work. Until that question is answered this stays categorical, and
    /// there is deliberately no numeric field to fill in (RK-017).
    type Certainty =
        /// Declared by the database itself. Not an inference.
        | Certain
        /// Strong, repeated support.
        | High
        /// Some support.
        | Medium
        /// Weak support; present so it can be reported, not acted on.
        | Low

    /// A fact together with the evidence that supports it.
    ///
    /// Construction requires at least one evidence item. `Fact.create` returns
    /// an option rather than raising: an empty evidence list is a programming
    /// error at a boundary, and ER-008 forbids collapsing it into a silent
    /// success.
    type Fact<'T> =
        private
            { Value: 'T
              Certainty: Certainty
              Evidence: EvidenceItem list }

        member this.Item = this.Value
        member this.Support = this.Evidence
        member this.HowCertain = this.Certainty

    [<RequireQualifiedAccess>]
    module Fact =

        let create (certainty: Certainty) (evidence: EvidenceItem list) (value: 'T) : Fact<'T> option =
            match evidence with
            | [] -> None
            | _ -> Some { Value = value; Certainty = certainty; Evidence = evidence }

        /// A fact the database declared about itself. Always `Certain`.
        let declared (source: EvidenceSource) (detail: string) (value: 'T) : Fact<'T> =
            { Value = value
              Certainty = Certain
              Evidence = [ { Source = source; Detail = detail } ] }

        let value (f: Fact<'T>) = f.Item
        let certainty (f: Fact<'T>) = f.HowCertain
        let evidence (f: Fact<'T>) = f.Support
        let evidenceCount (f: Fact<'T>) = List.length f.Support
