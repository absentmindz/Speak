---
name: Speak Web
version: 2.0
description: App-native monochrome website for Speak for Windows.
sourceOfTruth:
  file: "../MainWindow.xaml"
  note: "Website colors and surface hierarchy must match the Windows app."
colors:
  canvas: "#111214"
  panel: "#18191C"
  ink: "#D8D3C7"
  muted: "#918D84"
  line: "#2B2C30"
  accent: "#B8B1A4"
  bright: "#CBC4B5"
typography:
  display:
    fontFamily: "Aptos Display, Aptos, Segoe UI Variable Display, Segoe UI, sans-serif"
    fontWeight: 700
    lineHeight: 1.02
    letterSpacing: "-0.045em"
  body:
    fontFamily: "Aptos, Segoe UI Variable, Segoe UI, system-ui, sans-serif"
    fontWeight: 400
    lineHeight: 1.55
rounded:
  small: "8px"
  medium: "10px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "16px"
  lg: "32px"
  xl: "64px"
components:
  page:
    backgroundColor: "{colors.canvas}"
    textColor: "{colors.ink}"
  card:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.ink}"
    borderColor: "{colors.line}"
    rounded: "{rounded.small}"
  buttonPrimary:
    backgroundColor: "{colors.bright}"
    textColor: "{colors.canvas}"
    rounded: "{rounded.small}"
    height: "50px"
  buttonGhost:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.ink}"
    borderColor: "{colors.line}"
    rounded: "{rounded.small}"
    height: "50px"
  mutedCopy:
    backgroundColor: "{colors.panel}"
    textColor: "{colors.muted}"
  divider:
    backgroundColor: "{colors.line}"
    textColor: "{colors.ink}"
  focus:
    outlineColor: "{colors.accent}"
    offsetColor: "{colors.canvas}"
---

## Overview

Speak Web extends the Windows app rather than presenting a separate campaign identity. It should feel dark, precise, private, and calm: charcoal structure, warm silver typography, compact radii, restrained rules, and an authentic product view doing most of the visual work.

## Source of truth

`MainWindow.xaml` is authoritative for the palette and surface hierarchy. The website, social card, demo poster, touch icon, and future generated assets must use the seven tokens above. Do not introduce pure white surfaces, saturated signal colors, colorful gradients, or a second marketing palette.

## Colors

Use `canvas` for the page and `panel` for meaningful elevation. `line` separates regions without creating heavy boxes. `ink` is the default readable foreground, `muted` supports secondary copy, `accent` handles restrained emphasis and focus, and `bright` is reserved for the primary action or the strongest typographic moment. Authentic product screenshots and the supplied logo are not recolored.

## Typography

Use Aptos and Segoe UI—the same families as the app. Outcome headlines can use tight tracking and short line lengths; body copy stays calm and readable. Eyebrows are compact, high-tracking labels and should not become decorative pills.

## Layout

Use a strong 1440px composition with a centered content shell and clearly ruled bands. Alternate canvas and panel through surface hierarchy rather than light/dark color blocking. Preserve generous space, a dominant authentic app screenshot, and a direct reading order. Avoid screenshot galleries and ornamental dashboard mockups.

## Elevation and depth

Use one-pixel `line` borders and subtle black shadows only where hierarchy needs them. App-native shallow depth is preferred to glow, glass, blur, neon, or colored ambient effects. Gradients, when necessary, stay within the charcoal app surfaces.

## Shapes

Use 8px corners for cards and actions, 10px only for larger grouped surfaces, and circles for icon-only controls. Avoid oversized rounded cards and pill-heavy layouts. The logo retains its native shape.

## Components

Primary buttons use `bright` on `canvas`; secondary actions use `panel`, `ink`, and a `line` border. Cards use the panel surface and a single subtle rule. Focus indicators use `accent` with a canvas offset so keyboard state remains visible. Motion should be small, purposeful, and disabled or reduced when the user requests reduced motion.

## Do's and don'ts

- Do match the app's exact seven-token monochrome palette across every web surface and generated asset.
- Do lead with the writing outcome, then earn trust with privacy and workflow details.
- Do state the $21 one-time offer plainly: no comparisons, subscriptions, or price ladders.
- Do preserve transparent language where checkout is not yet live.
- Don't add light page bands, saturated accent colors, a screenshot gallery, a pricing matrix, autoplay media, or motion that blocks reading.
- Don't imply cloud processing is local, or that payment and entitlements are live before they are.
