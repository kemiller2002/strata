namespace Strata.Application.SchemaDiff

open Strata.Analysis.ProposedChange

/// Comparing desired state against actual state (PR-022, PR-023).
///
/// Authority for: what differs between two snapshots, and which of those
/// differences Strata is willing to propose acting on.
///
/// Those are two questions, not one, and keeping them apart is the whole
/// design. A diff that emits only `Changes` cannot distinguish "nothing
/// differs" from "plenty differs and I declined to say so", and a deployment
/// tool that cannot make that distinction is the one that drops your table.
/// `DropSafety` owns the second question; each family module owns the first
/// for its own kind of object; this module only composes them.
module Plan =

    /// Compare desired state against actual state.
    ///
    /// `allowDrops` defaults OFF at every call site, and deliberately. Managed
    /// schemas are inferred from the directory tree, so creating
    /// `schema/crm/` would otherwise be an implicit claim to own every object
    /// in `crm` and remove anything undeclared. Every comparable tool made the
    /// same choice: SSDT's DropObjectsNotInSource is false by default, sqldef
    /// disabled DROP by default in 2.0.0, migra requires --unsafe, and
    /// pg-schema-diff requires --allow-hazards. A removal that is not enabled
    /// is still REPORTED, as a suppression — the difference is real and the
    /// user needs to see it; what is withheld is the proposal, not the fact.
    let run (inputs: Inputs) : DiffResult =
        let policy = DropSafety.policyOf inputs
        let tables = Tables.matching inputs

        let schemas = Schemas.compare inputs
        let sequences = Sequences.compare policy inputs
        let enums = Enums.compare policy inputs
        let domains = Domains.compare policy inputs
        let grants = Grants.compare policy inputs
        let comments = Comments.compare policy inputs
        let policies = RowSecurity.comparePolicies inputs
        let extensions = Extensions.compare inputs
        let tableChanges = Tables.compare policy inputs tables
        let constraints = TableConstraints.compare policy inputs tables
        let objects = Objects.compare policy inputs
        let rows = ReferenceRows.compare inputs

        // The order families are listed in is the order their differences are
        // assembled in, and so the order statements of the same phase execute
        // in. Domains follow enums because a domain may stand on an enum that
        // arrives in this same plan.
        let differences =
            List.concat
                [ schemas.Differences
                  sequences.Differences
                  enums.Differences
                  domains.Differences
                  grants.Differences
                  comments.Differences
                  policies.Differences
                  extensions.Differences
                  tableChanges.Differences
                  constraints.Differences
                  objects.Differences
                  rows.Differences ]

        let changes =
            differences
            |> List.choose (function
                | Ok change -> Some change
                | Error _ -> None)
            |> Ordering.sort tables.Created inputs.Declarations

        let sources = PostgresDdl.sources inputs

        { Changes = changes
          Statements =
            changes
            |> List.map (fun c ->
                { Change = c
                  Sql = PostgresDdl.statement sources c })
          Suppressed =
            List.concat
                [ differences
                  |> List.choose (function
                      | Error s -> Some s
                      | Ok _ -> None)
                  constraints.Disclosures
                  objects.Disclosures
                  rows.Disclosures
                  ReferenceRows.enumTwoStep enums inputs.DataFailures
                  schemas.Disclosures
                  grants.Disclosures
                  comments.Disclosures
                  RowSecurity.disclosures inputs
                  policies.Disclosures
                  extensions.Disclosures
                  // Empty today. Listed so a disclosure added to one of these
                  // families is reported rather than silently dropped.
                  sequences.Disclosures
                  enums.Disclosures
                  domains.Disclosures
                  tableChanges.Disclosures ]
          DesiredStateComplete = policy.DesiredComplete }
