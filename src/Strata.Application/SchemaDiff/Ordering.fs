namespace Strata.Application.SchemaDiff

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Analysis.ProposedChange

/// When, in one transaction, a kind of change runs.
///
/// DECLARATION ORDER IS EXECUTION ORDER: the plan sorts on this type, and F#
/// compares union cases by their position. Creates come before the things
/// that reference them, drops after the things that depend on them, and
/// columns are dropped before their table so a statement never runs against
/// an object the previous one removed. Within one phase the sort is stable,
/// so statements keep the order their family assembled them in.
[<RequireQualifiedAccess>]
type Phase =
    /// First, and alone: nothing else can run until the namespace its objects
    /// live in exists.
    | Namespaces
    /// After the schema an extension may be placed in, and before everything
    /// else: an extension brings TYPES, and a column may be declared with one.
    /// A table created before its extension fails outright.
    | Extensions
    /// Sequences, enums and domains, after the schema that holds them and
    /// before any table whose column is declared with one or whose DEFAULT
    /// draws from one. Adding an enum label ranks here too — a table created
    /// later in the same plan may use it as a default — and so does every
    /// ALTER DOMAIN: the domain exists by now, and a table created later should
    /// meet the domain the project declares rather than the one it replaces.
    /// Within the phase, `Domains` puts a constraint's drop ahead of the
    /// re-add that takes its name back.
    | Types
    /// Renames preserve data and every later statement refers to the NEW name.
    /// Column renames precede the table rename so both can name the table as
    /// it stands before either runs.
    | ColumnRenames
    /// Table creations and table renames. Among creations, foreign-key depth
    /// breaks the tie: see `creationDepths`.
    | Relations
    /// Columns added, and constraints dropped — one step ahead of the adds,
    /// because a constraint whose definition changed is dropped and re-added
    /// under the SAME name, and the add would fail on a name still taken.
    | ColumnsAndConstraintReleases
    /// Column types, indexes and constraints: after the columns they cover
    /// exist, before the drops.
    | Structure
    /// Views and routines reference tables and columns, so they come after
    /// every table change that might create what they read.
    | ViewsAndRoutines
    /// Strictly after routines: a trigger names the function it executes, and
    /// PostgreSQL rejects a CREATE TRIGGER whose function does not exist yet.
    /// Sharing a phase with routines would not be enough — the sort is stable,
    /// and trigger changes are assembled before routine ones.
    | Triggers
    /// After the table and its columns exist, and after routines: a policy
    /// expression may call one.
    | Policies
    /// After every object they name exists — and STRICTLY before grants,
    /// which is not tidiness.
    ///
    /// `REVOKE SELECT ON t FROM r` removes r's column privileges on `t` as
    /// well as the table-wide one: PostgreSQL treats revoking a table privilege
    /// as revoking the equivalent column privileges. So a plan that grants
    /// `SELECT (id, email)` and then revokes the standing table-wide `SELECT`
    /// ends with the role holding NOTHING — the revoke wipes the grants that
    /// just ran. That is the ordinary shape of narrowing a table grant to a
    /// column grant. Found by a live round-trip: the apply reported three
    /// statements ok and left the role unable to read the columns the file
    /// granted, and the re-plan did not converge.
    | Revocations
    | Grants
    /// After the table, its columns and its constraints exist, and after
    /// triggers: a trigger on a reference table should see the rows arrive the
    /// same way it would see any other write.
    | ReferenceRows
    /// LAST of everything additive, and after the reference rows in particular.
    ///
    /// `FORCE ROW LEVEL SECURITY` makes policies apply to the table's OWNER,
    /// which is the role running the plan. Enabling it before this plan's own
    /// INSERTs would have the server reject them — "new row violates row-level
    /// security policy" — and take the whole transaction with them. Verified
    /// against a live server.
    ///
    /// Switching it OFF sits here too. It is not additive, but it belongs with
    /// its opposite: a plan that disables row-level security and inserts rows
    /// means those rows to land whatever the policies said.
    | RowSecuritySwitches
    | Truncations
    | ColumnDrops
    | RelationDrops
    /// LAST, and after the tables: a type cannot be dropped while a column is
    /// still declared with it, so every table that used it has to go first or
    /// the statement fails.
    | TypeDrops
    | Unclassified

