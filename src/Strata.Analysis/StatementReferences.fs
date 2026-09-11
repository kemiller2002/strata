namespace Strata.Analysis

open Strata.Semantic.Identity

/// The dialect-neutral extraction result a parser adapter produces.
///
/// Authority for: the contract between Tier 4 parser adapters and Tier 2
/// analysis.
///
/// This is the boundary that keeps the protobuf AST out of the domain
/// (D-005, ER-014). A PostgreSQL adapter fills this in from libpg_query; a
/// future SQL Server adapter would fill the same shape from ScriptDom. Neither
/// parser's node types appear here.
module StatementReferences =

    /// How a relation name entered the statement.
    ///
    /// The distinction between a CTE definition and a table reference is the
    /// whole point of EV-STRATA-2026-B9C4: in the parse tree both appear as a
    /// relation name, and only the surrounding structure separates them. The
    /// adapter reports structure; Tier 2 decides meaning.
    type RelationRole =
        /// A name appearing in FROM/JOIN/INTO position.
        | RelationReference
        /// A name bound by WITH. Establishes a scope; is not a database object.
        | CommonTableExpressionDefinition
        /// A name bound by CREATE TEMP TABLE. A real object, but not managed.
        | TemporaryRelationDefinition

    /// A relation name as it appeared, with its role and any alias.
    type RelationMention =
        { Name: QualifiedName
          Role: RelationRole
          Alias: Identifier option
          /// Nesting depth of the query level this mention appeared in.
          /// 0 is the outermost level. Used to build the scope chain.
          QueryLevel: int }

    /// A column reference as it appeared.
    type ColumnMention =
        { /// Qualifier written before the column, if any: the `o` in `o.id`.
          /// This may be a table alias, a table name, or a CTE name.
          Qualifier: Identifier option
          /// `None` when the reference was `*`.
          Column: Identifier option
          IsWildcard: bool
          QueryLevel: int }

    /// An equality predicate between two column references.
    ///
    /// This is the raw material for observed relationships (§9). It is recorded
    /// as it was WRITTEN — qualifiers unresolved — because deciding which
    /// relation each side belongs to is scope resolution's job, not the
    /// adapter's. An adapter that guessed here would reintroduce RK-001.
    type JoinPredicate =
        { LeftQualifier: Identifier option
          LeftColumn: Identifier
          RightQualifier: Identifier option
          RightColumn: Identifier
          QueryLevel: int }

    /// One action inside an `ALTER TABLE`.
    ///
    /// A single `ALTER TABLE` can add one column and drop another, and the
    /// column names live in the statement's subcommands rather than in any
    /// `ColumnRef`. Without this the gate cannot tell `ADD COLUMN` from
    /// `DROP COLUMN` at all — and guessing from the statement text is a
    /// substring match waiting to misfire on a column literally named
    /// "drop_column".
    type AlterAction =
        { /// Adapter-reported action kind, e.g. "add-column", "drop-column",
          /// "alter-column-type", "add-constraint".
          Kind: string
          /// The column or constraint the action names, where it names one.
          Name: Identifier option }

    /// What a statement does, structurally.
    ///
    /// Populated by the adapter from the statement node type. Effect
    /// classification by consequence (PR-015, WI-0010) is a separate concern
    /// that consumes this.
    type StatementShape =
        | SelectShape
        | InsertShape
        | UpdateShape
        | DeleteShape
        | DdlShape of operation: string
        | UtilityShape of operation: string
        | UnsupportedShape of detail: string

    /// Everything one statement contributes, before any resolution.
    type StatementExtraction =
        { Shape: StatementShape
          Relations: RelationMention list
          Columns: ColumnMention list
          /// Constructs the adapter recognised but did not model. Surfaced so
          /// they become explicit analysis gaps rather than silence (§8).
          UnmodelledConstructs: string list
          /// True when the statement contains dynamic SQL execution. Notebook
          /// §11.4: this does not forbid the statement, it degrades
          /// analyzability, and that must be visible.
          ContainsDynamicSql: bool

          /// Equality predicates between two columns, from JOIN ... ON and from
          /// WHERE. Both are legitimate join evidence: `FROM a, b WHERE a.id =
          /// b.a_id` is a join written in the older style, and ignoring it would
          /// systematically under-count relationships in older corpora.
          JoinPredicates: JoinPredicate list

          /// Actions inside an ALTER TABLE, in statement order.
          AlterActions: AlterAction list

          /// True when the statement carries a WHERE predicate.
          ///
          /// This is the boundedness signal effect classification turns on
          /// (§10): `UPDATE orders SET status=...` and the same statement with
          /// `WHERE order_id = 42` differ only here. It deliberately says
          /// nothing about how selective the predicate is — Strata has not
          /// evaluated it and has no row counts.
          HasWherePredicate: bool }

    [<RequireQualifiedAccess>]
    module StatementExtraction =

        let empty shape =
            { Shape = shape
              Relations = []
              Columns = []
              UnmodelledConstructs = []
              ContainsDynamicSql = false
              JoinPredicates = []
              AlterActions = []
              HasWherePredicate = false }
