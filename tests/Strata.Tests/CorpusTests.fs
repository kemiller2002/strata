module Strata.Tests.CorpusTests

open Xunit
open Strata.Analysis.Corpus

/// Tests for PR-008 / §8: corpus units carry attributable provenance.

let private unit' sourceId status fingerprint =
    { SourceId = sourceId
      Origin = SqlFile sourceId
      Offset = 0
      Length = 10
      ContentHash = "hash-" + sourceId
      Fingerprint = fingerprint
      Dialect = "postgresql"
      ParserVersion = "17.5"
      Status = status }

[<Fact>]
let ``source identifiers are stable and prefixed by origin kind`` () =
    Assert.Equal("file:a.sql", SqlOrigin.sourceId (SqlFile "a.sql"))
    Assert.Equal("migration:001.sql", SqlOrigin.sourceId (MigrationScript "001.sql"))
    Assert.Equal("view:sales.v", SqlOrigin.sourceId (ViewDefinitionSource "sales.v"))
    Assert.Equal("routine:sales.f", SqlOrigin.sourceId (RoutineBodySource "sales.f"))
    Assert.Equal("input:candidate", SqlOrigin.sourceId (DirectInput "candidate"))

[<Fact>]
let ``parse failures and extraction gaps are counted separately`` () =
    // ER-008 at corpus level: a statement that did not parse is not a statement
    // that parsed with gaps.
    let index =
        CorpusIndex.ofStatements
            [ unit' "a.sql" Extracted None
              unit' "b.sql" (ParseFailed "syntax error") None
              unit' "c.sql" (ExtractedWithGaps 2) None ]

    Assert.Single(CorpusIndex.parseFailures index) |> ignore
    Assert.Single(CorpusIndex.extractionGaps index) |> ignore

[<Fact>]
let ``the corpus scope carries sources and gap counts`` () =
    // PR-021: a count derived from this index cannot be presented as exhaustive
    // without its gaps travelling alongside.
    let index =
        CorpusIndex.ofStatements
            [ unit' "a.sql" Extracted None
              unit' "b.sql" (ParseFailed "syntax error") None ]

    let scope = CorpusIndex.toScope index

    Assert.Equal<string list>([ "a.sql"; "b.sql" ], scope.IndexedSources)
    Assert.Equal(1, scope.ParseFailures)

[<Fact>]
let ``an empty corpus reports no sources, which blocks absence claims`` () =
    let scope = CorpusIndex.toScope CorpusIndex.empty

    Assert.Empty scope.IndexedSources

[<Fact>]
let ``statements group by fingerprint so query shapes deduplicate`` () =
    let index =
        CorpusIndex.ofStatements
            [ unit' "a.sql" Extracted (Some "fp1")
              unit' "b.sql" Extracted (Some "fp1")
              unit' "c.sql" Extracted (Some "fp2") ]

    let groups = CorpusIndex.byFingerprint index

    Assert.Equal(2, List.length groups)
    Assert.Equal(2, groups |> List.find (fun (f, _) -> f = "fp1") |> snd |> List.length)

[<Fact>]
let ``a missing fingerprint is not treated as a fingerprint`` () =
    // None must not collide with other unfingerprinted statements.
    let index =
        CorpusIndex.ofStatements [ unit' "a.sql" Extracted None; unit' "b.sql" Extracted None ]

    Assert.Empty(CorpusIndex.byFingerprint index)

[<Fact>]
let ``indexed sources are deduplicated and sorted`` () =
    // NFR-001: indexing order must not leak into output.
    let index =
        CorpusIndex.ofStatements
            [ unit' "z.sql" Extracted None
              unit' "a.sql" Extracted None
              unit' "z.sql" Extracted None ]

    Assert.Equal<string list>([ "a.sql"; "z.sql" ], CorpusIndex.indexedSources index)
