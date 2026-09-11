module Strata.Tests.EffectsTests

open Xunit
open Strata.Semantic.Identity
open Strata.Analysis.StatementReferences
open Strata.Analysis.Effects

/// Tests for PR-015 / ER-004 / notebook §10: classification by consequence.

let private target schema name =
    QualifiedName.qualified (Identifier.unquoted schema) (Identifier.unquoted name)

let private orders = target "sales" "orders"

[<Fact>]
let ``an unbounded UPDATE is unbounded and can lose data`` () =
    let effects = Effect.classify UpdateShape [ orders ] [] false false

    let update = List.exactlyOne effects
    Assert.Equal(Update, update.Kind)
    Assert.Equal(Unbounded, update.Boundedness)
    Assert.True update.CanLoseData
    Assert.Single(Effect.unboundedWrites effects) |> ignore

[<Fact>]
let ``a bounded UPDATE is not an unbounded write`` () =
    // The §10 contrast: same statement type, different consequence.
    let effects = Effect.classify UpdateShape [ orders ] [] true false

    Assert.Equal(BoundedByPredicate, (List.exactlyOne effects).Boundedness)
    Assert.Empty(Effect.unboundedWrites effects)

[<Fact>]
let ``an unbounded DELETE requires restore from backup`` () =
    let effects = Effect.classify DeleteShape [ orders ] [] false false

    Assert.Equal(RestoreFromBackupRequired, (List.exactlyOne effects).Reversibility)

[<Fact>]
let ``a bounded DELETE is roll-forward rather than restore`` () =
    let effects = Effect.classify DeleteShape [ orders ] [] true false

    Assert.Equal(RollForwardOnly, (List.exactlyOne effects).Reversibility)

[<Fact>]
let ``a complex read is not a data-loss risk`` () =
    // ER-004: constrain effects, not expressiveness. A SELECT over many
    // relations remains a read.
    let effects =
        Effect.classify SelectShape [] [ orders; target "sales" "customer"; target "sales" "invoice" ] true false

    Assert.Equal(3, List.length effects)
    Assert.All(effects, fun e -> Assert.Equal(Read, e.Kind))
    Assert.False(Effect.anyCanLoseData effects)

[<Fact>]
let ``TRUNCATE is unbounded regardless of any predicate signal`` () =
    // TRUNCATE takes no WHERE; passing hasWhere=true must not soften it.
    let effects = Effect.classify (UtilityShape "TRUNCATE") [ orders ] [] true false

    let truncate = List.exactlyOne effects
    Assert.Equal(Truncate, truncate.Kind)
    Assert.Equal(Unbounded, truncate.Boundedness)
    Assert.Equal(RestoreFromBackupRequired, truncate.Reversibility)

[<Fact>]
let ``INSERT does not lose data and is compensatable`` () =
    let insert = List.exactlyOne (Effect.classify InsertShape [ orders ] [] false false)

    Assert.False insert.CanLoseData
    Assert.Equal(Compensatable, insert.Reversibility)

[<Fact>]
let ``DROP can lose data`` () =
    let drop = List.exactlyOne (Effect.classify (DdlShape "DROP") [ orders ] [] false false)

    Assert.Equal(DdlDrop, drop.Kind)
    Assert.True drop.CanLoseData

[<Fact>]
let ``an unsupported statement reports an unknown effect, not no effect`` () =
    // ER-008. Reporting an empty effect list would assert the statement is
    // harmless, which Strata does not know.
    let effects = Effect.classify (UnsupportedShape "SomeExoticStmt") [] [] false false

    let effect = List.exactlyOne effects

    match effect.Kind with
    | UnknownEffect detail -> Assert.Equal("SomeExoticStmt", detail)
    | other -> failwithf "expected UnknownEffect, got %A" other

    Assert.True effect.DegradesAnalyzability
    Assert.Equal(ReversibilityUnknown, effect.Reversibility)

[<Fact>]
let ``dynamic SQL adds an analyzability gap and cannot be ruled data safe`` () =
    // §11.4: not forbidden, but it degrades what Strata can claim.
    let effects = Effect.classify SelectShape [] [ orders ] true true

    Assert.Single(Effect.analyzabilityGaps effects) |> ignore
    Assert.True(Effect.anyCanLoseData effects)

[<Fact>]
let ``a plain read has no analyzability gap`` () =
    // Control for the test above.
    let effects = Effect.classify SelectShape [] [ orders ] true false

    Assert.Empty(Effect.analyzabilityGaps effects)
    Assert.False(Effect.anyCanLoseData effects)
