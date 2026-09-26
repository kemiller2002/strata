namespace Strata.Host.Files

open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes
open Strata.Semantic.Provenance

/// The JSON codec for the Praxis provenance interchange record
/// (`praxis.provenance-record`, Praxis RQ-ROS-2026-A013, A015).
///
/// Authority for: reading, validating, extending and writing a provenance
/// record without losing anything (NFR-013).
///
/// ## Everything is written through the raw document
///
/// A reading keeps the record exactly as received beside the parsed model.
/// `append` clones that raw document and touches only the contributing run's
/// own entry, then proves — with `successorProblems` — that nothing else
/// changed. So fields this version does not model, on the record, on each
/// contribution and on each actor, survive every hop; an unsupported major
/// version is never interpreted, modified or extended; and a lineage source's
/// record is carried verbatim, never re-rendered.
///
/// No dependency on Praxis: this is Strata's own implementation of the
/// contract, checked against Praxis's vendored conformance fixtures.
module ProvenanceJson =

    [<RequireQualifiedAccess; NoComparison>]
    type Reading =
        /// `contract`/`version` present, supported major.
        | Current of record: Record * raw: JsonObject
        /// A bare `{"contributions": ...}` (legacy v1): read as 1.0.0.
        | Unversioned of record: Record * raw: JsonObject
        /// A well-labelled record in a major this build does not support:
        /// carried verbatim, never interpreted.
        | Unsupported of version: string * raw: JsonObject

    let raw (reading: Reading) =
        match reading with
        | Reading.Current (_, node)
        | Reading.Unversioned (_, node)
        | Reading.Unsupported (_, node) -> node

    let private problem field message : Problem = { Field = field; Message = message }

    let private stringOf (node: JsonNode) =
        match node with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, text -> Some text
            | _ -> None
        | _ -> None

    let private stringArray (prefix: string) (node: JsonNode) : Result<string list, Problem list> =
        match node with
        | null -> Ok []
        | :? JsonArray as items ->
            let values = items |> Seq.map stringOf |> Seq.toList

            if values |> List.forall Option.isSome then
                Ok(List.choose id values)
            else
                Error [ problem prefix "must be an array of strings" ]
        | _ -> Error [ problem prefix "must be an array of strings" ]

    let private collect (results: Result<'a, Problem list> list) : Result<'a list, Problem list> =
        match results |> List.collect (function Error ps -> ps | Ok _ -> []) with
        | [] -> results |> List.choose (function Ok v -> Some v | Error _ -> None) |> Ok
        | problems -> Error problems

    let private errors (results: Result<unit, Problem list> list) =
        results |> List.collect (function Error ps -> ps | Ok () -> [])

    // ---- actor --------------------------------------------------------------

    /// The canonical actor object. Key order kind, id, provider, model,
    /// runtime; the last three omitted for a human.
    let actorNode (actor: Actor) : JsonObject =
        let node = JsonObject()
        node.["kind"] <- JsonValue.Create(ActorKind.code actor.Kind)
        node.["id"] <- JsonValue.Create actor.Id

        [ "provider", actor.Provider; "model", actor.Model; "runtime", actor.Runtime ]
        |> List.iter (fun (name, value) -> value |> Option.iter (fun text -> node.[name] <- JsonValue.Create text))

        node

    let private parseActor (prefix: string) (node: JsonNode) : Result<Actor, Problem list> =
        match node with
        | :? JsonObject as item ->
            match stringOf item.["kind"] with
            | None -> Error [ problem (prefix + ".kind") "actor kind is required" ]
            | Some text ->
                match ActorKind.tryParse text with
                | None -> Error [ problem (prefix + ".kind") (sprintf "unknown actor kind '%s'" text) ]
                | Some kind ->
                    Ok
                        { Kind = kind
                          Id = stringOf item.["id"] |> Option.defaultValue ""
                          Provider = stringOf item.["provider"]
                          Model = stringOf item.["model"]
                          Runtime = stringOf item.["runtime"] }
        | null -> Error [ problem prefix "actor is required" ]
        | _ -> Error [ problem prefix "actor must be an object" ]

    // ---- contributions ------------------------------------------------------

    let private parseContribution (key: string) (node: JsonNode) : Result<Contribution, Problem list> =
        let prefix = "contributions." + key

        match node with
        | :? JsonObject as entry ->
            let operations = stringArray (prefix + ".operations") entry.["operations"]
            let evidence = stringArray (prefix + ".evidence") entry.["evidence"]
            let actor = parseActor (prefix + ".actor") entry.["actor"]

            match operations, evidence, actor with
            | Ok texts, Ok evidenceItems, Ok actor ->
                let unknown =
                    texts
                    |> List.filter (Operation.tryParse >> Option.isNone)
                    |> List.map (fun text -> problem (prefix + ".operations") (sprintf "unknown operation '%s'" text))

                let duplicates =
                    if List.length (List.distinct texts) <> List.length texts then
                        [ problem (prefix + ".operations") "operations must be unique" ]
                    else
                        []

                match unknown @ duplicates with
                | [] ->
                    Ok
                        { Key = key
                          Operations = texts |> List.choose Operation.tryParse
                          At = stringOf entry.["at"] |> Option.defaultValue ""
                          Last = stringOf entry.["last"]
                          Actor = actor
                          Reason = stringOf entry.["reason"]
                          Evidence = evidenceItems }
                | problems -> Error problems
            | _ ->
                Error(errors [ Result.map ignore operations; Result.map ignore evidence; Result.map ignore actor ])
        | _ -> Error [ problem prefix "contribution must be an object" ]

    let private parseContributions (node: JsonNode) : Result<Contribution list, Problem list> =
        match node with
        | :? JsonObject as entries ->
            entries |> Seq.map (fun pair -> parseContribution pair.Key pair.Value) |> Seq.toList |> collect
        | null -> Error [ problem "contributions" "contributions is required" ]
        | _ -> Error [ problem "contributions" "contributions must be an object keyed by execution (EXE-...) or contribution (CTB-...) ID" ]

    /// The canonical form of one contribution: operations, at, last?, actor,
    /// reason?, evidence?.
    let contributionNode (contribution: Contribution) : JsonObject =
        let node = JsonObject()
        let operations = JsonArray()
        contribution.Operations |> List.iter (fun op -> operations.Add(JsonValue.Create(Operation.code op)))
        node.["operations"] <- operations
        node.["at"] <- JsonValue.Create contribution.At
        contribution.Last |> Option.iter (fun last -> node.["last"] <- JsonValue.Create last)
        node.["actor"] <- actorNode contribution.Actor
        contribution.Reason |> Option.iter (fun reason -> node.["reason"] <- JsonValue.Create reason)

        if not (List.isEmpty contribution.Evidence) then
            let evidence = JsonArray()
            contribution.Evidence |> List.iter (fun item -> evidence.Add(JsonValue.Create item))
            node.["evidence"] <- evidence

        node

    // ---- the record ---------------------------------------------------------

    let rec private readAt (depth: int) (node: JsonNode) : Result<Reading, Problem list> =
        match node with
        | :? JsonObject as raw ->
            let body (version: ContractVersion) : Result<Record, Problem list> =
                let contributions = parseContributions raw.["contributions"]
                let derivedFrom = stringArray "derivedFrom" raw.["derivedFrom"]

                let subject =
                    match raw.["subject"] with
                    | null -> Ok None
                    | value ->
                        match stringOf value with
                        | Some text -> Ok(Some text)
                        | None -> Error [ problem "subject" "subject must be a string" ]

                let sources =
                    match raw.["sources"] with
                    | null -> Ok []
                    | :? JsonObject as snapshots when depth >= MaxSourceDepth && snapshots.Count > 0 ->
                        Error [ problem "sources" (sprintf "lineage snapshots nest deeper than %d levels" MaxSourceDepth) ]
                    | :? JsonObject as snapshots ->
                        snapshots
                        |> Seq.map (fun pair ->
                            match readAt (depth + 1) pair.Value with
                            | Ok (Reading.Current (source, _))
                            | Ok (Reading.Unversioned (source, _)) -> Ok(pair.Key, Snapshot.Known source)
                            | Ok (Reading.Unsupported (v, _)) -> Ok(pair.Key, Snapshot.Opaque v)
                            | Error problems ->
                                Error(problems |> List.map (fun p -> { p with Field = "sources." + pair.Key + "." + p.Field })))
                        |> Seq.toList
                        |> collect
                    | _ -> Error [ problem "sources" "sources must be an object keyed by lineage reference" ]

                match contributions, derivedFrom, subject, sources with
                | Ok contributions, Ok lineage, Ok subject, Ok snapshots ->
                    Ok
                        { Version = version
                          Subject = subject
                          Contributions = contributions
                          DerivedFrom = lineage
                          Sources = snapshots }
                | _ ->
                    Error(
                        errors
                            [ Result.map ignore contributions
                              Result.map ignore derivedFrom
                              Result.map ignore subject
                              Result.map ignore sources ]
                    )

            match raw.["contract"], raw.["version"] with
            | null, null -> body ContractVersion.current |> Result.map (fun r -> Reading.Unversioned(r, raw))
            | contract, _ when stringOf contract <> Some ContractName ->
                Error [ problem "contract" (sprintf "contract must be '%s'" ContractName) ]
            | _, version ->
                match version |> stringOf |> Option.bind ContractVersion.tryParse with
                | None -> Error [ problem "version" "version must be a semantic version (MAJOR.MINOR.PATCH)" ]
                | Some parsed when not (ContractVersion.isSupported parsed) ->
                    Ok(Reading.Unsupported(ContractVersion.code parsed, raw))
                | Some parsed -> body parsed |> Result.map (fun r -> Reading.Current(r, raw))
        | _ -> Error [ problem "" "a provenance record must be a JSON object" ]

    /// Reads a record's structure. See `validate` for the rules.
    let read (node: JsonNode) = readAt 0 node

    let parse (text: string) : Result<Reading, Problem list> =
        try
            JsonNode.Parse text |> read
        with :? JsonException as error ->
            Error [ problem "" ("not valid JSON: " + error.Message) ]

    /// Reads and applies every structural rule. An unsupported major is not
    /// an error: the reading says so, and the caller carries it verbatim.
    let validate (node: JsonNode) : Result<Reading, Problem list> =
        match read node with
        | Ok (Reading.Current (record, _) as reading)
        | Ok (Reading.Unversioned (record, _) as reading) ->
            match Record.problems record with
            | [] -> Ok reading
            | problems -> Error problems
        | other -> other

    /// Compact text of a node, escaping only what JSON requires, so a value
    /// read in is written back as the same characters.
    let toText (node: JsonNode) =
        if isNull node then "null" else node.ToJsonString(JsonSerializerOptions(Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping))

    let private sameNode (left: JsonNode) (right: JsonNode) = JsonNode.DeepEquals(left, right)

    /// JSON-level preservation for one hop: everything `before` carried,
    /// including fields this build does not model, is still present and
    /// unchanged, except the fields a run may extend in its own entry.
    let private preservationProblems (before: JsonObject) (after: JsonObject) : Problem list =
        let modelled = set [ "contributions"; "derivedFrom"; "sources"; "version"; "contract" ]

        let topLevel =
            before
            |> Seq.filter (fun pair -> not (modelled.Contains pair.Key))
            |> Seq.filter (fun pair -> not (after.ContainsKey pair.Key) || not (sameNode pair.Value after.[pair.Key]))
            |> Seq.map (fun pair -> problem pair.Key "field was removed or changed; fields a consumer does not model must be preserved")
            |> Seq.toList

        let extendable = set [ "operations"; "evidence"; "last"; "reason" ]

        let contributions =
            match before.["contributions"], after.["contributions"] with
            | (:? JsonObject as previous), (:? JsonObject as current) ->
                previous
                |> Seq.collect (fun pair ->
                    match pair.Value, current.[pair.Key] with
                    | (:? JsonObject as entry), (:? JsonObject as successor) ->
                        entry
                        |> Seq.filter (fun field -> not (extendable.Contains field.Key))
                        |> Seq.filter (fun field -> not (successor.ContainsKey field.Key) || not (sameNode field.Value successor.[field.Key]))
                        |> Seq.map (fun field ->
                            problem
                                ("contributions." + pair.Key + "." + field.Key)
                                "field was removed or changed; another contributor's entry must be preserved verbatim")
                        |> Seq.toList
                    | _ -> [])
                |> Seq.toList
            | _ -> []

        let snapshots =
            match before.["sources"], after.["sources"] with
            | (:? JsonObject as previous), (:? JsonObject as current) ->
                previous
                |> Seq.filter (fun pair -> current.ContainsKey pair.Key && not (sameNode pair.Value current.[pair.Key]))
                |> Seq.map (fun pair -> problem ("sources." + pair.Key) "lineage snapshot must be carried verbatim")
                |> Seq.toList
            | _ -> []

        topLevel @ contributions @ snapshots

    /// Whether `after` is a non-destructive successor of `before` (Praxis
    /// RQ-ROS-2026-A015): nothing dropped, no actor overwritten, no history
    /// replaced, no execution lost, unknown fields kept. An unsupported
    /// `before` must be carried unchanged.
    let successorProblems (before: JsonObject) (after: JsonObject) : Problem list =
        let interpreted reading =
            match reading with
            | Reading.Current (record, _)
            | Reading.Unversioned (record, _) -> Some record
            | Reading.Unsupported _ -> None

        match read before, read after with
        | Ok (Reading.Unsupported _), _ ->
            if sameNode before after then []
            else [ problem "" "a record in an unsupported major version must be carried verbatim" ]
        | Error _, _ -> [ problem "" "the previous record is malformed; refusing to judge a successor of it" ]
        | _, Error problems -> problems
        | Ok previous, Ok current ->
            match interpreted previous, interpreted current with
            | Some p, Some c -> Record.successorProblems p c @ preservationProblems before after
            | _ -> [ problem "version" "a supported record was replaced by an unsupported major version" ]

    let private envelope (subject: string option) =
        let node = JsonObject()
        node.["contract"] <- JsonValue.Create ContractName
        node.["version"] <- JsonValue.Create(ContractVersion.code ContractVersion.current)
        subject |> Option.iter (fun value -> node.["subject"] <- JsonValue.Create value)
        node

    /// Appends one run's contribution (or merges it into that run's own entry)
    /// and proves nothing else changed. Refuses a malformed record and an
    /// unsupported major. A legacy unversioned block gains the envelope.
    let append (contribution: Contribution) (raw: JsonObject) : Result<JsonObject, Problem list> =
        match validate raw with
        | Error problems -> Error(problem "" "refusing to extend malformed provenance" :: problems)
        | Ok (Reading.Unsupported (version, _)) ->
            Error [ problem "version" (sprintf "provenance version %s is not supported by this build; it is carried verbatim, not extended" version) ]
        | Ok (Reading.Current (record, _))
        | Ok (Reading.Unversioned (record, _)) ->
            match Record.append contribution record with
            | Error message -> Error [ problem ("contributions." + contribution.Key) message ]
            | Ok updated ->
                let merged = updated.Contributions |> List.find (fun c -> c.Key = contribution.Key)
                let result = raw.DeepClone().AsObject()

                if isNull result.["contract"] then
                    result.["contract"] <- JsonValue.Create ContractName
                    result.["version"] <- JsonValue.Create(ContractVersion.code ContractVersion.current)

                let contributions = result.["contributions"].AsObject()
                let canonical = contributionNode merged

                match contributions.[contribution.Key] with
                | :? JsonObject as existing ->
                    for name in [ "operations"; "last"; "evidence"; "reason" ] do
                        match canonical.[name] with
                        | null -> ()
                        | value -> existing.[name] <- value.DeepClone()
                | _ -> contributions.[contribution.Key] <- canonical

                match validate result with
                | Error problems -> Error problems
                | Ok _ ->
                    match successorProblems raw result with
                    | [] -> Ok result
                    | problems -> Error(problem "" "the appended record failed its own preservation check" :: problems)

    /// A fresh record whose history starts with this contribution.
    let create (subject: string option) (contribution: Contribution) : Result<JsonObject, Problem list> =
        let record = { Record.empty with Subject = subject; Contributions = [ contribution ] }

        match Record.problems record with
        | [] ->
            let node = envelope subject
            let contributions = JsonObject()
            contributions.[contribution.Key] <- contributionNode contribution
            node.["contributions"] <- contributions
            Ok node
        | problems -> Error problems

    /// A new subject derived from others. `creator` authors it; each supplied
    /// source record is carried verbatim under `sources` and named in
    /// `derivedFrom`; `lineageOnly` names sources whose record is not held.
    /// A source's contributors never become the subject's.
    let derive
        (subject: string)
        (creator: Contribution)
        (lineageOnly: string list)
        (sources: (string * JsonObject) list)
        : Result<JsonObject, Problem list> =
        let snapshots =
            sources
            |> List.map (fun (reference, node) ->
                match validate node with
                | Ok (Reading.Current (record, _))
                | Ok (Reading.Unversioned (record, _)) -> Ok(reference, Snapshot.Known record)
                | Ok (Reading.Unsupported (version, _)) -> Ok(reference, Snapshot.Opaque version)
                | Error problems -> Error(problems |> List.map (fun p -> { p with Field = "sources." + reference + "." + p.Field })))
            |> collect

        match snapshots with
        | Error problems -> Error problems
        | Ok snapshots ->
            match Record.derive subject creator lineageOnly snapshots with
            | Error message -> Error [ problem "contributions" message ]
            | Ok record ->
                match Record.problems record with
                | problems when not (List.isEmpty problems) -> Error problems
                | _ ->
                    let node = envelope (Some subject)
                    let contributions = JsonObject()
                    contributions.[creator.Key] <- contributionNode creator
                    node.["contributions"] <- contributions
                    let lineage = JsonArray()
                    record.DerivedFrom |> List.iter (fun r -> lineage.Add(JsonValue.Create r))
                    node.["derivedFrom"] <- lineage

                    if not (List.isEmpty sources) then
                        let carried = JsonObject()

                        sources
                        |> List.distinctBy fst
                        |> List.iter (fun (reference, source) -> carried.[reference] <- source.DeepClone())

                        node.["sources"] <- carried

                    Ok node

    let describeProblems (problems: Problem list) =
        problems
        |> List.map (fun p -> if p.Field = "" then p.Message else p.Field + ": " + p.Message)
        |> String.concat "; "
