namespace Strata.Analysis

open Strata.Semantic.Identity
open Strata.Analysis.StatementReferences

/// Effect classification by consequence.
///
/// Authority for: what a statement *does to the database*, as distinct from
/// what kind of statement it is.
///
/// Implements PR-015 / ER-004 / notebook §10. The notebook's central example is
/// that a complex SELECT may be low risk while a simple unbounded DELETE is
/// catastrophic, so classification keys on consequence — boundedness,
/// data-loss potential, reversibility — not on syntax complexity.
///
/// This module deliberately does NOT decide policy. It describes consequences;
/// a policy profile (WI-0019, blocked on Q-007) decides what to allow. Keeping
/// them apart is what stops Strata becoming "a policy engine that confuses
/// style with safety" (NG-009).
module Effects =

    /// What a statement does to a target.
    type EffectKind =
        | Read
        | Insert
        | Update
        | Delete
        | MergeUpsert
        | DdlCreate
        | DdlAlter
        | DdlDrop
        | Truncate
        | ExecuteRoutine
        | DynamicExecution
        | PrivilegeChange
        | TransactionControl
        /// Recognised as an effect, but Strata cannot say what it does.
        /// ER-008: this is not "no effect".
        | UnknownEffect of detail: string

    /// How tightly a write is bounded.
    ///
    /// The notebook's §10 example turns entirely on this: `UPDATE orders SET
    /// status=... WHERE order_id = 42` versus the same statement with no WHERE.
    ///
    /// `BoundedByPredicate` deliberately does NOT claim the predicate is
    /// selective. Strata has not evaluated it and has no row counts; claiming
    /// otherwise would be the false-confidence failure §144.2 warns about.
    type Boundedness =
        /// No WHERE clause at all. Affects every row.
        | Unbounded
        /// A predicate exists, but its selectivity is unknown to Strata.
        | BoundedByPredicate
        /// Not meaningful for this effect (e.g. a read, or DDL).
        | BoundednessNotApplicable

    /// Whether the effect can be undone, in the notebook's §P-012 vocabulary.
    ///
    /// Strata never promises rollback where rollback can lose data.
    type Reversibility =
        | Reversible
        | Compensatable
        | RollForwardOnly
        | RestoreFromBackupRequired
        | ReversibilityUnknown

    /// One classified effect.
    type Effect =
        { Kind: EffectKind
          /// The object affected, when Strata resolved one. `None` is honest:
          /// an unresolved target is not an absent target (RK-001).
          Target: QualifiedName option
          Boundedness: Boundedness
          /// Can this effect destroy data that was not written by it?
          CanLoseData: bool
          Reversibility: Reversibility
          /// Does this effect reduce Strata's ability to analyse anything?
          /// §11.4: dynamic SQL is not forbidden, but it degrades analyzability
          /// and that must be visible.
          DegradesAnalyzability: bool }

    [<RequireQualifiedAccess>]
    module Effect =

        let private readOf target =
            { Kind = Read
              Target = target
              Boundedness = BoundednessNotApplicable
              CanLoseData = false
              Reversibility = Reversible
              DegradesAnalyzability = false }

        /// Classify a statement's effects from its shape and resolved targets.
        ///
        /// `writeTargets` are the relations the statement writes; `readTargets`
        /// the ones it reads. Both come from Tier 2 resolution, so an
        /// unresolved reference never silently becomes a target.
        let classify
            (shape: StatementShape)
            (writeTargets: QualifiedName list)
            (readTargets: QualifiedName list)
            (hasWherePredicate: bool)
            (containsDynamicSql: bool)
            : Effect list =

            let boundedness =
                if hasWherePredicate then BoundedByPredicate else Unbounded

            let writeEffects =
                match shape with
                | SelectShape -> []
                | InsertShape ->
                    writeTargets
                    |> List.map (fun t ->
                        { Kind = Insert
                          Target = Some t
                          Boundedness = BoundednessNotApplicable
                          CanLoseData = false
                          Reversibility = Compensatable
                          DegradesAnalyzability = false })
                | UpdateShape ->
                    writeTargets
                    |> List.map (fun t ->
                        { Kind = Update
                          Target = Some t
                          Boundedness = boundedness
                          // An UPDATE overwrites prior values: the old values are
                          // gone unless something else captured them.
                          CanLoseData = true
                          Reversibility = RollForwardOnly
                          DegradesAnalyzability = false })
                | DeleteShape ->
                    writeTargets
                    |> List.map (fun t ->
                        { Kind = Delete
                          Target = Some t
                          Boundedness = boundedness
                          CanLoseData = true
                          Reversibility =
                            match boundedness with
                            | Unbounded -> RestoreFromBackupRequired
                            | BoundedByPredicate
                            | BoundednessNotApplicable -> RollForwardOnly
                          DegradesAnalyzability = false })
                | DdlShape operation ->
                    let kind, losesData, reversibility =
                        if operation.StartsWith "DROP" then DdlDrop, true, RestoreFromBackupRequired
                        elif operation.StartsWith "ALTER" then DdlAlter, false, ReversibilityUnknown
                        else DdlCreate, false, Reversible

                    let targets = if List.isEmpty writeTargets then [ None ] else writeTargets |> List.map Some

                    targets
                    |> List.map (fun t ->
                        { Kind = kind
                          Target = t
                          Boundedness = BoundednessNotApplicable
                          CanLoseData = losesData
                          Reversibility = reversibility
                          DegradesAnalyzability = false })
                | UtilityShape "TRUNCATE" ->
                    let targets =
                        if List.isEmpty writeTargets then readTargets else writeTargets

                    targets
                    |> List.map (fun t ->
                        { Kind = Truncate
                          Target = Some t
                          // TRUNCATE has no predicate. It is unconditionally
                          // every row, which is why it is called out separately
                          // from DELETE in §11.3.
                          Boundedness = Unbounded
                          CanLoseData = true
                          Reversibility = RestoreFromBackupRequired
                          DegradesAnalyzability = false })
                | UtilityShape "GRANT" ->
                    [ { Kind = PrivilegeChange
                        Target = None
                        Boundedness = BoundednessNotApplicable
                        CanLoseData = false
                        Reversibility = Reversible
                        DegradesAnalyzability = false } ]
                | UtilityShape "TRANSACTION" ->
                    [ { Kind = TransactionControl
                        Target = None
                        Boundedness = BoundednessNotApplicable
                        CanLoseData = false
                        Reversibility = Reversible
                        DegradesAnalyzability = false } ]
                | UtilityShape other ->
                    [ { Kind = UnknownEffect other
                        Target = None
                        Boundedness = BoundednessNotApplicable
                        CanLoseData = false
                        Reversibility = ReversibilityUnknown
                        DegradesAnalyzability = true } ]
                | UnsupportedShape detail ->
                    // The statement parsed but Strata does not model it. Reporting
                    // no effects here would assert the statement is harmless,
                    // which Strata does not know (ER-008).
                    [ { Kind = UnknownEffect detail
                        Target = None
                        Boundedness = BoundednessNotApplicable
                        CanLoseData = false
                        Reversibility = ReversibilityUnknown
                        DegradesAnalyzability = true } ]

            let readEffects = readTargets |> List.map (fun t -> readOf (Some t))

            let dynamicEffects =
                if containsDynamicSql then
                    [ { Kind = DynamicExecution
                        Target = None
                        Boundedness = BoundednessNotApplicable
                        // Strata cannot see what the constructed statement does,
                        // so it cannot rule out data loss.
                        CanLoseData = true
                        Reversibility = ReversibilityUnknown
                        DegradesAnalyzability = true } ]
                else
                    []

            readEffects @ writeEffects @ dynamicEffects

        /// Does this set of effects include anything that can destroy data?
        let anyCanLoseData (effects: Effect list) = effects |> List.exists (fun e -> e.CanLoseData)

        /// Unbounded writes. The notebook's §10 "risk: critical" case.
        let unboundedWrites (effects: Effect list) =
            effects
            |> List.filter (fun e ->
                e.Boundedness = Unbounded
                && match e.Kind with
                   | Update | Delete | Truncate -> true
                   | Read | Insert | MergeUpsert | DdlCreate | DdlAlter | DdlDrop
                   | ExecuteRoutine | DynamicExecution | PrivilegeChange
                   | TransactionControl | UnknownEffect _ -> false)

        /// Effects that reduce what Strata can claim about this statement.
        let analyzabilityGaps (effects: Effect list) =
            effects |> List.filter (fun e -> e.DegradesAnalyzability)
