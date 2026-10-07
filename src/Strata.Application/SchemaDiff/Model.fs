namespace Strata.Application.SchemaDiff


open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.DialectPort
open Strata.Analysis.ProposedChange

/// Why a real difference will not be proposed as a change.
type SuppressionReason =
    | OutsideManagedSchemas
    | DesiredStateIncomplete
    | ExtensionOwnedObject
    | NotModelled
    /// The difference is real and Strata would act on it, but removals were
    /// not enabled for this run.
    | DropsNotEnabled
    /// Strata holds both sides but cannot compare them faithfully, so it
    /// reports that rather than implying they match. This is the reason
    /// that must exist for the diff to be honest: without it, a property
    /// nobody compared renders identically to one that is equal.
    | NotCompared

[<RequireQualifiedAccess>]
module SuppressionReason =

    /// Wire tag, hand-written per Boundary Preservation.
    let tag (reason: SuppressionReason) =
        match reason with
        | OutsideManagedSchemas -> "outside-managed-schemas"
        | DesiredStateIncomplete -> "desired-state-incomplete"
        | ExtensionOwnedObject -> "extension-owned"
        | NotModelled -> "not-modelled"
        | DropsNotEnabled -> "drops-not-enabled"
        | NotCompared -> "not-compared"

type Suppression =
    { Object: QualifiedName
      Reason: SuppressionReason
      Detail: string }

/// A change together with the DDL that would effect it.
type PlannedStatement =
    { Change: Change
      /// `None` is NOT "nothing to do". It is "Strata classified this
      /// difference and cannot write DDL for it safely", which must stop an
      /// apply rather than be silently skipped — executing the rest would
      /// leave the database in a state matching neither side.
      Sql: string option }

type DiffResult =
    { /// Differences Strata proposes acting on. These go to the gate.
      Changes: Change list
      /// The same changes, with the DDL that would effect each.
      Statements: PlannedStatement list
      /// Differences Strata saw and will NOT act on, each with a reason.
      Suppressed: Suppression list
      /// True when the desired snapshot could support a deletion claim at
      /// all. A caller rendering a clean diff needs this: "no changes"
      /// means something different when desired state is partial.
      DesiredStateComplete: bool }

/// Rename intent declared in a project file.
///
/// §86: a rename and a drop-plus-add produce identical desired states, so
/// this can only ever be declared, never inferred. `ER-010` makes guessing
/// forbidden rather than merely unwise — guess wrong and you either destroy
/// a table or silently keep one that should have gone.
type DeclaredRename =
    { /// The object the annotation sits on, by its NEW name.
      Object: QualifiedName
      /// The object's previous name, if the object itself was renamed.
      RenamedFrom: QualifiedName option
      /// Previous column name -> current column name.
      Columns: (string * string) list }

/// Everything `run` compares.
///
/// A record rather than a parameter list, and not for tidiness. `run` took
/// sixteen positional arguments, of which `desired` and `actual` were
/// ADJACENT and both `SchemaSnapshot`: transposing them compiled cleanly
/// and inverted the entire diff, turning every declared object into a
/// proposed drop and every deployed one into a proposed create. Two more
/// were both `string list`. Named fields make that transposition impossible
/// to write, and a new input a field rather than a positional insertion
/// that renumbers every call site.
[<NoComparison>]
type Inputs =
    { AllowDrops: bool
      /// The schemas this project manages, derived from the directory names
      /// under the schema root (`DF-STRATA-2026-C3A2`).
      ///
      /// One field, where there were two. `ManagedSchemas` said what MAY be
      /// changed and `DeclaredInSchemas` what WAS declared, and the gap
      /// between them was the hazard: a schema managed with nothing declared
      /// in it reads as "this schema should be empty". Deriving both from the
      /// same directory tree closes the gap by construction — and the
      /// project loader refuses an empty schema directory, which is what
      /// keeps it closed.
      ManagedSchemas: string list
      /// The verbatim text that declared each object.
      Declarations: (QualifiedName * string) list
      TriggerDeclarations: ((QualifiedName * Identifier) * string) list
      /// The verbatim text of each declared index, keyed by table and index
      /// name. An index is created by executing this when it is present.
      IndexDeclarations: ((QualifiedName * Identifier) * string) list
      /// The verbatim text of each declared policy, keyed by table and
      /// policy name. A policy is created by executing this, never by
      /// reconstruction: its expressions are not in the model.
      PolicyDeclarations: ((QualifiedName * Identifier) * string) list
      /// Policies the project declares, with the server's rendering of
      /// their expressions where normalisation succeeded.
      DeclaredPolicies: (QualifiedName * Policy) list
      /// Row-level security settings the project declares.
      DeclaredRowSecurity: (QualifiedName * RowSecuritySetting) list
      /// Extensions the project declares.
      DeclaredExtensions: Extension list
      /// Extensions installed in the database. `None` means the caller did
      /// not ask; `Some (Error _)` that it asked and could not read them.
      ActualExtensions: Result<Extension list, string> option
      /// Schemas that exist in the database, or `None` when the list could
      /// not be read. `None` is not an empty list.
      ExistingSchemas: string list option
      /// Privileges the project declares.
      DeclaredGrants: Grant list
      /// Privileges the database holds, or `None` when they could not be
      /// read.
      ActualGrants: Grant list option
      /// Row-level security the database holds. Reported, never changed:
      /// Strata does not model policies as desired state yet, and says so
      /// rather than staying silent.
      ///
      /// THREE states, and the distinction is the same one this whole
      /// codebase turns on. `None` means the caller did not ask, so there is
      /// nothing to report. `Some (Error _)` means it was asked for and
      /// could not be read, which is disclosed. `Some (Ok _)` is the answer.
      ///
      /// A plain `option` collapsed the first two, and the result was a
      /// caller that never requested row-level security being told the
      /// database's row-level security could not be read.
      ActualRowLevelSecurity: Result<RowLevelSecurity list, string> option
      /// Declared and deployed reference rows, already rendered by the
      /// server. Empty when the project declares no data files.
      Data: ResolvedData list
      DataFailures: DataFailure list
      NormalisedViews: (string * string) list
      NormalisedTables: NormalisedTable list
      /// Declared domains as the server renders them. A domain missing from
      /// here is DISCLOSED rather than compared — its predicates never
      /// survived the parser, so there is nothing to compare it with.
      NormalisedDomains: NormalisedDomain list
      Renames: DeclaredRename list
      Desired: SchemaSnapshot
      Actual: SchemaSnapshot }

