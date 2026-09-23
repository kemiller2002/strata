# Echelon Shared Application Foundation Requirements

Status: **Required**
Application: **Strata**
Scope: **All current and future product requirements in this repository**
Date: 2026-09-23

## 1. Normative scope

These requirements are cross-cutting. Every existing and future requirement in this repository inherits them when the capability is applicable. A feature requirement does not need to repeat them.

An implementation MAY mark one of Aegis, Forma, or Folio not applicable only when the capability is genuinely outside that feature's boundary. The reason MUST be explicit and reviewable. Silence is not an exception.

Shared Echelon capabilities MUST be consumed rather than independently reimplemented when they already provide the required behavior. A missing shared capability MUST be recorded as a gap in the owning shared repository instead of being silently forked in Strata.

Dependencies MUST be pinned to an explicit released version or immutable artifact. Floating versions and tracking a moving repository branch are not acceptable application baselines.

## 2. Aegis

**Applicability for Strata: required at database, filesystem, parser-adapter, deployment, process, and other operational boundaries.**

Aegis is the required Echelon mechanism for **unexpected operational failure at architectural boundaries**.

### Requirements

1. Every .NET/F# host that owns an operational boundary MUST reference and use `EchelonFoundry.Aegis.Core`.
2. Integration-specific Aegis packages MUST be used when the corresponding integration exists. GitHub callers, for example, MUST use the approved Aegis GitHub translation package rather than inventing a second failure taxonomy.
3. Aegis MUST be configured explicitly at application composition/startup, with stable application identity, safe/redacted context, explicit sinks, and deterministic test substitutes.
4. Network, GitHub, storage, filesystem, parsing of externally sourced data, browser/WASM interop, renderer/export, database, process, and other external operational boundaries MUST route unexpected failures through Aegis capture/guard mechanisms as appropriate.
5. Expected domain outcomes MUST NOT be converted into Aegis faults. Validation refusal, illegal state transition, authorization refusal, ambiguity, conflict, and other modeled outcomes remain in Ordo/application types and legal transitions.
6. Raw technology exceptions MUST NOT leak across a declared integration boundary.
7. Retry/recovery MUST respect idempotency, authorization, verification, unknown-effect handling, and Ordo transition legality.
8. Secrets, credentials, sensitive values, and raw internal exception details MUST NOT be exposed through user-facing fault presentation.
9. Boundary translations and significant recovery paths MUST have deterministic contract tests.
10. The repository SHOULD maintain a machine-readable Aegis boundary declaration when the application owns multiple operational boundaries.

## 3. Forma

**Applicability for Strata: required only for Strata interactive web UI surfaces when such a surface is implemented.**

Forma is the required Echelon presentation system for interactive web application UI.

### Requirements

1. Interactive browser UI MUST consume the approved pinned `@echelon-foundry/design-system` Forma release.
2. Application code MUST use existing Forma patterns/components/tokens before creating application-local equivalents.
3. Forma CSS, patterns, or components MUST NOT be copied or forked into the application merely for convenience or branding.
4. Consumer markup SHOULD use Forma's documented inert `<ef-*>` authoring wrappers and canonical semantic HTML patterns.
5. Native HTML remains the semantic/accessibility authority. Forma owns presentation; Limen owns non-native browser/application interaction; Ordo/application state owns legal transitions, capabilities, obligations, permissions, and invariants.
6. Application-specific state or business meaning MUST NOT be moved into Forma components.
7. Mobile behavior MUST honor Forma's shared responsive contracts, including the 320px baseline. A new responsive need SHOULD be fixed in Forma rather than forked locally.
8. Accessibility behavior and non-color state cues supplied by Forma MUST be preserved.
9. Aegis user-facing fault presentation in interactive UI MUST use the standard Forma fault/error presentation components when those components cover the presentation intent. Applications MUST NOT maintain a parallel error-component system.
10. A Forma version upgrade is an explicit dependency change requiring build, browser, responsive, and accessibility evidence.

## 4. Folio

**Applicability for Strata: required only for printable/PDF/paginated schema reports, deployment plans, analysis reports, or other document outputs when such outputs are implemented.**

Folio is the required Echelon document-intent and print component system whenever the application produces a printable, PDF, paginated, or print-preview artifact.

### Requirements

1. Applicable document surfaces MUST consume the approved pinned `@echelon-foundry/print-components` Folio release.
2. Before implementing print layout locally, the application MUST use an existing Folio primitive when one expresses the required document intent.
3. Applications MUST NOT create competing local implementations of Folio-provided headers, footers, page numbers, title/back pages, page artwork/layers, columns, sidebars, breaks/keeps, metrics, integrity findings, callouts, figures, tables, code blocks, TOCs, notes, or equivalent shipped primitives.
4. Document content and domain meaning remain application-owned. Folio owns reusable print/layout intent; the selected renderer owns physical pagination/fragmentation.
5. Semantic HTML and logical reading order MUST remain valid without JavaScript/custom-element upgrade.
6. Renderer capability MUST be explicit. Portable browser behavior MUST NOT be confused with Chromium-specific or enhanced paged-media guarantees.
7. Deterministic PDF/export requirements MUST use a controlled renderer profile and include print/PDF regression evidence.
8. Limen/Ordo MAY own meaningful preview/configuration state, but MUST NOT be turned into a JavaScript/WASM pagination engine.
9. Screen interaction surrounding a document uses Forma; printable document composition uses Folio. Neither replaces the other.
10. If no printable/PDF/paginated output exists in a release, Folio need not be installed solely for symmetry. The first requirement that introduces such output automatically activates this Folio requirement.

## 5. Cross-capability composition

The normal composition is:

- **Ordo/application domain:** legal states, transitions, capabilities, obligations, validation, domain outcomes.
- **Aegis:** unexpected operational failures at external/architectural boundaries.
- **Limen:** browser/application interaction boundary.
- **Forma:** interactive presentation and shared application UI.
- **Folio:** printable/paginated document intent and print presentation.

For an operational failure shown to a user, the expected direction is:

`external boundary -> Aegis fault -> safe presentation intent -> application/Limen state -> Forma fault UI`

For a printable artifact:

`application-owned semantic data -> semantic HTML -> Folio document primitives -> declared renderer capability -> paper/PDF`

## 6. Verification and Definition of Done

A requirement using an applicable shared capability is not complete until evidence proves:

- the dependency is pinned and reproducibly restorable;
- the shared capability is actually used, not merely installed;
- no competing local implementation bypasses an existing shared primitive without an approved decision;
- Aegis boundary classification/redaction/recovery behavior is tested where relevant;
- Forma browser, mobile, keyboard, and accessibility behavior is tested where relevant;
- Folio print/PDF output and renderer capability assumptions are tested where relevant;
- exceptions are captured in a decision/architecture record with rationale and follow-up when the shared capability has a gap.

This document is normative for the repository and must be considered during requirement refinement, implementation planning, review, and acceptance.
