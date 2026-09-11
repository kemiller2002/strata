# ROS Agent Bootstrap

Follow this sequence before beginning work.

## 1. Read operating instructions

1. `framework/REP-SPECIFICATION.md`
2. `framework/policies/RESEARCH-POLICY.md`
3. `framework/policies/EVIDENCE-POLICY.md`
4. `framework/policies/OUTPUT-POLICY.md`
5. `framework/protocols/ARTIFACT-LIFECYCLE.md`
6. `framework/protocols/SUPERSESSION.md`
7. Any standards or discipline-specific profiles named by the mission

## 2. Read current shared context

1. `context/CURRENT-STATE.md`
2. `context/ARCHITECTURE.md`
3. `context/DECISIONS.md`
4. `context/KNOWN-RISKS.md`
5. Relevant project or domain context

## 3. Read the assigned mission

Read only the mission and source records needed to execute it. Do not load
the entire repository without a reason.

## 4. Execute

Before meaningful repository mutation, establish attributable intent with `./ros work begin WORK-ID` and inspect `./ros work context WORK-ID` for allowed actions and required evidence. Use `./ros status` for a compact repository check. Follow `docs/work-protocol.md`.

`work begin` automatically starts a provider-neutral execution record. Discover what the current runtime can expose, classify the work, ingest trustworthy runtime or tool output where available, and leave unavailable metrics unavailable rather than zero. Unknown provider fields belong in sanitized raw telemetry. `work complete` finalizes active records; follow `docs/development-telemetry.md` for adapters, R&D context, provenance, privacy, and aggregation.

- Identify the largest material uncertainty.
- Form explicit hypotheses.
- Seek supporting and contradicting evidence.
- Attempt to falsify conclusions.
- Preserve source traceability.
- Use deterministic tools before generative inference where practical.
- Record failures and invalidated assumptions.
- Record scope discoveries, correction signals, and telemetry provenance when they are knowable; do not ask a model to estimate what Git or a tool can calculate.

## 5. Record results

Create or update the appropriate canonical artifacts:

- Journal: `research/journals/`
- REP: `research/packages/`
- Evidence: `research/evidence/`
- Hypotheses: `research/hypotheses/`
- Theories: `research/theories/`
- Experiments: `research/experiments/`

Run `./ros registry build`; registries are generated and must not be edited
manually. Then run `./ros validate`.

## 6. Handoff

The next agent must be able to reconstruct the work, understand the current
theory, and continue without additional conversational context.
