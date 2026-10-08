namespace Strata.Application.SchemaDiff

open Strata.Semantic.Identity
open Strata.Semantic.Schema
open Strata.Analysis.ProposedChange

/// Object comments: `COMMENT ON ... IS`, which PostgreSQL keeps in
/// `pg_description`.
///
/// ## Ownership
///
/// The rule indexes and triggers follow. A project that declares no comment
/// anywhere says nothing about comments, so the database's are left alone and
/// disclosed. One that declares any takes ownership of the comments on every
/// object it DECLARES — and on nothing else: a comment on an object the project
/// does not declare belongs to whatever does, and the object itself is the
/// other families' business.
///
/// A deployed comment the project no longer declares is a removal, decided by
/// `DropSafety` like every other one. Nothing breaks when documentation goes,
/// but nothing about it can be recovered either, and a project that loaded
/// partially is not authority to remove anything.
module Comments =

    let private key (c: Comment) = CommentTarget.key c.Target

    /// Whether the commented object lives in a managed schema. A schema's own
    /// comment lives in that schema.
    let private inManagedSchema (managedSchemas: string list) (target: CommentTarget) =
        match CommentTarget.schema target with
        | Some schema -> DropSafety.isManaged managedSchemas (QualifiedName.qualified schema schema)
        | None -> false

    /// Whether the project declares the object a comment documents.
    let private declaresObjectOf (inputs: Inputs) (target: CommentTarget) =
        let declared (name: QualifiedName) =
            inputs.Desired.Objects |> List.exists (fun o -> Names.same (SchemaObject.name o) name)

        // An index's name is schema-scoped, and its schema is its table's.
        let declaresIndex (name: QualifiedName) =
            SnapshotObjects.tables inputs.Desired
            |> List.exists (fun t ->
                Option.map Identifier.folded t.Name.Schema = Option.map Identifier.folded name.Schema
                && t.Indexes |> List.exists (fun i -> Identifier.sameName i.Name name.Name))

        match target with
        | CommentTarget.Schema _ -> inManagedSchema inputs.ManagedSchemas target
        | CommentTarget.Relation (RelationKind.Index, name) -> declaresIndex name
        | CommentTarget.Routine (name, arguments) ->
            inputs.Desired.Objects
            |> List.exists (function
                | RoutineObject r -> Names.same r.Name name && r.ArgumentTypes = arguments
                | _ -> false)
        | CommentTarget.Relation (_, name)
        | CommentTarget.Type name
        | CommentTarget.Column (name, _)
        | CommentTarget.Constraint (name, _)
        | CommentTarget.Trigger (name, _) -> declared name

    /// Comment differences, and what could not be compared.
    let compare (policy: RemovalPolicy) (inputs: Inputs) : FamilyDiff =
        let declared = inputs.DeclaredComments

        match inputs.ActualComments with
        | None ->
            { Differences = []
              Disclosures =
                if List.isEmpty declared then
                    []
                else
                    [ { Object = QualifiedName.unqualified (Identifier.unquoted "(comments)")
                        Reason = NotCompared
                        Detail = "the database's comments could not be read, so declared comments were NOT compared" } ] }
        | Some deployed ->

        let owned = deployed |> List.filter (fun c -> declaresObjectOf inputs c.Target)

        if List.isEmpty declared then
            { Differences = []
              Disclosures =
                if List.isEmpty owned then
                    []
                else
                    [ { Object = QualifiedName.unqualified (Identifier.unquoted "(comments)")
                        Reason = NotModelled
                        Detail =
                          sprintf
                              "%d comment(s) exist on objects this project declares, and it declares none, so comments were NOT compared. Declaring any COMMENT ON takes ownership of them."
                              (List.length owned) } ] }
        else

        let deployedByKey = deployed |> List.map (fun c -> key c, c) |> Map.ofList
        let declaredKeys = declared |> List.map key |> Set.ofList

        // A comment on an object the project does not declare would be set on
        // whatever happens to hold that name, or fail because nothing does.
        // Withheld and said so, rather than guessed at.
        let setting =
            declared
            |> List.choose (fun d ->
                if not (declaresObjectOf inputs d.Target) then
                    Some(
                        Error
                            { Object = CommentTarget.name d.Target
                              Reason = NotModelled
                              Detail =
                                sprintf
                                    "a comment is declared on %s, which this project does not declare, so it is NOT applied"
                                    (CommentTarget.key d.Target) }
                    )
                else
                    match Map.tryFind (key d) deployedByKey with
                    | Some a when a.Text = d.Text -> None
                    | _ -> Some(Ok(SetComment(d.Target, d.Text))))

        let removing =
            owned
            |> List.filter (fun a -> not (Set.contains (key a) declaredKeys))
            |> List.map (fun a ->
                let detail = sprintf "the comment on %s is not declared" (CommentTarget.key a.Target)

                DropSafety.removal
                    policy
                    (CommentTarget.name a.Target)
                    [ InManagedSchema(
                          inManagedSchema policy.ManagedSchemas a.Target,
                          sprintf "%s, but it lies outside the managed schemas" detail
                      )
                      DesiredStateLoaded(sprintf "%s, but desired state did not load completely" detail)
                      DropsEnabled(sprintf "%s; pass --allow-drops to remove it" detail) ]
                    (RemoveComment a.Target))

        { Differences = setting @ removing; Disclosures = [] }
