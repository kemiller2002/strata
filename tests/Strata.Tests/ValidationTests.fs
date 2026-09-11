module Strata.Tests.ValidationTests

open Xunit
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Application.Validation
open Strata.Host.PgParser

/// Tests for PR-018 — validating candidate SQL against a schema.
///
/// The tests that matter here are the ones separating "does not exist" from
/// "cannot tell". A validator that confuses them makes an author change working
/// SQL, which is worse than having no validator at all.

let private id' = Identifier.unquoted
let private qn schema name = QualifiedName.qualified (id' schema) (id' name)

let private parser =
    PgParserAdapter.PostgresParser() :> Strata.Analysis.DialectPort.IDialectParser

let private column position name =
    { Name = id' name
      Type = { TypeName = QualifiedName.unqualified (id' "text"); IsNullable = true }
      Position = position
      HasDefault = false
      IsGenerated = false
      IsIdentity = false }

let private table schema name columns =
    TableObject
        { Name = qn schema name
          Columns = columns |> List.mapi (fun i c -> column (i + 1) c)
          PrimaryKey = None
          UniqueConstraints = []
          CheckConstraints = []
          ForeignKeys = []
          Indexes = []
          Scope = Managed }

/// A snapshot that CAN support an absence claim.
///
/// The category names here are the ones `CatalogIntrospection` really emits.
/// An earlier fixture used invented names that matched an equally invented
/// lookup in the module under test, so both agreed and both were wrong.
let private completeSnapshot =
    { Objects =
        [ table "sales" "orders" [ "id"; "customer_id"; "total" ]
          table "sales" "customers" [ "id"; "name" ] ]
      ServerVersion = None
      Completeness =
        Completeness.ofList [ "relations", Complete; "columns", Complete ] }

/// The same objects, but the snapshot admits it did not read everything.
let private partialSnapshot =
    { completeSnapshot with
        Completeness =
            Completeness.ofList
                [ "relations", Partial "permission denied on one schema"
                  "columns", Complete ] }

let private searchPath = [ id' "sales" ]

let private validate snapshot sql = Strata.Application.Validation.validate parser snapshot searchPath sql

// ---- the capability -------------------------------------------------------

[<Fact>]
let ``a statement whose references all exist is valid`` () =
    let report = validate completeSnapshot "SELECT id, total FROM sales.orders"

    Assert.Empty report.Findings
    Assert.Equal(0, Report.exitCode report)

[<Fact>]
let ``a misspelled table is reported as definitively invalid`` () =
    let report = validate completeSnapshot "SELECT id FROM sales.ordrs"

    let invalid = Report.invalidFindings report
    Assert.Single invalid |> ignore
    Assert.Contains("does not exist", (List.head invalid).Message)
    Assert.Equal(1, Report.exitCode report)

[<Fact>]
let ``a misspelled column is reported as definitively invalid`` () =
    // The motivating case: the author read the schema and still wrote the
    // wrong identifier. Reasoning does not catch this; a symbol table does.
    let report = validate completeSnapshot "SELECT custmer_id FROM sales.orders"

    Assert.NotEmpty(Report.invalidFindings report)
    Assert.Equal(1, Report.exitCode report)

[<Fact>]
let ``SQL that does not parse is invalid without consulting the catalog`` () =
    let report = validate completeSnapshot "SELECT FROM WHERE"

    Assert.NotEmpty(Report.invalidFindings report)

// ---- the distinction ER-008 exists to protect -----------------------------

[<Fact>]
let ``an incomplete snapshot cannot call a missing relation absent`` () =
    // THE test. Same SQL as the misspelled-table case, same missing relation —
    // but the snapshot admits it could not read every view. Strata must not
    // claim absence it cannot support, so this is unverifiable, NOT invalid.
    let report = validate partialSnapshot "SELECT id FROM sales.ordrs"

    Assert.Empty(Report.invalidFindings report)
    Assert.NotEmpty(Report.unverifiableFindings report)
    Assert.Equal(2, Report.exitCode report)

[<Fact>]
let ``a column is not called absent while a relation in scope is unresolved`` () =
    // `total` exists in sales.orders, `mystery` does not resolve at all. The
    // column resolver searches only relations it could resolve, so a column
    // living in the unresolved relation looks identical to one that does not
    // exist. Calling that invalid would be a fabricated error.
    let report = validate completeSnapshot "SELECT whatever FROM sales.orders JOIN sales.mystery ON true"

    let invalid = Report.invalidFindings report

    // The missing RELATION is provably absent and is still reported.
    Assert.Contains(invalid, fun f -> f.Message.Contains "sales.mystery")
    // But no column is condemned on the strength of an incomplete scope.
    Assert.DoesNotContain(invalid, fun f -> f.Message.Contains "column")

[<Fact>]
let ``dynamic SQL is unverifiable rather than silently passing`` () =
    let report = validate completeSnapshot "EXECUTE some_plan"

    Assert.Empty(Report.invalidFindings report)
    Assert.Contains(Report.unverifiableFindings report, fun f -> f.Message.Contains "dynamic")
    Assert.Equal(2, Report.exitCode report)

// ---- controls -------------------------------------------------------------

[<Fact>]
let ``a CTE name is not reported as a missing relation`` () =
    // RK-001. The CTE is bound by the statement, not the catalog, so a
    // validator that consults the catalog for it invents an error.
    let report =
        validate completeSnapshot "WITH recent AS (SELECT id FROM sales.orders) SELECT id FROM recent"

    Assert.Empty(Report.invalidFindings report)

[<Fact>]
let ``an alias is not reported as a missing relation`` () =
    let report = validate completeSnapshot "SELECT o.id FROM sales.orders o WHERE o.total > 0"

    Assert.Empty(Report.invalidFindings report)

[<Fact>]
let ``findings carry the statement index and offset`` () =
    // Position is what makes a finding actionable rather than a complaint.
    let report =
        validate completeSnapshot "SELECT id FROM sales.orders; SELECT id FROM sales.nope"

    let invalid = Report.invalidFindings report |> List.head
    Assert.Equal(1, invalid.StatementIndex)
    Assert.True(invalid.Offset > 0, "the second statement should not start at offset 0")

[<Fact>]
let ``an invalid column finding names the column`` () =
    // "a referenced column does not exist" is a complaint; naming it is a
    // finding someone can act on. This is why validation walks the column
    // mentions itself rather than reading columnDependencies' flat gap list.
    let report = validate completeSnapshot "SELECT custmer_id FROM sales.orders"

    let invalid = Report.invalidFindings report |> List.head
    Assert.Contains("custmer_id", invalid.Message)
    Assert.Contains("custmer_id", invalid.Subject)

[<Fact>]
let ``an alias-qualified column resolves against its own relation, not everything in scope`` () =
    // Both relations declare `id`. Resolving `o.id` against everything in scope
    // makes it Ambiguous, which it is not. This produced a spurious gap on
    // every join over a shared column name until the qualifier was honoured.
    let report =
        validate completeSnapshot
            "SELECT o.id, c.name FROM sales.orders o JOIN sales.customers c ON o.customer_id = c.id"

    Assert.Empty report.Findings
    Assert.Equal(0, Report.exitCode report)

[<Fact>]
let ``a column that does not exist on the relation its alias names is invalid`` () =
    // The control for the case above: honouring the qualifier must not turn
    // into ignoring the answer.
    let report = validate completeSnapshot "SELECT o.name FROM sales.orders o"

    Assert.NotEmpty(Report.invalidFindings report)

[<Fact>]
let ``a routine body is never silently passed`` () =
    // A CREATE FUNCTION body is a string literal to the parser, so a typo
    // inside it is invisible. Reporting VALID here would be a FALSE PASS —
    // the one outcome a validator must never produce, and worse than no
    // validator, because it is trusted.
    let report =
        validate completeSnapshot
            "CREATE FUNCTION sales.f() RETURNS int AS $$ SELECT custmer_id FROM sales.orders $$ LANGUAGE sql"

    Assert.NotEqual(0, Report.exitCode report)
    Assert.Contains(Report.unverifiableFindings report, fun f -> f.Message.Contains "body")
