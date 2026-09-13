namespace Strata.Semantic

/// Names and identity for database objects.
///
/// Authority for: how Strata names an object, and how a name differs from an
/// identity. Notebook §7 is explicit that names are not always identity and
/// that a rename must not look like drop+create. This module keeps the two
/// concepts separate from the start so later rename handling (D-015) has
/// something to attach to.
module Identity =

    /// A SQL identifier as written, plus whether it was quoted.
    ///
    /// Quoting matters: PostgreSQL folds unquoted identifiers to lower case but
    /// preserves quoted ones, so "Tbl" and Tbl are different objects. Discarding
    /// the distinction here would be a Representation Collapse at the very
    /// bottom of the model.
    [<StructuredFormatDisplay("{Display}")>]
    type Identifier =
        { Text: string
          WasQuoted: bool }

        member this.Display =
            if this.WasQuoted then "\"" + this.Text + "\"" else this.Text

    [<RequireQualifiedAccess>]
    module Identifier =

        let quoted (text: string) = { Text = text; WasQuoted = true }

        let unquoted (text: string) = { Text = text; WasQuoted = false }

        /// The name as PostgreSQL would fold it for comparison purposes.
        /// Unquoted identifiers fold to lower case; quoted ones are preserved.
        let folded (id: Identifier) =
            if id.WasQuoted then id.Text else id.Text.ToLowerInvariant()

        /// Two identifiers denote the same name when their folded forms match.
        let sameName (a: Identifier) (b: Identifier) = folded a = folded b

    /// The kind of database object a name refers to.
    ///
    /// Closed on purpose. Adding a case forces every consumer to decide what it
    /// means, which is the exhaustiveness benefit the four-tier doctrine relies
    /// on. Only kinds a current requirement needs are present (ER-014).
    type ObjectKind =
        | Schema
        | Table
        | View
        | MaterializedView
        | Column
        | Index
        | Sequence
        | Routine
        | Trigger
        | Constraint
        | EnumType
        | DomainType

    /// A name as it appeared in SQL or the catalog: optionally schema-qualified.
    ///
    /// `Schema = None` is a real, meaningful state — it means the SQL did not
    /// say, and resolution against search_path has not happened yet. It must not
    /// be defaulted to "public" at construction time (RK-006).
    type QualifiedName =
        { Schema: Identifier option
          Name: Identifier }

    [<RequireQualifiedAccess>]
    module QualifiedName =

        let unqualified (name: Identifier) = { Schema = None; Name = name }

        let qualified (schema: Identifier) (name: Identifier) =
            { Schema = Some schema; Name = name }

        let isSchemaQualified (q: QualifiedName) = Option.isSome q.Schema

        let display (q: QualifiedName) =
            match q.Schema with
            | Some s -> s.Display + "." + q.Name.Display
            | None -> q.Name.Display

    /// Whether Strata manages an object, merely observes it, or cannot tell.
    ///
    /// Notebook §6 requires deciding what Strata manages versus observes, and
    /// §144.14 requires modelling management ownership explicitly *before*
    /// Strata ever deletes anything. `Unknown` is deliberate: an object whose
    /// ownership has not been established is not implicitly Strata's.
    type ManagementScope =
        | Managed
        | Observed
        | ExtensionOwned
        | SystemOwned
        | UnknownOwnership
