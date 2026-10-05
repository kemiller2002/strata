namespace Strata.Application.SchemaDiff

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Semantic.AnalysisScope
open Strata.Analysis.ProposedChange

/// The facts every removal is judged against, read once per run.
type RemovalPolicy =
    { AllowDrops: bool
      ManagedSchemas: string list
      /// The DESIRED snapshot loaded completely for relations. A drop is only
      /// ever as trustworthy as the desired state that implies it.
      DesiredComplete: bool }

/// One condition a removal must satisfy, with the wording that reports it
/// when it does not.
///
/// Different families check different subsets — a column has no extension
/// owner, an index is gated by ownership of the table it is on — but every
/// family that removes anything states its guards here and lets `removal`
/// decide. The order of a guard list is the order the guards are checked,
/// and the first one that fails names the reason.
type RemovalGuard =
    /// The object lies in a schema the project manages (`NG-006`, §1437).
    | InManagedSchema of managed: bool * detail: string
    /// The object is not owned by an extension (`RK-008`).
    | NotExtensionOwned of scope: ManagementScope * detail: string
    /// Desired state loaded completely, so absence from it is evidence.
    | DesiredStateLoaded of detail: string
    /// Removals were enabled for this run (`--allow-drops`).
    | DropsEnabled of detail: string

/// What a removal of a whole schema object reports at each guard.
type ObjectRemovalWording =
    { OutsideManaged: string
      ExtensionOwned: string
      Incomplete: string
      DropsNotEnabled: string }

/// The single authority for whether Strata may propose removing anything.
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
[<RequireQualifiedAccess>]
module DropSafety =

    let private inManagedSchema (managedSchemas: string list) (schema: Identifier option) =
        match schema with
        | None -> false
        | Some schema ->
            managedSchemas
            |> List.exists (fun m -> Identifier.sameName (Identifier.unquoted m) schema)

    /// Is this object's schema one the project is allowed to change?
    let isManaged (managedSchemas: string list) (name: QualifiedName) = inManagedSchema managedSchemas name.Schema

    /// Whether a grant's object lies inside the schemas the project manages.
    ///
    /// A SCHEMA grant is managed when the schema itself is managed, not when
    /// some enclosing schema is: a schema is not inside anything. Getting this
    /// wrong in the lenient direction would let Strata revoke privileges on a
    /// schema the project never claimed.
    let isManagedGrant (managedSchemas: string list) (target: GrantTarget) =
        inManagedSchema managedSchemas (GrantTarget.schema target)

    /// A drop is only ever as trustworthy as the desired state that implies
    /// it, so this single flag gates every removal.
    let desiredComplete (desired: SchemaSnapshot) =
        match Completeness.stateOf "relations" desired.Completeness with
        | Complete -> true
        | Partial _
        | Inaccessible _
        | NotRequested -> false

    /// Whether the project claims ownership of a whole category — indexes,
    /// triggers — by declaring any of it.
    ///
    /// A project that declares NO index anywhere says nothing about them; one
    /// that declares any takes ownership, and only then may an undeclared one
    /// be dropped. The loader's completeness carries that distinction.
    let claimsCategory (category: string) (desired: SchemaSnapshot) =
        match Completeness.stateOf category desired.Completeness with
        | NotRequested -> false
        | Complete
        | Partial _
        | Inaccessible _ -> true

    let policyOf (inputs: Inputs) =
        { AllowDrops = inputs.AllowDrops
          ManagedSchemas = inputs.ManagedSchemas
          DesiredComplete = desiredComplete inputs.Desired }

    let private refusal (policy: RemovalPolicy) (guard: RemovalGuard) =
        match guard with
        | InManagedSchema (managed, detail) -> if managed then None else Some(OutsideManagedSchemas, detail)
        | NotExtensionOwned (scope, detail) -> if scope = ExtensionOwned then Some(ExtensionOwnedObject, detail) else None
        | DesiredStateLoaded detail -> if policy.DesiredComplete then None else Some(DesiredStateIncomplete, detail)
        | DropsEnabled detail -> if policy.AllowDrops then None else Some(DropsNotEnabled, detail)

    /// Propose `change`, or report the first guard it fails against `object'`.
    let removal
        (policy: RemovalPolicy)
        (object': QualifiedName)
        (guards: RemovalGuard list)
        (change: Change)
        : Result<Change, Suppression> =
        match guards |> List.tryPick (refusal policy) with
        | Some (reason, detail) ->
            Result.Error
                { Object = object'
                  Reason = reason
                  Detail = detail }
        | None -> Ok change

    /// A whole schema object present in the database and absent from desired
    /// state: every guard, in the canonical order.
    let objectRemoval
        (policy: RemovalPolicy)
        (name: QualifiedName)
        (scope: ManagementScope)
        (wording: ObjectRemovalWording)
        (change: Change)
        =
        removal
            policy
            name
            [ InManagedSchema(isManaged policy.ManagedSchemas name, wording.OutsideManaged)
              NotExtensionOwned(scope, wording.ExtensionOwned)
              DesiredStateLoaded wording.Incomplete
              DropsEnabled wording.DropsNotEnabled ]
            change
