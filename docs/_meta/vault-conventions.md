---
title: Vault Conventions
domain: meta
status: active
last-reviewed: 2026-07-27
---

# Vault Conventions

> **What's in this doc:** filename rules, required frontmatter, doc anatomy (the "What's in / What's NOT" callout), wikilink syntax, code-citation conventions, schema/API/live-behavior verification rules, mermaid diagram threshold, TOC threshold, archive policy, backlog conventions, the "touching a doc" checklist.
>
> **What's NOT:** the "update docs" / "sync docs" workflow (→ [[docs-sync-prompt]]), the code-path routing map (→ `_meta/doc-ownership.yml`), per-feature design history (specs, plans, ADRs — those have their own conventions outside this vault).

The rules for writing and maintaining docs in this vault. This is the playbook.

> **Scope:** the domain docs under `docs/` and their index/meta files. Does NOT govern per-feature design history (specs, plans, ADRs).

## Core principles

1. **Verify before writing.** Any claim about schema, RLS, API shape, or live behavior must be checked against the source of truth at write time (DB introspection, OpenAPI spec, MCP query, `kubectl get`, whatever the project uses). Never paraphrase from memory or older docs.
2. **Cite by `file:line`, not `file`.** `app/api/auth/route.ts:42` beats `app/api/auth/route.ts`. The line number is the difference between "I know roughly where this is" and "I can jump there."
3. **Open with What's in / What's NOT.** Every doc starts with a callout listing what it covers AND what it doesn't (with wikilinks to the docs that DO cover the excluded topics). Lets an agent confirm it's in the right doc in two lines.
4. **Archive, don't delete.** Obsolete docs move to `docs/_archive/` with `superseded-by:` frontmatter. Backlinks from older specs/plans stay valid.
5. **Bump `last-reviewed` on every edit.** It's the only signal an agent has for whether to trust the doc.

## Filenames

- `kebab-case.md`. No spaces, no SCREAMING_SNAKE.
- Domain docs live at `docs/<domain>.md` (not in a subfolder).
- Meta docs live under `docs/_meta/`.
- Archive docs live under `docs/_archive/` with `status: archived` frontmatter.

## Frontmatter (required)

Every doc starts with:

```yaml
---
title: Human-readable title
domain: <matches filename stem>
status: active | archived
last-reviewed: YYYY-MM-DD
verified-against:         # required only when citing schema/API/live behavior
  - migration: <filename>
  - mcp-query: <query summary>
  - api-spec: <spec file>
---
```

`verified-against:` is required only for docs citing verifiable ground truth. Pure narrative docs (architecture overviews, runbooks) can omit it.

## Doc anatomy

Every domain doc opens with:

```markdown
# <Title>

> **What's in this doc:** <one sentence listing covered topics>.
>
> **What's NOT:** <topics that belong in OTHER docs, with [[wikilinks]]>.

## <first major section>
...
```

The "What's in / What's NOT" callout exists so an agent can verify it's in the right doc before reading further. Never skip it.

## Cross-references

- Use `[[filename]]` or `[[filename#section-anchor]]`. Filename without `.md`.
- Filename in the link text is grep-friendly — prefer `[[business#subscription-tiers]]` over `see business doc`.
- Never use relative paths (`../foo.md`). If a doc gets renamed, grep for the old filename finds every reference in one pass.

## Code citations

Every code reference includes `file:line`:

```
Step 1 — user clicks Google in `app/(auth)/sign-in/page.tsx:42`
```

Not just `app/(auth)/sign-in/page.tsx`. The line number is the difference between "I know roughly where this is" and "I can jump straight to the right line."

## Verification (schema / API / live behavior)

Any section citing schema, RLS policies, API shape, or live system state must:

1. **Be written or refreshed by querying the source of truth.** Use whatever this project provides — DB introspection, MCP query, OpenAPI spec, GraphQL introspection, `kubectl get`, etc.
2. **Include a section-level HTML comment recording the query used:**

   ```html
   <!-- verified-against: <query or query-summary> on YYYY-MM-DD -->
   ```

3. **Update the doc's frontmatter:** bump `last-reviewed` to today, add the relevant migration/spec to `verified-against:` if it's now load-bearing.

If a section cannot be verified (tool unavailable, query ambiguous), add a `TODO verify: <what>` entry to `docs/_backlog.md` rather than guessing.

## Coverage

For docs with `paths:` declared in `doc-ownership.yml`, the linter
(`vault-doctor --check-coverage`) reports source files in that glob that
the doc does not cite. The unit is **files** (a path is "cited" iff it
appears as a literal substring in the doc body — usually as a `file:line`
reference inside backticks).

Defaults:
- Config/asset extensions (`.md`, `.json`, `.yml`, `.svg`, `.png`, …) are
  exempt. Override per-doc via frontmatter:
  - `coverage-extensions: [.ts, .tsx]` — allowlist (only these count).
  - `coverage-exclude: [src/legacy/foo.ts]` — exempt specific files.
