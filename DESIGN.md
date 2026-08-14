---
name: Undertow
description: A collaborative document service stated as reference data — card stock, table ink, and handbook teal.
colors:
  stock: "#f5f3ea"
  stock-tint: "#e9efdf"
  ink: "#1c2427"
  ink-soft: "#44525a"
  rule: "rgba(28, 36, 39, 0.35)"
  rule-faint: "rgba(28, 36, 39, 0.16)"
  teal: "#0d5460"
  teal-deep: "#0a434d"
  on-teal: "#f2efe4"
  on-teal-soft: "#bcd7d6"
  red: "#b23a26"
typography:
  display:
    fontFamily: "Archivo Narrow, sans-serif"
    fontSize: "clamp(3.4rem, 9vw, 6rem)"
    fontWeight: 700
    lineHeight: 0.94
    letterSpacing: "-0.01em"
  headline:
    fontFamily: "Archivo Narrow, sans-serif"
    fontSize: "1rem"
    fontWeight: 700
    lineHeight: 1.2
    letterSpacing: "0.09em"
  title:
    fontFamily: "Archivo Narrow, sans-serif"
    fontSize: "1rem"
    fontWeight: 700
    lineHeight: 1.3
    letterSpacing: "0.06em"
  body:
    fontFamily: "Archivo Variable, Archivo, system-ui, sans-serif"
    fontSize: "1rem"
    fontWeight: 400
    lineHeight: 1.55
    letterSpacing: "normal"
  label:
    fontFamily: "Archivo Narrow, sans-serif"
    fontSize: "0.78rem"
    fontWeight: 700
    lineHeight: 1.3
    letterSpacing: "0.08em"
  data:
    fontFamily: "Fragment Mono, ui-monospace, monospace"
    fontSize: "0.86em"
    fontWeight: 400
    lineHeight: 1.4
    letterSpacing: "normal"
  doc-code:
    fontFamily: "Fragment Mono, ui-monospace, monospace"
    fontSize: "0.72rem"
    fontWeight: 400
    lineHeight: 1.4
    letterSpacing: "0.09em"
rounded:
  none: "0"
spacing:
  xs: "0.4rem"
  sm: "0.6rem"
  md: "0.9rem"
  lg: "1.6rem"
  xl: "2rem"
  panel-x: "clamp(1.25rem, 4vw, 3rem)"
  panel-y: "clamp(2rem, 5vw, 3.5rem)"
components:
  button-solid:
    backgroundColor: "{colors.on-teal}"
    textColor: "{colors.teal-deep}"
    typography: "{typography.label}"
    rounded: "{rounded.none}"
    padding: "0.75rem 1.4rem"
  button-solid-hover:
    backgroundColor: "#ffffff"
    textColor: "{colors.teal-deep}"
  button-line:
    backgroundColor: "transparent"
    textColor: "{colors.on-teal}"
    typography: "{typography.label}"
    rounded: "{rounded.none}"
    padding: "0.75rem 1.4rem"
  section-bar:
    backgroundColor: "{colors.ink}"
    textColor: "{colors.stock}"
    typography: "{typography.headline}"
    rounded: "{rounded.none}"
    padding: "0.5rem 0.9rem"
  section-bar-inverse:
    backgroundColor: "{colors.on-teal}"
    textColor: "{colors.teal-deep}"
    typography: "{typography.headline}"
    rounded: "{rounded.none}"
    padding: "0.5rem 0.9rem"
  command-box:
    backgroundColor: "{colors.stock-tint}"
    textColor: "{colors.ink}"
    typography: "{typography.data}"
    rounded: "{rounded.none}"
    padding: "0.6rem 0.8rem"
  copy-button:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    typography: "{typography.label}"
    rounded: "{rounded.none}"
    padding: "0 0.9rem"
  copy-button-hover:
    backgroundColor: "{colors.ink}"
    textColor: "{colors.stock}"
  table-header-cell:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    typography: "{typography.label}"
    padding: "0.55rem 0.9rem 0.55rem 0"
  table-row-alt:
    backgroundColor: "{colors.stock-tint}"
    textColor: "{colors.ink}"
  field-cell:
    backgroundColor: "transparent"
    textColor: "{colors.ink}"
    rounded: "{rounded.none}"
    padding: "0.8rem 0.8rem 0.9rem"
  footnote-marker:
    backgroundColor: "transparent"
    textColor: "{colors.red}"
    typography: "{typography.data}"
    padding: "0 0 0 0.15rem"
  stamp:
    backgroundColor: "{colors.stock}"
    textColor: "{colors.red}"
    rounded: "{rounded.none}"
    padding: "0.1rem 0.35rem"
  toc-item:
    backgroundColor: "transparent"
    textColor: "{colors.on-teal}"
    typography: "{typography.title}"
    rounded: "{rounded.none}"
    padding: "0.85rem 0.3rem"
