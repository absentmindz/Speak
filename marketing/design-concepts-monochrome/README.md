# Speak website — active monochrome design specification

This directory is the active visual source of truth for the Speak website. The concepts translate the desktop app's warm-charcoal monochrome system to the full marketing journey. Production copy and repository facts still outrank any text rendered inside the concept PNGs.

## App-derived color tokens

Use these values directly; do not reintroduce lime, blue, colored gradients, pure-white content bands, or generic neutral substitutes.

| Role | Token |
| --- | --- |
| Canvas | `#111214` |
| Input / deepest surface | `#121316` |
| Sidebar | `#151618` |
| Window chrome | `#17181B` |
| Panel | `#18191C` |
| Soft surface | `#202124` |
| Premium soft surface | `#242529` |
| Elevated surface | `#26272B` |
| Divider / border | `#2B2C30` |
| Premium surface | `#34363C` |
| Deep metallic accent | `#4A4843` |
| Signal / quiet text | `#77736C` |
| Muted text | `#918D84` |
| Rail / primary accent | `#B8B1A4` |
| Metallic highlight | `#CBC4B5` |
| Primary ink | `#D8D3C7` |
| Selected / brightest ink | `#F0ECE3` |

Subtle app-native gradients are allowed only where they clarify hierarchy: hero `#202124 → #18191C → #121316` and premium `#34363C → #202124 → #16171A`. They must read as surface depth, not decoration.

## Typography, geometry, and motion

- Typeface stack: `Aptos`, `Segoe UI Variable`, `Segoe UI`, system UI, sans-serif.
- Headings are compact and decisive, with tight tracking and no ornamental display face. Body copy remains comfortably readable at 16px or larger.
- Use a disciplined 1440px desktop grid, generous negative space, ruled columns, and one-pixel `#2B2C30` dividers.
- Default corner radius is 8px, rail items may use 10px, and icon-only controls may be circular. Avoid pill-heavy UI, oversized rounding, glass panels, floating orbs, and faux browser or Mac chrome.
- Product imagery keeps the authentic Windows title bar and app appearance. Do not tint, recolor, or redraw screenshots.
- Motion is quiet and functional: short opacity/position transitions, modest hover lift, and no parallax or ambient looping effects. Honor `prefers-reduced-motion`; video playback remains user initiated.

## Content-accuracy lock

- Repository documentation and current release state override generated concept text.
- Do not invent testimonials, customers, usage metrics, compatibility claims, or sections.
- Describe Speak as pre-release where required, and identify Edge TTS as online.
- Community remains free and open source under Apache-2.0.
- The `$21 once` supported build is planned and interest-list only. Checkout is not live, and the site must not imply that payment is currently accepted.
- Keep the privacy boundary explicit: transcription can run locally; configured third-party providers may send the selected audio or text to that provider.
- Keep the established information architecture and approved page copy even where ImageGen abbreviated or misnumbered a concept label.

## Concept set

1. [`01-hero.png`](./01-hero.png) — header, app-led hero, calls to action, and trust rail.
2. [`02-transformation.png`](./02-transformation.png) — raw-to-ready transformation and ruled use-case columns.
3. [`03-demo-privacy.png`](./03-demo-privacy.png) — native video presentation and local-versus-provider privacy boundary.
4. [`04-editions-proof.png`](./04-editions-proof.png) — Community and planned supported editions, followed by public-verification proof.
5. [`05-faq-footer.png`](./05-faq-footer.png) — FAQ disclosure rows, closing action, and footer.
6. [`06-mobile.png`](./06-mobile.png) — 390px-class mobile treatment for navigation, hero, product image, and continuation.

Together, these six references cover every production section and both desktop and mobile hierarchy. Treat them as one coordinated system rather than independent page alternatives.

## Built-in ImageGen direction

The concepts were generated in built-in ImageGen mode using the real Speak app screenshot and logo as references. The prompt requested a production-ready Windows-native marketing site in the exact app-derived palette above, with Aptos/Segoe typography, 1px rules, restrained 8px geometry, a strong 1440px editorial grid, open bands, authentic product imagery, and coordinated desktop/mobile views. It explicitly excluded lime or other color accents, cream/white bands, glassmorphism, neon, decorative gradients, faux browser chrome, hero badges, invented metrics, and unsupported claims.