/// Execution order. The one place each change kind is assigned a phase.
module Ordering =

    let phase (change: Change) : Phase =
        match change with
        | CreateSchema _ -> Phase.Namespaces
        | CreateExtension _
        | UpdateExtension _
        | SetExtensionSchema _ -> Phase.Extensions
        | CreateSequence _
        | AlterSequence _
        | CreateEnumType _
        | AddEnumValue _
        | CreateDomainType _
        | SetDomainDefault _
        | DropDomainDefault _
        | SetDomainNotNull _
        | DropDomainNotNull _
        | AddDomainConstraint _
        | DropDomainConstraint _
        | ValidateDomainConstraint _ -> Phase.Types
        | RenameColumn _ -> Phase.ColumnRenames
        | RenameTable _
        | CreateTable _ -> Phase.Relations
        | AddColumn _
        | DropConstraint _ -> Phase.ColumnsAndConstraintReleases
        | AlterColumnType _
        | CreateIndex _
        | DropIndex _
        | AddConstraint _ -> Phase.Structure
        | CreateView _
        | ReplaceView _
        | ReplaceRoutine _
        | CreateRoutine _ -> Phase.ViewsAndRoutines
        | CreateTrigger _
        | ReplaceTrigger _
        | DropTrigger _ -> Phase.Triggers
        | CreatePolicy _
        | ReplacePolicy _ -> Phase.Policies
        | RevokePrivileges _ -> Phase.Revocations
        | GrantPrivileges _ -> Phase.Grants
        | InsertRow _
        | UpdateRow _ -> Phase.ReferenceRows
        | EnableRowLevelSecurity _
        | ForceRowLevelSecurity _
        | DisableRowLevelSecurity _
        | NoForceRowLevelSecurity _ -> Phase.RowSecuritySwitches
        | TruncateTable _ -> Phase.Truncations
        | DropColumn _ -> Phase.ColumnDrops
        | DropSequence _
        | DropTable _ -> Phase.RelationDrops
        | DropEnumType _
        | DropDomainType _ -> Phase.TypeDrops
        | UnclassifiedChange _ -> Phase.Unclassified

    /// How many other tables being created this one must wait for.
    ///
    /// A foreign key needs its referenced table to exist first. Every
    /// CreateTable shares a phase, which is fine until two of them reference
    /// each other's table — and then the order is whatever the file names
    /// sorted to. Found by a live round-trip: a project declaring
    /// `order_audit` (which references `orders`) and `orders` could not be
    /// applied AT ALL. The whole plan is one transaction, so a wrong order does
    /// not half-apply; it fails outright with 42P01 and rolls back.
    ///
    /// A cycle has no satisfying order, so `Tables` withholds those creations;
    /// this returns 0 for them rather than recursing.
    let creationDepths (creating: Table list) =
        let parents = Tables.createdParents creating

        let rec depth (seen: Set<string>) (name: string) =
            if Set.contains name seen then
                0
            else
                match Map.tryFind name parents with
                | None
                | Some [] -> 0
                | Some ps -> 1 + (ps |> List.map (depth (Set.add name seen)) |> List.max)

        parents |> Map.map (fun name _ -> depth Set.empty name)

    /// Sort changes into execution order: phase first, then foreign-key depth
    /// among creations. F#'s sort is stable, so everything else keeps the order
    /// it was assembled in.
    let sort (creating: Table list) (changes: Change list) =
        let depths = creationDepths creating

        let depthOf (change: Change) =
            match change with
            | CreateTable name -> depths |> Map.tryFind (QualifiedName.display name) |> Option.defaultValue 0
            | _ -> 0

        changes |> List.sortBy (fun c -> phase c, depthOf c)