---

# Design System: Undertow

## Overview

**Creative North Star: "The Programmer's Reference Card"**

Undertow presents itself the way a DEC handbook or an IBM green card presents a machine: as reference data. Not a pitch with tables attached — the tables *are* the page. An operating procedure numbered in a box, byte-field diagrams drawn from the real frame formats, a conformance record with lettered footnotes, a parameter table with a required marker. The reader is assumed to be someone who will run the thing, and the page's job is to hand them the numbers without ceremony.

The material is printed card. Pale stock, blue-black table ink, and a deep handbook teal that carries the cover band, the manual index, and the colophon — the parts a real card would print on its heavier folded cover. Alternating table rows take a faint green-card tint. Red is a marking pen, not a color: it appears on footnote markers, the required-parameter dot, and the rubber stamp that lands when a command is copied. Nothing floats; nothing glows. Depth comes from rules, double rules, a printed frame inset from the viewport edge, and fold creases between panels.

This world exists by refusing the dev-tool default it would otherwise inherit: no dark hero with an animated terminal, no three-across feature grid with glyph icons, no gradient accents, no product screenshot standing in for evidence. Density is high and deliberate — a reader scanning for `UNDERTOW_MAX_FRAME_BYTES` should find it in one pass, and a reader who wants the prose can follow it into the manual, which is themed as the card's backing handbook in the same inks and faces.

**Key Characteristics:**
- Tables and rules are the layout system, never decoration
- Two grounds only: pale card stock and handbook teal
- Squared corners everywhere; radius is zero by rule
- Flat by construction — no shadows anywhere in the system
- Condensed grotesque caps label; mono carries every machine value
- Red is reserved for exceptions, notes, and stamps

## Colors

A printed-card palette: warm pale stock against a cool blue-black ink, with one deep teal doing the work of a handbook cover and one oxide red doing the work of a marking pen.

### Primary
- **Handbook Teal** (`{colors.teal}`): The cover ground. Fills the first-viewport cover band, the manual-index section, and the docs-site header band; also the link color in body prose. This is the color that says "this is a bound reference," and it is the only large color field on the page.
- **Cover Board Teal** (`{colors.teal-deep}`): One step deeper than the cover. Grounds the colophon at the foot of the page — the back cover to the front — and supplies the text color that sits inside solid buttons.
- **Warm Cover White** (`{colors.on-teal}`): The ink printed *on* teal. Headings, body text, and button fills inside the cover, index, and colophon bands.
- **Faded Cover White** (`{colors.on-teal-soft}`): The secondary voice on teal — chapter descriptions, the index intro, the colophon body, the edition/revision codes.

### Secondary
- **Marking Red** (`{colors.red}`): The only accent that is not a ground. It appears exactly four places: footnote markers and their boxed letters in the notes list, the required-parameter dot in the parameter table, the COPIED rubber stamp, and the underline that lights under a nav item on hover. It is never a fill, never a heading color, and never marks a passing result.

