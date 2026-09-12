module Strata.Tests.InvariantTests

open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Application

/// Rules a project declares about itself.
///
/// These are checks of the same KIND as the type check — decidable from the
/// project alone, answerable before anything is deployed, worth stopping a build
/// over — and the difference is only whose rule it is
/// (`DF-STRATA-2026-8B60`).

let private id' = Identifier.unquoted
let private qn schema name = QualifiedName.qualified (id' schema) (id' name)

let private column name : Column =
    { Name = id' name
      Type = { TypeName = QualifiedName.unqualified (id' "bigint"); IsNullable = false }
      Position = 1
      HasDefault = false
      DefaultExpression = None
      IsGenerated = false
      IsIdentity = false }

let private table name : Table =
    { Name = qn "shop" name
      Columns = [ column "id" ]
      PrimaryKey = Some { ConstraintName = Some(id' (name + "_pkey")); Columns = [ id' "id" ] }
      UniqueConstraints = []
      CheckConstraints = []
      ForeignKeys = []
      Indexes = []
      Triggers = []
      Scope = Managed }

let private snapshotOf (tables: Table list) : SchemaSnapshot =
    { Objects = tables |> List.map TableObject
      ServerVersion = None
      Completeness = Strata.Semantic.AnalysisScope.Completeness.empty }

let private check requested tables grants =
    Invariants.check requested (snapshotOf tables) grants

// ---- opting in --------------------------------------------------------------

[<Fact>]
let ``a rule nobody asked for is not enforced`` () =
    // Opt-in by name. Turning them all on by default would fail every existing
    // project on upgrade, and a check everyone disables protects nobody.
    let keyless = { table "keyless" with PrimaryKey = None }
    Assert.Empty(check [] [ keyless ] [])

[<Fact>]
let ``a misspelled rule name is reported rather than ignored`` () =
    // The important one. A project that asks for `everyTableHasAPrimarykey` and
    // gets silence has a control it believes is on and is not, which is strictly
    // worse than never having asked.
    Assert.Equal<string list>(
        [ "everyTableHasAPrimarykey" ],
        Invariants.unknown [ "everyTableHasAPrimaryKey"; "everyTableHasAPrimarykey" ])

[<Fact>]
let ``every known rule is recognised by its own name`` () =
    Assert.Empty(Invariants.unknown (Invariants.all |> List.map (fun r -> r.Name)))

[<Fact>]
let ``every rule states what it rules out`` () =
    // The summary is what the author reads when the build stops. A rule that
    // fails a build without saying why is a rule that gets deleted.
    for rule in Invariants.all do
        Assert.False(System.String.IsNullOrWhiteSpace rule.Summary, rule.Name)
        Assert.True(rule.Summary.Length > 30, rule.Name)

// ---- everyTableHasAPrimaryKey -----------------------------------------------

[<Fact>]
let ``a table with no primary key is a violation`` () =
    let keyless = { table "keyless" with PrimaryKey = None }
    let violations = check [ Invariants.everyTableHasAPrimaryKey.Name ] [ table "ok"; keyless ] []

    Assert.Single violations |> ignore
    Assert.Equal("shop.keyless", QualifiedName.display (List.head violations).Object)

// ---- noGrantsToPublic -------------------------------------------------------

let private grant grantee : Grant =
    { Target = GrantTarget.Relation(qn "shop" "parent")
      Grantee = grantee
      Privileges = [ "SELECT" ]
      Grantable = [] }

[<Fact>]
let ``a grant to PUBLIC is a violation`` () =
    let violations = check [ Invariants.noGrantsToPublic.Name ] [] [ grant "PUBLIC" ]
    Assert.Single violations |> ignore

[<Fact>]
let ``PUBLIC is recognised however it is spelled`` () =
    // It is a keyword, not a role name, and PostgreSQL does not care about case.
    // Missing a lower-case spelling would let the rule pass a project it should
    // have stopped — the quiet direction of wrong.
    Assert.Single(check [ Invariants.noGrantsToPublic.Name ] [] [ grant "public" ]) |> ignore
    Assert.Single(check [ Invariants.noGrantsToPublic.Name ] [] [ grant "Public" ]) |> ignore

[<Fact>]
let ``a grant to a named role is not a violation`` () =
    Assert.Empty(check [ Invariants.noGrantsToPublic.Name ] [] [ grant "app_user" ])

// ---- everyForeignKeyIsIndexed -----------------------------------------------

let private withForeignKey columns indexes =
    { table "child" with
        ForeignKeys =
          [ { ConstraintName = Some(id' "child_fkey")
              Columns = columns |> List.map id'
              ReferencedTable = qn "shop" "parent"
              ReferencedColumns = [ id' "id" ] } ]
        Indexes =
          indexes
          |> List.map (fun (name, cols) ->
              { Name = id' name; Columns = cols |> List.map id'; IsUnique = false; Predicate = None }) }

[<Fact>]
let ``an unindexed foreign key is a violation`` () =
    let child = withForeignKey [ "parent_id" ] []
    Assert.Single(check [ Invariants.everyForeignKeyIsIndexed.Name ] [ child ] []) |> ignore

[<Fact>]
let ``an exactly matching index satisfies the rule`` () =
    let child = withForeignKey [ "parent_id" ] [ "idx", [ "parent_id" ] ]
    Assert.Empty(check [ Invariants.everyForeignKeyIsIndexed.Name ] [ child ] [])

[<Fact>]
let ``an index that BEGINS with the foreign key satisfies the rule`` () =
    // A prefix, not an exact match: an index on (a, b) serves a foreign key on
    // (a) and PostgreSQL will use it. Requiring exactness would report a project
    // that is already correct, and a rule that cries wolf gets switched off.
    let child = withForeignKey [ "parent_id" ] [ "idx", [ "parent_id"; "created_at" ] ]
    Assert.Empty(check [ Invariants.everyForeignKeyIsIndexed.Name ] [ child ] [])

[<Fact>]
let ``an index that merely CONTAINS the foreign key does not satisfy the rule`` () =
    // (created_at, parent_id) cannot serve a lookup on parent_id alone, so
    // accepting it would be the rule lying about what it checked.
    let child = withForeignKey [ "parent_id" ] [ "idx", [ "created_at"; "parent_id" ] ]
    Assert.Single(check [ Invariants.everyForeignKeyIsIndexed.Name ] [ child ] []) |> ignore

[<Fact>]
let ``a primary key counts as an index`` () =
    // It is backed by one. So is a unique constraint.
    let child =
        { withForeignKey [ "id" ] [] with
            PrimaryKey = Some { ConstraintName = Some(id' "child_pkey"); Columns = [ id' "id" ] } }

    Assert.Empty(check [ Invariants.everyForeignKeyIsIndexed.Name ] [ child ] [])

[<Fact>]
let ``a unique constraint counts as an index`` () =
    let child =
        { withForeignKey [ "parent_id" ] [] with
            UniqueConstraints = [ { ConstraintName = Some(id' "u"); Columns = [ id' "parent_id" ] } ] }

    Assert.Empty(check [ Invariants.everyForeignKeyIsIndexed.Name ] [ child ] [])

[<Fact>]
let ``a composite foreign key needs an index in the same order`` () =
    let wrongOrder = withForeignKey [ "a"; "b" ] [ "idx", [ "b"; "a" ] ]
    Assert.Single(check [ Invariants.everyForeignKeyIsIndexed.Name ] [ wrongOrder ] []) |> ignore

    let rightOrder = withForeignKey [ "a"; "b" ] [ "idx", [ "a"; "b"; "c" ] ]
    Assert.Empty(check [ Invariants.everyForeignKeyIsIndexed.Name ] [ rightOrder ] [])

// ---- reporting --------------------------------------------------------------

[<Fact>]
let ``every violation is reported, not just the first`` () =
    // A build that reports one problem per run makes the author run it once per
    // problem.
    let a = { table "a" with PrimaryKey = None }
    let b = { table "b" with PrimaryKey = None }
    Assert.Equal(2, List.length (check [ Invariants.everyTableHasAPrimaryKey.Name ] [ a; b ] []))

[<Fact>]
let ``violations come back in a stable order`` () =
    // NFR-001. Two runs over the same project must produce the same output, or
    // a diff of two build logs is noise.
    let a = { table "a" with PrimaryKey = None }
    let b = { table "b" with PrimaryKey = None }
    let requested = [ Invariants.everyTableHasAPrimaryKey.Name ]

    Assert.Equal<string list>(
        check requested [ a; b ] [] |> List.map (fun v -> QualifiedName.display v.Object),
        check requested [ b; a ] [] |> List.map (fun v -> QualifiedName.display v.Object))
