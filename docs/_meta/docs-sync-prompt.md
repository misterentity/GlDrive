---
title: Docs Sync Prompt
domain: meta
status: active
last-reviewed: 2026-07-27
---

# Docs Sync Prompt

When the user says "update docs" or "sync docs", follow these steps. This is the local workflow for keeping domain docs fresh — usually before merging a feature branch.

> If you have the `sync-ai-docs` skill installed in Claude Code, that skill mirrors this workflow. This in-repo copy lets contributors without the skill follow the same steps.

## Step 1 — Determine the diff base

Ask the user (or infer from context):

- On a feature branch about to merge: base is `master...HEAD` (or `main...HEAD`).
- Just merged into master: base is `HEAD~1 HEAD`.
- User specifies a base ref: use that.

## Step 2 — Run the routing script

```bash
node scripts/match-docs.mjs <base-ref>
```

This writes:

- `affected-docs.txt` — newline-separated list of doc filenames to update.
- `match-summary.md` — human-readable explanation of which files changed and which docs are affected.

Read both.

## Step 3 — Handle unmatched files

If `match-summary.md` lists unmatched files (files that changed but no doc owns), do not proceed silently. Options:

- Add the paths to `docs/_meta/doc-ownership.yml` under the right doc, then re-run the script.
- Confirm with the user that these files genuinely don't belong to any doc.

## Step 4 — Refresh memory of conventions

Re-read `docs/_meta/vault-conventions.md`. Specifically:

- Frontmatter requirements.
- The "What's in / What's NOT" callout format.
- Section-level `verified-against` HTML comments.
- `file:line` anchors in code citations.
- Mermaid diagram threshold.

## Step 5 — For each affected doc, update it

For each doc in `affected-docs.txt`:

1. Read the current doc.
2. Read the changed code files routed to this doc.
3. If the doc has `verified-against:` frontmatter pointing at schema/API/live behavior, re-run the underlying queries. Diff results against current claims.
4. Edit the doc to reflect actual behavior:
   - Update sections describing changed code (with new `file:line` anchors).
   - Update schema/API tables if columns/types changed.
   - Update sequence diagrams if flow changed.
   - Bump `last-reviewed` to today's date.
   - Add new migrations/specs/queries to `verified-against:` if load-bearing.
5. If a section cannot be verified, append `TODO verify: <what>` to `docs/_backlog.md`. Do not guess.

## Step 6 — Do not invent content

If the merge adds behavior with no existing doc section:

- Append a `## TODO: <feature-name>` stub to the right doc.
- Add a matching entry to `docs/_backlog.md`.
- Tell the user: "Merge introduces new behavior with no existing doc section. Stub added; fill in manually in a dedicated writing session."

Do not hallucinate a full section.

## Step 7 — Summarize

Report back in a compact bullet list:

- Which docs were updated (with line-count diff).
- What new entries went into `_backlog.md`.
- Any unmatched files the user must decide about.
- Any verification queries that failed.

## What NOT to do

- Do not touch files outside `docs/**` and `docs/_backlog.md`.
- Do not modify code to "match the docs."
- Do not remove sections the code no longer supports — append `(removed YYYY-MM-DD)` and move to `_archive/` on a future pass.
- Do not write long narrative prose without a code citation or verification anchor backing each claim.
- Do not skip Step 3 (unmatched files) silently — that's how vaults rot.

## Common rationalizations to refuse

| Excuse | Reality |
|---|---|
| "The code change is small, docs probably aren't affected" | Run the routing script anyway. It takes 2 seconds. |
| "I'll re-verify the schema later" | Later = never. Re-verify now or add a `TODO verify` to `_backlog.md`. |
| "The unmatched file is obviously just a config tweak" | Confirm with the user before skipping. Configs become load-bearing. |
| "I can paraphrase the existing doc to match the new code" | No. Re-verify against ground truth. Paraphrasing is how docs hallucinate. |
