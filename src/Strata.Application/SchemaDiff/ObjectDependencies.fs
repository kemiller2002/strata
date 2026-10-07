namespace Strata.Application.SchemaDiff

open Strata.Semantic.Identity
open Strata.Analysis.ProposedChange

/// Which views and routines of one plan must be created before which others.
///
/// Kept out of `Ordering`, which ranks change KINDS, because this ranks
/// OBJECTS by what their declaring text names — a different question with its
/// own failure modes.
module ObjectDependencies =

    /// The view or routine a change creates or replaces, if it is one.
    let viewOrRoutine (change: Change) =
        match change with
        | CreateView name
        | ReplaceView name
        | CreateRoutine name
        | ReplaceRoutine name -> Some name
        | _ -> None

    /// SQL text with its comments removed, so a name mentioned only in prose
    /// does not become an ordering constraint.
    let private withoutComments (text: string) =
        let blocks = System.Text.RegularExpressions.Regex.Replace(text, @"/\*[\s\S]*?\*/", " ")
        System.Text.RegularExpressions.Regex.Replace(blocks, @"--[^\n]*", " ")

    /// Whether `text` names `target` as a whole identifier: `app.v`, `"app"."v"`
    /// or a bare `v`, never `v` inside `v_total` or `tmp_v`.
    let private mentions (text: string) (target: QualifiedName) =
        let name = System.Text.RegularExpressions.Regex.Escape target.Name.Text

        let pattern =
            sprintf @"(?<![A-Za-z0-9_$])""?%s""?(?![A-Za-z0-9_$])" name

        System.Text.RegularExpressions.Regex.IsMatch(
            text,
            pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
        )

    /// How many other views and routines in this plan each one must wait for.
    ///
    /// Views and routines share a phase, and inside it the stable sort kept
    /// the order their family assembled them in — routines, then views, each
    /// alphabetically. PostgreSQL does not accept that order in general:
    ///
    ///   - `RETURNS SETOF app.open_orders` needs the VIEW's row type, so the
    ///     function fails with 42704 if it is created first;
    ///   - a `LANGUAGE sql` body is checked at CREATE, so a function that
    ///     selects from a view fails with 42P01 if the view comes later;
    ///   - a view that selects from another view, or calls a function, needs
    ///     that object first.
    ///
    /// Found by applying a real project (53 tables, 23 views, 114 routines) to
    /// an empty database: the plan could not be applied AT ALL, and being one
    /// transaction it rolled back whole.
    ///
    /// A dependency is any other view or routine of this plan whose name the
    /// declaring text mentions as a whole identifier, outside comments. That
    /// is deliberately broad: a mention that is not a real reference costs
    /// only an ordering constraint, never a statement. A cycle — which a real
    /// reference cannot form — stops the walk rather than recursing, and the
    /// objects in it keep their assembled order.
    let depths (declarations: (QualifiedName * string) list) (changes: Change list) =
        let objects = changes |> List.choose viewOrRoutine |> List.distinctBy QualifiedName.display

        let textOf (name: QualifiedName) =
            declarations
            |> List.tryFind (fun (n, _) -> Names.same n name)
            |> Option.map (snd >> withoutComments)

        let dependencies =
            objects
            |> List.map (fun name ->
                let found =
                    match textOf name with
                    | None -> []
                    | Some text ->
                        objects
                        |> List.filter (fun other -> not (Names.same other name) && mentions text other)

                QualifiedName.display name, found |> List.map QualifiedName.display)
            |> Map.ofList

        let rec depth (seen: Set<string>) (name: string) =
            if Set.contains name seen then
                0
            else
                match Map.tryFind name dependencies with
                | None
                | Some [] -> 0
                | Some ds -> 1 + (ds |> List.map (depth (Set.add name seen)) |> List.max)

        dependencies |> Map.map (fun name _ -> depth Set.empty name)
