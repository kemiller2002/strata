namespace Strata.Host.Files

open System.IO
open System.Text.Json

/// Writing and reconciling `strata.json`.
///
/// Authority for: `strata init --from-tree`, `strata add` and `strata sync`.
///
/// ## Why these are required rather than a convenience
///
/// `DF-STRATA-2026-7D14` made the project directory CLOSED: every `.sql` file
/// under the schema root must be listed in `include`, and every listed path must
/// exist. Both directions are errors. That redundancy is the whole mechanism —
/// a directory walk cannot notice that a file was added, because there is
/// nothing for it to disagree with.
///
/// The cost is that every new object needs a manifest line, with zero leniency.
/// Without a way to write that line, the pressure to add a glob or a lenient
/// mode becomes irresistible, and both give the property straight back. So these
/// commands are part of the decision, not an afterthought to it.
///
/// ## And why `sync` shows its work
///
/// A `sync` that silently adopts whatever is on disk IS the directory walk,
/// wearing a command's clothes. It would mean a file dropped into the tree —
/// by anyone, or anything — joins the project the next time someone runs a
/// build. So `sync` prints every path it would add or remove and does nothing
/// without `--confirm`.
///
/// The reviewable diff is the point. `init --from-tree` is the same idea at the
/// other end: it writes a manifest a human reads once, rather than a mode that
/// keeps deciding on their behalf.
module Manifests =

    let private normalise (path: string) = path.Replace('\\', '/')

    /// Every `.sql` file under the schema root, as paths relative to the project
    /// root, sorted.
    ///
    /// `*.sql` only, the same scope `Project.read` enforces: a README must not
    /// break a build, and `customer.sql.orig` left by a merge is not an object.
    let objectFiles (root: string) (schemaRoot: string) =
        if not (Directory.Exists schemaRoot) then
            []
        else
            Directory.GetFiles(schemaRoot, "*.sql", SearchOption.AllDirectories)
            |> Array.map (fun full -> normalise (Path.GetRelativePath(root, full)))
            |> Array.sort
            |> Array.toList

    /// Render a manifest.
    ///
    /// Hand-written rather than serialized, and indented rather than compact:
    /// this file is READ and EDITED by people, which is the opposite of the
    /// artifact's requirement. Paths are sorted so that two people who add
    /// different files do not produce a conflict on the same line.
    let render (existing: JsonElement option) (includes: string list) =
        let others =
            match existing with
            | None -> []
            | Some root ->
                root.EnumerateObject()
                |> Seq.filter (fun p -> p.Name <> "include")
                |> Seq.map (fun p -> p.Name, p.Value.GetRawText())
                |> List.ofSeq

        let body =
            [ yield
                  "  \"include\": [\n"
                  + (includes
                     |> List.sort
                     |> List.map (fun p -> "    " + JsonSerializer.Serialize p)
                     |> String.concat ",\n")
                  + "\n  ]"
              for name, raw in others -> sprintf "  %s: %s" (JsonSerializer.Serialize name) raw ]

        "{\n" + String.concat ",\n" body + "\n}\n"

    /// What a reconciliation would change.
    type Reconciliation =
        { /// On disk and not listed. These JOIN the project.
          ToAdd: string list
          /// Listed and not on disk. These LEAVE it — and the file is already
          /// gone, so this removes a line that refers to nothing.
          ToRemove: string list }

    let isClean (r: Reconciliation) = List.isEmpty r.ToAdd && List.isEmpty r.ToRemove

    let reconcile (declared: string list) (onDisk: string list) =
        let declaredSet = declared |> List.map normalise |> Set.ofList
        let onDiskSet = onDisk |> List.map normalise |> Set.ofList

        { ToAdd = Set.difference onDiskSet declaredSet |> Set.toList |> List.sort
          ToRemove = Set.difference declaredSet onDiskSet |> Set.toList |> List.sort }
