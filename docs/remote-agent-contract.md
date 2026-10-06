# Praxis remote agent contract

This page is for an agent — or a person, or automation — that can reach
this repository on GitHub but has **no local .NET, F#, or Praxis runtime**.
Praxis governs such work just as strictly as it governs local work. You
send typed requests, and Praxis runs them for you.

The full specification is [`remote-protocol.md`](remote-protocol.md). This
page is all you need to take part.

## 1. Discover

Dispatch a `praxis.describe` request, described in section 2. The response's
`result` tells you:

- whether remote execution is available here;
- the Praxis version and protocol versions;
- every operation, with its required and optional arguments and the
  capability it needs;
- what this repository allows (`repository.capabilities`);
- the open and ready work;
- where results are kept.

If the repository does not have `.github/workflows/praxis-remote.yml`,
remote execution is not installed. Work locally, or ask a maintainer.

## 2. Send a request

Write the request as a document:

```json
{
  "protocol": "praxis.remote",
  "protocolVersion": "1.0",
  "requestId": "req-<something unique you keep>",
  "operation": "work.start",
  "repository": { "ref": "refs/heads/<branch>", "expectedSha": "<the commit you just read>" },
  "actor": { "kind": "agent", "id": "<your stable id>", "provider": "<if known>", "model": "<if known>", "runtime": "<if known>", "sessionId": "<if known>" },
  "arguments": { "workItemIds": ["<ID>"] }
}
```

Dispatch it on the branch it targets:

```
gh workflow run praxis-remote.yml --ref <branch> -f request_id=<requestId> -f request="$(cat request.json)"
```

You can use the REST API instead:
`POST /repos/{owner}/{repo}/actions/workflows/praxis-remote.yml/dispatches`,
with the body `{"ref": "<branch>", "inputs": {"request": "...", "request_id": "..."}}`.

**If you can commit to the repository but cannot dispatch a workflow.**
Some agent integrations can create branches and commit files but have no
Actions dispatch operation, and some run with no network in their sandbox.
Use the inbox instead (`DF-ROS-2026-A045`):

1. Create a branch named `praxis-inbox/<anything>` from the default branch,
   or reuse one. Never use the branch your request targets.
2. Commit the request document, unchanged, as
   `.praxis-inbox/<requestId>.json` on that branch.
3. The **Praxis remote inbox** workflow sees the push and dispatches
   `praxis-remote.yml` on the request's `repository.ref` with the file's
   exact bytes. A request with no ref, which is a read, runs on the default
   branch.

Everything else is the same. Praxis validates, authorizes and binds the
request, and your identity is what the request says. Because the target
branch does not move, your `expectedSha` stays valid. Read the result from
the journal on the target branch (section 3). A file the inbox cannot route
is not dispatched: the inbox run fails with an error that names the file.
To retry, commit the identical document again, since same `requestId` means
same intent. For your next request, commit a new file.

**Several operations in one run.** Use `"operation": "batch"` with
`"protocolVersion": "1.2"` and
`"arguments": {"requests": [{"requestId": "...", "operation": "...", "arguments": {...}}, ...]}`.
Each constituent keeps its own request ID and outcome. The batch stops at
the first constituent that does not succeed, and `result.stoppedAt` names
it.

## 3. Get the result

- **For a mutation, read the journal on the branch.** It is at
  `.ros/remote/requests/<requestId>.json` (a `:` in your request ID becomes
  `~`). This is the durable record. You can also send `request.status` with
  `{"requestId": "..."}`.
- **Or open the run.** It is named `praxis remote <requestId>`. Its summary
  and its `praxis-remote-response` artifact hold the same response.
- **Or read the job log.** The job that executed the request prints the
  same response in a `praxis.remote response` group. This is the way to
  read a read-only result, or a rejection, when you can read job logs but
  cannot download artifacts.

## 4. Rules that keep you governed

- **Say who you are, and nothing more.** Fill in only the actor fields you
  actually know. Leave out anything you do not know; Praxis records it as
  `unknown`. Never reuse another agent's ID or execution. Continue only your
  own execution, through `execution.id`.
