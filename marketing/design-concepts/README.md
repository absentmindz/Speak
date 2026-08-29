# Speak website design specification — archived

This lime-and-white concept package is archived and no longer guides production. The active source of truth is the [app-derived monochrome concept set](../design-concepts-monochrome/).

## Direction

- Windows-native precision editorial; authentic product imagery with the existing Windows title bar.
- True white (`#ffffff`) content bands and graphite (`#101214`) media bands.
- Graphite text (`#0c0e10`), muted steel (`#5e646b`), hairlines (`#c9cdd1`), and electric signal lime (`#b8f51a`).
- No gradients, glassmorphism, faux browser/Mac chrome, floating orbs, generic card grids, or color overlays on product imagery.

## Typography and layout

- Display and body: `"Segoe UI Variable", "Segoe UI", system-ui, sans-serif`.
- Display headings: 700-760 weight, tight tracking, compact line height.
- Body: 16-20px on desktop, at least 16px on mobile.
- Maximum content width: 1440px with 44px desktop gutters and 20px mobile gutters.
- Section rhythm alternates open white editorial layouts with full-width graphite bands.
- Corners stay restrained (0-12px); one-pixel rules provide most grouping.

## Component families

- Rectangular primary, outline, and dark-outline buttons with clear focus rings.
- Quiet desktop navigation and an accessible disclosure menu below 900px.
- Open ruled columns for trust facts, use cases, privacy, editions, and proof.
- Native `<video controls>` inside a stable 16:9 frame, with a custom accessible poster play affordance.
- Native `<details>/<summary>` FAQ rows.

## Icons

Production SVGs use a 24×24 viewBox, `currentColor`, 1.75-2px round strokes, and no decorative containers unless shown in the concepts:

- download arrow; play triangle; menu lines; waveform; cloud; shield; document/SBOM;
- message; code braces; notes; local computer/database; optional outbound cloud;
- plus/minus disclosure marks and simple directional arrows.

## Section order

1. Header, hero, trust rail.
2. Authentic raw-to-ready transformation.
3. Three use cases.
4. 60-second demo.
5. Local-versus-configured-cloud privacy boundary.
6. Community versus planned supported launch.
7. Public verification proof.
8. FAQ, final CTA, footer.

## Above-the-fold copy lock

- Brand: `Speak`
- Navigation: `Product`, `Demo`, `Privacy`, `Editions`, `FAQ`, `GitHub`
- Header action: `Download free`
- H1: `Write anywhere on Windows—just by speaking.`
- Body: `Press one shortcut, talk naturally, and Speak turns it into polished text in the app you’re already using. Run transcription locally or choose your own provider.`
- Primary CTA: `Download Community — Free`
- Secondary CTA: `Watch the 60-second demo`
- Facts: `Windows 10/11 · Local-first · Apache-2.0`

## Recorded accuracy deviations

Repository documentation outranks concept copy where a claim needs qualification. Implementation must identify Speak as pre-release, describe Edge TTS as online, describe the $21 option as planned/interest-only, and avoid presenting v0.5.3 as public until it is released.
