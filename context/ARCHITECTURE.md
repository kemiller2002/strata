# Strata architecture

## Current architecture

No product architecture has been accepted.

The repository currently separates:

- governance and operating contracts;
- compact current context;
- research evidence, hypotheses, experiments, and packages;
- architecture and decision documentation;
- generated registries and derived outputs;
- implementation and tests, once the first slice is selected.

## Required first decision

After selecting the first vertical slice, record the smallest architecture that
can deliver it and the boundaries that would be costly to reverse.

## Architectural constraints

- Canonical records must remain independent of any model vendor or chat.
- Secrets and sensitive communication content must not enter fixtures, logs, or
  prompts.
- Generated views must not silently replace canonical source records.