[<RequireQualifiedAccess>]
module Inputs =

    /// The two snapshots and nothing else.
    ///
    /// Every other input defaults to the state that proposes the LEAST: no
    /// managed schemas, no drops, and `None` — could not be read — rather
    /// than `[]` wherever the two differ, so a caller that forgets to
    /// supply something gets silence instead of a confident wrong answer.
    let between (desired: SchemaSnapshot) (actual: SchemaSnapshot) =
        { AllowDrops = false
          ManagedSchemas = []
          Declarations = []
          TriggerDeclarations = []
          IndexDeclarations = []
          PolicyDeclarations = []
          DeclaredPolicies = []
          DeclaredRowSecurity = []
          DeclaredExtensions = []
          ActualExtensions = None
          ExistingSchemas = None
          DeclaredGrants = []
          ActualGrants = None
          ActualRowLevelSecurity = None
          Data = []
          DataFailures = []
          NormalisedViews = []
          NormalisedTables = []
          NormalisedDomains = []
          Renames = []
          Desired = desired
          Actual = actual }

/// What comparing one family of schema objects produced.
///
/// Every family answers in this one shape, so the orchestration composes them
/// without knowing what is inside any of them.
type FamilyDiff =
    { /// Each difference, in the order the family assembled it: a change Strata
      /// proposes, or the suppression saying why it will not. The order is
      /// load-bearing — the plan's sort is stable, so within one rank
      /// statements run in exactly this order.
      Differences: Result<Change, Suppression> list
      /// Disclosures that belong to no single difference: what could not be
      /// compared, and what the project does not claim.
      Disclosures: Suppression list }

[<RequireQualifiedAccess>]
module FamilyDiff =

    let ofDifferences (differences: Result<Change, Suppression> list) =
        { Differences = differences; Disclosures = [] }

/// The objects of one kind in a snapshot, in snapshot order.
[<RequireQualifiedAccess>]
module SnapshotObjects =

    let tables (snapshot: SchemaSnapshot) =
        snapshot.Objects |> List.choose (function TableObject t -> Some t | _ -> None)

    let sequences (snapshot: SchemaSnapshot) =
        snapshot.Objects |> List.choose (function SequenceObject sq -> Some sq | _ -> None)

    let enums (snapshot: SchemaSnapshot) =
        snapshot.Objects |> List.choose (function EnumObject e -> Some e | _ -> None)

    let domains (snapshot: SchemaSnapshot) =
        snapshot.Objects |> List.choose (function DomainObject d -> Some d | _ -> None)

[<RequireQualifiedAccess>]
module Names =

    /// Two qualified names denote the same object. By display form, which is
    /// how every family has always matched objects across the two snapshots.
    let same (a: QualifiedName) (b: QualifiedName) =
        QualifiedName.display a = QualifiedName.display b

[<RequireQualifiedAccess>]
module Lists =

    /// Removes the FIRST element matching `predicate`, returning it and the
    /// rest in order — not every equal one. Two declarations that do the same
    /// thing need two deployed objects, not one counted twice.
    let removeFirst (predicate: 'a -> bool) (items: 'a list) : ('a * 'a list) option =
        let rec go acc rest =
            match rest with
            | [] -> None
            | head :: tail when predicate head -> Some(head, List.rev acc @ tail)
            | head :: tail -> go (head :: acc) tail

        go [] items