### Neutral
- **Card Stock** (`{colors.stock}`): The page. Warm, slightly yellowed pale — the color of a card that has been in a desk drawer. Also the ink color reversed out of solid section bars.
- **Green-Card Tint** (`{colors.stock-tint}`): The faint chlorophyll wash that marks machine content and scan-assist rows: alternating table rows, command boxes, the transcript-conventions legend, and the row a footnote link jumps to.
- **Table Ink** (`{colors.ink}`): Blue-black, not pure black. Body copy, table values, the fill of solid section bars, and the copy button's hover ground.
- **Soft Ink** (`{colors.ink-soft}`): The second-rank voice. Figure captions, field descriptions, table footnotes, the alternate-command lead-in, and the document codes in the topbar.
- **Rule** (`{colors.rule}`) and **Faint Rule** (`{colors.rule-faint}`): The two line weights, both derived from table ink at 35% and 16%. Rule draws structural boundaries — table frames, figure enclosures, the printed page frame, step-number boxes. Faint rule separates rows inside a structure that is already enclosed.

### Dark scheme
The landing page carries a full dark scheme via `prefers-color-scheme`; the manual carries the equivalent under Starlight's `[data-theme='dark']`. It is the same card at night, not a different design: stock inverts to a deep blue-green (`#121d21`), ink to a warm bone (`#e6e2d3`), the teals darken rather than brighten, the green-card tint becomes a translucent olive wash, and red warms to `#e0694f` so it stays legible against the dark ground. Full dark values live in `.impeccable/design.json` under `extensions.colorMeta.*.dark`.

### Named Rules

**The Two-Ground Rule.** Every surface is either card stock or handbook teal. Teal grounds only the cover, the manual index, the colophon, and the docs header band; the body of the card is always stock. There is no third background color, no card-on-card layering, and no tinted section to signal importance.

**The Marking-Pen Rule.** Red marks exceptions only — footnotes, required parameters, stamps, and hover states. It never fills a region, never sets a heading, and never indicates success. If a new element wants red, ask whether it is an exception; if it isn't, it gets ink.

**The Tint-Means-Machine Rule.** The green-card tint appears on content a machine produced or consumes (command boxes, transcript legends) and on alternating table rows for scan assistance. It is never used to make a block look important.

## Typography

**Display Font:** Archivo Narrow 700 (condensed grotesque, uppercase only)
**Body Font:** Archivo Variable (with Archivo, system-ui, sans-serif fallbacks)
**Label/Mono Font:** Fragment Mono (with ui-monospace fallback)

**Character:** A condensed grotesque doing signage work against a neutral humanist grotesque doing prose, with a Helvetica-flavored monospace carrying every machine value. The pairing reads as printed-industrial rather than editorial: Archivo Narrow is the stamped label on the equipment, Archivo is the manual text, and Fragment Mono is the value on the dial.

### Hierarchy
- **Display** (Archivo Narrow 700, `clamp(3.4rem, 9vw, 6rem)`, line-height 0.94, letter-spacing -0.01em, uppercase): The product name on the cover band. Exactly one per page. The tight line-height and negative tracking let it sit as a printed masthead rather than a marketing headline.
- **Headline** (Archivo Narrow 700, 1rem, letter-spacing 0.09em, uppercase): Section-bar titles. Always reversed out of a solid ink bar, always preceded by a mono §-number.
- **Title** (Archivo Narrow 700, 1rem, letter-spacing 0.06em, uppercase): Chapter titles in the manual index; at 0.82rem the same face sets figure numbers and the legend heading.
- **Body** (Archivo Variable 400, 1rem, line-height 1.55): All prose. Measure is capped tightly per context — 58ch inside procedure steps, 60ch for figure captions, 62ch for the cover subtitle and index intro, 70ch for table intros, 72ch in the colophon, 75ch in the legend, 80ch for notes.
- **Label** (Archivo Narrow 700, 0.72–0.95rem, letter-spacing 0.07–0.1em, uppercase): Anything that names rather than says — table column headers, buttons, the copy button, spec-strip items, nav links, the stamp.
- **Data** (Fragment Mono, 0.86em inline / 0.88rem in numeric cells / 1.3rem in step numbers): Commands, parameter keys, defaults, results, topic strings, footnote letters, §-numbers, and chapter numbers.
- **Doc code** (Fragment Mono, 0.72rem, letter-spacing 0.09em, uppercase, soft ink): The printed identifiers — `UND-1 · REFERENCE DATA` in the topbar, `FIRST EDITION` / `REV. 2026-08` on the cover, and the bracket labels above field diagrams.

