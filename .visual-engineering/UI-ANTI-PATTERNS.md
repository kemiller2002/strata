---
project: visual-engineering
purposes:
  - apply
  - reference
audiences:
  - practitioner
  - contributor
---

# Visual Engineering UI Anti-Patterns

- Generic containers used in place of a meaningful information model
- Equal emphasis across competing elements
- Color as the only state or urgency signal
- Low-contrast text used to manufacture hierarchy
- CSS visual reordering that conflicts with source, reading, or focus order
- Responsive layouts that merely shrink instead of recomposing
- Excess whitespace that destroys comparison or scanning
- Excess density without grouping, alignment, or hierarchy
- Icon-only critical actions without established meaning
- Hidden system status or recovery paths
- Shadow DOM used by default rather than by demonstrated need
- Public component APIs that expose internal styling structure
- Token proliferation used to avoid structural decisions
- Accessibility treated as a final audit
- Visual novelty prioritized over recognition and verification
- Research guidance applied without inspecting the product context

- Semantic state collapsed into a generic success, failure, loading, empty, or complete state when the product distinguishes consequential alternatives
- Presentation code independently recreates domain action legality instead of consuming the authoritative capability or legal-action model when one exists
- Scoped uncertainty or coverage flattened into a global completion percentage, pass/fail verdict, or status without a valid product aggregation rule
- Unknown external effect presented as definite failure with an unsafe blind retry path
- Previous render, screenshot, or cached presentation state treated as authority for a newer semantic state

- Arbitrary component-local animation durations or easing curves with no named interaction model
- Spring or bounce applied to every state change regardless of the phenomenon
- Decorative lag layered onto drag, resize, scrub, scroll, slider, or other direct manipulation
- Determinate progress that overshoots, bounces past, or visually outruns the authoritative value
- Infinite shimmer, spin, pulse, or other repeated motion with no reduced-motion substitution
- Animation speed, mass, bounce, or travel used to imply severity, priority, permission, confidence, or correctness
- View-transition morphs between elements that do not represent continuous identity
- Concurrent animations that overwrite the same transform/property and silently cancel one another
- Motion used as the only cue that state, activity, success, failure, selection, or validation changed
