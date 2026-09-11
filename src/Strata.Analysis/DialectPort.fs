namespace Strata.Analysis

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

    /// The contract Tier 3 depends on.
    type IDialectParser =
        abstract Identity: ParserIdentity
        /// Parse a script into statements. A script with a failing statement
        /// still returns the statements around it.
        abstract ParseScript: sql: string -> StatementParse list
        /// Parse a routine body in the dialect's procedural language.
        abstract ParseRoutineBody: body: string -> Result<unit, ParseError>
        /// A literal-independent fingerprint, for corpus deduplication (§123).
        abstract Fingerprint: sql: string -> Result<string, ParseError>