- **One intent, one request ID.** If you do not know whether a request ran,
  because of a timeout, a lost result, or an outcome of `unknown`, send
  **the same document with the same `requestId`** again. If it already
  happened, you get the recorded result back with `replayed: true`. Never
  mint a new ID for the same intent, and never reuse an ID for a different
  intent. The second case is refused as `idempotency-conflict`.
- **Bind mutations to what you saw.** `repository.expectedSha` must be the
  commit you read before forming the request. If the branch has moved, you
  get `stale-ref`: read the branch again and form a new request.
- **Operations only.** There is no way to run a command. The operation
  catalog in `praxis.describe` is the whole surface.
- **Never put secrets in a request.** A request that contains what looks
  like a credential is refused as `secret-detected`.
- **Telemetry is what you report.** Values you send through
  `telemetry.record` are labelled as supplied by you. Report only numbers
  you actually have, and omit the rest. A missing value stays unknown; it
  is never zero.

## 5. Continue someone else's work, and what to do when you cannot reach Praxis

- **Make your work durable (protocol 1.3).** Push your commits through
  GitHub, then send `work.checkpoint` with `{workItemId, summary,
  nextAction, stepId?}` and your own `execution.id`. Praxis verifies that
  the branch head it is checked out at is exactly the remote branch head,
  and records the checkpoint. Checkpoint at coherent boundaries: after a
  meaningful slice, before a handoff, and before `work.complete`. Where the
  repository enforces durable checkpoints, `work.complete` refuses Git-backed
  work without a current one.
- **Taking over from an agent that disappeared (protocol 1.3).** Read
  `work.context`. Its `continuity` block names the checkpoint commit, what
  was completed, the next action, and whether the remote still carries it.
  Then send `work.continue` with `{workItemId}`. You get your own execution,
  with the predecessor as its parent; the predecessor is recorded as
  interrupted. No block and resume are needed. Continue before you
  complete: `work.complete` is refused as `domain-rejected` while the item
  still has an active execution that is not yours, whether or not you name
  an execution.
- **Handing off intentionally.** Checkpoint, then send `work.block` with a
  reason that says it is a handoff. If work that no checkpoint covers must be
  left behind, add `unrecoverableReason` stating truthfully why it cannot be
  made durable. `work.resume` is legal only from `blocked`.
- **Taking over from another agent.** Resume the work item as yourself. You
  get your own execution, and Praxis records its `parentExecutionId` as the
  predecessor's execution. You never continue, or record telemetry into, an
  execution that is not yours. Praxis refuses that, and it applies equally
  to another run of your own agent.
- **If the predecessor left the item active** and the executor only
  supports protocol 1.2, `work.resume` is refused as `domain-rejected`. Send
  `work.block` yourself, with a reason that names the predecessor's session
  or execution and says you are taking over, then `work.resume`. The block
  is recorded as your action, not the predecessor's. With 1.3, use
  `work.continue`.
- **You cannot run Praxis, cannot dispatch, and cannot commit.** Stop.
  Report what you recovered (the checkpoint, what was completed, the next
  action) and why you cannot continue. Hand off to an executor that can.
  Do not edit the work or `.ros/` state by hand, and do not ask anyone to
  send requests under your name. A request must come from the agent it
  names.
- **You could not invoke Praxis at all.** Commit your legitimate work
  normally. When Praxis is reachable again, attribute that work with
  `work.reconcile`, naming the commits. The attribution is recorded as
  `post-hoc`. The original Git author and committer are preserved, and you
  are recorded separately as the reconciliation actor. Never touch or
  recommit files to make them look attributed. Reconciliation is
  idempotent, and a change already reconciled to another work item is a
  conflict. It is never overwritten.

## 6. Read the outcome

| `failure.retry` | What to do |
|---|---|
| `never` | Fix the request. Examples: `invalid-request`, `unauthorized`, `domain-rejected`. |
| `after-refresh` | Re-read the branch, then form a new request with a new `expectedSha`. Examples: `stale-ref`, `concurrency-conflict`. |
| `same-request` | Retry the identical document with the same `requestId`. Examples: `timeout`, `transport-failed`. |

An `outcome` of `unknown` never means success. Retry with the same request
ID, or ask `request.status`.

A later agent can read everything you recorded and continue the work under
its own identity. That handoff is how continuity works here; nobody needs to
impersonate anyone.
