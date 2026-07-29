---
name: Speak Web
version: 1.0
description: Calm, private, high-trust landing experience for Speak for Windows.
colors:
  canvas: "#090b12"
  surface: "#121722"
  surfaceRaised: "#181f2d"
  ink: "#f5f0e8"
  muted: "#aab1c0"
  line: "#2a3344"
  primary: "#d7d0c2"
  primaryInk: "#101217"
  signal: "#bff58b"
  signalSoft: "#e7ffd0"
typography:
  display:
    fontFamily: "Aptos Display, Aptos, Segoe UI Variable Display, Segoe UI, sans-serif"
    fontWeight: 800
    lineHeight: 1.02
    letterSpacing: "-0.052em"
  body:
    fontFamily: "Aptos, Segoe UI Variable, Segoe UI, system-ui, sans-serif"
    fontWeight: 400
    lineHeight: 1.55
rounded:
  small: "12px"
  medium: "18px"
  large: "28px"
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
    backgroundColor: "{colors.surface}"
    textColor: "{colors.ink}"
    rounded: "{rounded.medium}"
  buttonPrimary:
    backgroundColor: "{colors.primary}"
    textColor: "{colors.primaryInk}"
    rounded: "{rounded.small}"
    height: "50px"
  buttonGhost:
    backgroundColor: "{colors.surfaceRaised}"
    textColor: "{colors.ink}"
    rounded: "{rounded.small}"
    height: "50px"
  mutedCopy:
    backgroundColor: "{colors.surface}"
    textColor: "{colors.muted}"
  divider:
    backgroundColor: "{colors.line}"
    textColor: "{colors.ink}"
  signalStatus:
    backgroundColor: "{colors.signal}"
    textColor: "{colors.primaryInk}"
  signalCaption:
    backgroundColor: "{colors.surfaceRaised}"
    textColor: "{colors.signalSoft}"
---

## Overview

Speak Web should feel precise, private, and unusually calm for voice software. The visual system prioritizes one strong product view, generous negative space, and short outcome-led copy over a gallery of screens or a busy SaaS dashboard aesthetic.

## Colors

The existing dark-charcoal and warm-ivory Speak palette remains the brand anchor. Cool blue-black surfaces provide depth; `signal` is reserved for active, living moments such as the listening pulse and workflow animation. Maintain WCAG AA contrast for body copy and all interactive controls.

## Typography

Use the display family for outcome headlines with tight tracking and short line lengths. Use the body family for calm, readable descriptions. Eyebrows are compact, high-tracking labels; never use them as dense paragraphs.

## Layout

Use a single primary visual in the hero, then let purpose-built motion diagrams and well-spaced cards communicate supporting features. Avoid screenshot grids. Desktop content sits inside a centered 1180px shell; use the spacing scale for section rhythm.

## Elevation & Depth

Surfaces use low-contrast borders and soft shadows. Ambient radial gradients and a faint grid add depth without competing with content. Depth should clarify hierarchy, not make the site feel glossy or noisy.

## Shapes

Use rounded rectangles with `rounded.medium` for product surfaces and `rounded.small` for actions. Do not mix sharp cards with pill-heavy UI. The logo retains its own native rounding.

## Components

Primary buttons use `buttonPrimary`; ghost buttons use `buttonGhost`. Cards use the `card` surface and a subtle `divider` border. Motion should be small, purposeful, and disabled or reduced when the user requests reduced motion.

## Do's and Don'ts

- Do lead with the writing outcome, then earn trust with privacy and workflow details.
- Do state the $21 one-time offer plainly: no comparisons, subscriptions, or price ladders.
- Do preserve transparent language where checkout is not yet live.
- Don't add a screenshot gallery, a pricing matrix, autoplay media, or motion that blocks reading.
- Don't imply cloud processing is local, or that payment/entitlements are live before they are.
