# Feature Manifest — `<feature name>`

This manifest routes engineers and agents to authority. Link to semantic rules;
do not restate or independently implement them here. Use `none` or
`not applicable — <reason>` rather than silently omitting a category.

## Purpose

`<one or two sentences>`

## Ownership

- State, including presentation state: `<paths and symbols>`
- Transitions / commands / messages: `<paths and symbols>`
- Invariants and guards: `<paths and symbols or none — reason>`
- Capabilities / authority: `<paths and symbols or none — reason>`
- Important effects and effect contracts: `<paths and symbols or none — reason>`

## Interfaces

- Inbound: `<public contracts/endpoints/events or none — reason>`
- Outbound: `<public contracts/effects/events or none — reason>`

## Tests and verification

- Local behavior tests: `<paths/commands>`
- Boundary/contract tests: `<paths/commands or none — reason>`
- Integration/live verification: `<paths/commands or none — reason>`

## Dependencies

- Allowed direct dependencies: `<feature/contract names or none — reason>`
- Required composition context: `<paths or none — reason>`

## Modification boundaries

- Normal: `<paths>`
- Escalation required: `<paths/boundaries and why>`

## Local agent instructions

- `<path to nested instruction file or none — reason>`

## Maintenance

- Owner: `<team/role>`
- Last checked against implementation: `<YYYY-MM-DD>`
- Known gaps: `<none or explicit gaps>`

