namespace Strata.Semantic

open Strata.Semantic.Identity
open Strata.Semantic.Evidence

/// The canonical Strata schema model.
///
/// Authority for: what Strata durably knows about database structure.
///
/// This is deliberately smaller than the parser AST (P-014, D-005, D-006,
/// ER-014) and smaller than the notebook's §6 introspection wish-list. Only
/// concepts a current requirement needs are present. Notebook §134 warns that a
/// universal canonical model becomes an endless taxonomy project; RK-018 tracks
/// that risk. Add a concept when a requirement needs it, not before.
module Schema =

    /// A reference to a type. Not modelled structurally yet: S1 needs to report
    /// and compare type names, not reason about type lattices.
    type TypeRef =
        { TypeName: QualifiedName
          IsNullable: bool }

    type Column =
        { Name: Identifier
          Type: TypeRef
          /// Ordinal position as the catalog reports it. Part of identity for
          /// deterministic serialization (NFR-001).
          Position: int
          HasDefault: bool
          /// The default EXPRESSION as the catalog renders it, when known.
          ///
          /// `None` on a declared column: a file says `DEFAULT 'open'` and the
          /// catalog says `'open'::text`, so the declared text is not
          /// comparable until the server has rendered it. `HasDefault` stays
          /// the reliable presence signal; this is the comparable value when
          /// there is one.
          DefaultExpression: string option
          IsGenerated: bool
          IsIdentity: bool }

    /// A constraint's name, where there is one.
    ///
    /// `None` is what an UNNAMED declared constraint has, and it is a real
    /// state rather than a missing value. `CREATE TABLE t (a bigint REFERENCES
    /// u (id))` names nothing; the server assigns `t_a_fkey` when the table is
    /// created. Filling that in with a fabricated placeholder is what a
    /// previous version did, and it made every such project unconvergeable: the
    /// declared side said `foreign_key`, the catalog said `t_a_fkey`, and every
    /// re-plan proposed adding one and dropping the other, forever.
    ///
    /// The two states also mean different things to a diff. A NAMED declared
    /// constraint says "a constraint with this name and this meaning must
    /// exist"; an unnamed one says "a constraint with this meaning must exist,
    /// and I do not care what it is called". So a name is matched on when there
    /// is one, and the definition is matched on when there is not.
    ///
    /// An INTROSPECTED constraint always has a name — the catalog has no
    /// nameless ones — so `None` on the actual side never occurs.
    type ConstraintName = Identifier option

    type PrimaryKey =
        { ConstraintName: ConstraintName
          Columns: Identifier list }

    type UniqueConstraint =
        { ConstraintName: ConstraintName
          Columns: Identifier list }

    type CheckConstraint =
        { ConstraintName: ConstraintName
          /// The expression as the catalog renders it. Strata does not parse it
          /// yet; storing the text with no pretence of understanding is honest.
          Expression: string }

    type ForeignKey =
        { ConstraintName: ConstraintName
          Columns: Identifier list
          ReferencedTable: QualifiedName
          ReferencedColumns: Identifier list }

    type Index =
        { Name: Identifier
          Columns: Identifier list
          IsUnique: bool
          /// Partial-index predicate text, if any.
          Predicate: string option }

    /// When a trigger fires relative to the statement that provoked it.
    ///
    /// Qualified access: `Before` and `After` are words a caller is likely to
    /// bind to something else, and `Schema` is opened widely.
    [<RequireQualifiedAccess>]
    type TriggerTiming =
        | Before
        | After
        | InsteadOf

    /// How often a trigger fires: once per affected row, or once per statement.
    [<RequireQualifiedAccess>]
    type TriggerLevel =
        | Row
        | Statement

    /// A trigger, as both a declaring file and the catalog can describe it.
    type Trigger =
        { Name: Identifier
          Timing: TriggerTiming
          /// The events that fire it, lowercase, in the fixed order
          /// insert, delete, update, truncate.
          ///
          /// A list because `AFTER INSERT OR UPDATE` is ONE trigger with two
          /// events, not two triggers. The order is fixed so two sides that
          /// agree always compare equal: PostgreSQL stores these as a bitmask
          /// in both `CreateTrigStmt.events` and `pg_trigger.tgtype`, so
          /// neither side preserves the order the author wrote.
          Events: string list
          Level: TriggerLevel
          /// Columns an `UPDATE OF` restricts firing to. Empty means every
          /// column, which is what an unrestricted UPDATE trigger means.
          UpdateColumns: Identifier list
          /// The function the trigger executes.
          Function: QualifiedName
          /// Literal arguments passed to that function, in order.
          Arguments: string list
          /// Whether a `WHEN` clause restricts firing.
          ///
          /// Presence only, and deliberately. The expression is unavailable on
          /// BOTH sides: a file's `WHEN` clause is a parse tree Strata does not
          /// deparse, and the catalog refuses to render `tgqual` at all --
          /// `pg_get_expr` fails with "expression contains variables of more
          /// than one relation" because OLD and NEW are two relations. So the
          /// EXISTENCE of a condition is comparable and its text is not, and
          /// the two must not be confused (ER-008).
          HasCondition: bool }

    type Table =
        { Name: QualifiedName
          Columns: Column list
          PrimaryKey: PrimaryKey option
          UniqueConstraints: UniqueConstraint list
          CheckConstraints: CheckConstraint list
          ForeignKeys: ForeignKey list
          Indexes: Index list
          Triggers: Trigger list
          Scope: ManagementScope }

    type View =
        { Name: QualifiedName
          Columns: Column list
          IsMaterialized: bool
          Definition: string
          Scope: ManagementScope }

    type RoutineKind =
        | Function
        | Procedure

    type Routine =
        { Name: QualifiedName
          Kind: RoutineKind
          /// Argument type names in positional order. Enough to distinguish
          /// overloads by signature; not enough to resolve a call's arguments,
          /// which is why overload resolution is an `Ambiguous` outcome until a
          /// catalog lookup happens (EV-STRATA-2026-B9C4).
          ArgumentTypes: string list
          ReturnType: string option
          Language: string
          /// The body as text, when there is one.
          ///
          /// PostgreSQL stores a classic `AS $$...$$` body VERBATIM in
          /// `prosrc`, so a declared body and a deployed one compare directly —
          /// unlike a view, whose definition is rewritten on the way in.
          ///
          /// `None` means the server holds no text: a SQL-standard
          /// `BEGIN ATOMIC` body is parsed and stored as a tree, and a C
          /// function's `prosrc` names a symbol rather than a body. It does NOT
          /// mean the body is empty, and nothing may compare on it.
          Body: string option
          Scope: ManagementScope }

    /// A sequence declared in its own right.
    ///
    /// NOT the sequences PostgreSQL creates for you. A `serial` column and an
    /// `IDENTITY` column each get one, and neither belongs to the project: the
    /// column owns it, `ALTER SEQUENCE` on it is the column's business, and
    /// dropping it would break the column. Introspection excludes both — see
    /// `CatalogQueries.sequences`, where the exclusion is spelled out.
    ///
    /// The sequence's CURRENT VALUE is deliberately absent. `last_value` is
    /// data, not structure: it changes every time anything calls `nextval`, so
    /// comparing it would report a difference on a sequence nobody touched and
    /// "fixing" it would mean handing out a number twice.
    type Sequence =
        { Name: QualifiedName
          /// `bigint`, `integer` or `smallint`, as the catalog renders it.
          DataType: string
          Start: int64
          Increment: int64
          MinValue: int64
          MaxValue: int64
          Cache: int64
          Cycle: bool
          Scope: ManagementScope }

    /// What kind of object a grant is on.
    ///
    /// A discriminator rather than a flag on a flat record, because the three
    /// are not variations on one thing. They live in three different catalogs
    /// (`pg_class.relacl`, `pg_namespace.nspacl`, `pg_proc.proacl`), they take
    /// three different `GRANT` spellings, and their identities have different
    /// shapes: a schema has no schema to be qualified by, and a routine is not
    /// identified by name alone.
    ///
    /// A single `QualifiedName` could hold the first two by convention, and
    /// that convention is exactly the kind of thing that reads a schema grant
    /// as a table grant once and hands out the wrong access.
    [<RequireQualifiedAccess>]
    type GrantTarget =
        /// A table, view, materialized view or sequence. `pg_class.relacl`.
        | Relation of QualifiedName
        /// A schema itself. `pg_namespace.nspacl`.
        ///
        /// An `Identifier`, not a `QualifiedName`: a schema is not inside
        /// anything. `USAGE` here is what makes every qualified name inside the
        /// schema reachable at all, so a project that grants on a table and not
        /// on its schema has granted nothing usable.
        | Schema of Identifier
        /// A function or procedure. `pg_proc.proacl`.
        ///
        /// The argument types are part of the identity and not decoration:
        /// `GRANT EXECUTE ON FUNCTION f(integer)` and `f(text)` name two
        /// different functions, and granting on the wrong one is silent.
        ///
        /// Rendered the way `Routine.ArgumentTypes` is rendered — without type
        /// modifiers, as `format_type(typid, NULL)` produces — because a grant
        /// that spells its types differently from the routine it is on can
        /// never be matched to it.
        | Routine of QualifiedName * string list
        /// One COLUMN of a relation. `pg_attribute.attacl`.
        ///
        /// One target per column, so `GRANT SELECT (id, total)` is two grants.
        /// That matches how the column ACLs are actually stored — per column,
        /// not as a list hanging off the table — and it means a project can
        /// declare different privileges on different columns without the model
        /// needing a shape for "these privileges, but only on those columns".
        ///
        /// A column privilege is NOT a narrower table privilege. They live in
        /// separate ACLs and the effective access is the union: a role with
        /// table-wide `SELECT` can read every column with no `attacl` entry
        /// anywhere, and a role with only `SELECT (id)` can read that column
        /// while `has_table_privilege(..., 'SELECT')` is false. Both verified on
        /// a live server. Collapsing the two is what made an earlier version
        /// read `GRANT SELECT (id) ON t` as a table-wide grant and hand out
        /// access to columns the file withheld.
        | RelationColumn of QualifiedName * Identifier

    [<RequireQualifiedAccess>]
    module GrantTarget =

        /// The name to REPORT a grant against.
        ///
        /// Lossy on purpose, and only for display: a routine's arguments are
        /// dropped and a schema is rendered unqualified. Nothing that builds
        /// DDL may use this — `GRANT ... ON s` and `GRANT ... ON SCHEMA s` are
        /// different statements.
        let name (t: GrantTarget) =
            match t with
            | GrantTarget.Relation n -> n
            | GrantTarget.Schema s -> QualifiedName.unqualified s
            | GrantTarget.Routine (n, _) -> n
            | GrantTarget.RelationColumn (n, _) -> n

        /// The schema the grant's object lives in, where that is a question.
        ///
        /// For a schema grant the object IS the schema, so it answers itself.
        /// This is what decides whether a grant lies inside the managed
        /// schemas, and a schema grant that reported `None` here would put
        /// every `GRANT USAGE` permanently outside Strata's remit.
        let schema (t: GrantTarget) =
            match t with
            | GrantTarget.Relation n -> n.Schema
            | GrantTarget.Schema s -> Some s
            | GrantTarget.Routine (n, _) -> n.Schema
            | GrantTarget.RelationColumn (n, _) -> n.Schema

        /// A stable key for matching a declared grant to a deployed one.
        ///
        /// Includes the kind, so a schema named `orders` and a table named
        /// `orders` are never the same key.
        let key (t: GrantTarget) =
            match t with
            | GrantTarget.Relation n -> sprintf "relation:%s" (QualifiedName.display n)
            | GrantTarget.Schema s -> sprintf "schema:%s" (Identifier.folded s)
            | GrantTarget.Routine (n, args) ->
                sprintf "routine:%s(%s)" (QualifiedName.display n) (String.concat "," args)
            | GrantTarget.RelationColumn (n, column) ->
                sprintf "column:%s.%s" (QualifiedName.display n) (Identifier.folded column)

        /// The scope a declared grant takes OWNERSHIP of.
        ///
        /// Coarser than `key`, and deliberately: a column grant and a table
        /// grant on the same relation share a scope. Declaring
        /// `GRANT SELECT (id) ON t TO r` therefore claims r's table-wide
        /// privileges on `t` as well, so the table-wide `SELECT` that would let
        /// r read every other column becomes a proposed revoke.
        ///
        /// Without that, a file saying "r may read only id" could not achieve
        /// it: the column grant would be added, the table-wide grant would be
        /// left in place as something the project never mentioned, and r would
        /// keep reading everything. Keyed grant-by-grant, the feature would not
        /// do the one thing it exists for.
        ///
        /// The grantee is still part of the scope at the call site, so this does
        /// NOT widen ownership across grantees: managing `app_user` on a table
        /// still leaves the replication role's privileges alone.
        let ownershipScope (t: GrantTarget) =
            match t with
            | GrantTarget.Relation n
            | GrantTarget.RelationColumn (n, _) -> sprintf "relation:%s" (QualifiedName.display n)
            | GrantTarget.Schema s -> sprintf "schema:%s" (Identifier.folded s)
            | GrantTarget.Routine (n, args) ->
                sprintf "routine:%s(%s)" (QualifiedName.display n) (String.concat "," args)

    /// Privileges one grantee holds on one object.
    ///
    /// The grantee is a role NAME, or the literal `PUBLIC`. PostgreSQL stores
    /// PUBLIC as grantee OID 0, which `pg_get_userbyid` renders as the string
    /// `unknown (OID=0)` — so it is translated at the catalog boundary rather
    /// than carried here, or a revoke would name a role that does not exist.
    ///
    /// The OWNER's privileges are not grants and never appear here. PostgreSQL
    /// materialises them into the ACL as soon as anything is granted
    /// (`postgres=arwdDxt/postgres`), and reading those as grants would have
    /// Strata propose revoking the owner's own access to its own table.
    type Grant =
        { Target: GrantTarget
          Grantee: string
          /// Uppercase and sorted, so two sides that agree compare equal.
          Privileges: string list
          /// The subset of `Privileges` the grantee may in turn grant to
          /// others — `WITH GRANT OPTION`, `aclexplode`'s `is_grantable`.
          ///
          /// Always empty on a DECLARED grant: a file asking for `WITH GRANT
          /// OPTION` is refused rather than read as a plain grant, for the same
          /// reason a column-level grant is. Reading it as plain would let a
          /// file hand out the power to re-grant while the plan said nothing
          /// about it.
          ///
          /// Populated on an INTROSPECTED grant, and it is not compared. It
          /// exists so that a deployed grantable privilege is DISCLOSED rather
          /// than silently matched against a declared plain one — the privilege
          /// itself does converge, and the extra power nobody declared is
          /// reported instead of vanishing (ER-008: "holds SELECT" and "holds
          /// SELECT and can pass it on" are different states).
          Grantable: string list }

    type SchemaObject =
        | TableObject of Table
        | ViewObject of View
        | RoutineObject of Routine
        | SequenceObject of Sequence

    [<RequireQualifiedAccess>]
    module SchemaObject =

        let name (o: SchemaObject) =
            match o with
            | TableObject t -> t.Name
            | ViewObject v -> v.Name
            | RoutineObject r -> r.Name
            | SequenceObject s -> s.Name

        let kind (o: SchemaObject) =
            match o with
            | TableObject _ -> ObjectKind.Table
            | ViewObject v -> if v.IsMaterialized then ObjectKind.MaterializedView else ObjectKind.View
            | RoutineObject _ -> ObjectKind.Routine
            | SequenceObject _ -> ObjectKind.Sequence

        let scope (o: SchemaObject) =
            match o with
            | TableObject t -> t.Scope
            | ViewObject v -> v.Scope
            | RoutineObject r -> r.Scope
            | SequenceObject s -> s.Scope

    /// Server identity for a snapshot.
    type ServerVersion =
        { /// e.g. 16
          Major: int
          /// Full version string as the server reports it.
          Full: string }

    /// A point-in-time view of a database's structure.
    ///
    /// `Completeness` is not optional decoration. Notebook §6 requires
    /// introspection completeness be reported, and P-009 requires failing closed
    /// when visibility is incomplete. A snapshot without it could not support
    /// either rule, so the field is mandatory.
    type SchemaSnapshot =
        { Objects: SchemaObject list
          ServerVersion: Fact<ServerVersion> option
          Completeness: AnalysisScope.Completeness }
