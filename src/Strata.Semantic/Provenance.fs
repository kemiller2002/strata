namespace Strata.Semantic

open System
open System.Globalization
open System.Text.RegularExpressions

/// Who or what produced a Strata artifact, and in which run: the Praxis
/// provenance interchange record (`praxis.provenance-record` 1.x, Praxis
/// RQ-ROS-2026-A013), as Strata's own types.
///
/// Authority for: the local representation of the contract and its
/// structural rules (PR-026, NFR-013, DF-STRATA-2026-E4B7).
///
/// ## Not the same provenance as `Evidence`
///
/// `Evidence` says why Strata believes a fact about a DATABASE (a catalog row,
/// a view body). This module says which ACTOR produced or acted on an
/// artifact. The two are never mixed: the actor who compiled or analysed a
/// project is not the author of the SQL in it, and nothing here names one.
///
/// ## Self-reported
///
/// Everything here is self-reported identity (Praxis RQ-ROS-2026-A010). It is
/// not authentication, not authorization, and not evidence. The signature in
/// `Attestation` is the custody evidence; this is a claim beside it.
///
/// ## Mapping to the wire, stated so it can be checked
///
/// Each type below is the contract's shape one-to-one: `Actor` is the actor
/// object, `Contribution` one entry of `contributions` (its map key is `Key`),
/// `Record` the envelope. Fields the contract has and this version does not
/// model are NOT held here; the host codec keeps the raw JSON beside the
/// parsed record and writes through it, so they survive (Praxis
/// RQ-ROS-2026-A015). Tier 1 holds no JSON.
module Provenance =

    /// An actor category. `Extension` is a namespaced `x-...` kind.
    [<RequireQualifiedAccess>]
    type ActorKind =
        | Agent
        | Human
        | Automation
        | Unknown
        | Extension of string

    /// The stable "who". `Provider`/`Model`/`Runtime` are `None` for a human
    /// (not applicable) and `Some "unknown"` when applicable and not known;
    /// the two are never conflated.
    type Actor =
        { Kind: ActorKind
          Id: string
          Provider: string option
          Model: string option
          Runtime: string option }

    [<RequireQualifiedAccess>]
    type Operation =
        | Created
        | Modified
        | Reviewed
        | Approved
        | Superseded
        | Migrated
        | Extension of string

    /// One actor's contribution, keyed by the run that made it (`EXE-...`) or,
    /// for a human or automation outside any run, a `CTB-...` key.
    type Contribution =
        { Key: string
          Operations: Operation list
          At: string
          Last: string option
          Actor: Actor
          Reason: string option
          Evidence: string list }

    type ContractVersion = { Major: int; Minor: int; Patch: int }

    /// A lineage source's own record, carried verbatim. One in a major version
    /// this build does not support is opaque: kept, never interpreted.
    [<RequireQualifiedAccess>]
    type Snapshot =
        | Known of Record
        | Opaque of version: string

    and Record =
        { Version: ContractVersion
          Subject: string option
          Contributions: Contribution list
          DerivedFrom: string list
          Sources: (string * Snapshot) list }

    type Problem = { Field: string; Message: string }

    [<Literal>]
    let ContractName = "praxis.provenance-record"

    [<Literal>]
    let UnknownValue = "unknown"

    [<Literal>]
    let MaxSourceDepth = 16

    let private regex pattern = Regex(pattern, RegexOptions.CultureInvariant)

    let private extensionPattern = regex "^x-[a-z0-9][a-z0-9-]*$"

    let private problem field message = { Field = field; Message = message }

    /// Refuses the unambiguous shapes of common credentials, so a value that
    /// would leak one is rejected rather than recorded. A guard against
    /// accidents, not a secret scanner.
    [<RequireQualifiedAccess>]
    module Credentials =
        let private patterns =
            [ @"\bsk-(?:ant-|proj-)?[A-Za-z0-9_-]{16,}"
              @"\bgh[pousr]_[A-Za-z0-9]{20,}"
              @"\bgithub_pat_[A-Za-z0-9_]{20,}"
              @"\bxox[abposr]-[A-Za-z0-9-]{10,}"
              @"\bAKIA[0-9A-Z]{16}\b"
              @"\bAIza[0-9A-Za-z_-]{30,}"
              @"-----BEGIN [A-Z ]*PRIVATE KEY-----"
              @"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{16,}"
              @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}"
              @"(?i)\b(?:api[_-]?key|access[_-]?token|secret|password|passwd)\s*[=:]\s*\S{8,}" ]
            |> List.map regex

        let looksLikeCredential (value: string) =
            not (isNull value) && patterns |> List.exists (fun p -> p.IsMatch value)

    [<RequireQualifiedAccess>]
    module ActorKind =
        let code kind =
            match kind with
            | ActorKind.Agent -> "agent"
            | ActorKind.Human -> "human"
            | ActorKind.Automation -> "automation"
            | ActorKind.Unknown -> "unknown"
            | ActorKind.Extension value -> value

        let tryParse (value: string) =
            match value with
            | "agent" -> Some ActorKind.Agent
            | "human" -> Some ActorKind.Human
            | "automation" -> Some ActorKind.Automation
            | "unknown" -> Some ActorKind.Unknown
            | other when not (isNull other) && extensionPattern.IsMatch other -> Some(ActorKind.Extension other)
            | _ -> None

    [<RequireQualifiedAccess>]
    module Operation =
        let code operation =
            match operation with
            | Operation.Created -> "created"
            | Operation.Modified -> "modified"
            | Operation.Reviewed -> "reviewed"
            | Operation.Approved -> "approved"
            | Operation.Superseded -> "superseded"
            | Operation.Migrated -> "migrated"
            | Operation.Extension value -> value

        let tryParse (value: string) =
            match value with
            | "created" -> Some Operation.Created
            | "modified" -> Some Operation.Modified
            | "reviewed" -> Some Operation.Reviewed
            | "approved" -> Some Operation.Approved
            | "superseded" -> Some Operation.Superseded
            | "migrated" -> Some Operation.Migrated
            | other when not (isNull other) && extensionPattern.IsMatch other -> Some(Operation.Extension other)
            | _ -> None

    [<RequireQualifiedAccess>]
    module Actor =
        /// Nothing declared and nothing detected: recorded as unknown, never
        /// guessed.
        let unknown =
            { Kind = ActorKind.Unknown
              Id = UnknownValue
              Provider = Some UnknownValue
              Model = Some UnknownValue
              Runtime = Some UnknownValue }

        let describe (actor: Actor) =
            let detail =
                [ actor.Provider; actor.Model; actor.Runtime ]
                |> List.choose id
                |> function
                    | [] -> ""
                    | values -> " (" + String.concat ", " values + ")"

            ActorKind.code actor.Kind + ":" + actor.Id + detail

        let problems (actor: Actor) : (string * string) list =
            [ if isNull actor.Id || actor.Id.Trim().Length = 0 then
                  "id", "actor id must not be empty; use 'unknown' when it is not known"
              match actor.Kind with
              | ActorKind.Extension value when ActorKind.tryParse value <> Some actor.Kind ->
                  "kind", sprintf "invalid actor kind '%s'" value
              | ActorKind.Agent ->
                  for field, value in [ "provider", actor.Provider; "model", actor.Model; "runtime", actor.Runtime ] do
                      match value with
                      | None -> field, sprintf "agent actor must record %s (use 'unknown' when it is not known)" field
                      | Some text when text.Trim().Length = 0 -> field, sprintf "agent actor %s must not be empty" field
                      | Some _ -> ()
              | _ -> ()
              for field, value in [ "id", Some actor.Id; "provider", actor.Provider; "model", actor.Model; "runtime", actor.Runtime ] do
                  match value with
                  | Some text when Credentials.looksLikeCredential text ->
                      field, "value looks like a credential; identity must never carry secrets"
                  | _ -> () ]

        /// Two records describe the same actor when kind and every known
        /// attribute agree; an `unknown` attribute is not a contradiction.
        let agrees (left: Actor) (right: Actor) =
            let known (value: string) = value.Trim().Length > 0 && value <> UnknownValue

            let compatible (a: string option) (b: string option) =
                match a, b with
                | Some x, Some y when known x && known y -> x = y
                | _ -> true

            left.Kind = right.Kind
            && (left.Id = right.Id || not (known left.Id) || not (known right.Id))
            && compatible left.Provider right.Provider
            && compatible left.Model right.Model
            && compatible left.Runtime right.Runtime

    [<RequireQualifiedAccess>]
    module Contribution =
        let private executionPattern = regex "^EXE-[A-Za-z0-9._-]+$"
        let private contributionPattern = regex "^CTB-[A-Za-z0-9._-]+$"

        let private timestampPattern =
            regex "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?Z$"

        let private systemPattern = regex "^[a-z][a-z0-9-]*$"
        let private runPattern = regex "^[A-Za-z0-9_-][A-Za-z0-9._-]*$"

        let isExecutionKey (value: string) = not (isNull value) && executionPattern.IsMatch value

        let isValidKey (value: string) =
            isExecutionKey value || (not (isNull value) && contributionPattern.IsMatch value)

        let private parseInstant (value: string) =
            match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
            | true, instant -> Some instant
            | _ -> None

        let isTimestamp (value: string) =
            not (isNull value) && timestampPattern.IsMatch value && (parseInstant value).IsSome

        /// Invalid timestamps sort last, so they never pose as the originator.
        let instant (contribution: Contribution) =
            parseInstant contribution.At |> Option.defaultValue DateTimeOffset.MaxValue

        /// A run of a system other than Praxis, namespaced so it can never be
        /// mistaken for a Praxis execution (Praxis RQ-ROS-2026-A014).
        let foreignExecutionKey (system: string) (run: string) : Result<string, string> =
            if not (systemPattern.IsMatch system) then
                Error(sprintf "system '%s' must match ^[a-z][a-z0-9-]*$" system)
            elif not (runPattern.IsMatch run) then
                Error(sprintf "run '%s' must match ^[A-Za-z0-9_-][A-Za-z0-9._-]*$" run)
            else
                Ok(sprintf "EXE-%s.%s" system run)

        let isCreation (contribution: Contribution) =
            contribution.Operations |> List.contains Operation.Created

        let problems (contribution: Contribution) : (string * string) list =
            [ if not (isValidKey contribution.Key) then
                  "key",
                  sprintf "contribution key '%s' must be an execution ID (EXE-...) or a contribution ID (CTB-...)" contribution.Key
              if List.isEmpty contribution.Operations then
                  "operations", "contribution must record at least one operation"
              if List.length (List.distinct contribution.Operations) <> List.length contribution.Operations then
                  "operations", "operations must be unique"
              if not (isTimestamp contribution.At) then
                  "at", sprintf "'%s' is not an ISO-8601 UTC timestamp (yyyy-MM-ddTHH:mm:ss[.fff]Z)" contribution.At
              match contribution.Last with
              | Some last when not (isTimestamp last) -> "last", sprintf "'%s' is not an ISO-8601 UTC timestamp" last
              | Some last when instant { contribution with At = last } < instant contribution -> "last", "last must not precede at"
              | _ -> ()
              yield! Actor.problems contribution.Actor |> List.map (fun (field, message) -> "actor." + field, message)
              if contribution.Actor.Kind = ActorKind.Agent && not (isExecutionKey contribution.Key) then
                  "key", "an agent contribution must be keyed by the execution (EXE-...) that produced it"
              if contribution.Evidence |> List.exists (fun item -> isNull item || item.Trim().Length = 0) then
                  "evidence", "evidence references must not be empty"
              if contribution.Reason |> Option.exists Credentials.looksLikeCredential then
                  "reason", "reason looks like it contains a credential; provenance must never carry secrets"
              if contribution.Evidence |> List.exists Credentials.looksLikeCredential then
                  "evidence", "an evidence reference looks like a credential; provenance must never carry secrets" ]

    [<RequireQualifiedAccess>]
    module ContractVersion =
        let private pattern = regex "^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$"

        /// The version Strata writes.
        let current = { Major = 1; Minor = 0; Patch = 0 }

        let code (v: ContractVersion) = sprintf "%d.%d.%d" v.Major v.Minor v.Patch

        let tryParse (value: string) =
            if isNull value then
                None
            else
                let m = pattern.Match value

                if not m.Success then
                    None
                else
                    let part (i: int) = Int32.TryParse(m.Groups.[i].Value, NumberStyles.None, CultureInfo.InvariantCulture)

                    match part 1, part 2, part 3 with
                    | (true, major), (true, minor), (true, patch) -> Some { Major = major; Minor = minor; Patch = patch }
                    | _ -> None

        /// Any minor or patch of a supported major is read.
        let isSupported (v: ContractVersion) = v.Major = current.Major

    [<RequireQualifiedAccess>]
    module Record =
        let private referencePattern = regex "^\S+$"

        let empty =
            { Version = ContractVersion.current
              Subject = None
              Contributions = []
              DerivedFrom = []
              Sources = [] }

        let private ordered (contributions: Contribution list) =
            contributions
            |> List.sortWith (fun left right ->
                match compare (Contribution.instant left) (Contribution.instant right) with
                | 0 -> String.CompareOrdinal(left.Key, right.Key)
                | byTime -> byTime)

        let originator (record: Record) = record.Contributions |> List.tryFind Contribution.isCreation

        let rec private problemsAt (prefix: string) (depth: int) (record: Record) : Problem list =
            let at field = if prefix = "" then field else prefix + "." + field

            let perContribution =
                record.Contributions
                |> List.collect (fun c ->
                    Contribution.problems c
                    |> List.map (fun (field, message) -> problem (at ("contributions." + c.Key + "." + field)) message))

            let creationProblems =
                match record.Contributions |> List.filter Contribution.isCreation with
                | [] -> []
                | [ creation ] ->
                    record.Contributions
                    |> List.filter (fun c -> Contribution.instant c < Contribution.instant creation)
                    |> List.map (fun c ->
                        problem
                            (at ("contributions." + c.Key + ".at"))
                            (sprintf "contribution precedes the recorded creation (%s at %s)" creation.Key creation.At))
                | many ->
                    [ problem
                          (at "contributions")
                          ("more than one contribution claims 'created': " + (many |> List.map (fun c -> c.Key) |> String.concat ", ")) ]

            let referenceProblems name (values: string list) =
                values
                |> List.filter (fun v -> isNull v || not (referencePattern.IsMatch v))
                |> List.map (fun v -> problem (at name) (sprintf "reference '%s' must be a non-empty token without whitespace" v))

            let credentialProblems =
                [ yield! record.Subject |> Option.toList |> List.map (fun v -> "subject", v)
                  yield! record.DerivedFrom |> List.map (fun v -> "derivedFrom", v) ]
                |> List.filter (snd >> Credentials.looksLikeCredential)
                |> List.map (fun (field, _) -> problem (at field) "value looks like a credential; provenance must never carry secrets")

            let lineageProblems =
                [ match record.Subject with
                  | Some subject when List.contains subject record.DerivedFrom ->
                      problem (at "derivedFrom") (sprintf "'%s' cannot be derived from itself" subject)
                  | _ -> ()
                  if List.length (List.distinct record.DerivedFrom) <> List.length record.DerivedFrom then
                      problem (at "derivedFrom") "lineage references must be unique"
                  for reference, snapshot in record.Sources do
                      if not (List.contains reference record.DerivedFrom) then
                          problem (at ("sources." + reference)) "a lineage snapshot must name a reference listed in derivedFrom"

                      match snapshot with
                      | Snapshot.Known source when source.Subject.IsSome && source.Subject <> Some reference ->
                          problem
                              (at ("sources." + reference + ".subject"))
                              (sprintf
                                  "the snapshot describes '%s', not '%s'; a lineage snapshot must be the named source's own provenance"
                                  source.Subject.Value
                                  reference)
                      | _ -> () ]

            let sourceProblems =
                record.Sources
                |> List.collect (fun (reference, snapshot) ->
                    match snapshot with
                    | Snapshot.Opaque _ -> []
                    | Snapshot.Known _ when depth >= MaxSourceDepth ->
                        [ problem (at ("sources." + reference)) (sprintf "lineage snapshots nest deeper than %d levels" MaxSourceDepth) ]
                    | Snapshot.Known source -> problemsAt (at ("sources." + reference)) (depth + 1) source)

            perContribution
            @ creationProblems
            @ referenceProblems "subject" (Option.toList record.Subject)
            @ referenceProblems "derivedFrom" record.DerivedFrom
            @ credentialProblems
            @ lineageProblems
            @ sourceProblems

        /// Structural problems of a record and its lineage snapshots. Empty
        /// means well-formed; it says nothing about whether the identities
        /// recorded are true.
        let problems (record: Record) = problemsAt "" 0 record

        /// Appends a contribution, append-only: a new key is added; the same
        /// key (the same run acting again) merges new operations and evidence
        /// into its own entry and advances `last`, but only when the actor
        /// agrees. A second `created`, a late `created` and re-attribution are
        /// refused.
        let append (contribution: Contribution) (record: Record) : Result<Record, string> =
            match record.Contributions |> List.tryFind (fun c -> c.Key = contribution.Key) with
            | Some existing when not (Actor.agrees existing.Actor contribution.Actor) ->
                Error(
                    sprintf
                        "contribution '%s' is already attributed to %s; refusing to re-attribute it to %s"
                        contribution.Key
                        (Actor.describe existing.Actor)
                        (Actor.describe contribution.Actor))
            | Some existing ->
                let added xs ys = xs @ (ys |> List.filter (fun y -> not (List.contains y xs)))
                let latest = { existing with At = existing.Last |> Option.defaultValue existing.At }

                let merged =
                    { existing with
                        Operations = added existing.Operations contribution.Operations
                        Evidence = added existing.Evidence contribution.Evidence
                        Last =
                            if Contribution.instant contribution > Contribution.instant latest then
                                Some contribution.At
                            else
                                existing.Last
                        Reason = existing.Reason |> Option.orElse contribution.Reason }

                Ok { record with Contributions = record.Contributions |> List.map (fun c -> if c.Key = existing.Key then merged else c) |> ordered }
            | None when Contribution.isCreation contribution && (originator record).IsSome ->
                Error "the subject already has a recorded creator; record this contribution as 'modified' instead of 'created'"
            | None when
                Contribution.isCreation contribution
                && record.Contributions |> List.exists (fun c -> Contribution.instant c < Contribution.instant contribution)
                ->
                Error "a 'created' contribution cannot follow existing contributions"
            | None -> Ok { record with Contributions = record.Contributions @ [ contribution ] |> ordered }

        /// Starts the record of a subject derived from others. The creator
        /// authors the subject; the sources are lineage only and are never
        /// merged into its contributions.
        let derive (subject: string) (creator: Contribution) (derivedFrom: string list) (sources: (string * Snapshot) list) : Result<Record, string> =
            if not (Contribution.isCreation creator) then
                Error "the first contribution to a derived subject must be 'created'"
            else
                Ok
                    { empty with
                        Subject = Some subject
                        Contributions = [ creator ]
                        DerivedFrom = (derivedFrom @ (sources |> List.map fst)) |> List.distinct
                        Sources = sources |> List.distinctBy fst }

        /// One-hop destructive-transformation check at the model level: every
        /// contribution of `before` survives in `after` with the same actor,
        /// the same `at`, its operations, evidence and reason; the originator,
        /// lineage and subject are unchanged. JSON-level preservation of
        /// fields this version does not model is the host codec's check.
        let successorProblems (before: Record) (after: Record) : Problem list =
            let afterByKey = after.Contributions |> List.map (fun c -> c.Key, c) |> Map.ofList

            let contributionProblems =
                before.Contributions
                |> List.collect (fun previous ->
                    let field name = "contributions." + previous.Key + name

                    match afterByKey |> Map.tryFind previous.Key with
                    | None -> [ problem (field "") "contribution was removed; provenance history is append-only" ]
                    | Some current ->
                        [ if current.Actor <> previous.Actor then
                              problem
                                  (field ".actor")
                                  (sprintf "actor changed from %s to %s" (Actor.describe previous.Actor) (Actor.describe current.Actor))
                          if current.At <> previous.At then
                              problem (field ".at") "the time of the first recorded operation changed"
                          for operation in previous.Operations do
                              if not (List.contains operation current.Operations) then
                                  problem (field ".operations") (sprintf "operation '%s' was removed" (Operation.code operation))
                          for item in previous.Evidence do
                              if not (List.contains item current.Evidence) then
                                  problem (field ".evidence") (sprintf "evidence '%s' was removed" item)
                          match previous.Reason with
                          | Some reason when current.Reason <> Some reason -> problem (field ".reason") "reason was rewritten"
                          | _ -> () ])

            let originProblems =
                match originator before, originator after with
                | Some previous, Some current when previous.Key <> current.Key ->
                    [ problem "contributions" (sprintf "originator changed from %s to %s" previous.Key current.Key) ]
                | _ -> []

            let lineageProblems =
                [ for reference in before.DerivedFrom do
                      if not (List.contains reference after.DerivedFrom) then
                          problem "derivedFrom" (sprintf "lineage reference '%s' was removed" reference)
                  for reference, _ in before.Sources do
                      if not (after.Sources |> List.exists (fun (key, _) -> key = reference)) then
                          problem ("sources." + reference) "lineage snapshot was removed"
                  if before.Subject.IsSome && after.Subject <> before.Subject then
                      problem "subject" "subject changed; a different subject needs its own record that derives from this one"
                  if after.Version.Major <> before.Version.Major then
                      problem "version" "major version changed in place; an unsupported major must be carried verbatim"
                  elif compare (after.Version.Minor, after.Version.Patch) (before.Version.Minor, before.Version.Patch) < 0 then
                      problem
                          "version"
                          (sprintf
                              "version was lowered from %s to %s; a newer record must not be relabelled as an older one"
                              (ContractVersion.code before.Version)
                              (ContractVersion.code after.Version)) ]

            contributionProblems @ originProblems @ lineageProblems
