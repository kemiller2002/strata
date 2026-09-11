module Strata.Tests.AnalysisScopeTests

open Xunit
open Strata.Semantic.AnalysisScope

/// Tests for PR-021 / §144.11: an absence claim must stay bounded by what was
/// actually analyzed.

[<Fact>]
let ``nothing-analyzed scope cannot support an absence claim`` () =
    Assert.False(Scope.supportsAbsenceClaim Scope.nothingAnalyzed)

[<Fact>]
let ``inaccessible metadata blocks an absence claim`` () =
    // RK-004: the account cannot see RLS policies, so "no policy found" is not
    // a fact about the database.
    let scope =
        { Scope.nothingAnalyzed with
            LiveDatabaseInspected = true
            SchemaCompleteness =
                Completeness.ofList
                    [ "tables", Complete
                      "rls_policies", Inaccessible "permission denied" ]
            Corpus = { CorpusScope.empty with IndexedSources = [ "sql/" ] } }

    Assert.False(Scope.supportsAbsenceClaim scope)
    Assert.Single(Completeness.inaccessibleCategories scope.SchemaCompleteness) |> ignore

[<Fact>]
let ``complete inspection with an indexed corpus supports an absence claim`` () =
    let scope =
        { Scope.nothingAnalyzed with
            LiveDatabaseInspected = true
            SchemaCompleteness = Completeness.ofList [ "tables", Complete; "views", Complete ]
            Corpus = { CorpusScope.empty with IndexedSources = [ "sql/" ] } }

    Assert.True(Scope.supportsAbsenceClaim scope)

[<Fact>]
let ``an empty corpus blocks an absence claim even with complete schema`` () =
    // RK-005: hidden external consumers. A complete catalog says nothing about
    // who reads an object.
    let scope =
        { Scope.nothingAnalyzed with
            LiveDatabaseInspected = true
            SchemaCompleteness = Completeness.ofList [ "tables", Complete ] }

    Assert.False(Scope.supportsAbsenceClaim scope)

[<Fact>]
let ``not-requested is distinct from inaccessible`` () =
    // ER-008: these must not collapse. NotRequested does not block a claim;
    // Inaccessible does.
    let notRequested = Completeness.ofList [ "roles", NotRequested ]
    let inaccessible = Completeness.ofList [ "roles", Inaccessible "permission denied" ]

    Assert.True(Completeness.isFullyComplete notRequested)
    Assert.False(Completeness.isFullyComplete inaccessible)
    Assert.Empty(Completeness.inaccessibleCategories notRequested)

[<Fact>]
let ``an unmentioned category reads as NotRequested rather than Complete`` () =
    let completeness = Completeness.ofList [ "tables", Complete ]

    Assert.Equal(NotRequested, Completeness.stateOf "triggers" completeness)
