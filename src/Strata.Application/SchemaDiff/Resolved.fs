namespace Strata.Application.SchemaDiff

// Host-boundary transfer types for the schema diff.
//
// The host tier resolves these against a live server (`Strata.Cli.Resolution`)
// and carries them in the signed artifact (`Strata.Host.Files.Artifact`). They
// live in their own file so those consumers depend on the DTOs alone, not on
// the diff algorithm: a change to how differences are computed must not
// recompile, or re-risk, the code that signs what was resolved.

/// One reference row, as the SERVER rendered it.
///
/// Mirrors `ReferenceData.ResolvedRow`, which lives in the host tier. The
/// duplication is the architecture working: Tier 3 may not reference a
/// host project, so the host maps its result onto this on the way in —
/// exactly as it already does for `NormalisedTable`.
type ResolvedRow =
    { Key: string
      Rendered: string
      /// Re-injectable SQL for each declared column, from the server's own
      /// `quote_nullable`. Empty for a deployed row, which is never written
      /// back.
      Literals: string list }

/// Declared and deployed rows for one reference table.
type ResolvedData =
    { Table: string
      Columns: string list
      KeyColumns: string list
      Declared: ResolvedRow list
      Deployed: ResolvedRow list }

/// A reference table whose rows could not be resolved, and why.
///
/// Never folded into an empty resolution: a table Strata could not read is
/// not a table with no rows, and collapsing the two would propose
/// inserting every declared row into a table that already holds them.
type DataFailure = { Table: string; Reason: string }

/// Declared defaults and checks as the SERVER renders them, for one table.
type NormalisedTable =
    { Table: string
      Defaults: (string * string) list
      Checks: (string * string) list }

/// A declared partial index's WHERE predicate as the SERVER renders it.
///
/// Absent for an index the server could not build in the shadow, whose
/// predicate is then disclosed as not-compared rather than compared with
/// nothing.
type NormalisedIndex =
    { Table: string
      Index: string
      Predicate: string }

/// A declared domain as the SERVER renders it.
///
/// A domain read from a file carries no default expression and no check
/// predicates at all — neither is recoverable from the parse tree — so
/// WITHOUT this there is nothing to compare and the honest report is
/// "not compared". A domain absent from the resolved list gets exactly that.
///
/// `Constraints` is one entry per DECLARED constraint, in declaration order,
/// so the caller pairs them positionally with what the file wrote and keeps
/// each one's declared name — or its declared namelessness, which is the
/// thing a server-assigned `<domain>_check` would destroy (WI-0054).
type NormalisedDomain =
    { Domain: string
      BaseType: string
      Collation: string option
      NotNull: bool
      Default: string option
      /// `(definition, validated)`. No name: the shadow's own is a per-run
      /// GUID and the diff never reads it — see
      /// `ShadowNormalisation.DomainNormalisation`.
      Constraints: (string * bool) list }