### Named Rules

**The Three-Faces Rule.** Each face has one job and never takes another's. Archivo Narrow caps label; Archivo sets prose; Fragment Mono carries anything a machine produced or consumes. A heading never uses mono for flavor, and a command never renders in the body face.

**The Caps-Are-Structural Rule.** Uppercase marks a label, a section bar, a button, or a document code. Prose is never uppercased for emphasis, and a sentence never appears in Archivo Narrow.

**The Document-Code Rule.** The small mono caps line above the title is a printed document identifier — edition, revision, doc number, figure label. It carries identifiers only. The moment it would carry a marketing phrase or a value proposition, it has stopped being a doc code and must be deleted rather than reworded.

## Layout

The page is a single column of full-bleed horizontal panels stacked without gaps, each panel's content constrained to a 1120px measure and centered. Panels use `clamp(2rem, 5vw, 3.5rem)` block padding and `clamp(1.25rem, 4vw, 3rem)` inline padding, so the card breathes on a wide screen and tightens on a phone without a breakpoint. The teal bands (cover, manual index) and the colophon run edge to edge; the stock panels between them are width-limited by the same measure plus their own inline padding, so ink content and cover content align on the same vertical rails.

Inside a panel, the recurring device is a two-column split: `minmax(0, 7fr) / minmax(0, 5fr)` generally, tuned to `6fr / 5fr` for the procedure panel so the step list gets the wider side. Both collapse to a single column at 900px. The manual index runs a two-column list that collapses at 800px. Command boxes stack their code and copy button vertically below 560px, and field diagrams wrap their cells to full width at the same breakpoint (five-cell diagrams wrap to two-up instead). At 720px the data tables abandon table layout entirely — headers are hidden and each row becomes a stacked block with its result value promoted to 1rem — and the printed page frame is hidden, since a frame inset from a phone viewport is just lost width.

A sticky topbar holds the doc code and two links, separated from the page by a 3px double rule; `scroll-padding-top: 4rem` on the root keeps anchor jumps from landing underneath it, and `scroll-behavior: smooth` carries the §-links.

**Spacing rhythm.** The vertical rhythm is small and printed rather than airy: 0.4rem between a field key and its description, 0.6rem between paragraphs inside a step, 0.9rem in table cell gutters and grid gaps, 1.6rem below a section bar, 2rem above the legend and inside the colophon. Grid gaps between split columns scale with `clamp(1.5rem, 3vw, 2.5rem)`.

### Named Rules

**The Crease Rule.** Panels are separated by a 14px fold crease — a shadow-to-highlight gradient over a faint hairline — never by empty whitespace and never by a card edge. The crease is what makes the stack read as one folded card rather than a sequence of sections.

**The Measure Rule.** No prose block runs unconstrained. Every text container carries an explicit `ch` cap sized to its role (58–80ch), so the 1120px panel never produces a 1120px line.

## Elevation & Depth

**This system has no shadows.** There is not one `box-shadow` on any surface, and the manual stylesheet explicitly strips the shadow Starlight puts on its pagination links. Nothing is lifted, nothing hovers, nothing has a drop shadow, and no element is elevated to signal importance.

Depth is printed instead, by four devices: **rules** (1px hairlines at two opacities), **double rules** (3px double, marking outer boundaries), the **printed page frame** (a fixed 1px border inset 10px from the viewport with a second faint outline offset 3px beyond it, drawn over everything at z-index 10 and hidden below 720px), and the **crease gradient** between panels, which is the only place in the system where a soft edge appears at all. Layering is tonal where it exists: the green-card tint sits on stock, and reversed content sits on solid ink or teal.

### Named Rules

**The No-Shadow Rule.** No `box-shadow` on any element, in any state, ever. A component that seems to need elevation needs a rule, a tint, or a reversed ground instead. When adopting a third-party component, strip its shadow explicitly.

