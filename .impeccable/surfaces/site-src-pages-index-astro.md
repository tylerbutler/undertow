---
version: 1
slug: "site-src-pages-index-astro"
primary_target: "site/src/pages/index.astro"
related_targets: ["site/src/content/docs"]
---

# Surface: project site (landing + manual)

Scope: `site/` — landing page (`src/pages/index.astro`, Persuade) + Starlight docs at `/manual/` (Read). Deployed to https://undertow.tylerbutler.com (Netlify).

Audience & job: self-hosting devs (get a live server in minutes) and protocol implementers (wire-level precision). Primary action: the quickstart — clone, `docker compose up -d --wait` (or `dotnet run` with `UNDERTOW_JWT_SECRET`). No package registry exists or is implied: install is build-from-source only.

Proof: real conformance record only (dual-mode 38+7, parity 53/54 w/ intentional ADR-009 401, readiness 8/8, 187 tests, e2e 9/9), golden fixtures in `tests/fixtures/wire/`. Nothing fabricated.

Direction: **Programmer's Reference Card** world (seed 9d50a094) — IBM green card / DEC handbook lineage. The product stated as reference data: operating-procedure box, parameter tables, byte-field diagrams from real frame formats, conformance as a condition-code table, divergences as red footnotes. Pale card stock + blue-black table ink; deep teal handbook-cover panels carry masthead/dividers/footer (~30–60%); red reserved for divergences. Tables and rules are the layout system, never texture. Starlight themed as the backing manual (chapter numbers, tab index, same inks). Memorable moment: the fold — sections unfold at crease lines; copy feedback is a rubber-stamp mark.

Constraints/anti-goals: no ops dashboard; no testimonials/benchmarks/adoption/pricing; no NuGet or package-install implication; Undertow-first (trio is lineage context); not the dark+neon dev-tool default. Counts drift with the repo — tables tolerate updated numbers.

Unresolved: how much plan-doc narrative gets published verbatim (currently: summarized in manual/lineage).
