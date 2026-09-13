module Strata.Tests.DeploymentGateTests

open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.Graph
open Strata.Analysis.ProposedChange
open Strata.Application.DeploymentGate

/// Tests for the deployment gate — the capability HY-STRATA-2026-6F14 claims
/// stands independently of agent leverage.

let private id' = Identifier.unquoted
let private qn schema name = QualifiedName.qualified (id' schema) (id' name)
let private orders = qn "sales" "orders"

let private graphWithReader column viaWildcard =
    { SemanticGraph.empty with
        ColumnDependencies =
            [ { SourceId = "file:r.sql"
                Table = orders
                Column = id' column
                Kind = Reads
                ViaWildcard = viaWildcard } ] }

/// A scope that DOES support absence claims: live DB inspected, schema
/// complete, corpus non-empty.
let private completeScope =
    { Scope.nothingAnalyzed with
        LiveDatabaseInspected = true
        SchemaCompleteness = Completeness.ofList [ "relations", Complete ]
        Corpus = { CorpusScope.empty with IndexedSources = [ "file:r.sql" ] } }

let private emptyScope = Scope.nothingAnalyzed

[<Fact>]
let ``dropping a column with a named reader is blocked`` () =
    let result = run (graphWithReader "status" false) completeScope [ DropColumn(orders, id' "status") ]

    Assert.Equal(Block, result.Verdict)
    Assert.Equal(1, Verdict.exitCode result.Verdict)
    Assert.Contains("file:r.sql", (List.head result.Findings).AffectedSources)

[<Fact>]
let ``dropping a column read ONLY via SELECT star is still blocked`` () =
    // RK-002 at the gate. A wildcard reader genuinely breaks, and is exactly
    // what a text search for the column name would miss.
    let result = run (graphWithReader "total" true) completeScope [ DropColumn(orders, id' "total") ]

    Assert.Equal(Block, result.Verdict)
    Assert.Contains("SELECT *", (List.head result.Findings).Rationale)

[<Fact>]
let ``a clean result under an INCOMPLETE scope is not clearance`` () =
    // §144.11 / P-009. THE load-bearing rule: absence of evidence inside a
    // bounded scope is not evidence of absence, so it cannot return Allow.
    let result = run SemanticGraph.empty emptyScope [ DropColumn(orders, id' "note") ]

    Assert.Equal(RequiresApproval, result.Verdict)
    Assert.Contains("not clearance", (List.head result.Findings).Rationale)

[<Fact>]
let ``a clean result under a COMPLETE scope still requires approval, and says why`` () =
    // This asserted `Allow` until `DF-STRATA-2026-5E9F`. A clean scan under a
    // supporting scope no longer clears a DESTRUCTIVE change — the scope proves
    // the search was meaningful, not that it was complete.
    //
    // What the scope still decides is what the finding SAYS, and that
    // distinction is worth keeping: this rationale and the "not clearance" one
    // above are different claims about the same verdict, and a reader deciding
    // whether to approve needs to know which they are looking at.
    let result = run SemanticGraph.empty completeScope [ DropColumn(orders, id' "note") ]

    Assert.Equal(RequiresApproval, result.Verdict)
    Assert.Contains("supports that conclusion", (List.head result.Findings).Rationale)
    Assert.DoesNotContain("not clearance", (List.head result.Findings).Rationale)

    // The gate must still be capable of saying yes, or it is useless. It says it
    // for additive changes — see the two tests below.
    Assert.Equal(Allow, (run SemanticGraph.empty completeScope [ AddColumn(orders, id' "note") ]).Verdict)

[<Fact>]
let ``additive changes are allowed even under an incomplete scope`` () =
    // RK-016 / §136: a gate that blocks safe changes gets disabled. No existing
    // reader can depend on a column that does not yet exist, so scope is
    // irrelevant here.
    let added = run SemanticGraph.empty emptyScope [ AddColumn(orders, id' "priority") ]
    let created = run SemanticGraph.empty emptyScope [ CreateTable(qn "sales" "shipment") ]

    Assert.Equal(Allow, added.Verdict)
    Assert.Equal(Allow, created.Verdict)

[<Fact>]
let ``truncate is blocked unconditionally`` () =
    // No predicate can narrow it; §11.3 lists it separately from DELETE.
    let result = run SemanticGraph.empty completeScope [ TruncateTable orders ]

    Assert.Equal(Block, result.Verdict)

[<Fact>]
let ``a type change with affected sources requires approval rather than blocking`` () =
    // A widening type change is safe and a narrowing one is not. Strata knows
    // WHO is affected, not whether the new type is compatible — claiming
    // otherwise would be the false-confidence failure §144.2 warns about.
    let result =
        run (graphWithReader "status" false) completeScope [ AlterColumnType(orders, id' "status", "varchar(10)") ]

    Assert.Equal(RequiresApproval, result.Verdict)

[<Fact>]
let ``a statement Strata cannot model requires approval, never allow`` () =
    // ER-008: not knowing what a statement does is not the same as it being safe.
    let result = run SemanticGraph.empty completeScope [ UnclassifiedChange "CREATE TRIGGER" ]

    Assert.Equal(RequiresApproval, result.Verdict)

[<Fact>]
let ``an empty migration is not approved by default`` () =
    let result = run SemanticGraph.empty completeScope []

    Assert.Equal(RequiresApproval, result.Verdict)

[<Fact>]
let ``a migration is as safe as its least safe statement`` () =
    let result =
        run
            (graphWithReader "status" false)
            completeScope
            [ AddColumn(orders, id' "priority"); DropColumn(orders, id' "status") ]

    Assert.Equal(Block, result.Verdict)

[<Fact>]
let ``every verdict carries a next safe move`` () =
    // §130: a gate that only refuses gets routed around.
    let changes =
        [ DropColumn(orders, id' "status")
          AddColumn(orders, id' "x")
          TruncateTable orders
          AlterColumnType(orders, id' "status", "text")
          UnclassifiedChange "something" ]

    let result = run (graphWithReader "status" false) completeScope changes

    Assert.All(result.Findings, fun f -> Assert.False(System.String.IsNullOrWhiteSpace f.NextSafeMove))

[<Fact>]
let ``gate output is byte-identical across runs`` () =
    // The property that distinguishes a gate from an agent: same input, same
    // verdict, every time. A CI gate cannot be built on a probabilistic answer.
    let changes = [ DropColumn(orders, id' "status"); AddColumn(orders, id' "x") ]
    let graph = graphWithReader "status" false

    let a = toJson (run graph completeScope changes)
    let b = toJson (run graph completeScope changes)

    Assert.Equal<string>(a, b)

[<Fact>]
let ``exit codes distinguish all three verdicts`` () =
    Assert.Equal(0, Verdict.exitCode Allow)
    Assert.Equal(1, Verdict.exitCode Block)
    Assert.Equal(2, Verdict.exitCode RequiresApproval)


// ---- the destructive-verdict property ---------------------------------------
//
// `DF-STRATA-2026-5E9F`. Before this, a clean corpus scan under a scope that
// supported absence claims returned `Allow` for `DropColumn`, `DropTable`,
// `AlterColumnType`, both renames, `ReplaceView`, `ReplaceRoutine`, the three
// trigger cases and `UpdateRow` — every one of them destructive.
//
// `scopeSupportsAbsence` proves the search was MEANINGFUL. It does not prove it
// was COMPLETE: dynamic SQL, an ORM, a BI tool, another service and a cron job
// are all outside anything Strata indexes. That was the one place in the design
// where a bounded result became an unbounded conclusion, and it sat at the
// highest-stakes change in the vocabulary.
//
// A single example test would pin one case. This pins the whole vocabulary,
// which is the point: six cases were added to `Change` in one session, and
// nothing would have caught one of them being misclassified.

/// One representative value per case in the `Change` vocabulary.
///
/// Hand-written rather than reflected, because every case needs a payload. The
/// coverage test below is what keeps this honest: a new case fails there until
/// someone adds it here, and then the property runs over it automatically.
let private everyChange : Change list =
    [ CreateEnumType(qn "sales" "status")
      AddEnumValue(qn "sales" "status", "shipped", Some "pending")
      DropEnumType(qn "sales" "status")
      DropColumn(orders, id' "c")
      DropTable orders
      AlterColumnType(orders, id' "c", "bigint")
      AddColumn(orders, id' "c")
      RenameTable(orders, qn "sales" "orders_v2")
      RenameColumn(orders, id' "a", id' "b")
      CreateSchema(id' "sales")
      GrantPrivileges(GrantTarget.Relation orders, "app_user", [ "SELECT" ])
      RevokePrivileges(GrantTarget.Relation orders, "app_user", [ "SELECT" ])
      CreateExtension(id' "citext")
      UpdateExtension(id' "citext", "1.7")
      SetExtensionSchema(id' "citext", id' "ext")
      CreatePolicy(orders, id' "p")
      ReplacePolicy(orders, id' "p")
      EnableRowLevelSecurity orders
      DisableRowLevelSecurity orders
      ForceRowLevelSecurity orders
      NoForceRowLevelSecurity orders
      CreateSequence(qn "sales" "s")
      DropSequence(qn "sales" "s")
      AlterSequence(qn "sales" "s")
      CreateTable orders
      CreateIndex(orders, id' "ix")
      DropIndex(orders, id' "ix")
      CreateView(qn "sales" "v")
      ReplaceView(qn "sales" "v")
      ReplaceRoutine(qn "sales" "f")
      CreateRoutine(qn "sales" "f")
      CreateTrigger(orders, id' "t")
      DropTrigger(orders, id' "t")
      ReplaceTrigger(orders, id' "t")
      InsertRow(orders, "1")
      UpdateRow(orders, "1")
      AddConstraint(orders, Some(id' "ck"), ConstraintKind.Check, [ id' "c" ])
      DropConstraint(orders, id' "ck", ConstraintKind.Check)
      TruncateTable orders
      UnclassifiedChange "something Strata does not model" ]

[<Fact>]
let ``every change case has a representative in the property corpus`` () =
    // Without this the property below silently stops covering new cases, which
    // is exactly how the gap it pins was introduced.
    let nameOf (change: Change) =
        fst (Microsoft.FSharp.Reflection.FSharpValue.GetUnionFields(change, typeof<Change>))
        |> fun case -> case.Name

    let declared =
        Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<Change>
        |> Array.map (fun c -> c.Name)
        |> Set.ofArray

    let covered = everyChange |> List.map nameOf |> Set.ofList

    Assert.Equal<Set<string>>(Set.empty, Set.difference declared covered)

[<Fact>]
let ``a destructive change never receives an Allow verdict`` () =
    // Deliberately the scope that DOES support absence claims, against an EMPTY
    // graph — the most permissive combination there is, and the one that used to
    // return Allow. Under an incomplete scope this would pass vacuously.
    let offenders =
        everyChange
        |> List.filter Change.isPotentiallyDestructive
        |> List.filter (fun change ->
            let result = run SemanticGraph.empty completeScope [ change ]
            (List.head result.Findings).Verdict = Allow)
        |> List.map Change.tag

    Assert.Equal<string list>([], offenders)

[<Fact>]
let ``an additive change is still allowed on a clean scan`` () =
    // The other half. Tightening destructive changes must not drag the additive
    // ones with it, or every plan becomes requires-approval and the verdict
    // stops carrying information.
    let result = run SemanticGraph.empty completeScope [ AddColumn(orders, id' "note") ]

    Assert.Equal(Allow, result.Verdict)

// ---- enumerated types -------------------------------------------------------

[<Fact>]
let ``creating an enum type is allowed`` () =
    Assert.Equal(
        Allow,
        (run SemanticGraph.empty completeScope [ CreateEnumType(qn "sales" "status") ]).Verdict)

[<Fact>]
let ``adding an enum value requires approval`` () =
    // Additive to the type, and two things follow that no schema check can see.
    // A value added inside a transaction cannot be USED in that transaction, so
    // a plan that also writes a row with it fails as a unit. And every
    // exhaustive CASE over the type in application code is now missing an arm.
    let result = run SemanticGraph.empty completeScope [ AddEnumValue(qn "sales" "status", "shipped", Some "pending") ]

    Assert.Equal(RequiresApproval, result.Verdict)
    Assert.Contains("shipped", (List.head result.Findings).Detected)

[<Fact>]
let ``dropping an enum type is blocked`` () =
    // Not a judgement call about blast radius: DROP TYPE takes every column
    // declared with it, and the data in those columns goes too. PostgreSQL will
    // refuse while a dependency exists, so this either fails or destroys
    // something — which is what Block is for.
    let result = run SemanticGraph.empty completeScope [ DropEnumType(qn "sales" "status") ]

    Assert.Equal(Block, result.Verdict)
    Assert.Contains("every column declared with it", (List.head result.Findings).Rationale)