## Shapes

Everything is square. `border-radius` is zero on every element in the system — buttons, boxes, stamps, step-number cells, chapter-number cells, figure enclosures, code blocks, asides, and pagination controls — and the manual stylesheet resets radius to `0` on four separate Starlight components to enforce it against framework defaults.

The form language is entirely rectangular enclosure. Boxes are drawn with 1px rules, not fills: step numbers sit in a 2.2rem square outline, chapter numbers in a 2rem square outline, footnote letters in a 1.5rem red square outline, and the COPIED stamp in a 2px red rectangle rotated -7°, which is the only rotated element in the system. Field diagrams are drawn as a bracket — a bottomless rule with a centered label notched into its top edge, its background punched out of the stock — sitting directly on top of a row of divided cells, so the diagram reads as a labeled byte-field rather than a set of cards.

### Named Rules

**The Squared Rule.** Radius is 0. There is no small/medium/large radius scale because there is no radius. Any imported component gets its corners squared before it ships.

**The Double-Rule Rule.** A 3px double rule marks the outer boundary of a structure — the top and bottom of a table, the head of a procedure list or notes list, the underside of the topbar, an h2 in the manual. A 1px hairline separates items *inside* that structure. Never use double rules between rows; never close a table with a hairline.

**The Outline-Not-Fill Rule.** Numbered markers (steps, chapters, footnote letters) are outlined squares with transparent grounds, never filled chips.

## Components

### Buttons
- **Shape:** Square (0 radius), 1px border, `0.75rem 1.4rem` padding, condensed caps at 0.95rem with 0.08em tracking.
- **Solid:** Warm cover white ground with cover-board teal text — the reverse of the band it sits on. Used for the primary action on the cover (jump to the operating procedure).
- **Line:** Transparent ground, cover-white border and text. The secondary action.
- **Hover / Focus:** Solid goes to pure white (`#f7f4e6` in dark) and matches its border to it; line fills with cover white at 14% opacity. Both use `:hover` and `:focus-visible` together — never hover alone.
- **Placement:** Buttons exist only inside teal bands. The stock panels use links and the copy button, not buttons.

### Command Box
The recurring quickstart primitive: a green-card-tinted rectangle with a 1px rule, holding a mono command that wraps rather than scrolls (`white-space: pre-wrap`, `overflow-wrap: anywhere`), with a copy button divided off by a single vertical rule. Below 560px the box turns vertical and the divider becomes a horizontal top border on the button. Long invocations use a variant that drops to 0.78em (0.65em on small screens) and marks unbreakable runs with a `nowrap` span, so an env-var assignment never splits mid-token.

### Copy Button
Condensed caps at 0.72rem on a transparent ground; on hover or focus it reverses to ink with stock text. On successful copy it appends a **COPIED rubber stamp** — red 2px outline, red text, rotated -7°, on a stock ground, scaling in from 1.6× over 0.25s with the world's standard `cubic-bezier(0.16, 1, 0.3, 1)` easing — which removes itself after 1400ms. The stamp is the system's signature feedback gesture; it is the only animated element that carries meaning.

### Tables
- **Frame:** 3px double rule top and bottom, no outer side borders, collapsed borders, 0.93rem body size.
- **Header row:** Condensed caps at 0.78rem with 0.08em tracking, closed by a 1px rule.
- **Rows:** Left-aligned, top-aligned cells with `0.55rem 0.9rem 0.55rem 0` padding and a faint hairline below. Even rows take the green-card tint.
- **Row headers:** The first cell of each row is a `th[scope="row"]` at weight 600, not a plain cell — the suite name and the parameter key are labels, not data.
- **Values:** Cells carrying machine values take the mono face at 0.88rem with `white-space: nowrap`, so a result or a default never wraps mid-number.
- **Markers:** A red superscript letter links to the matching footnote; a red dot marks a required parameter, explained in a footer line beneath the table.
- **Mobile (≤720px):** The table de-tables — header row hidden, every cell becomes a block, rows become padded stacks separated by hairlines, and the mono result value grows to 1rem to stay the row's anchor.

