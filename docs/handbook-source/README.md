# EventWOS Operations Handbook — Source

The handbook PDF at `../EventWOS_Handbook_2026.pdf` is generated from two Python
files that use ReportLab as the layout engine:

- **`build_p1.py`** — infrastructure: fonts, colors, paragraph styles, custom
  flowables (FlowChart, Callout, ChecklistBox, RoleCard, HR), page templates
  (Cover, Chapter opener, Body), and the `compile_final_story` helper that
  interleaves `NextPageTemplate` markers.
- **`build_p2.py`** — the actual content: every chapter, every checklist, every
  flowchart node.

## Rebuild

```bash
pip install reportlab
cat build_p1.py build_p2.py > build.py
python3 build.py
# Produces EventWOS_Handbook_2026.pdf
```

## Design language (matches the app)

| Token          | Hex       | Used for                             |
|----------------|-----------|--------------------------------------|
| navy (primary) | `#1e2a55` | Chapter openers, headers, H1s        |
| emerald        | `#0d9488` | Accents, positive actions, V (Vendor)|
| amber          | `#d97706` | Warnings, day-of, C (Crew)           |
| indigo         | `#4f46e5` | Planning-phase blocks, M (Manager)   |
| slate 500/700  | `#64748b` | Body text                            |

## Structure

14 chapters + cover + TOC + back-cover, 42 pages A4. Every chapter opens on a
full-bleed dark-navy title page (BookMyShow-style) followed by body pages with
a slim navy header band and an emerald hairline separator.
