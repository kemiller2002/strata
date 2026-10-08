/// The Echelon repository lifecycle contract's decisions (Lifecycle.fs). These
/// are pure: the identity and the observed pin in, a decision out. The process
/// wiring - exit codes, one JSON document on stdout, idempotency on disk - is
/// covered in CliTests.
module Strata.Tests.LifecycleTests

open Xunit
open Strata.Cli.Lifecycle

let private commitA = String.replicate 40 "a"
let private commitB = String.replicate 40 "b"

let private release version commit =
    { ReleaseVersion = version; SourceCommit = commit }

let private pinned version commit =
    Pinned { InstalledVersion = version; SourceCommit = commit }

[<Fact>]
let ``releaseOf splits the informational version into version and commit`` () =
    Assert.Equal(release "0.1.0" (Some commitA), releaseOf ("0.1.0+" + commitA))

[<Fact>]
let ``releaseOf does not mistake a short or absent suffix for a commit`` () =
    Assert.Equal(release "0.1.0" None, releaseOf "0.1.0")
    Assert.Equal(release "0.1.0" None, releaseOf "0.1.0+abc123")

[<Fact>]
let ``only the contract's own invocations are lifecycle invocations`` () =
    Assert.Equal(Some(Ok Version), tryParse [ "version" ])
    Assert.Equal(Some(Ok(RepositoryOperation(Verify, "/r"))), tryParse [ "verify"; "--root"; "/r" ])
    Assert.Equal(Some(Ok(RepositoryOperation(Init, "/r"))), tryParse [ "init"; "--root"; "/r" ])
    // `init` without --root is the project command and is left alone.
    Assert.Equal(None, tryParse [ "init"; "--project"; "db" ])
    Assert.Equal(None, tryParse [ "plan"; "--project"; "db" ])

[<Fact>]
let ``a lifecycle operation without --root, or with anything else, is an invalid invocation`` () =
    for args in [ [ "verify" ]; [ "status"; "--root" ]; [ "doctor"; "--root"; "/r"; "--json" ]; [ "version"; "--json" ] ] do
        match tryParse args with
        | Some(Error(InvocationError _)) -> ()
        | other -> failwithf "%A was accepted as %A" args other

[<Fact>]
let ``init pins an absent repository and leaves an equal pin alone`` () =
    let identity = release "0.1.0" (Some commitA)
    Assert.Equal(Write({ InstalledVersion = "0.1.0"; SourceCommit = Some commitA }, Created), decideInit identity Absent)
    Assert.Equal(Keep, decideInit identity (pinned "0.1.0" (Some commitA)))

[<Fact>]
let ``init never moves another release's pin`` () =
    match decideInit (release "0.2.0" (Some commitB)) (pinned "0.1.0" (Some commitA)) with
    | Refuse reason -> Assert.Contains("upgrade", reason)
    | other -> failwithf "init moved a pin: %A" other

[<Fact>]
let ``nothing overwrites a file Strata cannot establish it owns`` () =
    let identity = release "0.1.0" (Some commitA)

    for observed in [ Foreign "dokimos"; Unreadable "not valid JSON" ] do
        match decideInit identity observed, decideUpgrade identity observed with
        | Refuse _, Refuse _ -> ()
        | other -> failwithf "%A was not refused: %A" observed other

[<Fact>]
let ``upgrade moves an older pin forward, refuses a missing installation and a downgrade`` () =
    let identity = release "0.2.0" (Some commitB)
    Assert.Equal(Write({ InstalledVersion = "0.2.0"; SourceCommit = Some commitB }, Updated), decideUpgrade identity (pinned "0.1.0" (Some commitA)))
    Assert.Equal(Keep, decideUpgrade identity (pinned "0.2.0" (Some commitB)))

    match decideUpgrade identity Absent, decideUpgrade identity (pinned "0.3.0" (Some commitA)) with
    | Refuse _, Refuse downgrade -> Assert.Contains("downgrade", downgrade)
    | other -> failwithf "expected two refusals, got %A" other

[<Fact>]
let ``verify is healthy only for exactly this release`` () =
    let identity = release "0.1.0" (Some commitA)
    Assert.Empty(problems identity (pinned "0.1.0" (Some commitA)))
    Assert.NotEmpty(problems identity Absent)
    Assert.NotEmpty(problems identity (pinned "0.0.9" (Some commitA)))
    // Same version, different build: not the release the repository pinned.
    Assert.NotEmpty(problems identity (pinned "0.1.0" (Some commitB)))

[<Fact>]
let ``a development build is an advisory, not a problem`` () =
    Assert.NotEmpty(advisories (release "0.1.0" None))
    Assert.Empty(advisories (release "0.1.0" (Some commitA)))

[<Fact>]
let ``the written pin reads back as the same pin`` () =
    let pin = { InstalledVersion = "0.1.0"; SourceCommit = Some commitA }
    Assert.Equal(Pinned pin, parse (render pin))
    Assert.Equal(render pin, render pin)

[<Fact>]
let ``another tool's manifest and a damaged file are recognised as such`` () =
    Assert.Equal(Foreign "tutela", parse """{"tool":"tutela","installedVersion":"0.1.0"}""")

    match parse "{ not json", parse """{"installedVersion":"0.1.0"}""" with
    | Unreadable _, Unreadable _ -> ()
    | other -> failwithf "expected two unreadable files, got %A" other

[<Fact>]
let ``versions compare by precedence, with a release above its prereleases`` () =
    Assert.Equal(Some -1, compareVersions "0.1.0" "0.2.0")
    Assert.Equal(Some 1, compareVersions "1.0.0" "0.9.9")
    Assert.Equal(Some 1, compareVersions "1.0.0" "1.0.0-rc.1")
    Assert.Equal(Some 0, compareVersions "1.0.0" "1.0.0")
    Assert.Equal(None, compareVersions "1.0" "1.0.0")
