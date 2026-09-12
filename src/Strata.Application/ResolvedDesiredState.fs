namespace Strata.Application

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Analysis.DialectPort

/// Desired state with every expression already rendered by a server.
///
/// Authority for: what a project means, independent of any target database.
///
/// ## Why this exists
///
/// `DesiredState.load` reads files into the model, but several things in that
/// model are NOT comparable as written. A file's `DEFAULT 'open'` is the
/// catalog's `'open'::text`; a policy's `USING (tenant = 'acme')` is
/// `(tenant = 'acme'::text)`; a view's text is rewritten on the way in; and a
/// declared row's `1.250` may or may not be the `1.25` a `numeric(12,2)` column
/// holds. Only PostgreSQL can settle any of those, so the declared side is
/// executed in a throwaway schema inside an always-rolled-back transaction and
/// read back through the same catalog functions the real objects are read
/// through.
///
/// That resolution depends on the source and on *a* PostgreSQL — never on the
/// TARGET. Which is the whole point: this is the half of a deployment that
/// compiles. The change list cannot, because it depends on what the target
/// holds at the moment of deployment (`DF-STRATA-2026-2F6B`).
///
/// So this is the shape a compiled artifact carries. Today it is assembled and
/// consumed in one process; when `strata compile` lands, it is assembled once,
/// written out, and `strata deploy` reads it without a source tree.
///
/// ## Where it is assembled
///
/// Not here. Assembling it needs `ShadowNormalisation` and `ReferenceData`,
/// which are Tier 4 adapters, and Tier 3 must not acquire a host dependency
/// (`DF-STRATA-2026-D3F8`, enforced by `scripts/check-semantic-architecture.sh`).
/// So the TYPE lives here, where its consumers are, and the assembly lives in
/// the composition root — the same split that already puts
/// `SchemaDiff.NormalisedTable` here and the mapping from
/// `ShadowNormalisation.TableNormalisation` in the CLI.
[<NoComparison>]
type ResolvedDesiredState =
    { /// The project as loaded, before any server rendering.
      Declared: DesiredState.Loaded

      /// View name -> definition as the catalog renders it. A view whose DDL
      /// the server rejected is absent rather than guessed at, and is then
      /// disclosed as not-compared.
      NormalisedViews: (string * string) list

      /// Defaults and check expressions, rendered.
      NormalisedTables: SchemaDiff.NormalisedTable list

      /// Declared policies, with `Using` and `WithCheck` filled in by the
      /// server where normalisation succeeded. A policy that kept its
      /// placeholder is compared on everything except its expressions, and the
      /// expressions are disclosed — never assumed equal.
      Policies: (QualifiedName * Policy) list

      /// Row security settings, carried through unchanged: an `ALTER TABLE
      /// ... ENABLE ROW LEVEL SECURITY` needs no rendering.
      RowSecurity: (QualifiedName * RowSecuritySetting) list

      /// Reference rows, both sides rendered by the server through the real
      /// column types.
      Data: SchemaDiff.ResolvedData list

      /// Tables whose rows could not be resolved. Never folded into an empty
      /// `Data` entry: a table Strata could not read is not a table with no
      /// rows, and treating it as one would propose inserting every declared
      /// row into a table that already has them.
      DataFailures: SchemaDiff.DataFailure list

      /// What could not be rendered, and why. Carried rather than printed so a
      /// compiled artifact can record the conditions it was built under.
      Warnings: string list }