### Field Diagram
The signature component. A labeled bracket sits above a row of equal-or-fixed-width cells divided by 1px rules; each cell carries a mono key and an optional soft-ink description. Fixed-width cells (`field-fixed`) are used for literal byte values, flexible cells for variable-length regions. A `figcaption` beneath carries a condensed-caps figure number followed by prose at 0.85rem, capped at 60ch. Diagrams wrap to full-width cells below 560px; the five-cell variant wraps two-up with its right borders re-derived by `:nth-child(2n)`.

### Notes / Footnotes
An ordered list rendered as a two-column grid: a 1.5rem red outlined square holding the note letter, then the note prose at 0.93rem capped at 80ch. Opened by a 3px double rule, rows separated by hairlines. A targeted note (`:target`) takes the green-card tint so a reader arriving from a footnote marker sees where they landed.

### Section Bar
A solid ink rectangle spanning the panel's measure, holding a mono §-number and a condensed-caps title in stock ink, with 1.6rem of clearance beneath it. The inverse variant (cover white ground, cover-board teal text) is used when the bar sits on a teal band. Every content panel opens with one; there is no unlabeled section.

### Navigation
The topbar is sticky, stock-grounded, and closed by a 3px double rule, with a doc code on the left and condensed-caps links on the right. Links carry a 2px transparent bottom border that turns marking red on hover or focus — the only red in the chrome. In the manual, the sidebar becomes the tab index: condensed caps at 0.85rem for top-level entries and group labels, square (never pill) hit areas, and the current page reversed out in the accent color.

### Manual Index
A two-column list of chapter links on the teal band. Each row is a grid: an outlined square chapter number spanning both rows on the left, the condensed-caps chapter title, and a faded-white description beneath. Rows are separated by cover-white hairlines at 25% and take a 10% cover-white wash on hover or focus.

### Skip Link
Parked at `top: -3rem` and sliding to `top: 1rem` on focus over 0.15s — ink ground, stock text, square. Present on the landing page and the first thing in the tab order.

## Do's and Don'ts

### Do:
- **Do** ground every new surface in either card stock or handbook teal, and reverse the type accordingly (ink on stock, cover white on teal).
- **Do** open every content section with a solid ink section bar carrying a mono §-number.
- **Do** close a table or a list group with 3px double rules and separate its rows with 1px hairlines.
- **Do** set every machine value — commands, defaults, results, keys, topics — in Fragment Mono, and every label in Archivo Narrow 700 caps.
- **Do** cap prose measure explicitly in `ch` (58–80ch by role) even inside a wide panel.
- **Do** pair `:hover` with `:focus-visible` on every interactive element, as every component in the build does.
- **Do** square the corners of any third-party component you adopt, and strip its shadow.
- **Do** give a de-tabled mobile row an anchor: promote its mono result value to 1rem so the stack stays scannable.
- **Do** let motion be optional — the build gates its unfold on `prefers-reduced-motion` and ships every panel visible by default.

### Don't:
- **Don't** add a `box-shadow`, anywhere, in any state. Depth is rules, tint, and the crease.
- **Don't** round a corner. There is no radius scale to reach for.
- **Don't** use marking red for anything but exceptions, notes, required markers, stamps, and hover underlines — never for a heading, a fill, or a passing result.
- **Don't** introduce a third background color, a card-on-card layer, or a tinted "callout" block to signal importance.
- **Don't** separate panels with whitespace or a card edge; the crease is the separator.
- **Don't** uppercase prose or set a sentence in Archivo Narrow.
- **Don't** let the document-code line carry a marketing phrase. It holds identifiers — edition, revision, doc number, figure label — or it gets deleted.
- **Don't** animate anything except the copy stamp, the single first-panel unfold, and the skip link. There is no scroll-driven choreography here.
- **Don't** reach for glyph icons or an icon set; the system's only marks are outlined squares, rules, brackets, and the rotated stamp.
- **Don't** state a number the repository can't back. Result counts on this page trace to the conformance record, and the fixtures — not the card — are the authority.
