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
          IsGenerated: bool
          IsIdentity: bool }

    type PrimaryKey =
        { ConstraintName: Identifier
          Columns: Identifier list }

    type UniqueConstraint =
        { ConstraintName: Identifier
          Columns: Identifier list }

    type CheckConstraint =
        { ConstraintName: Identifier
          /// The expression as the catalog renders it. Strata does not parse it
          /// yet; storing the text with no pretence of understanding is honest.
          Expression: string }

    type ForeignKey =
        { ConstraintName: Identifier
          Columns: Identifier list
          ReferencedTable: QualifiedName
          ReferencedColumns: Identifier list }

    type Index =
        { Name: Identifier
          Columns: Identifier list
          IsUnique: bool
          /// Partial-index predicate text, if any.
          Predicate: string option }

    type Table =
        { Name: QualifiedName
          Columns: Column list
          PrimaryKey: PrimaryKey option
          UniqueConstraints: UniqueConstraint list
          CheckConstraints: CheckConstraint list
          ForeignKeys: ForeignKey list
          Indexes: Index list
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
          Scope: ManagementScope }

    type SchemaObject =
        | TableObject of Table
        | ViewObject of View
        | RoutineObject of Routine

    [<RequireQualifiedAccess>]
    module SchemaObject =

        let name (o: SchemaObject) =
            match o with
            | TableObject t -> t.Name
            | ViewObject v -> v.Name
            | RoutineObject r -> r.Name

        let kind (o: SchemaObject) =
            match o with
            | TableObject _ -> ObjectKind.Table
            | ViewObject v -> if v.IsMaterialized then ObjectKind.MaterializedView else ObjectKind.View
            | RoutineObject _ -> ObjectKind.Routine

        let scope (o: SchemaObject) =
            match o with
            | TableObject t -> t.Scope
            | ViewObject v -> v.Scope
            | RoutineObject r -> r.Scope

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
