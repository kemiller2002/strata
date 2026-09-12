namespace Strata.Application

open System
open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Semantic.Wire
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
///
/// ## Absence is not deletion
///
/// `NG-006` names a schema diff that assumes absence means delete as an
/// explicit NON-GOAL, and §1437 says it directly: "Do not delete unmanaged
/// objects simply because they are not in desired state."
///
/// A drop is proposed only when ALL of these hold:
///
///   1. the object's schema is listed in the project's `managedSchemas`;
///   2. the DESIRED snapshot is `Complete` for relations — a desired state
///      missing an object because its file failed to parse must never cause
///      that object to be dropped;
///   3. the object is not extension-owned (`RK-008`).
///
/// Every difference failing any of those is reported as a `Suppression`, which
/// names the object and why Strata will not act. Silence is not an option:
/// an unreported suppression is indistinguishable from no difference at all.
module SchemaDiff =

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

    let private tables (snapshot: SchemaSnapshot) =
        snapshot.Objects
        |> List.choose (fun o ->
            match o with
            | TableObject t -> Some t
            | ViewObject _
            | RoutineObject _
            | SequenceObject _ -> None)

    let private sameName (a: QualifiedName) (b: QualifiedName) =
        QualifiedName.display a = QualifiedName.display b

    /// Is this object's schema one the project is allowed to change?
    let private isManaged (managedSchemas: string list) (name: QualifiedName) =
        match name.Schema with
        | None -> false
        | Some schema ->
            managedSchemas
            |> List.exists (fun m -> Identifier.sameName (Identifier.unquoted m) schema)

    /// Whether a grant's object lies inside the schemas the project manages.
    ///
    /// A SCHEMA grant is managed when the schema itself is managed, not when
    /// some enclosing schema is: a schema is not inside anything. Getting this
    /// wrong in the lenient direction would let Strata revoke privileges on a
    /// schema the project never claimed.
    let private isManagedGrant (managedSchemas: string list) (target: GrantTarget) =
        match GrantTarget.schema target with
        | None -> false
        | Some schema ->
            managedSchemas
            |> List.exists (fun m -> Identifier.sameName (Identifier.unquoted m) schema)

    /// Objects present in the database and absent from desired state.
    ///
    /// This is the dangerous direction and the only one that can destroy data,
    /// so every guard lives here.
    let private removals
        (allowDrops: bool)
        (managedSchemas: string list)
        (desiredComplete: bool)
        (desired: Table list)
        (actual: Table list)
        =
        actual
        |> List.filter (fun a -> not (desired |> List.exists (fun d -> sameName d.Name a.Name)))
        |> List.map (fun a ->
            if not (isManaged managedSchemas a.Name) then
                Microsoft.FSharp.Core.Error
                    { Object = a.Name
                      Reason = OutsideManagedSchemas
                      Detail =
                        "present in the database and absent from desired state, but its schema is not managed by this project" }
            elif a.Scope = ExtensionOwned then
                Microsoft.FSharp.Core.Error
                    { Object = a.Name
                      Reason = ExtensionOwnedObject
                      Detail = "owned by an extension; absence from desired state does not make it removable" }
            elif not desiredComplete then
                Microsoft.FSharp.Core.Error
                    { Object = a.Name
                      Reason = DesiredStateIncomplete
                      Detail =
                        "absent from desired state, but desired state did not load completely, so its absence is not evidence it should be dropped" }
            elif not allowDrops then
                Microsoft.FSharp.Core.Error
                    { Object = a.Name
                      Reason = DropsNotEnabled
                      Detail = "would be dropped; pass --allow-drops to propose removals" }
            else
                Ok(DropTable a.Name))

    /// Column-level differences for a table present on both sides.
    let private columnChanges
        (allowDrops: bool)
        (managedSchemas: string list)
        (desiredComplete: bool)
        (desired: Table)
        (actual: Table)
        =
        let managed = isManaged managedSchemas desired.Name

        let added =
            desired.Columns
            |> List.filter (fun d ->
                not (actual.Columns |> List.exists (fun a -> Identifier.sameName a.Name d.Name)))
            |> List.map (fun d -> Ok(AddColumn(desired.Name, d.Name)))

        let removed =
            actual.Columns
            |> List.filter (fun a ->
                not (desired.Columns |> List.exists (fun d -> Identifier.sameName d.Name a.Name)))
            |> List.map (fun a ->
                if not managed then
                    Microsoft.FSharp.Core.Error
                        { Object = desired.Name
                          Reason = OutsideManagedSchemas
                          Detail = sprintf "column '%s' is absent from desired state, but the schema is not managed" a.Name.Text }
                elif not desiredComplete then
                    Microsoft.FSharp.Core.Error
                        { Object = desired.Name
                          Reason = DesiredStateIncomplete
                          Detail =
                            sprintf
                                "column '%s' is absent from desired state, but desired state did not load completely"
                                a.Name.Text }
                elif not allowDrops then
                    Microsoft.FSharp.Core.Error
                        { Object = desired.Name
                          Reason = DropsNotEnabled
                          Detail =
                            sprintf "column '%s' would be dropped; pass --allow-drops to propose removals" a.Name.Text }
                else
                    Ok(DropColumn(desired.Name, a.Name)))

        let altered =
            desired.Columns
            |> List.choose (fun d ->
                actual.Columns
                |> List.tryFind (fun a -> Identifier.sameName a.Name d.Name)
                |> Option.bind (fun a ->
                    let desiredType = QualifiedName.display d.Type.TypeName
                    let actualType = QualifiedName.display a.Type.TypeName

                    if desiredType <> actualType then
                        Some(Ok(AlterColumnType(desired.Name, d.Name, desiredType)))
                    elif d.Type.IsNullable <> a.Type.IsNullable then
                        // The change vocabulary has no nullability case. Saying
                        // so is honest; inventing one that the gate would judge
                        // by the wrong rules is not.
                        Some(
                            Ok(
                                UnclassifiedChange(
                                    sprintf
                                        "%s.%s nullability differs: desired %b, actual %b"
                                        (QualifiedName.display desired.Name)
                                        d.Name.Text
                                        d.Type.IsNullable
                                        a.Type.IsNullable)))
                    else
                        None))

        added @ removed @ altered

    /// Schemas a project declares objects in that the database does not have.
    ///
    /// `existing` is `None` when the schema list could not be read, and that is
    /// not an empty list: a caller that cannot see the schemas must not
    /// conclude one is missing and propose creating it. It is disclosed instead.
    ///
    /// Only schemas something is actually DECLARED in are created. A directory
    /// that contributes a managed-schema name but no loaded object is a schema
    /// the project has not shown a use for, and creating it would be acting on
    /// a name rather than on a declaration.
    ///
    /// There is no counterpart for removal, deliberately. A schema is a
    /// container: dropping one takes everything inside it, including objects
    /// the project never declared and so never claimed.
    let private schemaChanges (existing: string list option) (declaredIn: string list) =
        match existing with
        | None ->
            [],
            (if List.isEmpty declaredIn then
                 []
             else
                 [ { Object = QualifiedName.unqualified (Identifier.unquoted "(schemas)")
                     Reason = NotCompared
                     Detail =
                       "the database's schema list could not be read, so whether the declared schemas exist was NOT checked" } ])
        | Some present ->
            let folded = present |> List.map (fun s -> s.ToLowerInvariant())

            declaredIn
            |> List.filter (fun d -> not (List.contains (d.ToLowerInvariant()) folded))
            |> List.map (fun d -> Ok(CreateSchema(Identifier.unquoted d))),
            []

    /// Sequences, compared on every property the model carries.
    ///
    /// Not by presence alone, as views and routines are: a sequence's increment
    /// and bounds are structure, they are declared in the file, and the catalog
    /// reports them exactly. There is nothing here that cannot be compared —
    /// except the CURRENT VALUE, which is not in the model at all because it is
    /// data, changes on every `nextval`, and "correcting" it would hand out a
    /// number twice.
    let private sequenceChanges
        (allowDrops: bool)
        (managedSchemas: string list)
        (desiredComplete: bool)
        (desired: SchemaObject list)
        (actual: SchemaObject list)
        =
        let sequences objects =
            objects
            |> List.choose (fun o ->
                match o with
                | SequenceObject sq -> Some sq
                | _ -> None)

        let declared = sequences desired
        let deployed = sequences actual

        let shape (sq: Sequence) =
            sprintf
                "%s|%d|%d|%d|%d|%b"
                sq.DataType
                sq.Increment
                sq.MinValue
                sq.MaxValue
                sq.Cache
                sq.Cycle

        let created =
            declared
            |> List.filter (fun d -> not (deployed |> List.exists (fun a -> sameName a.Name d.Name)))
            |> List.map (fun d -> Ok(CreateSequence d.Name))

        // START is excluded from `shape` deliberately. It only takes effect
        // when the sequence is created or explicitly restarted, so a deployed
        // sequence that has moved past its start is not a difference — and
        // proposing one would mean resetting a live counter.
        let altered =
            declared
            |> List.choose (fun d ->
                deployed
                |> List.tryFind (fun a -> sameName a.Name d.Name)
                |> Option.bind (fun a -> if shape a <> shape d then Some(Ok(AlterSequence d.Name)) else None))

        let dropped =
            deployed
            |> List.filter (fun a -> not (declared |> List.exists (fun d -> sameName d.Name a.Name)))
            |> List.map (fun a ->
                if not (isManaged managedSchemas a.Name) then
                    Microsoft.FSharp.Core.Error
                        { Object = a.Name
                          Reason = OutsideManagedSchemas
                          Detail = "a sequence outside the project's managed schemas" }
                elif a.Scope = ExtensionOwned then
                    Microsoft.FSharp.Core.Error
                        { Object = a.Name
                          Reason = ExtensionOwnedObject
                          Detail = "an extension owns this sequence" }
                elif not desiredComplete then
                    Microsoft.FSharp.Core.Error
                        { Object = a.Name
                          Reason = DesiredStateIncomplete
                          Detail = "absent from desired state, but desired state did not load completely" }
                elif not allowDrops then
                    Microsoft.FSharp.Core.Error
                        { Object = a.Name
                          Reason = DropsNotEnabled
                          Detail = "this sequence would be dropped; pass --allow-drops" }
                else
                    Ok(DropSequence a.Name))

        created @ altered @ dropped

    /// Privileges, compared per object AND per grantee.
    ///
    /// ## Why the ownership rule is finer here than anywhere else
    ///
    /// Indexes and triggers use a coarse rule: declare one on a table and the
    /// project owns them all. That is right when the cost of a wrong removal is
    /// a rebuild. It is wrong for privileges, where a project managing
    /// `app_user` would silently revoke the replication role, the DBA's access
    /// and the monitoring user's along with it.
    ///
    /// So declaring a grant for a grantee on an object takes ownership of THAT
    /// GRANTEE's privileges on THAT object, and of nothing else. A grantee the
    /// project never names is left alone and disclosed — drift Strata can see
    /// and will not act on.
    ///
    /// `actual` is `None` when the ACLs could not be read, which is not the
    /// same as nobody holding anything: that case proposes nothing at all.
    let private grantChanges
        (allowDrops: bool)
        (managedSchemas: string list)
        (desiredComplete: bool)
        (declared: Grant list)
        (actual: Grant list option) =

        match actual with
        | None ->
            [],
            (if List.isEmpty declared then
                 []
             else
                 [ { Object = QualifiedName.unqualified (Identifier.unquoted "(grants)")
                     Reason = NotCompared
                     Detail = "the database's privileges could not be read, so declared grants were NOT compared" } ])
        | Some deployed ->

        // The kind is part of the key. A schema named `orders` and a table named
        // `orders` are two different things to grant on, and a key that could
        // not tell them apart would match a declared schema grant against a
        // deployed table grant and propose revoking privileges nobody declared.
        let key (g: Grant) = GrantTarget.key g.Target, g.Grantee.ToLowerInvariant()

        // What a declaration takes ownership of. Coarser than the key: a column
        // grant and a table grant on the same relation share a scope, so
        // declaring `GRANT SELECT (id) ON t TO r` claims r's table-wide
        // privileges on `t` too. Without that the table-wide SELECT would be
        // left in place as something the project never mentioned, and r would
        // go on reading every column — the column grant would add access and
        // narrow none, which is the opposite of what the file asked for.
        //
        // The grantee is still part of the scope, so this does not widen
        // ownership across grantees: the replication role is untouched.
        let scope (g: Grant) = GrantTarget.ownershipScope g.Target, g.Grantee.ToLowerInvariant()

        let deployedByKey = deployed |> List.map (fun g -> key g, g) |> Map.ofList
        let declaredByKey = declared |> List.map (fun g -> key g, g) |> Map.ofList
        let claimedScopes = declared |> List.map scope |> Set.ofList

        // Every key the project declares, plus every deployed key inside a scope
        // it claims. The second half is what catches a privilege held at a
        // different key in the same scope — the table-wide grant standing behind
        // a declared column grant — which iterating over `declared` alone never
        // reaches.
        let comparable =
            (declared |> List.map (fun g -> key g, g.Target))
            @ (deployed
               |> List.filter (fun a -> Set.contains (scope a) claimedScopes)
               |> List.map (fun g -> key g, g.Target))
            |> List.distinctBy fst

        let changes =
            comparable
            |> List.collect (fun (k, target) ->
                let grantee =
                    Map.tryFind k declaredByKey
                    |> Option.orElse (Map.tryFind k deployedByKey)
                    |> Option.map (fun g -> g.Grantee)
                    |> Option.defaultValue ""

                let wanted =
                    Map.tryFind k declaredByKey
                    |> Option.map (fun g -> g.Privileges)
                    |> Option.defaultValue []

                let held =
                    Map.tryFind k deployedByKey
                    |> Option.map (fun g -> g.Privileges)
                    |> Option.defaultValue []

                let d = {| Target = target; Grantee = grantee |}

                let missing = wanted |> List.filter (fun p -> not (List.contains p held))
                let extra = held |> List.filter (fun p -> not (List.contains p wanted))

                [ if not (List.isEmpty missing) then
                      Ok(GrantPrivileges(d.Target, d.Grantee, missing))
                  if not (List.isEmpty extra) then
                      if not (isManagedGrant managedSchemas d.Target) then
                          Microsoft.FSharp.Core.Error
                              { Object = GrantTarget.name d.Target
                                Reason = OutsideManagedSchemas
                                Detail = sprintf "privileges held by %s lie outside the managed schemas" d.Grantee }
                      elif not desiredComplete then
                          Microsoft.FSharp.Core.Error
                              { Object = GrantTarget.name d.Target
                                Reason = DesiredStateIncomplete
                                Detail =
                                  sprintf
                                      "%s holds %s beyond what is declared, but desired state did not load completely"
                                      d.Grantee
                                      (String.concat ", " extra) }
                      elif not allowDrops then
                          Microsoft.FSharp.Core.Error
                              { Object = GrantTarget.name d.Target
                                Reason = DropsNotEnabled
                                Detail =
                                  sprintf
                                      "%s holds %s beyond what is declared; pass --allow-drops to revoke"
                                      d.Grantee
                                      (String.concat ", " extra) }
                      else
                          Ok(RevokePrivileges(d.Target, d.Grantee, extra)) ])

        // Grantees the project never mentions. Reported, never revoked — this
        // is the whole point of the per-grantee rule, so it is stated in the
        // output rather than left to the reader to infer from silence.
        let unclaimed =
            deployed
            |> List.filter (fun a -> not (Set.contains (scope a) claimedScopes))
            |> List.groupBy (fun a -> GrantTarget.key a.Target)
            |> List.map (fun (_, gs) ->
                { Object = GrantTarget.name (List.head gs).Target
                  Reason = NotModelled
                  Detail =
                    sprintf
                        "%s holds privileges here and the project declares none for them, so they were NOT compared and nothing is revoked: %s"
                        (gs |> List.map (fun g -> g.Grantee) |> List.distinct |> List.sort |> String.concat ", ")
                        (gs
                         |> List.map (fun g -> sprintf "%s=%s" g.Grantee (String.concat "," g.Privileges))
                         |> String.concat "; ") })

        // Privileges held WITH GRANT OPTION.
        //
        // These are NOT a difference: the privilege itself matches what the
        // file declares, so nothing is proposed and the project converges. What
        // does not match is the power to pass the privilege on, which the model
        // does not hold — a file cannot ask for it (the loader refuses `WITH
        // GRANT OPTION`) and so the diff can never take it away.
        //
        // Reported for exactly that reason. "r holds SELECT" and "r holds
        // SELECT and can give it to anyone" are different states (ER-008), and
        // silently treating the second as the first would have Strata report a
        // converged project while a grantee widens access on its own.
        let grantable =
            deployed
            |> List.filter (fun a -> not (List.isEmpty a.Grantable))
            |> List.map (fun a ->
                { Object = GrantTarget.name a.Target
                  Reason = NotModelled
                  Detail =
                    sprintf
                        "%s holds %s WITH GRANT OPTION and can pass %s on to others; Strata does not model the grant option, so this was NOT compared and nothing removes it"
                        a.Grantee
                        (String.concat ", " a.Grantable)
                        (if List.length a.Grantable = 1 then "it" else "them") })

        changes, unclaimed @ grantable

    /// Compare desired state against actual state.
    // ---- DDL emission -------------------------------------------------------

    /// Render an identifier for execution.
    ///
    /// Always quoted. A name from the catalog is already case-folded, so
    /// quoting it changes nothing; a name that was quoted in the file keeps the
    /// case it needs. Emitting unquoted would silently fold a mixed-case
    /// identifier into a different object.
    let private quote (identifier: Identifier) =
        "\"" + identifier.Text.Replace("\"", "\"\"") + "\""

    let private quoteName (name: QualifiedName) =
        match name.Schema with
        | Some schema -> sprintf "%s.%s" (quote schema) (quote name.Name)
        | None -> quote name.Name

    /// How a grant's object is SPELLED in `GRANT` and `REVOKE`.
    ///
    /// Not `quoteName`: the three kinds take three different statements.
    /// `GRANT USAGE ON app` grants on a TABLE called `app`, which is a
    /// different object from the schema of that name and may not exist at all;
    /// `GRANT EXECUTE ON f` without a signature resolves against whatever is
    /// deployed rather than what the file named.
    ///
    /// Argument types are emitted unquoted because they are type names rather
    /// than identifiers — `integer`, `character varying`, `timestamp without
    /// time zone` — and quoting one would name a type that does not exist.
    let private grantTargetSql (target: GrantTarget) =
        match target with
        | GrantTarget.Relation name -> quoteName name
        | GrantTarget.Schema schema -> sprintf "SCHEMA %s" (quote schema)
        | GrantTarget.Routine (name, arguments) ->
            sprintf "ROUTINE %s(%s)" (quoteName name) (String.concat ", " arguments)
        // The columns go on the PRIVILEGE, not the object: the SQL is
        // `GRANT SELECT (id) ON t`, not `GRANT SELECT ON t (id)`. So a column
        // target renders as the table here and the caller places the column
        // list — the one target whose SQL is not a substitution of this string.
        | GrantTarget.RelationColumn (name, _) -> quoteName name

    /// The privilege list for a `GRANT` or `REVOKE`.
    ///
    /// A column restriction attaches to each PRIVILEGE, not to the object:
    /// `GRANT SELECT (id), UPDATE (id) ON t`. Writing the column once after the
    /// table is not valid SQL, and writing it after only the first privilege
    /// would silently grant the rest table-wide — more access than asked for,
    /// which is the defect this whole area exists to prevent.
    let private privilegesSql (target: GrantTarget) (privileges: string list) =
        match target with
        | GrantTarget.RelationColumn (_, column) ->
            privileges |> List.map (fun p -> sprintf "%s (%s)" p (quote column)) |> String.concat ", "
        | _ -> String.concat ", " privileges

    /// A column as it appears in DDL.
    ///
    /// The default EXPRESSION is not carried by the semantic model — only the
    /// fact that one exists — so a column with a default cannot be emitted
    /// without inventing its value. Callers handle that by refusing to emit,
    /// not by dropping the default.
    let private columnDdl (column: Column) =
        sprintf
            "%s %s%s"
            (quote column.Name)
            (QualifiedName.display column.Type.TypeName)
            (if column.Type.IsNullable then "" else " NOT NULL")

    /// Rewrite a leading `CREATE X` into `CREATE OR REPLACE X`.
    ///
    /// Only the keyword is touched, so the author's body stays byte-for-byte —
    /// which is the whole reason the declaring file is used rather than a
    /// reconstruction. A shape this does not recognise (a materialized view,
    /// which cannot be replaced in place) yields None and stops the apply
    /// rather than guessing.
    let private replaceKeyword (keyword: string) (text: string) =
        let trimmed = text.TrimStart()

        if trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase) then
            Some("CREATE OR REPLACE" + trimmed.Substring("CREATE".Length))
        else
            None

    /// DDL for one change, or `None` when Strata cannot write it faithfully.
    ///
    /// Every `None` here is a deliberate refusal, and each is a gap in the
    /// semantic model rather than an oversight: the model carries that a
    /// default or a check constraint EXISTS but not its expression, because
    /// the catalog reports those already normalised and storing a
    /// half-understood expression would be worse than storing none. Emitting a
    /// table without its checks would create an object that differs from what
    /// the project declared while reporting success.
    let private emit
        (declarations: (QualifiedName * string) list)
        (triggerDeclarations: ((QualifiedName * Identifier) * string) list)
        (data: ResolvedData list)
        (desiredSequences: Sequence list)
        /// Declared check expressions as the SERVER renders them. A check's
        /// expression is not recoverable from the parse tree, so this is the
        /// only source for one.
        (normalised: NormalisedTable list)
        (desired: Table list)
        (change: Change)
        : string option =
        let desiredTable name = desired |> List.tryFind (fun t -> sameName t.Name name)

        let declaredText name =
            declarations
            |> List.tryPick (fun (declared, text) -> if sameName declared name then Some text else None)

        let declaredTriggerText table trigger =
            triggerDeclarations
            |> List.tryPick (fun ((t, n), text) ->
                if sameName t table && Identifier.sameName n trigger then Some text else None)

        // A reference row is written from the literals the SERVER produced for
        // the declared row, via quote_nullable. Strata does not re-render a
        // value it never interpreted: what goes into the database is what came
        // back out of the shadow table.
        let granteeSql (grantee: string) =
            if grantee.ToUpperInvariant() = "PUBLIC" then "PUBLIC"
            else quote (Identifier.unquoted grantee)

        let desiredSequence name =
            desiredSequences |> List.tryFind (fun (sq: Sequence) -> sameName sq.Name name)

        let declaredRow table key =
            data
            |> List.tryFind (fun d -> d.Table = QualifiedName.display table)
            |> Option.bind (fun d ->
                d.Declared
                |> List.tryFind (fun r -> r.Key = key)
                |> Option.map (fun r -> d, r))

        let columnOf (d: ResolvedData) (row: ResolvedRow) (column: string) =
            d.Columns
            |> List.tryFindIndex (fun c -> c.ToLowerInvariant() = column.ToLowerInvariant())
            |> Option.bind (fun i -> row.Literals |> List.tryItem i)

        match change with
        // Most unclassified changes cannot be written — they describe a
        // difference rather than name an object. A declared view or routine is
        // the exception: the change names it, and the file holds exactly the
        // DDL that creates it. Matching on the message is unpleasant, but the
        // alternative is inventing CreateView and CreateFunction cases that the
        // gate would then judge by rules written for tables.
        | UnclassifiedChange _ -> None

        // A view or routine is created by executing the file that declares it.
        // There is nothing to reconstruct: the definition text is not
        // recoverable from the parse tree, so a project without the file cannot
        // create one, and says so by emitting nothing.
        | CreateView name
        | CreateRoutine name -> declaredText name

        // The declaring file says CREATE VIEW; replacing needs CREATE OR
        // REPLACE VIEW. Rewriting only the leading keyword keeps the author's
        // body byte-for-byte, which is the whole reason the file is used.
        | ReplaceView name -> declaredText name |> Option.bind (replaceKeyword "CREATE VIEW")

        | ReplaceRoutine name ->
            declaredText name
            |> Option.bind (fun text ->
                match replaceKeyword "CREATE FUNCTION" text with
                | Some rewritten -> Some rewritten
                | None -> replaceKeyword "CREATE PROCEDURE" text)

        // The declaring file holds exactly the DDL the author wrote, defaults
        // and check expressions included. Reconstruction below is the fallback
        // for a snapshot built without files, and it still refuses whatever it
        // cannot render faithfully.
        // Bare CREATE SCHEMA, with no AUTHORIZATION: the connected role owns
        // it, and that is the same role that will create everything in it.
        | CreateSchema schema -> Some(sprintf "CREATE SCHEMA %s" (quote schema))

        // Written from the model rather than the declaring file, because every
        // property is carried and none of them is an expression. START is what
        // the file asked for; where the sequence already exists, ALTER leaves
        // the current value alone, which is the point — RESTART would hand out
        // a number twice.
        | CreateSequence name
        | AlterSequence name ->
            desiredSequence name
            |> Option.map (fun sq ->
                let body =
                    sprintf
                        "AS %s INCREMENT BY %d MINVALUE %d MAXVALUE %d CACHE %d%s"
                        sq.DataType
                        sq.Increment
                        sq.MinValue
                        sq.MaxValue
                        sq.Cache
                        (if sq.Cycle then " CYCLE" else " NO CYCLE")

                match change with
                | CreateSequence _ ->
                    sprintf "CREATE SEQUENCE %s %s START WITH %d" (quoteName name) body sq.Start
                | _ -> sprintf "ALTER SEQUENCE %s %s" (quoteName name) body)

        | DropSequence name -> Some(sprintf "DROP SEQUENCE %s" (quoteName name))

        // PUBLIC is a keyword, not a role name, so it is never quoted — quoting
        // it would name a role called "PUBLIC" that almost certainly does not
        // exist. Privileges are keywords too and are already uppercase.
        | GrantPrivileges (target, grantee, privileges) ->
            Some(
                sprintf
                    "GRANT %s ON %s TO %s"
                    (privilegesSql target privileges)
                    (grantTargetSql target)
                    (granteeSql grantee))

        | RevokePrivileges (target, grantee, privileges) ->
            Some(
                sprintf
                    "REVOKE %s ON %s FROM %s"
                    (privilegesSql target privileges)
                    (grantTargetSql target)
                    (granteeSql grantee))

        | CreateTable name when (declaredText name).IsSome -> declaredText name

        | CreateTable name ->
            match desiredTable name with
            | None -> None
            | Some table when not (List.isEmpty table.CheckConstraints) -> None
            | Some table when table.Columns |> List.exists (fun c -> c.HasDefault) -> None
            | Some table ->
                let columns = table.Columns |> List.map columnDdl

                // An unnamed constraint is written WITHOUT a CONSTRAINT clause,
                // so the server assigns the name it would have assigned had the
                // file been executed directly. Inventing one here would create
                // an object the project did not ask for.
                let namedAs (name: ConstraintName) =
                    match name with
                    | Some n -> sprintf "CONSTRAINT %s " (quote n)
                    | None -> ""

                let primaryKey =
                    match table.PrimaryKey with
                    | Some pk ->
                        [ sprintf
                            "%sPRIMARY KEY (%s)"
                            (namedAs pk.ConstraintName)
                            (pk.Columns |> List.map quote |> String.concat ", ") ]
                    | None -> []

                let uniques =
                    table.UniqueConstraints
                    |> List.map (fun u ->
                        sprintf
                            "%sUNIQUE (%s)"
                            (namedAs u.ConstraintName)
                            (u.Columns |> List.map quote |> String.concat ", "))

                let foreignKeys =
                    table.ForeignKeys
                    |> List.map (fun f ->
                        sprintf
                            "%sFOREIGN KEY (%s) REFERENCES %s (%s)"
                            (namedAs f.ConstraintName)
                            (f.Columns |> List.map quote |> String.concat ", ")
                            (quoteName f.ReferencedTable)
                            (f.ReferencedColumns |> List.map quote |> String.concat ", "))

                Some(
                    sprintf
                        "CREATE TABLE %s (\n    %s\n)"
                        (quoteName name)
                        (columns @ primaryKey @ uniques @ foreignKeys |> String.concat ",\n    "))

        | RenameTable (from, to') ->
            Some(sprintf "ALTER TABLE %s RENAME TO %s" (quoteName from) (quote to'.Name))

        | RenameColumn (table, from, to') ->
            Some(sprintf "ALTER TABLE %s RENAME COLUMN %s TO %s" (quoteName table) (quote from) (quote to'))

        | CreateIndex (table, index) ->
            desiredTable table
            |> Option.bind (fun t -> t.Indexes |> List.tryFind (fun i -> Identifier.sameName i.Name index))
            // A partial index's predicate is not carried, so one cannot be
            // created faithfully — building it without the WHERE would index
            // every row and silently differ from what was declared.
            |> Option.filter (fun i -> i.Predicate.IsNone)
            |> Option.map (fun i ->
                sprintf
                    "CREATE %sINDEX %s ON %s (%s)"
                    (if i.IsUnique then "UNIQUE " else "")
                    (quote i.Name)
                    (quoteName table)
                    (i.Columns |> List.map quote |> String.concat ", "))

        | DropIndex (table, index) ->
            // An index is dropped by name in its schema, not via its table.
            Some(
                sprintf
                    "DROP INDEX %s"
                    (match table.Schema with
                     | Some schema -> sprintf "%s.%s" (quote schema) (quote index)
                     | None -> quote index))

        | AddColumn (table, column) ->
            desiredTable table
            |> Option.bind (fun t -> t.Columns |> List.tryFind (fun c -> Identifier.sameName c.Name column))
            // A column whose default expression Strata does not carry cannot be
            // added faithfully: emitting it without the default would populate
            // existing rows with NULL instead of the declared value.
            |> Option.filter (fun c -> not c.HasDefault)
            |> Option.map (fun c -> sprintf "ALTER TABLE %s ADD COLUMN %s" (quoteName table) (columnDdl c))

        | AlterColumnType (table, column, newType) ->
            Some(
                sprintf
                    "ALTER TABLE %s ALTER COLUMN %s TYPE %s"
                    (quoteName table)
                    (quote column)
                    newType)

        | DropColumn (table, column) ->
            Some(sprintf "ALTER TABLE %s DROP COLUMN %s" (quoteName table) (quote column))

        | DropTable table -> Some(sprintf "DROP TABLE %s" (quoteName table))

        | TruncateTable table -> Some(sprintf "TRUNCATE TABLE %s" (quoteName table))

        | InsertRow (table, key) ->
            declaredRow table key
            |> Option.map (fun (d, row) ->
                sprintf
                    "INSERT INTO %s (%s) VALUES (%s)"
                    (quoteName table)
                    (d.Columns |> List.map (fun c -> quote (Identifier.unquoted c)) |> String.concat ", ")
                    (row.Literals |> String.concat ", "))

        | UpdateRow (table, key) ->
            declaredRow table key
            |> Option.bind (fun (d, row) ->
                // The key identifies the row and is never assigned: an UPDATE
                // that rewrote its own WHERE clause would be a different row.
                let isKey (c: string) =
                    d.KeyColumns |> List.exists (fun k -> k.ToLowerInvariant() = c.ToLowerInvariant())

                let assignments =
                    d.Columns
                    |> List.filter (fun c -> not (isKey c))
                    |> List.choose (fun c ->
                        columnOf d row c
                        |> Option.map (fun v -> sprintf "%s = %s" (quote (Identifier.unquoted c)) v))

                let predicate =
                    d.KeyColumns
                    |> List.map (fun k ->
                        columnOf d row k
                        |> Option.map (fun v -> sprintf "%s = %s" (quote (Identifier.unquoted k)) v))

                // Every declared column is a key column, so there is nothing to
                // set. That is a row that either exists or does not; there is
                // no update it could need, and emitting `SET` with no
                // assignments would not parse.
                if List.isEmpty assignments || predicate |> List.exists Option.isNone then
                    None
                else
                    Some(
                        sprintf
                            "UPDATE %s SET %s WHERE %s"
                            (quoteName table)
                            (assignments |> String.concat ", ")
                            (predicate |> List.choose id |> String.concat " AND ")))

        // A trigger is created by executing the file that declares it, for a
        // stronger reason than a view is. A `WHEN` clause and an `UPDATE OF`
        // column list are not in the model, so a reconstruction would create a
        // trigger that fires MORE OFTEN than the file asked for — and it would
        // report success while doing it.
        | CreateTrigger (table, trigger) -> declaredTriggerText table trigger

        // Drop and recreate, not CREATE OR REPLACE TRIGGER.
        //
        // Both reach the same state, but `CREATE OR REPLACE TRIGGER` needs
        // PostgreSQL 14, and this form needs nothing. The two statements are
        // atomic regardless: `Execution.apply` runs the whole plan in one
        // transaction, and PostgreSQL rolls DDL back like anything else.
        | ReplaceTrigger (table, trigger) ->
            declaredTriggerText table trigger
            |> Option.map (fun text ->
                sprintf "DROP TRIGGER %s ON %s;\n%s" (quote trigger) (quoteName table) text)

        // A trigger is dropped by naming it AND its table: trigger names are
        // scoped to the table, not to the schema.
        | DropTrigger (table, trigger) ->
            Some(sprintf "DROP TRIGGER %s ON %s" (quote trigger) (quoteName table))

        | AddConstraint (table, name, kind, columns) ->
            let named =
                match name with
                | Some n -> sprintf "CONSTRAINT %s " (quote n)
                // Written without a CONSTRAINT clause so the server assigns the
                // name it would have assigned had the declaring file been run
                // directly. Inventing one creates an object nobody asked for.
                | None -> ""

            let columnList = columns |> List.map quote |> String.concat ", "

            let body =
                match kind with
                | ConstraintKind.PrimaryKey -> Some(sprintf "PRIMARY KEY (%s)" columnList)
                | ConstraintKind.Unique -> Some(sprintf "UNIQUE (%s)" columnList)
                | ConstraintKind.ForeignKey ->
                    // The referenced table and columns are not on the change,
                    // so they come from the declared table — matched on the
                    // columns, which is what identifies an unnamed one.
                    desiredTable table
                    |> Option.bind (fun t ->
                        t.ForeignKeys
                        |> List.tryFind (fun f ->
                            let folded (cs: Identifier list) = cs |> List.map Identifier.folded
                            folded f.Columns = folded columns))
                    |> Option.map (fun f ->
                        sprintf
                            "FOREIGN KEY (%s) REFERENCES %s (%s)"
                            columnList
                            (quoteName f.ReferencedTable)
                            (f.ReferencedColumns |> List.map quote |> String.concat ", "))
                | ConstraintKind.Check ->
                    // A check's expression exists only as the server's
                    // rendering of the declared DDL. Without that rendering
                    // there is nothing to write, and writing the constraint
                    // without its expression is not an option.
                    match name with
                    | None -> None
                    | Some n ->
                        normalised
                        |> List.tryFind (fun x -> x.Table = QualifiedName.display table)
                        |> Option.bind (fun x ->
                            x.Checks
                            |> List.tryPick (fun (checkName, definition) ->
                                if Identifier.sameName (Identifier.unquoted checkName) n then Some definition
                                else None))
                        // pg_get_constraintdef already renders the full
                        // `CHECK (...)`, so it is used as the body rather than
                        // wrapped again.
                        |> Option.map (fun definition -> definition.Trim())

            body |> Option.map (fun b -> sprintf "ALTER TABLE %s ADD %s%s" (quoteName table) named b)

        // Dropped by name, which is the only handle a constraint has. The
        // matching index goes with a primary key or unique constraint
        // automatically; PostgreSQL will not let one be dropped separately.
        | DropConstraint (table, name, _) ->
            Some(sprintf "ALTER TABLE %s DROP CONSTRAINT %s" (quoteName table) (quote name))

    /// How many other tables being created this one must wait for.
    ///
    /// A foreign key needs its referenced table to exist first. `orderKey`
    /// below puts every CreateTable at the same rank, which is fine until two
    /// of them reference each other's table — and then the order is whatever
    /// the file names sorted to. Found by a live round-trip: a project
    /// declaring `order_audit` (which references `orders`) and `orders` could
    /// not be applied AT ALL. The whole plan is one transaction, so a wrong
    /// order does not half-apply; it fails outright with 42P01 and rolls back.
    ///
    /// Only tables being CREATED count. A reference to a table that already
    /// exists imposes no order, and neither does a self-reference: PostgreSQL
    /// accepts a foreign key to the table being defined.
    ///
    /// A cycle has no satisfying order — two tables that reference each other
    /// cannot both be created first — so `cyclicCreations` below reports it
    /// instead, and this returns 0 for those rather than recursing.
    let private creationDepths (creating: Table list) =
        let byName =
            creating |> List.map (fun t -> QualifiedName.display t.Name, t) |> Map.ofList

        let parentsOf (name: string) (t: Table) =
            t.ForeignKeys
            |> List.map (fun f -> QualifiedName.display f.ReferencedTable)
            |> List.filter (fun p -> p <> name && Map.containsKey p byName)
            |> List.distinct

        let rec depth (seen: Set<string>) (name: string) =
            if Set.contains name seen then 0
            else
                match Map.tryFind name byName with
                | None -> 0
                | Some t ->
                    match parentsOf name t with
                    | [] -> 0
                    | parents -> 1 + (parents |> List.map (depth (Set.add name seen)) |> List.max)

        byName |> Map.map (fun name _ -> depth Set.empty name)

    /// Tables being created whose foreign keys form a cycle among themselves.
    ///
    /// No order of plain CREATE TABLE statements satisfies one. Strata says so
    /// rather than emitting an order it knows cannot execute: the creations are
    /// withheld and the reason is reported, which is the same choice every
    /// other thing it cannot do faithfully gets.
    let private cyclicCreations (creating: Table list) =
        let byName =
            creating |> List.map (fun t -> QualifiedName.display t.Name, t) |> Map.ofList

        let parentsOf (name: string) =
            match Map.tryFind name byName with
            | None -> []
            | Some t ->
                t.ForeignKeys
                |> List.map (fun f -> QualifiedName.display f.ReferencedTable)
                |> List.filter (fun p -> p <> name && Map.containsKey p byName)
                |> List.distinct

        let rec reaches (seen: Set<string>) (target: string) (name: string) =
            parentsOf name
            |> List.exists (fun p ->
                p = target || (not (Set.contains p seen) && reaches (Set.add p seen) target p))

        byName
        |> Map.toList
        |> List.map fst
        |> List.filter (fun name -> reaches (Set.singleton name) name name)

    /// Execution order.
    ///
    /// Creates before the things that reference them, drops after the things
    /// that depend on them, and columns dropped before their table so a
    /// statement never runs against an object the previous one removed.
    let private orderKey (change: Change) =
        match change with
        // Renames first: they preserve data, and every later statement refers
        // to the NEW name. Column renames precede the table rename so both can
        // name the table as it stands before either runs.
        // First, and alone at its rank: nothing else can run until the
        // namespace its objects live in exists.
        | CreateSchema _ -> 0
        // After the schema that holds them, before any table whose column
        // DEFAULT draws from one.
        | CreateSequence _ -> 1
        | AlterSequence _ -> 1
        | RenameColumn _ -> 2
        | RenameTable _ -> 3
        | CreateTable _ -> 3
        | AddColumn _ -> 4
        | AlterColumnType _ -> 5
        // After the columns they cover exist, before the drops.
        | CreateIndex _ -> 5
        | DropIndex _ -> 5
        | AddConstraint _ -> 5
        // One step ahead of the adds, because a constraint whose definition
        // changed is dropped and re-added under the SAME name: the add would
        // fail on a name that is still taken.
        | DropConstraint _ -> 4
        // Views and routines reference tables and columns, so they come after
        // every table change that might create what they read.
        | CreateView _ -> 6
        | ReplaceView _ -> 6
        | ReplaceRoutine _ -> 6
        | CreateRoutine _ -> 6
        // After routines, and strictly after: a trigger names the function it
        // executes, and PostgreSQL rejects a CREATE TRIGGER whose function does
        // not exist yet. Sharing key 4 with CreateRoutine would not be enough —
        // the sort is stable, and trigger changes are assembled before routine
        // ones, so they would run first.
        | CreateTrigger _ -> 7
        | ReplaceTrigger _ -> 7
        | DropTrigger _ -> 7
        // After the table, its columns and its constraints exist, and after
        // triggers: a trigger on a reference table should see the rows arrive
        // the same way it would see any other write.
        // After every object they name exists — and revokes STRICTLY before
        // grants, which is not tidiness.
        //
        // `REVOKE SELECT ON t FROM r` removes r's column privileges on `t` as
        // well as the table-wide one: PostgreSQL treats revoking a table
        // privilege as revoking the equivalent column privileges. So a plan that
        // grants `SELECT (id, email)` and then revokes the standing table-wide
        // `SELECT` ends with the role holding NOTHING — the revoke wipes the
        // grants that just ran.
        //
        // That is the ordinary shape of narrowing a table grant to a column
        // grant, which is the main thing column privileges are for. Found by a
        // live round-trip: the apply reported three statements ok and left the
        // role unable to read the columns the file granted, and the re-plan did
        // not converge. Both ranks sat at 8 and the sort is stable, so the
        // grants were assembled first and ran first.
        | RevokePrivileges _ -> 8
        | GrantPrivileges _ -> 9
        | InsertRow _ -> 10
        | UpdateRow _ -> 10
        | TruncateTable _ -> 11
        | DropColumn _ -> 12
        | DropSequence _ -> 13
        | DropTable _ -> 13
        | UnclassifiedChange _ -> 14

    /// Constraint and default differences for a table present on both sides.
    ///
    /// These were not compared at all until `EV-STRATA-2026-D3A8`, which found
    /// `plan` reporting "already matches desired state" for a table whose
    /// foreign key, check constraint and column default all differed. The
    /// model carried every one of those facts on both sides; nothing looked at
    /// them. A difference nobody compared rendered identically to no
    /// difference, which is the exact collapse `ER-008` exists to forbid.
    ///
    /// What can be compared faithfully is compared. What cannot is reported as
    /// `NotCompared` — never passed over.
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


    let private constraintChanges
        (allowDrops: bool)
        (desiredComplete: bool)
        (indexesDeclared: bool)
        (triggersDeclared: bool)
        (normalised: NormalisedTable list)
        (desired: Table)
        (actual: Table)
        =
        let rendered =
            normalised |> List.tryFind (fun n -> n.Table = QualifiedName.display desired.Name)

        // A serial column's default names its SEQUENCE, and the shadow table's
        // sequence is named after the shadow table — so the two renderings can
        // never match however identical the declarations are. The type already
        // carries what serial means, so these are excluded from comparison
        // rather than reported as a permanent difference.
        let isSequenceDefault (expression: string) =
            expression.TrimStart().StartsWith("nextval(", StringComparison.OrdinalIgnoreCase)
        let columnList (columns: Identifier list) =
            columns |> List.map (fun c -> Identifier.folded c) |> String.concat ","

        let named (name: Identifier) = Identifier.folded name

        /// One constraint, reduced to the two things a comparison can use.
        ///
        /// `Name` is `None` for a constraint the declaring file did not name.
        /// `Definition` is what it does — its columns, or its target — which is
        /// the only handle an unnamed one has.
        let entry (name: ConstraintName) (definition: string) (columns: Identifier list) =
            (name |> Option.map named), definition, columns

        /// Compare two sets of constraints.
        ///
        /// A NAMED declared constraint is matched by name, because a name is
        /// the identity the catalog and the file agree on and the project asked
        /// for that specific name. An UNNAMED one is matched by definition: the
        /// file asked for a constraint that does this, and did not care what
        /// the server calls it. Matching an unnamed one by a fabricated name is
        /// what made every project using `REFERENCES u (id)` inline
        /// unconvergeable — the declared side said `foreign_key` forever and
        /// the catalog said `t_a_fkey` forever.
        ///
        /// Names are claimed first so a name match always wins over a
        /// definition match: a project that named a constraint gets that
        /// constraint, not whichever one happens to share its shape.
        let compareSet
            (label: string)
            (kind: ConstraintKind)
            (desiredEntries: (string option * string * Identifier list) list)
            (actualEntries: (string option * string * Identifier list) list)
            =
            let byName =
                desiredEntries
                |> List.choose (fun (n, d, c) -> n |> Option.map (fun n -> n, d, c))

            let claimedNames =
                byName
                |> List.filter (fun (n, _, _) -> actualEntries |> List.exists (fun (m, _, _) -> m = Some n))
                |> List.map (fun (n, _, _) -> n)

            // Definitions still free after the name matches, matched one for
            // one so two identical unnamed constraints do not both match the
            // same deployed one.
            let unmatchedActual =
                actualEntries
                |> List.filter (fun (n, _, _) ->
                    match n with
                    | Some n -> not (claimedNames |> List.contains n)
                    | None -> true)

            let unnamedDesired = desiredEntries |> List.filter (fun (n, _, _) -> Option.isNone n)

            // Removes the FIRST structural match, not every equal one: two
            // declarations that do the same thing need two deployed
            // constraints, not one counted twice.
            let removeFirst (definition: string) (from: (string option * string * Identifier list) list) =
                let rec go acc rest =
                    match rest with
                    | [] -> None
                    | (n, d, c) :: tail when d = definition -> Some(List.rev acc @ tail, (n, d, c))
                    | head :: tail -> go (head :: acc) tail

                go [] from

            let unnamedAdded, leftoverActual =
                unnamedDesired
                |> List.fold
                    (fun (added, pool) (_, definition, columns) ->
                        match removeFirst definition pool with
                        | Some (rest, _) -> added, rest
                        | None -> added @ [ columns ], pool)
                    ([], unmatchedActual)

            let added =
                (byName
                 |> List.filter (fun (n, _, _) -> not (actualEntries |> List.exists (fun (m, _, _) -> m = Some n)))
                 |> List.map (fun (n, _, c) ->
                     Ok(AddConstraint(desired.Name, Some(Identifier.unquoted n), kind, c))))
                @ (unnamedAdded |> List.map (fun c -> Ok(AddConstraint(desired.Name, None, kind, c))))

            let removed =
                leftoverActual
                |> List.map (fun (n, _, _) ->
                    match n with
                    | Some name when not desiredComplete ->
                        Microsoft.FSharp.Core.Error
                            { Object = desired.Name
                              Reason = DesiredStateIncomplete
                              Detail =
                                sprintf
                                    "%s '%s' is absent from desired state, but desired state did not load completely"
                                    label
                                    name }
                    | Some name when not allowDrops ->
                        Microsoft.FSharp.Core.Error
                            { Object = desired.Name
                              Reason = DropsNotEnabled
                              Detail = sprintf "%s '%s' would be dropped; pass --allow-drops" label name }
                    | Some name -> Ok(DropConstraint(desired.Name, Identifier.unquoted name, kind))
                    // The catalog has no nameless constraints, so this cannot
                    // happen. Saying so beats dropping it silently.
                    | None ->
                        Microsoft.FSharp.Core.Error
                            { Object = desired.Name
                              Reason = NotModelled
                              Detail = sprintf "an unnamed %s was reported by the catalog, which should not occur" label })

            // Same name on both sides, different membership.
            //
            // PostgreSQL has no ALTER CONSTRAINT that changes what one covers,
            // so the only way to get there is to drop it and add it back. Both
            // halves are emitted, and `orderKey` ranks drops one step ahead of
            // adds so the name is free when the add runs.
            //
            // That makes it a REMOVAL, and it is gated like every other one: a
            // redefinition Strata cannot drop is a redefinition it cannot make,
            // and emitting the add alone would fail on the name that is still
            // taken. So when drops are not enabled — or desired state did not
            // load completely — neither half is proposed and the difference is
            // reported instead.
            let redefined =
                byName
                |> List.collect (fun (n, dcols, dcolumns) ->
                    actualEntries
                    |> List.tryFind (fun (m, _, _) -> m = Some n)
                    |> Option.map (fun (_, acols, _) ->
                        if dcols = acols then
                            []
                        elif not desiredComplete || not allowDrops then
                            [ Microsoft.FSharp.Core.Error
                                { Object = desired.Name
                                  Reason = if desiredComplete then DropsNotEnabled else DesiredStateIncomplete
                                  Detail =
                                    sprintf
                                        "%s '%s' covers (%s) in desired state and (%s) in the database. Changing it means dropping and re-adding it, so it needs %s."
                                        label
                                        n
                                        dcols
                                        acols
                                        (if desiredComplete then "--allow-drops"
                                         else "a desired state that loaded completely") } ]
                        else
                            [ Ok(DropConstraint(desired.Name, Identifier.unquoted n, kind))
                              Ok(AddConstraint(desired.Name, Some(Identifier.unquoted n), kind, dcolumns)) ])
                    |> Option.defaultValue [])

            added @ removed @ redefined

        let primaryKey =
            match desired.PrimaryKey, actual.PrimaryKey with
            | Some d, Some a when columnList d.Columns <> columnList a.Columns ->
                [ Ok(
                    UnclassifiedChange(
                        sprintf
                            "%s: primary key covers (%s) in desired state and (%s) in the database"
                            (QualifiedName.display desired.Name)
                            (columnList d.Columns)
                            (columnList a.Columns))) ]
            | Some d, None ->
                [ Ok(AddConstraint(desired.Name, d.ConstraintName, ConstraintKind.PrimaryKey, d.Columns)) ]
            | None, Some a ->
                [ Ok(
                    UnclassifiedChange(
                        sprintf
                            "%s: primary key '%s' exists in the database and not in desired state"
                            (QualifiedName.display desired.Name)
                            (match a.ConstraintName with Some n -> n.Text | None -> "(unnamed)"))) ]
            | Some _, Some _
            | None, None -> []

        let uniques =
            compareSet
                "unique constraint"
                ConstraintKind.Unique
                (desired.UniqueConstraints
                 |> List.map (fun u -> entry u.ConstraintName (columnList u.Columns) u.Columns))
                (actual.UniqueConstraints
                 |> List.map (fun u -> entry u.ConstraintName (columnList u.Columns) u.Columns))

        let foreignKeys =
            let describe (f: ForeignKey) =
                sprintf
                    "%s -> %s(%s)"
                    (columnList f.Columns)
                    (QualifiedName.display f.ReferencedTable)
                    (columnList f.ReferencedColumns)

            compareSet
                "foreign key"
                ConstraintKind.ForeignKey
                (desired.ForeignKeys |> List.map (fun f -> entry f.ConstraintName (describe f) f.Columns))
                (actual.ForeignKeys |> List.map (fun f -> entry f.ConstraintName (describe f) f.Columns))

        // A check's PRESENCE is comparable by name. Its EXPRESSION is not: the
        // declared side does not carry one, and the catalog reports its own
        // normalised rendering. Presence is therefore compared and equality of
        // expression is explicitly not claimed, below.
        // An unnamed check has neither a name to match on nor, from the parse
        // tree, an expression — so unlike an unnamed foreign key it has no
        // shape of its own to compare. The shadow rendering supplies one: the
        // same DDL executed in a rolled-back transaction, which keeps an
        // explicit constraint name and lets the server invent one for a check
        // the file did not name. A rendering whose name the file never used is
        // therefore the rendering of an unnamed declaration, and its EXPRESSION
        // is directly comparable with the deployed one.
        let namedChecks (t: Table) =
            t.CheckConstraints |> List.filter (fun c -> Option.isSome c.ConstraintName)

        let unnamedDeclaredChecks =
            desired.CheckConstraints |> List.filter (fun c -> Option.isNone c.ConstraintName)

        let declaredCheckNames =
            namedChecks desired |> List.choose (fun c -> c.ConstraintName |> Option.map named)

        let unnamedRenderings =
            match rendered with
            | None -> []
            | Some n ->
                n.Checks
                |> List.filter (fun (name, _) ->
                    not (declaredCheckNames |> List.contains (named (Identifier.unquoted name))))
                |> List.map snd

        /// Deployed checks explained by an unnamed declaration, removed one for
        /// one so two identical declarations claim two deployed constraints.
        let unmatchedUnnamedChecks, deployedChecksToCompare =
            let removeFirst (expression: string) (pool: CheckConstraint list) =
                let rec go acc rest =
                    match rest with
                    | [] -> None
                    | (c: CheckConstraint) :: tail when c.Expression.Trim() = expression.Trim() ->
                        Some(List.rev acc @ tail)
                    | head :: tail -> go (head :: acc) tail

                go [] pool

            unnamedRenderings
            |> List.fold
                (fun (unmatched, pool) expression ->
                    match removeFirst expression pool with
                    | Some rest -> unmatched, rest
                    | None -> expression :: unmatched, pool)
                ([], actual.CheckConstraints)

        // No shadow rendering and an unnamed declaration: Strata holds nothing
        // that could attribute a deployed check to it, so it compares none of
        // them rather than reporting every one as absent from desired state.
        let checksUnattributable =
            List.isEmpty unnamedRenderings && not (List.isEmpty unnamedDeclaredChecks)

        let checks =
            if checksUnattributable then
                []
            else
                compareSet
                    "check constraint"
                    ConstraintKind.Check
                    (namedChecks desired |> List.map (fun c -> entry c.ConstraintName "" []))
                    (deployedChecksToCompare |> List.map (fun c -> entry c.ConstraintName "" []))
                @ (unmatchedUnnamedChecks
                   |> List.map (fun _ -> Ok(AddConstraint(desired.Name, None, ConstraintKind.Check, []))))

        let defaults =
            desired.Columns
            |> List.choose (fun d ->
                actual.Columns
                |> List.tryFind (fun a -> Identifier.sameName a.Name d.Name)
                |> Option.bind (fun a ->
                    let declaredExpression =
                        rendered
                        |> Option.bind (fun n ->
                            n.Defaults |> List.tryPick (fun (c, e) -> if Identifier.sameName (Identifier.unquoted c) d.Name then Some e else None))

                    match declaredExpression, a.DefaultExpression with
                    | Some declared, Some deployed when
                        not (isSequenceDefault declared)
                        && not (isSequenceDefault deployed)
                        && declared.Trim() <> deployed.Trim() ->
                        Some(
                            Ok(
                                UnclassifiedChange(
                                    sprintf
                                        "%s.%s: default is %s in desired state and %s in the database"
                                        (QualifiedName.display desired.Name)
                                        d.Name.Text
                                        declared
                                        deployed)))
                    | _ ->

                    if d.HasDefault <> a.HasDefault then
                        Some(
                            Ok(
                                UnclassifiedChange(
                                    sprintf
                                        "%s.%s: default %s in desired state and %s in the database"
                                        (QualifiedName.display desired.Name)
                                        d.Name.Text
                                        (if d.HasDefault then "present" else "absent")
                                        (if a.HasDefault then "present" else "absent"))))
                    else
                        None))

        let checkRedefinitions =
            match rendered with
            | None -> []
            | Some n ->
                actual.CheckConstraints
                |> List.choose (fun a ->
                    n.Checks
                    |> List.tryPick (fun (name, definition) ->
                        match a.ConstraintName with
                        | Some actualName when named (Identifier.unquoted name) = named actualName ->
                            Some definition
                        | _ -> None)
                    |> Option.bind (fun declared ->
                        if declared.Trim() <> a.Expression.Trim() then
                            Some(
                                Ok(
                                    UnclassifiedChange(
                                        sprintf
                                            "%s: check constraint '%s' is %s in desired state and %s in the database"
                                            (QualifiedName.display desired.Name)
                                            (match a.ConstraintName with
                                             | Some n -> n.Text
                                             | None -> "(unnamed)")
                                            declared
                                            a.Expression)))
                        else
                            None))

        // PostgreSQL creates an index to back each primary key and unique
        // constraint, naming it after the constraint. Those are compared as
        // CONSTRAINTS above, so they are excluded here — proposing to drop one
        // would be proposing to drop its constraint by a side door.
        // Every catalog constraint has a name, so `choose` drops nothing here;
        // it is how the shared optional type is read on the actual side.
        let constraintBackedNames =
            (match actual.PrimaryKey with
             | Some pk -> pk.ConstraintName |> Option.map named |> Option.toList
             | None -> [])
            @ (actual.UniqueConstraints |> List.choose (fun u -> u.ConstraintName |> Option.map named))

        let secondaryIndexes (t: Table) =
            t.Indexes |> List.filter (fun i -> not (constraintBackedNames |> List.contains (named i.Name)))

        let declaredIndexes = secondaryIndexes desired
        let deployedIndexes = secondaryIndexes actual

        // A project that declares NO index anywhere says nothing about them;
        // one that declares any takes ownership. `indexesDeclared` carries that
        // distinction from the loader's completeness.
        let indexChanges =
            if not indexesDeclared then []
            else
                let created =
                    declaredIndexes
                    |> List.filter (fun d -> not (deployedIndexes |> List.exists (fun a -> named a.Name = named d.Name)))
                    |> List.map (fun d -> Ok(CreateIndex(desired.Name, d.Name)))

                let dropped =
                    deployedIndexes
                    |> List.filter (fun a -> not (declaredIndexes |> List.exists (fun d -> named d.Name = named a.Name)))
                    |> List.map (fun a ->
                        if not desiredComplete then
                            Microsoft.FSharp.Core.Error
                                { Object = desired.Name
                                  Reason = DesiredStateIncomplete
                                  Detail =
                                    sprintf
                                        "index '%s' is absent from desired state, but desired state did not load completely"
                                        a.Name.Text }
                        elif not allowDrops then
                            Microsoft.FSharp.Core.Error
                                { Object = desired.Name
                                  Reason = DropsNotEnabled
                                  Detail =
                                    sprintf "index '%s' would be dropped; pass --allow-drops" a.Name.Text }
                        else
                            Ok(DropIndex(desired.Name, a.Name)))

                // Same name, different columns or uniqueness. Reported rather
                // than silently rebuilt: dropping and recreating an index is a
                // different operation with a different cost.
                let redefined =
                    declaredIndexes
                    |> List.choose (fun d ->
                        deployedIndexes
                        |> List.tryFind (fun a -> named a.Name = named d.Name)
                        |> Option.bind (fun a ->
                            let columnsOf (i: Index) = i.Columns |> List.map named |> String.concat ","

                            if columnsOf d <> columnsOf a || d.IsUnique <> a.IsUnique then
                                Some(
                                    Ok(
                                        UnclassifiedChange(
                                            sprintf
                                                "%s: index '%s' covers (%s)%s in desired state and (%s)%s in the database"
                                                (QualifiedName.display desired.Name)
                                                d.Name.Text
                                                (columnsOf d)
                                                (if d.IsUnique then " unique" else "")
                                                (columnsOf a)
                                                (if a.IsUnique then " unique" else ""))))
                            else
                                None))

                created @ dropped @ redefined

        let uncomparedIndexes = if indexesDeclared then [] else deployedIndexes

        // ---- triggers ------------------------------------------------------
        //
        // Same ownership rule as indexes, and for the same reason: a project
        // with no trigger file is saying nothing about triggers, not that the
        // table should have none. Shipping this without the distinction would
        // propose dropping every trigger in every existing database.
        //
        // Note what is NOT here: an internal trigger. Every foreign key is
        // implemented as a pair of them, and the catalog query filters them out
        // by `tgisinternal` — without that filter a table with two foreign keys
        // would show four undeclared triggers to drop.
        let triggerIdentity (t: Trigger) =
            // Everything the model carries, in one comparable string. A
            // condition is compared by PRESENCE only, because its text is
            // unavailable on both sides.
            String.concat
                "|"
                [ (match t.Timing with
                   | TriggerTiming.Before -> "before"
                   | TriggerTiming.After -> "after"
                   | TriggerTiming.InsteadOf -> "instead")
                  t.Events |> String.concat ","
                  (match t.Level with
                   | TriggerLevel.Row -> "row"
                   | TriggerLevel.Statement -> "statement")
                  t.UpdateColumns |> List.map named |> String.concat ","
                  // The function is matched on its NAME only. A declared
                  // trigger names `touch()` and the catalog reports
                  // `public.touch`, so an unqualified declaration would never
                  // match a qualified deployment however identical they are.
                  named t.Function.Name
                  t.Arguments |> String.concat ","
                  (if t.HasCondition then "when" else "always") ]

        let triggerChanges =
            if not triggersDeclared then []
            else
                let created =
                    desired.Triggers
                    |> List.filter (fun d ->
                        not (actual.Triggers |> List.exists (fun a -> named a.Name = named d.Name)))
                    |> List.map (fun d -> Ok(CreateTrigger(desired.Name, d.Name)))

                let dropped =
                    actual.Triggers
                    |> List.filter (fun a ->
                        not (desired.Triggers |> List.exists (fun d -> named d.Name = named a.Name)))
                    |> List.map (fun a ->
                        if not desiredComplete then
                            Microsoft.FSharp.Core.Error
                                { Object = desired.Name
                                  Reason = DesiredStateIncomplete
                                  Detail =
                                    sprintf
                                        "trigger '%s' is absent from desired state, but desired state did not load completely"
                                        a.Name.Text }
                        elif not allowDrops then
                            Microsoft.FSharp.Core.Error
                                { Object = desired.Name
                                  Reason = DropsNotEnabled
                                  Detail =
                                    sprintf "trigger '%s' would be dropped; pass --allow-drops" a.Name.Text }
                        else
                            Ok(DropTrigger(desired.Name, a.Name)))

                // Same name, different definition. Unlike an index, this IS
                // proposed as a change: a trigger is dropped and recreated from
                // the declaring file, which is exactly what the file asks for.
                let redefined =
                    desired.Triggers
                    |> List.choose (fun d ->
                        actual.Triggers
                        |> List.tryFind (fun a -> named a.Name = named d.Name)
                        |> Option.bind (fun a ->
                            if triggerIdentity d <> triggerIdentity a then
                                Some(Ok(ReplaceTrigger(desired.Name, d.Name)))
                            else
                                None))

                created @ dropped @ redefined

        let uncomparedTriggers = if triggersDeclared then [] else actual.Triggers

        // A trigger with a WHEN clause on both sides, whose definitions
        // otherwise agree. Its condition may still differ and nothing here can
        // tell: `pg_get_expr` will not render `tgqual` at all.
        let uncomparedConditions =
            if not triggersDeclared then []
            else
                desired.Triggers
                |> List.filter (fun d ->
                    d.HasCondition
                    && actual.Triggers
                       |> List.exists (fun a ->
                           named a.Name = named d.Name
                           && a.HasCondition
                           && triggerIdentity a = triggerIdentity d))

        // Everything above establishes PRESENCE. Two expressions Strata cannot
        // read might still differ, and saying so is the difference between a
        // bounded result and a false clean.
        let notCompared =
            let comparedCheck (name: Identifier) =
                match rendered with
                | None -> false
                | Some n -> n.Checks |> List.exists (fun (c, _) -> named (Identifier.unquoted c) = named name)

            let sharedChecks =
                namedChecks desired
                |> List.filter (fun d ->
                    actual.CheckConstraints
                    |> List.exists (fun a -> a.ConstraintName |> Option.map named = (d.ConstraintName |> Option.map named))
                    && not (d.ConstraintName |> Option.map comparedCheck |> Option.defaultValue false))

            let sharedDefaults =
                desired.Columns
                |> List.filter (fun d ->
                    d.HasDefault
                    && actual.Columns
                       |> List.exists (fun a -> Identifier.sameName a.Name d.Name && a.HasDefault)
                    && (match rendered with
                        | None -> true
                        | Some n ->
                            // Compared, unless the rendering is a sequence
                            // default that can never match across schemas.
                            n.Defaults
                            |> List.tryPick (fun (c, e) ->
                                if Identifier.sameName (Identifier.unquoted c) d.Name then Some e else None)
                            |> function
                               | Some e -> isSequenceDefault e
                               | None -> true))

            [ if not (List.isEmpty sharedChecks) then
                  { Object = desired.Name
                    Reason = NotCompared
                    Detail =
                      sprintf
                          "%d check constraint(s) exist on both sides; their expressions were NOT compared, so they may differ"
                          (List.length sharedChecks) }
              if not (List.isEmpty sharedDefaults) then
                  { Object = desired.Name
                    Reason = NotCompared
                    Detail =
                      sprintf
                          "%d column default(s) exist on both sides; their expressions were NOT compared, so they may differ"
                          (List.length sharedDefaults) }
              if not (List.isEmpty uncomparedIndexes) then
                  { Object = desired.Name
                    Reason = NotModelled
                    Detail =
                      sprintf
                          "%d index(es) exist in the database and this project declares none, so indexes were NOT compared. Declaring any index file takes ownership of them."
                          (List.length uncomparedIndexes) }
              if not (List.isEmpty uncomparedTriggers) then
                  { Object = desired.Name
                    Reason = NotModelled
                    Detail =
                      sprintf
                          "%d trigger(s) exist in the database and this project declares none, so triggers were NOT compared. Declaring any trigger file takes ownership of them."
                          (List.length uncomparedTriggers) }
              if checksUnattributable then
                  { Object = desired.Name
                    Reason = NotCompared
                    Detail =
                      sprintf
                          "%d check constraint(s) in the declaring file are unnamed and the declared DDL could not be normalised through the server, so no deployed check could be attributed to them and NONE were compared. Name them, or restore normalisation, to have them compared."
                          (List.length unnamedDeclaredChecks) }
              if not (List.isEmpty uncomparedConditions) then
                  { Object = desired.Name
                    Reason = NotCompared
                    Detail =
                      sprintf
                          "%d trigger(s) carry a WHEN clause on both sides; the conditions were NOT compared, so they may differ"
                          (List.length uncomparedConditions) } ]

        primaryKey
        @ uniques
        @ foreignKeys
        @ checks
        @ defaults
        @ checkRedefinitions
        @ indexChanges
        @ triggerChanges,
        notCompared

    /// Views and routines, compared by PRESENCE only.
    ///
    /// Their definitions are deliberately not compared. A declared view's
    /// definition text is not recoverable from the parse tree, and PostgreSQL
    /// rewrites what it stores — `SELECT 1 AS x` comes back schema-qualified
    /// and reformatted — so even a perfect deparse would differ from
    /// `pg_get_viewdef` on a view nobody changed. A routine body has the same
    /// problem. Comparing text would report churn on every run.
    ///
    /// A declared view also carries an EMPTY column list, which means "not
    /// knowable from a file", not "no columns". Nothing here may read it as a
    /// fact, which is why only presence is compared.
    ///
    /// Presence alone is still worth having: before this, a view in the
    /// database was suppressed as not-modelled and a view in a file failed to
    /// load, so a project could not express that a view should exist at all.
    let private otherObjectChanges
        (allowDrops: bool)
        (managedSchemas: string list)
        (desiredComplete: bool)
        /// Declared view definitions as the SERVER renders them, keyed by
        /// display name. Produced by executing the declared DDL in a
        /// rolled-back transaction, which is the only way to compare a view
        /// faithfully. A view missing from this list could not be normalised —
        /// no privilege, a read-only target, DDL the server rejected — and is
        /// disclosed rather than assumed equal.
        (normalisedViews: (string * string) list)
        (desired: SchemaObject list)
        (actual: SchemaObject list)
        =
        // Routines are identified by name AND argument types: PostgreSQL
        // allows overloads, so f(int) and f(text) are different objects and
        // matching on name alone would read one as a redefinition of the other.
        let identity (o: SchemaObject) =
            match o with
            | TableObject t -> "table:" + QualifiedName.display t.Name
            | ViewObject v -> "view:" + QualifiedName.display v.Name
            | RoutineObject r ->
                sprintf "routine:%s(%s)" (QualifiedName.display r.Name) (String.concat "," r.ArgumentTypes)
            | SequenceObject s -> "sequence:" + QualifiedName.display s.Name

        let nonTable (objects: SchemaObject list) =
            objects
            |> List.filter (fun o ->
                match o with
                // Sequences are excluded here and compared on their own:
                // this path compares views and routines by PRESENCE alone,
                // and a sequence has a start, an increment and bounds that a
                // presence check would pass over in silence.
                | TableObject _
                | SequenceObject _ -> false
                | ViewObject _
                | RoutineObject _ -> true)

        let desiredOther = nonTable desired
        let actualOther = nonTable actual

        let kindOf (o: SchemaObject) =
            match o with
            | ViewObject v -> if v.IsMaterialized then "materialized view" else "view"
            | RoutineObject r -> (match r.Kind with Procedure -> "procedure" | Function -> "function")
            | TableObject _ -> "table"
            // Excluded from this path by `nonTable`; the compiler still wants
            // the arm, and inventing a wrong one would be worse than saying so.
            | SequenceObject _ -> "sequence"

        let created =
            desiredOther
            |> List.filter (fun d -> not (actualOther |> List.exists (fun a -> identity a = identity d)))
            |> List.map (fun d ->
                match d with
                | ViewObject v -> Ok(CreateView v.Name)
                | RoutineObject r -> Ok(CreateRoutine r.Name)
                | TableObject t -> Ok(CreateTable t.Name)
                | SequenceObject sq -> Ok(CreateSequence sq.Name))

        let removed =
            actualOther
            |> List.filter (fun a -> not (desiredOther |> List.exists (fun d -> identity d = identity a)))
            |> List.map (fun a ->
                let name = SchemaObject.name a

                if not (isManaged managedSchemas name) then
                    Microsoft.FSharp.Core.Error
                        { Object = name
                          Reason = OutsideManagedSchemas
                          Detail = sprintf "%s exists in the database and not in desired state, but its schema is not managed" (kindOf a) }
                elif SchemaObject.scope a = ExtensionOwned then
                    Microsoft.FSharp.Core.Error
                        { Object = name
                          Reason = ExtensionOwnedObject
                          Detail = sprintf "%s is owned by an extension" (kindOf a) }
                elif not desiredComplete then
                    Microsoft.FSharp.Core.Error
                        { Object = name
                          Reason = DesiredStateIncomplete
                          Detail =
                            sprintf
                                "%s is absent from desired state, but desired state did not load completely"
                                (kindOf a) }
                elif not allowDrops then
                    Microsoft.FSharp.Core.Error
                        { Object = name
                          Reason = DropsNotEnabled
                          Detail = sprintf "%s would be dropped; pass --allow-drops to propose removals" (kindOf a) }
                else
                    Ok(
                        UnclassifiedChange(
                            sprintf
                                "%s %s exists in the database and not in desired state"
                                (kindOf a)
                                (QualifiedName.display name))))

        let onBothSides =
            desiredOther
            |> List.choose (fun d ->
                actualOther
                |> List.tryFind (fun a -> identity a = identity d)
                |> Option.map (fun a -> d, a))

        // A view whose declared DDL the server normalised can be compared
        // exactly: both sides now carry PostgreSQL's own rendering.
        let redefinitions =
            onBothSides
            |> List.choose (fun (d, a) ->
                match d, a with
                | ViewObject dv, ViewObject av when not dv.IsMaterialized ->
                    normalisedViews
                    |> List.tryPick (fun (name, definition) ->
                        if name = QualifiedName.display dv.Name then Some definition else None)
                    |> Option.bind (fun declaredDefinition ->
                        if declaredDefinition.Trim() <> av.Definition.Trim() then
                            Some(Ok(ReplaceView dv.Name))
                        else
                            None)
                // A routine body needs no shadow: PostgreSQL stores a classic
                // `AS $$...$$` body verbatim in prosrc, so the declared text
                // and the deployed text compare directly. A body the server
                // holds only as a parse tree (SQL-standard BEGIN ATOMIC) or as
                // a symbol name (C) yields None on one side and is disclosed.
                | RoutineObject dr, RoutineObject ar ->
                    match dr.Body, ar.Body with
                    | Some declared, Some deployed when declared.Trim() <> deployed.Trim() ->
                        Some(Ok(ReplaceRoutine dr.Name))
                    | _ -> None

                | _ -> None)

        // Everything on both sides that could NOT be compared. A view that
        // normalised and matched produces nothing here — silence is correct
        // once the comparison actually happened.
        let notCompared =
            onBothSides
            |> List.choose (fun (d, _) ->
                let comparedExactly =
                    match d with
                    | ViewObject dv when not dv.IsMaterialized ->
                        normalisedViews |> List.exists (fun (name, _) -> name = QualifiedName.display dv.Name)
                    | RoutineObject dr ->
                        // Compared only when BOTH sides hold body text.
                        dr.Body.IsSome
                        && actualOther
                           |> List.exists (fun a ->
                               match a with
                               | RoutineObject ar -> identity a = identity d && ar.Body.IsSome
                               | _ -> false)
                    | _ -> false

                if comparedExactly then None
                else
                    Some
                        { Object = SchemaObject.name d
                          Reason = NotCompared
                          Detail =
                            sprintf
                                "%s exists on both sides; its definition was NOT compared, so the bodies may differ"
                                (kindOf d) })

        created @ removed @ redefinitions, notCompared

    /// Differences between declared reference rows and deployed ones.
    ///
    /// Matching is by KEY, which is the table's primary key — the identity the
    /// database itself asserts. Equality is by the server's rendering of the
    /// declared columns, so a column the file does not name takes no part.
    ///
    /// ## A row nobody declared is not a row to delete
    ///
    /// This is `NG-006` again, and the stakes are higher than for an object. A
    /// reference row that user data points at cannot be removed without either
    /// failing on the foreign key or, worse, cascading into that data. So an
    /// undeclared row is REPORTED and never proposed for removal — not even
    /// under `--allow-drops`, which authorises dropping objects a project owns,
    /// not deleting rows it never claimed.
    let private dataChanges (data: ResolvedData list) (failures: DataFailure list) =
        let nameOf (table: string) =
            match table.Split('.') with
            | [| schema; object' |] ->
                QualifiedName.qualified (Identifier.unquoted schema) (Identifier.unquoted object')
            | _ -> QualifiedName.unqualified (Identifier.unquoted table)

        let changes =
            data
            |> List.collect (fun d ->
                let table = nameOf d.Table
                let deployedByKey = d.Deployed |> List.map (fun r -> r.Key, r) |> Map.ofList

                d.Declared
                |> List.choose (fun declared ->
                    match Map.tryFind declared.Key deployedByKey with
                    | None -> Some(Ok(InsertRow(table, declared.Key)))
                    | Some deployed when deployed.Rendered <> declared.Rendered ->
                        Some(Ok(UpdateRow(table, declared.Key)))
                    | Some _ -> None))

        let undeclared =
            data
            |> List.choose (fun d ->
                let declaredKeys = d.Declared |> List.map (fun r -> r.Key) |> Set.ofList
                let extra = d.Deployed |> List.filter (fun r -> not (Set.contains r.Key declaredKeys))

                if List.isEmpty extra then
                    None
                else
                    Some
                        { Object = nameOf d.Table
                          Reason = NotModelled
                          Detail =
                            sprintf
                                "%d row(s) in this table are not declared by the project: %s. They are NOT proposed for deletion — user data may reference them."
                                (List.length extra)
                                (extra |> List.map (fun r -> r.Key) |> List.sort |> String.concat ", ") })

        let unresolved =
            failures
            |> List.map (fun f ->
                { Object = nameOf f.Table
                  Reason = NotCompared
                  Detail = sprintf "declared rows were NOT compared: %s" f.Reason })

        changes, undeclared @ unresolved
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
    type Inputs =
        { AllowDrops: bool
          ManagedSchemas: string list
          /// The verbatim text that declared each object.
          Declarations: (QualifiedName * string) list
          TriggerDeclarations: ((QualifiedName * Identifier) * string) list
          /// Schemas that exist in the database, or `None` when the list could
          /// not be read. `None` is not an empty list.
          ExistingSchemas: string list option
          /// Schemas the project actually declared objects in.
          DeclaredInSchemas: string list
          /// Privileges the project declares.
          DeclaredGrants: Grant list
          /// Privileges the database holds, or `None` when they could not be
          /// read.
          ActualGrants: Grant list option
          /// Declared and deployed reference rows, already rendered by the
          /// server. Empty when the project declares no data files.
          Data: ResolvedData list
          DataFailures: DataFailure list
          NormalisedViews: (string * string) list
          NormalisedTables: NormalisedTable list
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
              ExistingSchemas = None
              DeclaredInSchemas = []
              DeclaredGrants = []
              ActualGrants = None
              Data = []
              DataFailures = []
              NormalisedViews = []
              NormalisedTables = []
              Renames = []
              Desired = desired
              Actual = actual }

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

        // Bound once, so the body below reads as it always did.
        let allowDrops = inputs.AllowDrops
        let managedSchemas = inputs.ManagedSchemas
        let declarations = inputs.Declarations
        let triggerDeclarations = inputs.TriggerDeclarations
        let existingSchemas = inputs.ExistingSchemas
        let declaredInSchemas = inputs.DeclaredInSchemas
        let declaredGrants = inputs.DeclaredGrants
        let actualGrants = inputs.ActualGrants
        let data = inputs.Data
        let dataFailures = inputs.DataFailures
        let normalisedViews = inputs.NormalisedViews
        let normalisedTables = inputs.NormalisedTables
        let renames = inputs.Renames
        let desired = inputs.Desired
        let actual = inputs.Actual

        // A drop is only ever as trustworthy as the desired state that implies
        // it, so this single flag gates every removal below.
        let desiredComplete =
            match Completeness.stateOf "relations" desired.Completeness with
            | Complete -> true
            | Partial _
            | Inaccessible _
            | NotRequested -> false

        let desiredTables = tables desired
        let actualTables = tables actual

        // An object whose file declares a previous name that EXISTS in the
        // database is a rename, not a create-plus-drop. Resolving it here keeps
        // both halves out of the plan: the create is replaced, and the drop is
        // suppressed below because the old name is no longer treated as absent.
        let renamedTables =
            desiredTables
            |> List.choose (fun d ->
                renames
                |> List.tryFind (fun r -> sameName r.Object d.Name)
                |> Option.bind (fun r -> r.RenamedFrom)
                |> Option.bind (fun from ->
                    if
                        actualTables |> List.exists (fun a -> sameName a.Name from)
                        && not (actualTables |> List.exists (fun a -> sameName a.Name d.Name))
                    then
                        Some(from, d.Name)
                    else
                        // The old name is not in the database, or the new name
                        // already is. Either way the rename already happened or
                        // never applied, and the annotation is spent.
                        None))

        let renamedAway = renamedTables |> List.map fst

        let created =
            desiredTables
            |> List.filter (fun d ->
                not (actualTables |> List.exists (fun a -> sameName a.Name d.Name))
                && not (renamedTables |> List.exists (fun (_, to') -> sameName to' d.Name)))

        // Two tables that reference each other cannot both be created first.
        // Withheld with a reason rather than emitted in an order that is known
        // not to execute.
        let cyclic = cyclicCreations created

        let creations =
            created
            |> List.map (fun d ->
                if cyclic |> List.contains (QualifiedName.display d.Name) then
                    Microsoft.FSharp.Core.Error
                        { Object = d.Name
                          Reason = NotModelled
                          Detail =
                            sprintf
                                "this table's foreign keys form a cycle with %s, and no order of plain CREATE TABLE statements satisfies one. Create them by hand, or declare one side's foreign key as a separate ALTER TABLE."
                                (cyclic
                                 |> List.filter (fun n -> n <> QualifiedName.display d.Name)
                                 |> String.concat ", ") }
                else
                    Ok(CreateTable d.Name))

        // Depth in the foreign-key graph, used as the tiebreak below so a
        // referenced table is created before the table referencing it.
        let depthOfCreation = creationDepths created

        let creationDepth (change: Change) =
            match change with
            | CreateTable name ->
                depthOfCreation
                |> Map.tryFind (QualifiedName.display name)
                |> Option.defaultValue 0
            | _ -> 0

        let tableRenames = renamedTables |> List.map (fun (from, to') -> Ok(RenameTable(from, to')))

        // A table being created needs its declared indexes created too. Index
        // comparison below only runs for tables present on BOTH sides, so
        // without this a new table's indexes would need a second apply — the
        // first run created the table and reported the index as a change it had
        // not made.
        // A table withheld as cyclic is not being created, so nothing that
        // depends on its existence may be proposed either.
        let newTables =
            created |> List.filter (fun d -> not (cyclic |> List.contains (QualifiedName.display d.Name)))

        let indexesForNewTables =
            newTables
            |> List.collect (fun d -> d.Indexes |> List.map (fun i -> Ok(CreateIndex(d.Name, i.Name))))

        // And its triggers, for the identical reason: trigger comparison runs
        // only for tables on both sides, so without this a new table's triggers
        // would need a second apply.
        let triggersForNewTables =
            newTables
            |> List.collect (fun d -> d.Triggers |> List.map (fun t -> Ok(CreateTrigger(d.Name, t.Name))))

        let shared =
            desiredTables
            |> List.choose (fun d ->
                actualTables
                |> List.tryFind (fun a -> sameName a.Name d.Name)
                |> Option.map (fun a -> d, a))

        // A table matched by its NEW name after a rename still needs its columns
        // compared, so renamed pairs join the shared set.
        let sharedIncludingRenamed =
            shared
            @ (renamedTables
               |> List.choose (fun (from, to') ->
                   match
                       desiredTables |> List.tryFind (fun d -> sameName d.Name to'),
                       actualTables |> List.tryFind (fun a -> sameName a.Name from)
                   with
                   | Some d, Some a -> Some(d, a)
                   | _ -> None))

        let columnRenamesFor (d: Table) (a: Table) =
            renames
            |> List.tryFind (fun r -> sameName r.Object d.Name)
            |> Option.map (fun r ->
                r.Columns
                |> List.filter (fun (from, to') ->
                    // Only a rename whose OLD name is in the database and whose
                    // NEW name is not. Anything else is already applied.
                    a.Columns |> List.exists (fun c -> Identifier.sameName c.Name (Identifier.unquoted from))
                    && not (a.Columns |> List.exists (fun c -> Identifier.sameName c.Name (Identifier.unquoted to'))))
                |> List.map (fun (from, to') ->
                    // The table is named by its CURRENT name, not its desired
                    // one, for two reasons that point the same way: the corpus
                    // records dependencies against the name that exists today,
                    // so the gate can only find readers under it; and column
                    // renames are ordered BEFORE a table rename, so at
                    // execution time the table still answers to it.
                    Ok(RenameColumn(a.Name, Identifier.unquoted from, Identifier.unquoted to'))))
            |> Option.defaultValue []

        let columnResults =
            sharedIncludingRenamed
            |> List.collect (fun (d, a) ->
                let renamed = columnRenamesFor d a

                let renamedFrom =
                    renamed
                    |> List.choose (function
                        | Ok (RenameColumn (_, from, _)) -> Some from
                        | _ -> None)

                let renamedTo =
                    renamed
                    |> List.choose (function
                        | Ok (RenameColumn (_, _, to')) -> Some to'
                        | _ -> None)

                // A renamed column must not also appear as a drop of the old
                // name and an add of the new one, so both are hidden from the
                // column comparison.
                let withoutRenamed =
                    { a with
                        Columns =
                            a.Columns
                            |> List.filter (fun c -> not (renamedFrom |> List.exists (Identifier.sameName c.Name))) },
                    { d with
                        Columns =
                            d.Columns
                            |> List.filter (fun c -> not (renamedTo |> List.exists (Identifier.sameName c.Name))) }

                let actualMinus, desiredMinus = withoutRenamed

                renamed
                @ columnChanges allowDrops managedSchemas desiredComplete desiredMinus actualMinus)

        let constraintResults =
            let indexesDeclared =
                match Completeness.stateOf "indexes" desired.Completeness with
                | NotRequested -> false
                | Complete
                | Partial _
                | Inaccessible _ -> true

            let triggersDeclared =
                match Completeness.stateOf "triggers" desired.Completeness with
                | NotRequested -> false
                | Complete
                | Partial _
                | Inaccessible _ -> true

            shared
            |> List.map (fun (d, a) ->
                constraintChanges
                    allowDrops
                    desiredComplete
                    indexesDeclared
                    triggersDeclared
                    normalisedTables
                    d
                    a)

        let constraintChangeResults = constraintResults |> List.collect fst
        let notCompared = constraintResults |> List.collect snd

        let newSchemas, schemaSuppressions = schemaChanges existingSchemas declaredInSchemas

        let grantResults, grantSuppressions =
            grantChanges allowDrops managedSchemas desiredComplete declaredGrants actualGrants

        let sequenceResults =
            sequenceChanges allowDrops managedSchemas desiredComplete desired.Objects actual.Objects
        let rowChanges, rowSuppressions = dataChanges data dataFailures

        let otherChangeResults, otherNotCompared =
            otherObjectChanges
                allowDrops
                managedSchemas
                desiredComplete
                normalisedViews
                desired.Objects
                actual.Objects

        let unmodelled = otherNotCompared

        let all =
            newSchemas
            @ sequenceResults
            @ grantResults
            @ creations
            @ tableRenames
            @ indexesForNewTables
            @ triggersForNewTables
            @ removals
                allowDrops
                managedSchemas
                desiredComplete
                desiredTables
                (actualTables |> List.filter (fun a -> not (renamedAway |> List.exists (sameName a.Name))))
            @ columnResults
            @ constraintChangeResults
            @ otherChangeResults
            @ rowChanges

        let changes =
            all
            |> List.choose (function Ok change -> Some change | Microsoft.FSharp.Core.Error _ -> None)
            // Rank first, then foreign-key depth within the creations. F#'s
            // sort is stable, so everything else keeps the order it was
            // assembled in.
            |> List.sortBy (fun c -> orderKey c, creationDepth c)

        { Changes = changes
          Statements =
            changes
            |> List.map (fun c ->
                { Change = c
                  Sql =
                    emit
                        declarations
                        triggerDeclarations
                        data
                        (desired.Objects
                         |> List.choose (function SequenceObject sq -> Some sq | _ -> None))
                        normalisedTables
                        desiredTables
                        c })
          Suppressed =
            (all |> List.choose (function Microsoft.FSharp.Core.Error s -> Some s | Ok _ -> None))
            @ notCompared
            @ unmodelled
            @ rowSuppressions
            @ schemaSuppressions
            @ grantSuppressions
          DesiredStateComplete = desiredComplete }

    // ---- output -------------------------------------------------------------

    /// A grant's object, for a human reading a plan.
    ///
    /// Says the KIND out loud. A plan line reading `grant USAGE on orders` does
    /// not tell a reader whether the schema or a table of that name is about to
    /// become reachable, and that is the one thing they need to know.
    let private describeGrantTarget (target: GrantTarget) =
        match target with
        | GrantTarget.Relation name -> QualifiedName.display name
        | GrantTarget.Schema schema -> sprintf "schema %s" schema.Text
        | GrantTarget.Routine (name, arguments) ->
            sprintf "routine %s(%s)" (QualifiedName.display name) (String.concat ", " arguments)
        | GrantTarget.RelationColumn (name, column) ->
            sprintf "%s(%s)" (QualifiedName.display name) column.Text

    /// One line describing a change, for a human reading a plan.
    let private describe (change: Change) =
        match change with
        | UnclassifiedChange detail -> sprintf "unclassified: %s" detail
        | DropColumn (table, column) ->
            sprintf "drop-column        %s.%s" (QualifiedName.display table) column.Text
        | AlterColumnType (table, column, newType) ->
            sprintf "alter-column-type  %s.%s -> %s" (QualifiedName.display table) column.Text newType
        | AddColumn (table, column) ->
            sprintf "add-column         %s.%s" (QualifiedName.display table) column.Text
        | AddConstraint (table, name, kind, _) ->
            sprintf
                "add-constraint     %s %s %s"
                (QualifiedName.display table)
                (ConstraintKind.tag kind)
                (match name with Some n -> n.Text | None -> "(unnamed in the declaring file)")
        | DropConstraint (table, name, kind) ->
            sprintf
                "drop-constraint    %s %s %s"
                (QualifiedName.display table)
                (ConstraintKind.tag kind)
                name.Text
        | DropTable table -> sprintf "drop-table         %s" (QualifiedName.display table)
        | CreateSchema schema -> sprintf "create-schema      %s" schema.Text
        | CreateSequence n -> sprintf "create-sequence    %s" (QualifiedName.display n)
        | GrantPrivileges (t, g, p) ->
            sprintf "grant              %s on %s to %s" (String.concat "," p) (describeGrantTarget t) g
        | RevokePrivileges (t, g, p) ->
            sprintf "revoke             %s on %s from %s" (String.concat "," p) (describeGrantTarget t) g
        | DropSequence n -> sprintf "drop-sequence      %s" (QualifiedName.display n)
        | AlterSequence n -> sprintf "alter-sequence     %s" (QualifiedName.display n)
        | CreateTable table -> sprintf "create-table       %s" (QualifiedName.display table)
        | RenameTable (from, to') ->
            sprintf "rename-table       %s -> %s" (QualifiedName.display from) (QualifiedName.display to')
        | RenameColumn (table, from, to') ->
            sprintf "rename-column      %s.%s -> %s" (QualifiedName.display table) from.Text to'.Text
        | CreateIndex (table, index) ->
            sprintf "create-index       %s on %s" index.Text (QualifiedName.display table)
        | DropIndex (table, index) ->
            sprintf "drop-index         %s on %s" index.Text (QualifiedName.display table)
        | InsertRow (table, key) ->
            sprintf "insert-row         %s %s" (QualifiedName.display table) key
        | UpdateRow (table, key) ->
            sprintf "update-row         %s %s" (QualifiedName.display table) key
        | CreateTrigger (table, trigger) ->
            sprintf "create-trigger     %s on %s" trigger.Text (QualifiedName.display table)
        | DropTrigger (table, trigger) ->
            sprintf "drop-trigger       %s on %s" trigger.Text (QualifiedName.display table)
        | ReplaceTrigger (table, trigger) ->
            sprintf "replace-trigger    %s on %s" trigger.Text (QualifiedName.display table)
        | CreateView view -> sprintf "create-view        %s" (QualifiedName.display view)
        | ReplaceView view -> sprintf "replace-view       %s" (QualifiedName.display view)
        | ReplaceRoutine r -> sprintf "replace-routine    %s" (QualifiedName.display r)
        | CreateRoutine routine -> sprintf "create-routine     %s" (QualifiedName.display routine)
        | TruncateTable table -> sprintf "truncate-table     %s" (QualifiedName.display table)

    let private suppressionJson (s: Suppression) =
        JObject [ "object", JString(QualifiedName.display s.Object)
                  "reason", JString(SuppressionReason.tag s.Reason)
                  "detail", JString s.Detail ]

    /// A gate finding, rendered here rather than reused from `DeploymentGate`
    /// because its own renderer is private to that module.
    ///
    /// The reasons matter more in JSON than in text: a pipeline that gets a
    /// verdict with no explanation can only obey or override it, and neither
    /// is a decision anyone can review afterwards.
    let private findingJson (f: DeploymentGate.Finding) =
        JObject [ "verdict", JString(DeploymentGate.Verdict.tag f.Verdict)
                  "change", JString(describe f.Change)
                  "detected", JString f.Detected
                  "rationale", JString f.Rationale
                  "affectedSources", JArray(f.AffectedSources |> List.sort |> List.map JString)
                  "nextSafeMove", JString f.NextSafeMove ]

    let toJson (result: DiffResult) (gate: DeploymentGate.GateResult) =
        JObject [ "verdict", JString(DeploymentGate.Verdict.tag gate.Verdict)
                  "exitCode", JInt(DeploymentGate.Verdict.exitCode gate.Verdict)
                  "desiredStateComplete", JBool result.DesiredStateComplete
                  "changeCount", JInt(List.length result.Changes)
                  "changes", JArray(result.Changes |> List.map (fun c -> JString(describe c)))
                  "findings", JArray(gate.Findings |> List.map findingJson)
                  "suppressed", JArray(result.Suppressed |> List.map suppressionJson)
                  "scope", scope gate.Scope ]
        |> Json.render

    /// Human-readable dry run.
    let toText (result: DiffResult) (gate: DeploymentGate.GateResult) =
        let lines = ResizeArray<string>()

        lines.Add(
            sprintf
                "PLAN: %d change(s), %d suppressed  —  gate verdict %s"
                (List.length result.Changes)
                (List.length result.Suppressed)
                ((DeploymentGate.Verdict.tag gate.Verdict).ToUpperInvariant()))

        lines.Add ""

        if List.isEmpty result.Changes then
            lines.Add "  (no changes proposed)"
            lines.Add ""
        else
            for change in result.Changes do
                lines.Add(sprintf "  %s" (describe change))
            lines.Add ""

        if not (List.isEmpty result.Suppressed) then
            lines.Add "NOT PROPOSED — differences Strata saw and will not act on:"
            lines.Add ""

            for s in result.Suppressed do
                lines.Add(sprintf "  %-28s [%s]" (QualifiedName.display s.Object) (SuppressionReason.tag s.Reason))
                lines.Add(sprintf "      %s" s.Detail)

            lines.Add ""

        if not result.DesiredStateComplete then
            lines.Add "WARNING: desired state did NOT load completely. No object was proposed for"
            lines.Add "         removal, because absence from a partial desired state is not evidence"
            lines.Add "         that anything should be dropped."
            lines.Add ""

        for f in gate.Findings do
            lines.Add(sprintf "  [%s] %s" ((DeploymentGate.Verdict.tag f.Verdict).ToUpperInvariant()) f.Detected)
            lines.Add(sprintf "         why:  %s" f.Rationale)
            lines.Add(sprintf "         next: %s" f.NextSafeMove)
            lines.Add ""

        System.String.Join("\n", lines)
