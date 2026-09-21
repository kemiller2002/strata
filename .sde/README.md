# State-Directed Engineering

This project uses State-Directed Engineering (SDE).

Before engineering a change:

1. Identify the semantic feature; read the project's repository semantic map
   if ownership is unclear.
2. Read the feature manifest and declared local context. If the semantic map
   explicitly says a separate manifest is not needed for a small/obvious area,
   use that map entry as the routing declaration. If a nontrivial project has
   no usable map/manifest yet, use `templates/repository-semantic-map.md` and
   `templates/feature-manifest.md` without inventing feature boundaries.
3. Read `method/CONSTRUCTION-METHOD.md` and classify the requested work using
   `method/CHANGE-CLASSIFICATION.md`.
4. Follow `method/NAVIGATION-AND-CONTEXT.md` and
   `method/AGENT-EXECUTION-RULES.md`.
5. Apply the architecture documents in `architecture/` relevant to the change.
6. Follow `method/VERIFICATION-METHOD.md` before declaring completion.

Templates for recording work are in `templates/`. Term definitions and
engineering metrics are in `reference/`.

Files under `.sde/` are versioned methodology inputs. Do not modify them
directly. If this project needs to deviate from SDE, record that decision
outside this directory rather than editing these files.

Installed SDE version: see `VERSION`.
Installation provenance and file hashes: see `MANIFEST.json`.
What is installed, in machine-readable form: see `../.echelon/sde.json`.

Managing this installation:

```
npx @echelon-foundry/sde status    # what is installed (read-only)
npx @echelon-foundry/sde verify    # integrity and structural review (read-only)
npx @echelon-foundry/sde doctor    # explain any problem and how to fix it
npx @echelon-foundry/sde upgrade   # move to a newer SDE release
```

Every command accepts `--json` for machine-readable output, and `upgrade`
accepts `--dry-run` to see what it would change without changing anything.

Structural findings from `verify` are review warnings, not proof of
nonconformance; use a project-root `sde.config.json` only when the default
source extensions or line bands do not fit the project. That file belongs to
this project — SDE reads it and never rewrites it.
