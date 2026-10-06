---
project: visual-engineering
purposes:
  - apply
  - verify
audiences:
  - practitioner
  - contributor
---

# Application Polish

Polish is the systematic elimination of observable evidence that an application is unfinished. Treat it as engineering work throughout implementation, not a cosmetic pass at the end.

For each material user-facing surface, reason across visual precision, interaction, motion, state completeness, forms, feedback, content, responsiveness, accessibility, performance perception, resilience, data integrity, navigation, environment behavior, security UX, and fit-and-finish.

Prioritize seams such as loading to loaded, empty to populated, valid to invalid, editing to saving to saved, online to offline to recovered, authenticated to expired, wide to narrow, pointer to keyboard, ordinary to pathological content, overlay lifecycle, and optimistic action to rejection.

Use adversarial cases including extreme text, Unicode and RTL, numeric and collection boundaries, broken dependencies, slow or offline behavior, rapid repeated input, zoom and text scaling, constrained viewports, reduced motion, forced colors, expired sessions, stale or conflicting data and interrupted persistence.

A zero-finding review is not proof of polish. Report what was tested, what evidence exists, what failed, what was not applicable, and what remains unknown. Unknown is not pass.

Recoverable failures should preserve valid work. Persistence indicators must tell the truth. Recurrent defects should be moved upstream into shared components, rules, fixtures or automated probes so they become structurally harder to repeat.


Motion review follows the normative motion-selection discipline in `framework/standards/MOTION-AND-INTERACTION.md`: classify the phenomenon before choosing timing, keep semantic state authoritative, preserve zero-lag direct manipulation, keep determinate progress bounded, define interruption behavior, and apply a model-specific reduced-motion substitution.

Record motion as evidence, not impressions. For each material animated behavior, add a `motion.entries` item with:

- model, phenomenon and what it conveys;
- the authoritative source, and whether the semantic update is immediate;
- phase (direct or settle) and roles;
- the reduced-motion substitution;
- checks for semantic authority, interruption, rapid repeat, reduced motion, direct-manipulation lag, progress bounds, cadence stopping, boundaries, focus, progressive fallback, concurrent composition and semantic independence, as the model and roles require.

A surface with no material motion states `motion.notApplicable.rationale`. Unknown or missing checks leave the gate incomplete. Motion that conveys severity, priority, confidence, permission, risk, correctness or similar domain meaning fails.

Reusable motion probes (`registries/application-polish-probes.json`, ids `motion-*`) can produce these checks automatically:

- semantic authority, rapid repeat, interruption: selections, toggles and open/close;
- reduced-motion substitution and semantic cue: every spatial or repeated motion;
- focus: dialogs, popovers and view transitions;
- direct lag: drag, resize and scrub;
- progress bounds: determinate progress;
- cadence stops: spinners and shimmer;
- rejected drop and boundary: reorder, snap and resize;
- progressive fallback: View Transitions and other modern CSS;
- concurrent composition: stacked effects.

Probe results are pass, fail, not-applicable or unknown engineering evidence, not proof of human comfort.
