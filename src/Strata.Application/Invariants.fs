namespace Strata.Application

open Strata.Semantic.Identity
open Strata.Semantic.Schema

/// Rules a project declares about itself.
///
/// Authority for: compile-time checks about the PROJECT's rules, as opposed to
/// PostgreSQL's.
///
/// ## Why these belong with the type check rather than in a linter
///
/// `DF-STRATA-2026-8B60` puts three questions in one pass: does it parse, do the
/// names exist, do the types work. "Every table has a primary key" is the same
/// KIND of question — decidable from the project alone, answerable before
/// anything is deployed, and worth stopping a build over. The difference is only
/// whose rule it is.
///
/// Separating them would mean a project could compile and still be wrong in a
/// way its own authors had written down, which is the definition of a check
/// nobody runs.
///
/// ## An invariant nobody asked for is not enforced
///
/// Every rule here is opt-in, by name, in `strata.json`. Turning them all on by
/// default would fail every existing project on upgrade, and a check everyone
/// disables protects nobody — the same reasoning that keeps `--require-signature`
/// and `--types` opt-in.
///
/// ## An invariant NAME nobody recognises is an error
///
/// A misspelled rule in a manifest must not read as "no rule". A project that
/// asks for `everyTableHasAPrimarykey` and gets silence has a security or
/// correctness control it believes is on and is not, which is strictly worse
/// than never having asked. Unknown names are refused and the known ones listed.
module Invariants =

    /// One rule a project can ask for.
    type Rule =
        { /// The name as it appears in `strata.json`.
          Name: string
          /// What it rules out, in one line, for the error message and the help.
          Summary: string }

    /// A rule a project asked for and broke.
    type Violation =
        { Rule: string
          /// The object at fault, so the message points somewhere.
          Object: QualifiedName
          Detail: string }

    let everyTableHasAPrimaryKey =
        { Name = "everyTableHasAPrimaryKey"
          Summary = "a table with no primary key has no identity, so nothing can reference or de-duplicate its rows" }

    let noGrantsToPublic =
        { Name = "noGrantsToPublic"
          Summary = "PUBLIC includes every current and future role, so a grant to it cannot be reasoned about" }

    let everyForeignKeyIsIndexed =
        { Name = "everyForeignKeyIsIndexed"
          Summary =
            "an unindexed foreign key makes every delete on the parent scan the child, and takes a lock while it does" }

    /// Every rule this build knows. Named individually rather than discovered by
    /// reflection: a rule that appears because someone added a type is a rule
    /// nobody decided to ship.
    let all = [ everyTableHasAPrimaryKey; noGrantsToPublic; everyForeignKeyIsIndexed ]

    let private tables (snapshot: SchemaSnapshot) =
        snapshot.Objects
        |> List.choose (fun o ->
            match o with
            | TableObject t -> Some t
            | _ -> None)

    let private folded (names: Identifier list) = names |> List.map Identifier.folded

    /// Names a project asked for that this build does not know.
    let unknown (requested: string list) =
        let known = all |> List.map (fun r -> r.Name) |> Set.ofList
        requested |> List.filter (fun name -> not (known.Contains name))

    let private checkPrimaryKeys (snapshot: SchemaSnapshot) =
        tables snapshot
        |> List.filter (fun t -> t.PrimaryKey.IsNone)
        |> List.map (fun t ->
            { Rule = everyTableHasAPrimaryKey.Name
              Object = t.Name
              Detail = "declares no primary key" })

    let private checkPublicGrants (grants: Grant list) =
        grants
        |> List.filter (fun g -> g.Grantee.ToUpperInvariant() = "PUBLIC")
        |> List.map (fun g ->
            { Rule = noGrantsToPublic.Name
              Object =
                match GrantTarget.schema g.Target with
                | Some schema -> QualifiedName.qualified schema (Identifier.unquoted "(grant)")
                | None -> QualifiedName.unqualified (Identifier.unquoted "(grant)")
              Detail = sprintf "grants %s to PUBLIC" (String.concat ", " g.Privileges) })

    /// A foreign key is indexed when some declared index or key begins with its
    /// columns, in order.
    ///
    /// A PREFIX, not an exact match: an index on `(a, b)` serves a foreign key on
    /// `(a)`, and PostgreSQL will use it. Requiring exactness would report a
    /// project that is already correct, and a rule that cries wolf is a rule that
    /// gets switched off.
    ///
    /// The primary key and unique constraints count, because both are backed by
    /// an index.
    let private checkForeignKeyIndexes (snapshot: SchemaSnapshot) =
        tables snapshot
        |> List.collect (fun t ->
            let covering =
                [ yield! t.Indexes |> List.map (fun i -> folded i.Columns)
                  match t.PrimaryKey with
                  | Some pk -> yield folded pk.Columns
                  | None -> ()
                  yield! t.UniqueConstraints |> List.map (fun u -> folded u.Columns) ]

            t.ForeignKeys
            |> List.filter (fun fk ->
                let wanted = folded fk.Columns

                not (
                    covering
                    |> List.exists (fun columns ->
                        List.length columns >= List.length wanted
                        && List.truncate (List.length wanted) columns = wanted)
                ))
            |> List.map (fun fk ->
                { Rule = everyForeignKeyIsIndexed.Name
                  Object = t.Name
                  Detail =
                    sprintf
                      "the foreign key on (%s) has no index beginning with those columns"
                      (fk.Columns |> List.map (fun c -> c.Text) |> String.concat ", ") }))

    /// Check the rules a project asked for.
    ///
    /// Only the rules NAMED are checked. The result is every violation, not the
    /// first: a build that reports one problem per run makes the author run it
    /// once per problem.
    let check (requested: string list) (snapshot: SchemaSnapshot) (grants: Grant list) : Violation list =
        let asked name = requested |> List.contains name

        [ if asked everyTableHasAPrimaryKey.Name then yield! checkPrimaryKeys snapshot
          if asked noGrantsToPublic.Name then yield! checkPublicGrants grants
          if asked everyForeignKeyIsIndexed.Name then yield! checkForeignKeyIndexes snapshot ]
        |> List.sortBy (fun v -> QualifiedName.display v.Object, v.Rule, v.Detail)
