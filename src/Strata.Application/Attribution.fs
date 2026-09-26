namespace Strata.Application

open Strata.Semantic.Provenance

/// Who is acting in this Strata run, and what the approval policy says about it.
///
/// Authority for: resolving the acting identity from explicit declarations and
/// a fixed whitelist of non-secret environment variables (PR-026, PR-027),
/// choosing the run key a contribution is recorded under, and the opt-in
/// `--require-human-approval` policy (DF-STRATA-2026-E4B7).
///
/// Pure: the environment arrives as a lookup function and the local run id as
/// a value, so the host decides what is read and when the clock is read.
///
/// ## Nothing is guessed
///
/// An explicit flag wins over the environment; a known agent runtime or GitHub
/// Actions implies a kind only because that variable identifies it; anything
/// else is `unknown`. Git authorship, the SQL's author and earlier runs are
/// never consulted. Everything resolved here is self-reported (Praxis
/// RQ-ROS-2026-A010): it can say an AGENT is acting when an agent says so, and
/// it cannot prove a HUMAN is.
module Attribution =

    /// What the operator declared on the command line.
    type Declared =
        { Kind: string option
          Actor: string option
          Provider: string option
          Model: string option
          Runtime: string option
          Execution: string option }

    let nothingDeclared : Declared =
        { Kind = None
          Actor = None
          Provider = None
          Model = None
          Runtime = None
          Execution = None }

    /// The only environment variables read. None of them is a secret, and none
    /// of their VALUES is recorded except the declared identity fields and a
    /// run id.
    let environmentVariables =
        [ "ROS_ACTOR_KIND"; "ROS_ACTOR"; "ROS_TELEMETRY_PROVIDER"; "ROS_TELEMETRY_MODEL"; "ROS_TELEMETRY_RUNTIME"
          "ROS_EXECUTION_ID"; "CODEX_SESSION_ID"; "CODEX_THREAD_ID"; "CLAUDE_CODE_SESSION_ID"; "GEMINI_SESSION_ID"
          "COPILOT_SESSION_ID"; "GITHUB_ACTIONS"; "GITHUB_RUN_ID"; "GITHUB_RUN_ATTEMPT" ]

    /// The acting identity for one run.
    type Context =
        { Actor: Actor
          /// The contribution key: a propagated Praxis execution, or this
          /// Strata run namespaced as `EXE-strata.<run>`.
          Execution: string
          /// How the actor was determined, for the operator to read.
          Mechanism: string
          /// Whether anything identified the actor or the run. Nothing
          /// declared and nothing detected means an all-`unknown` actor.
          Identified: bool
          /// The runtime of a known agent whose session variable is present,
          /// whatever was declared. A declared human inside one is a
          /// contradiction the approval policy reports.
          AgentRuntime: string option }

    [<Literal>]
    let System = "strata"

    let private nonEmpty (value: string option) =
        value |> Option.map (fun v -> v.Trim()) |> Option.filter (fun v -> v.Length > 0)

    /// Resolve who is acting. `localRun` is this process's own run id, used
    /// only when no execution is propagated and no CI run id exists.
    let resolve (declared: Declared) (environment: string -> string option) (localRun: string) : Result<Context, string> =
        let env name = environment name |> nonEmpty
        let explicitKind = nonEmpty declared.Kind |> Option.orElse (env "ROS_ACTOR_KIND")
        let explicitId = nonEmpty declared.Actor |> Option.orElse (env "ROS_ACTOR")
        let explicitProvider = nonEmpty declared.Provider |> Option.orElse (env "ROS_TELEMETRY_PROVIDER")
        let explicitModel = nonEmpty declared.Model |> Option.orElse (env "ROS_TELEMETRY_MODEL")
        let explicitRuntime = nonEmpty declared.Runtime |> Option.orElse (env "ROS_TELEMETRY_RUNTIME")
        let githubActions = env "GITHUB_ACTIONS" = Some "true"

        let agentRuntime =
            if (env "CODEX_SESSION_ID").IsSome || (env "CODEX_THREAD_ID").IsSome then Some "codex"
            elif (env "CLAUDE_CODE_SESSION_ID").IsSome then Some "claude-code"
            elif (env "GEMINI_SESSION_ID").IsSome then Some "gemini-cli"
            elif (env "COPILOT_SESSION_ID").IsSome then Some "copilot"
            else None
        let neitherExplicit = explicitProvider.IsNone && explicitRuntime.IsNone

        // Presence of a known agent runtime's session variable identifies the
        // runtime; its value (a session id) is never recorded.
        let detected =
            if neitherExplicit && ((env "CODEX_SESSION_ID").IsSome || (env "CODEX_THREAD_ID").IsSome) then
                Some("openai", "codex", ActorKind.Agent, "codex-environment")
            elif neitherExplicit && (env "CLAUDE_CODE_SESSION_ID").IsSome then
                Some("anthropic", "claude-code", ActorKind.Agent, "claude-code-environment")
            elif neitherExplicit && (env "GEMINI_SESSION_ID").IsSome then
                Some("google", "gemini-cli", ActorKind.Agent, "gemini-cli-environment")
            elif neitherExplicit && (env "COPILOT_SESSION_ID").IsSome then
                Some("github", "copilot", ActorKind.Agent, "copilot-environment")
            elif explicitProvider.IsNone && githubActions then
                Some("github", explicitRuntime |> Option.defaultValue "github-actions", ActorKind.Automation, "github-actions-environment")
            else
                None

        let kindResult =
            match explicitKind with
            | Some text ->
                match ActorKind.tryParse text with
                | Some kind -> Ok kind
                | None -> Error(sprintf "unknown actor kind '%s'; expected agent, human, automation, unknown, or x-<extension>" text)
            | None ->
                Ok(detected |> Option.map (fun (_, _, kind, _) -> kind) |> Option.defaultValue ActorKind.Unknown)

        let executionResult =
            match nonEmpty declared.Execution |> Option.orElse (env "ROS_EXECUTION_ID") with
            | Some key when Contribution.isExecutionKey key && not (Credentials.looksLikeCredential key) -> Ok key
            | Some key -> Error(sprintf "execution '%s' is not an execution ID (EXE-...)" key)
            | None ->
                let run =
                    match env "GITHUB_RUN_ID" with
                    | Some runId when githubActions ->
                        sprintf "gh-%s-%s" runId (env "GITHUB_RUN_ATTEMPT" |> Option.defaultValue "1")
                    | _ -> localRun

                Contribution.foreignExecutionKey System run

        match kindResult, executionResult with
        | Error message, _
        | _, Error message -> Error message
        | Ok kind, Ok execution ->
            let provider = explicitProvider |> Option.orElse (detected |> Option.map (fun (p, _, _, _) -> p))
            let runtime = explicitRuntime |> Option.orElse (detected |> Option.map (fun (_, r, _, _) -> r))
            let known (value: string option) = value |> Option.exists (fun v -> v <> UnknownValue)

            let id =
                match explicitId with
                | Some value -> value
                | None when kind <> ActorKind.Human && known provider && known runtime -> provider.Value + "/" + runtime.Value
                | None -> UnknownValue

            let actor: Actor =
                match kind with
                | ActorKind.Human ->
                    { Kind = kind
                      Id = id
                      Provider = None
                      Model = None
                      Runtime = None }
                | _ ->
                    { Kind = kind
                      Id = id
                      Provider = Some(provider |> Option.defaultValue UnknownValue)
                      Model = Some(explicitModel |> Option.defaultValue UnknownValue)
                      Runtime = Some(runtime |> Option.defaultValue UnknownValue) }

            let explicitAnything =
                [ explicitKind; explicitId; explicitProvider; explicitModel; explicitRuntime ] |> List.exists Option.isSome

            let propagated = nonEmpty declared.Execution |> Option.orElse (env "ROS_EXECUTION_ID") |> Option.isSome

            match Actor.problems actor with
            | (field, message) :: _ -> Error(sprintf "the declared identity is not usable (%s: %s)" field message)
            | [] ->
                Ok
                    { Actor = actor
                      Execution = execution
                      Mechanism =
                        if explicitAnything then "declared"
                        else detected |> Option.map (fun (_, _, _, m) -> m) |> Option.defaultValue "none"
                      Identified = explicitAnything || detected.IsSome || propagated
                      AgentRuntime = agentRuntime }

    /// This run's contribution to an artifact.
    let contribution (context: Context) (operation: Operation) (at: string) (reason: string option) (evidence: string list) : Contribution =
        { Key = context.Execution
          Operations = [ operation ]
          At = at
          Last = None
          Actor = context.Actor
          Reason = reason
          Evidence = evidence }

    /// `strata sign`'s operation. Not `reviewed` — signing asserts custody of a
    /// key, not that anyone read the artifact — and not `approved`, which is
    /// the deployment decision. An `x-` operation says exactly what happened.
    let SignedOperation = Operation.Extension "x-signed"

    /// What the approval policy says about the actor approving a deployment.
    type ApprovalCheck =
        /// A human (self-declared) approved.
        | Accepted
        /// Allowed, and the operator must be told why it deserves a look.
        | AcceptedWithWarning of string
        /// `--require-human-approval` was given and the approver is not a
        /// declared human.
        | Refused of string

    let private approverWarning (actor: Actor) =
        match actor.Kind with
        | ActorKind.Agent ->
            sprintf
                "the approving actor is an AGENT (%s). The gate's requires-approval findings were accepted by an agent, not by a human."
                (Actor.describe actor)
        | ActorKind.Unknown ->
            "the approving actor is UNKNOWN: nothing declared who passed --approve. Declare it with --actor-kind/--actor \
             (or ROS_ACTOR_KIND/ROS_ACTOR) so the approval can be attributed."
        | _ ->
            sprintf "the approving actor is not a human (%s)." (Actor.describe actor)

    /// Identity is not authorization: this is a policy the operator opts into,
    /// over a SELF-REPORTED identity. It stops an agent that honestly reports
    /// itself (or an unidentified caller) from approving; it cannot stop one
    /// that lies. A declared human inside a detected agent runtime is treated
    /// as the contradiction it is, not as a human.
    let checkApproval (requireHuman: bool) (context: Context) : ApprovalCheck =
        let refuse message =
            Refused(message + " --require-human-approval was given, so this approval is refused. Nothing was executed.")

        match context.Actor.Kind, context.AgentRuntime with
        | ActorKind.Human, None -> Accepted
        | ActorKind.Human, Some runtime ->
            let message =
                sprintf
                    "the approving actor is declared human (%s), but this process is running inside an agent runtime (%s)."
                    (Actor.describe context.Actor)
                    runtime

            if requireHuman then refuse message else AcceptedWithWarning message
        | _ when requireHuman -> refuse (approverWarning context.Actor)
        | _ -> AcceptedWithWarning(approverWarning context.Actor)