- Output is informational (prefix `i`). To make low coverage a violation,
  run with `--enforce-coverage <pct>`.
- Frontmatter `vault-doctor-skip-checks: [coverage]` opts a doc out entirely.

The check exists to catch the failure mode of "doc passes freshness and
shape checks but silently covers half of its declared territory" — a
hallucination risk because an agent reading the doc sees a clean health
signal and may fabricate behavior for the undocumented files.

## Mermaid diagrams

Any flow crossing ≥2 layers (client → API → DB; middleware → auth helper → RLS) gets a mermaid sequence diagram at the top of its section. The diagram is the entry point; the prose below walks through each step with `file:line` anchors.

Don't add mermaid for linear single-layer flows — prose with citations conveys the same information more compactly.

## Doc size budget

**Soft cap: 600 lines per domain doc.** Above that an agent loading the doc end-to-end burns context on sections irrelevant to the current task. `vault-doctor` warns when a doc exceeds the cap. Three remedies, in order of preference:

1. **Extract a cheatsheet** for the highest-frequency lookup (see below). Cheaper than splitting.
2. **Split the doc** along a real seam (e.g. `auth.md` → `auth.md` + `auth-rls.md`). Update wikilinks and `doc-ownership.yml`.
3. **Archive stale sections** to `docs/_archive/` if the content is no longer current.

Per-doc override: frontmatter `max-doc-lines: 800` (or `0` to disable).

**Byte cap: 48 KiB default** (`--max-doc-bytes`, per-doc frontmatter `max-doc-bytes: N`, `0` disables). Long lines defeat a line cap — a 515-line doc has shipped at 81 KB. Bytes measure what actually hits the agent's context window.

## TOC

**No mandatory TOC.** Add a `## Quick map` TOC only when the doc crosses ~400 lines. Under that threshold, `##` headings + IDE outline view are sufficient.

## Cheatsheets

**The decision rule:** if an agent often asks a question whose answer is <50 lines but lives inside a domain doc that's >300 lines, write a cheatsheet at `docs/_cheatsheets/<topic>.md`. The agent loads the cheatsheet (small) instead of the full doc (large). That's the whole point of the layer.

Concrete triggers (any one is sufficient):

- A question repeatedly hits the same `<300-line> answer inside a 400+ line doc.
- A workflow needs a lookup table the parent doc has buried in prose (e.g. RLS policy by table, role permissions, env-var → service map).
- A pre-flight checklist for a recurring task (release, migration, incident).

Each cheatsheet:

- ≤50 lines total. If you can't fit it, the cheatsheet is being asked to do too much — narrow it.
- Same frontmatter shape as a domain doc, with `domain: cheatsheet`.
- Opens with a one-line "for full reasoning see `[[parent-doc]]`" pointer.
- Contains a single lookup table or short list — no narrative, no reasoning.
- Listed in `docs/_index.md` so an agent scanning the index can find it without reading the parent doc first.

**Don't** create cheatsheets proactively for every doc; only after a specific lookup has proven repetitive. **Do** link each cheatsheet from its parent doc's relevant section so an agent reading the parent first can drop down to the cheatsheet on subsequent passes.

## Archive, don't delete

Obsolete docs move to `docs/_archive/` with `status: archived` frontmatter and a `superseded-by: [[replacement-doc-filename]]` field. Never delete — backlinks from older specs/plans stay valid.

## Backlog

`docs/_backlog.md` tracks known gaps:

- Undocumented flows the author noticed but didn't have time to write up.
- Sections flagged "TODO verify" because verification was skipped.
- Code paths in `doc-ownership.yml` that have no corresponding doc section.

The "update docs" workflow appends to the backlog automatically when it encounters gaps.

## Touching a doc

When you edit any domain doc:

1. Bump `last-reviewed` to today.
2. If you touched a verification-citing section, re-run the underlying query and update the section-level `verified-against` HTML comment.
3. If you added a new section that maps to new code paths, add an entry to `doc-ownership.yml`.
4. If the doc grows past ~800 lines, add a `## Quick map` TOC at the top.

## Doc template

The bootstrap copies a skeleton to `docs/_meta/templates/domain-doc.md`. Copy it to start a new doc:

```bash
cp docs/_meta/templates/domain-doc.md docs/<your-domain>.md
```

A separate `docs/_meta/templates/cheatsheet.md` skeleton is available for ≤50-line lookup-style references.

## Anti-patterns

| Don't | Do |
|---|---|
| Paraphrase from CLAUDE.md or older docs | Re-verify against the source of truth |
| Cite a file without a line number | Always `file:line` |
| Skip the "What's in / What's NOT" callout | Always include it, even on short docs |
| Delete an obsolete doc | Move to `_archive/` with `superseded-by:` |
| Invent content for an undocumented behavior | Stub `## TODO: <feature>` and add to `_backlog.md` |
| Use relative paths in cross-refs | Always `[[filename]]` |
