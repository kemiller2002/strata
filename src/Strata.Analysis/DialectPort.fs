namespace Strata.Analysis

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Analysis.StatementReferences

/// The narrow dialect-adapter boundary.
///
/// Authority for: what Strata requires of any SQL dialect parser.
///
/// Notebook §5.4 specifies a narrow adapter interface and forbids exposing
/// parser-specific node types throughout Strata core. This is that interface.
/// A SQL Server adapter over ScriptDom (§143) would implement the same shape.
///
/// This is a PORT, not an adapter: it lives in Tier 2 because Tier 2 and Tier 3
/// depend on it, while every implementation lives in Tier 4. Keeping the
/// interface here is what lets the application tier orchestrate parsing without
/// referencing a host project, which the architecture check forbids.
module DialectPort =

    /// Where a statement sat in its source text.
    type StatementLocation =
        { /// Byte offset of the statement start.
          Offset: int
          Length: int }

    /// A parse failure, carrying enough to locate it.
    ///
    /// Modelled as data, not an exception. The selected wrapper already returns
    /// an explicit Result with a cursor position (EV-STRATA-2026-7A31), so the
    /// adapter maps one explicit representation onto another rather than
    /// translating exceptions.
    type ParseError =
        { Message: string
          /// 1-based cursor position within the statement, as the parser reports.
          CursorPosition: int
          Context: string option }

    /// The outcome of parsing one statement.
    ///
    /// `Failed` is a first-class outcome. ER-008: a statement that did not parse
    /// is not a statement with no references.
    type StatementParse =
        | Parsed of location: StatementLocation * extraction: StatementExtraction
        | Failed of location: StatementLocation * error: ParseError

    /// Versions the adapter is operating at.
    ///
    /// NFR-004 requires both be reported. RK-003: the parser's PostgreSQL major
    /// may differ from the live server's, and a caller cannot assess that
    /// without seeing this.
    type ParserIdentity =
        { /// e.g. "pgsqlparser 1.0.0"
          AdapterName: string
          /// PostgreSQL version whose grammar the parser implements.
          DialectVersion: string
          DialectMajor: int }

    /// What a desired-state object file turned out to declare.
    ///
    /// A file that declares nothing Strata models is NOT an empty file — it is
    /// a file whose declaration Strata could not represent, and the two must
    /// stay apart (ER-008). `Unmodelled` carries what it saw.
    type ObjectDeclaration =
        | Declared of SchemaObject
        /// An index, which belongs to a table rather than standing alone.
        ///
        /// `CREATE INDEX` is a separate statement but `Index` is a field of
        /// `Table`, so a declared index carries the table it attaches to and
        /// the loader joins them once every file is read.
        | DeclaredIndex of table: QualifiedName * index: Index
        /// A trigger, which belongs to a table for the same reason an index
        /// does: `CREATE TRIGGER` is its own statement, but the object whose
        /// behaviour it changes is the table, and the loader joins them once
        /// every file is read.
        | DeclaredTrigger of table: QualifiedName * trigger: Trigger
        /// Rows a reference table must contain.
        ///
        /// A lookup table's rows — account types, status codes — are part of
        /// the schema rather than user data, so a project has to be able to
        /// declare them.
        ///
        /// Each value is a LITERAL TOKEN, re-emitted from the parse tree, not a
        /// value Strata interpreted. `1.25` stays the four characters `1.25`;
        /// nothing here decides what they mean in a `numeric(12,2)` column.
        /// That distinction is the whole safety of this path: interpreting a
        /// literal means reimplementing PostgreSQL's type rules, and getting
        /// one subtly wrong produces a row that compares unequal to itself on
        /// every run forever. Re-emitting a token means the server does the
        /// interpreting, both when the row is compared and when it is written.
        ///
        /// The token kinds are a closed set — integer, float, string, boolean,
        /// bit-string, NULL — and anything outside it is refused rather than
        /// rendered.
        | DeclaredRows of table: QualifiedName * columns: Identifier list * rows: string list list
        | Unmodelled of detail: string
        | DeclarationFailed of ParseError

    /// The contract Tier 3 depends on.
    type IDialectParser =
        abstract Identity: ParserIdentity
        /// Parse a script into statements. A script with a failing statement
        /// still returns the statements around it.
        abstract ParseScript: sql: string -> StatementParse list

        /// Read a desired-state object file into the semantic model.
        ///
        /// Separate from `ParseScript` because the two answer different
        /// questions about the same grammar. `ParseScript` asks what a
        /// statement REFERENCES, which is what a query has; this asks what a
        /// statement DECLARES, which is what a `CREATE TABLE` has. Folding the
        /// second into `StatementExtraction` would put definition fields on
        /// every parsed query and put the desired-state path on the hot query
        /// path (PR-025, DF-STRATA-2026-B1E7).
        ///
        /// Returns one declaration per top-level statement so a file declaring
        /// more than one object is visible to the caller as such, rather than
        /// silently merged.
        abstract ParseObjectDefinitions: sql: string -> ObjectDeclaration list
        /// Parse a routine body in the dialect's procedural language.
        abstract ParseRoutineBody: body: string -> Result<unit, ParseError>
        /// A literal-independent fingerprint, for corpus deduplication (§123).
        abstract Fingerprint: sql: string -> Result<string, ParseError>
