# Strata

This repository is a greenfield pilot running Repository Operating System
2.0.1-main.78.1.

## Start here

1. Read [`AGENTS.md`](AGENTS.md) and [`BOOTSTRAP.md`](BOOTSTRAP.md).
2. Complete [`PROJECT-CHARTER.md`](PROJECT-CHARTER.md).
3. Establish the baseline in [`context/CURRENT-STATE.md`](context/CURRENT-STATE.md).
4. Select the first bounded mission and its observable acceptance criteria.
5. Record durable evidence, decisions, and handoffs as the work proceeds.

## Local operating commands

```bash
./ros work begin TASK-001 --type task
./ros work context TASK-001
./ros status
./ros registry check
./ros registry build
./ros validate
```

`work context` reports legal actions and completion evidence. Validation errors include repair instructions; use `./ros validate --json` for machine-readable output. Complete work with explicit evidence paths as described in `docs/work-protocol.md`.

The installed snapshot is self-contained. It does not read from the source ROS
repository. `.ros/installation.json` records the package version and checksums
of installed files.

## Pilot rule

The operating system is itself under evaluation. Do not infer that
Strata is a validated discipline, method, or product merely because
the repository follows a rigorous process. Measure whether the process improves
decisions, traceability, handoffs, and rework relative to the declared baseline.
