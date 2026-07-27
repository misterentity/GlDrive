#!/usr/bin/env node
// vault-doctor.mjs — lint a wts-ai-docs vault for structural and freshness issues.
//
// Usage:
//   node scripts/vault-doctor.mjs [--root <path>] [--max-age <days>] [--json]
//   node scripts/vault-doctor.mjs --help
//
// Exit codes:
//   0 — no violations
//   1 — at least one violation
//   2 — fatal error (vault not found, bad args, etc.)
//
// Checks:
//   1. Frontmatter shape — every .md under docs/ (except _archive/) has the
//      required keys: title, domain, status, last-reviewed.
//   2. last-reviewed staleness — older than --max-age days (default 60).
//   3. "What's in / What's NOT" callout — required on every top-level
//      docs/<slug>.md domain doc (not under _meta/, _archive/, _cheatsheets/,
//      and not underscore-prefixed like _index.md / _backlog.md).
//   4. Wikilinks resolve — every [[target]] and [[target#anchor]] resolves to
//      an existing doc and (if anchor) to an actual heading in that doc.
//   5. Domain stem match — for top-level domain docs, frontmatter `domain:`
//      equals the filename stem.
//   6. Doc length — domain docs longer than --max-doc-lines (default 600)
//      cost the agent context budget. The warning triggers a "split or extract
//      a cheatsheet" conversation before the doc becomes unreadable.
//   7. Archived location — docs with status: archived must live under _archive/
//      to prevent agents from reading them as live in the flat root.
//   8. Byte budget — domain docs larger than --max-doc-bytes (default 48 KiB)
//      burn the agent's context budget; long lines defeat the line cap.
//
// Deferred to a later milestone:
//   * doc-ownership.yml cite-against-paths.
//
// Zero runtime deps — Node built-ins only. The YAML frontmatter parser handles
// only the constrained shape vault docs use (string values, date values, simple
// `- item` lists). It is NOT a general YAML parser.

import { promises as fs } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { loadOwnership, globToRegExp, compileOwnership } from './match-docs.mjs';

const DEFAULT_MAX_AGE_DAYS = 60;
const DEFAULT_MAX_DOC_LINES = 600;
// ~600 lines × ~80 chars. A line cap can't see long lines: a 515-"line"
// doc was 81 KB in the field (one-paragraph-per-line prose). Bytes track
// what actually hits the agent's context window.
const DEFAULT_MAX_DOC_BYTES = 49152; // 48 KiB

// ---- CLI ----------------------------------------------------------------

const HELP = `vault-doctor — lint a wts-ai-docs vault.

USAGE
  node scripts/vault-doctor.mjs [--root <path>] [--max-age <days>]
                                [--max-doc-lines <n>] [--max-doc-bytes <n>] [--json]
  node scripts/vault-doctor.mjs --help

OPTIONS
  --root <path>           Vault root (default: ./docs relative to cwd).
  --max-age <days>        Default freshness threshold (default: ${DEFAULT_MAX_AGE_DAYS}).
                          Per-doc override: frontmatter \`max-age: N\`.
  --max-doc-lines <n>     Warn when a domain doc exceeds N lines (default: ${DEFAULT_MAX_DOC_LINES}).
                          A long doc burns agent context budget; split or
                          extract a cheatsheet. Per-doc override: frontmatter
                          \`max-doc-lines: N\` (set to 0 to disable for that doc).
  --max-doc-bytes <n>     Warn when a domain doc exceeds N bytes (default: ${DEFAULT_MAX_DOC_BYTES}).
                          Long lines defeat a line cap; bytes measure real
                          context cost. Per-doc override: frontmatter
                          \`max-doc-bytes: N\` (set to 0 to disable for that doc).
  --check-coverage        Enable the coverage check: for each domain doc
                          with paths declared in doc-ownership.yml, report
                          which files in that glob are not cited in the doc.
                          Off by default (opt-in).
  --enforce-coverage <n>  Implies --check-coverage. Treat docs with file
                          coverage below N% as violations (exit 1). Without
                          this flag, coverage gaps are informational only.
  --repo-root <path>      Path to repo root (where doc-ownership.yml's path
                          globs are evaluated). Default: parent of --root.
  --json                  Emit machine-readable JSON instead of human text.
  --help, -h              Show this help.

ESCAPE HATCHES (frontmatter, per-doc)
  vault-doctor: skip                              # skip ALL checks on this doc
  vault-doctor-skip-checks: [stale, wikilink]     # skip a subset
  max-age: 30                                     # override --max-age for this doc
  max-doc-lines: 800                              # override --max-doc-lines for this doc
  max-doc-bytes: 65536                            # override --max-doc-bytes for this doc
  coverage-extensions: [.ts, .tsx]                # restrict coverage to these exts only
  coverage-exclude: [path/to/file.ts]             # exempt specific files from coverage

STRAY DIRS CONFIG (_index.md frontmatter only)
  vault-doctor-ignore-dirs: [superpowers]         # silence stray .md file info lines (read only from _index.md)

INLINE ESCAPE HATCH (in doc body)
  See [[business#planned-section]] <!-- vault-doctor: ignore -->

INFO LINES (informational, do not cause exit 1)
  Stray .md files: .md files in non-blessed subdirs (not linted, but surfaced per-dir).

EXIT CODES
  0   no violations
  1   at least one violation
  2   fatal (e.g. vault root missing)
`;

function parseArgs(argv) {
  const args = {
    root: null,
    maxAge: DEFAULT_MAX_AGE_DAYS,
    maxDocLines: DEFAULT_MAX_DOC_LINES,
    maxDocBytes: DEFAULT_MAX_DOC_BYTES,
    json: false,
    help: false,
    checkCoverage: false,
    enforceCoverage: null,  // null = report only; integer = % threshold
    repoRoot: null,         // null = inferred from --root
  };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--help' || a === '-h') args.help = true;
    else if (a === '--json') args.json = true;
    else if (a === '--root') args.root = argv[++i];
    else if (a === '--max-age') {
      const n = parseInt(argv[++i], 10);
      if (!Number.isFinite(n) || n < 0) die(`--max-age must be a non-negative integer, got "${argv[i]}".`, 2);
      args.maxAge = n;
    } else if (a === '--max-doc-lines') {
      const n = parseInt(argv[++i], 10);
      if (!Number.isFinite(n) || n < 0) die(`--max-doc-lines must be a non-negative integer, got "${argv[i]}".`, 2);
      args.maxDocLines = n;
    } else if (a === '--max-doc-bytes') {
      const n = parseInt(argv[++i], 10);
      if (!Number.isFinite(n) || n < 0) die(`--max-doc-bytes must be a non-negative integer, got "${argv[i]}".`, 2);
      args.maxDocBytes = n;
    } else if (a === '--check-coverage') args.checkCoverage = true;
    else if (a === '--enforce-coverage') {
      const n = parseInt(argv[++i], 10);
      if (!Number.isFinite(n) || n < 0 || n > 100) {
        die(`--enforce-coverage must be an integer 0–100, got "${argv[i]}".`, 2);
      }
      args.enforceCoverage = n;
      args.checkCoverage = true; // enforce implies enabled
    }
    else if (a === '--repo-root') args.repoRoot = argv[++i];
    else {
      die(`Unknown argument: ${a}\nRun --help for usage.`, 2);
    }
  }
  return args;
}

function die(msg, code = 2) {
  console.error(msg);
  process.exit(code);
}

// ---- YAML frontmatter parser (constrained subset) ----------------------
//
// Handles: --- delimiters, `key: value` strings (quoted or not), date values
// (YYYY-MM-DD as string), and `- item` lists under a key. Rejects anything
// else (anchors, nested objects, multi-line scalars) with a useful error.

function parseFrontmatter(text) {
  // Strip UTF-8 BOM.
  if (text.charCodeAt(0) === 0xFEFF) text = text.slice(1);
  // Normalize line endings.
  const lines = text.split(/\r?\n/);

  // Frontmatter must start at line 1 with `---`.
  if (lines[0] !== '---') return { found: false };

  // Find closing `---`.
  let endIdx = -1;
  for (let i = 1; i < lines.length; i++) {
    if (lines[i] === '---') { endIdx = i; break; }
  }
  if (endIdx === -1) return { found: true, error: 'unterminated frontmatter (no closing `---`)' };

  const fm = {};
  let currentListKey = null;

  for (let i = 1; i < endIdx; i++) {
    const raw = lines[i];
    const stripped = raw.replace(/\s+$/, '');
    if (!stripped.trim()) { currentListKey = null; continue; }
    if (stripped.trimStart().startsWith('#')) continue; // comment

    const indent = stripped.length - stripped.trimStart().length;

    // List item under the previous key.
    if (currentListKey && indent > 0 && stripped.trimStart().startsWith('-')) {
      const value = stripped.trimStart().slice(1).trim();
      const unquoted = unquote(value);
      fm[currentListKey].push(unquoted);
      continue;
    }

    // key: value or key:
    if (indent !== 0) {
      return { found: true, error: `line ${i + 1}: unexpected indent (parser supports flat key:value and one-level lists only)` };
    }
    const m = stripped.match(/^([A-Za-z][\w-]*):\s*(.*)$/);
    if (!m) {
      return { found: true, error: `line ${i + 1}: could not parse "${stripped}" — expected \`key: value\` or \`key:\`` };
    }
    const key = m[1];
    const valueRaw = m[2];
    if (valueRaw === '') {
      // Begin a block list (or a sub-block — but we don't support sub-blocks).
      fm[key] = [];
      currentListKey = key;
    } else if (valueRaw.startsWith('[') && valueRaw.endsWith(']')) {
      // Inline list: `key: [a, b, "c"]`. Items are split on commas and unquoted.
      // Commas inside quoted values aren't supported (parser stays simple).
      const inner = valueRaw.slice(1, -1).trim();
      fm[key] = inner === ''
        ? []
        : inner.split(',').map(s => unquote(s.trim()));
      currentListKey = null;
    } else {
      fm[key] = unquote(valueRaw);
      currentListKey = null;
    }
  }

  return { found: true, data: fm, bodyStartLine: endIdx + 1 };
}

function unquote(s) {
  if (
    (s.startsWith("'") && s.endsWith("'")) ||
    (s.startsWith('"') && s.endsWith('"'))
  ) {
    return s.slice(1, -1);
  }
  return s;
}

// ---- File discovery -----------------------------------------------------

async function walk(dir, excludeDirs) {
  const out = [];
  async function recurse(d) {
    let entries;
    try {
      entries = await fs.readdir(d, { withFileTypes: true });
    } catch (err) {
      throw new Error(`cannot read directory ${d}: ${err.message}`);
    }
    for (const ent of entries) {
      const full = path.join(d, ent.name);
      if (ent.isDirectory()) {
        if (excludeDirs.has(ent.name)) continue;
        await recurse(full);
      } else if (ent.isFile() && ent.name.endsWith('.md')) {
        out.push(full);
      }
    }
  }
  await recurse(dir);
  return out;
}

const DEFAULT_REPO_EXCLUDES = new Set([
  'node_modules', '.git', 'dist', 'build', 'out', '.next',
  'coverage', 'bin', 'obj', '.venv', '__pycache__', 'target',
]);

// Walk a repo root (not the vault root). Returns posix-style relative paths.
// Defaults (node_modules, .git, dist, etc.) are always applied. Pass
// `excludeDirs` to add additional directories to exclude.
async function walkRepo(repoRoot, excludeDirs = DEFAULT_REPO_EXCLUDES) {
  const combined = new Set([...DEFAULT_REPO_EXCLUDES, ...excludeDirs]);
  const out = [];
  async function recurse(d, relPrefix) {
    let entries;
    try {
      entries = await fs.readdir(d, { withFileTypes: true });
    } catch {
      return;
    }
    for (const ent of entries) {
      if (ent.isDirectory()) {
        if (combined.has(ent.name)) continue;
        await recurse(path.join(d, ent.name), relPrefix ? `${relPrefix}/${ent.name}` : ent.name);
      } else if (ent.isFile()) {
        out.push(relPrefix ? `${relPrefix}/${ent.name}` : ent.name);
      }
    }
  }
  await recurse(repoRoot, '');
  return out;
}

// ---- Doc classification -------------------------------------------------
//
// Given a path relative to vault root (e.g. "auth.md", "_meta/foo.md",
// "session-review/2026-04-14.md"), return a classification:
//   - 'domain'      : top-level docs/<slug>.md — full checks (incl. callout + stem)
//   - 'meta'        : docs/_meta/*.md — frontmatter + wikilinks only
//   - 'cheatsheet'  : docs/_cheatsheets/*.md — frontmatter + wikilinks only
//   - 'index-like'  : docs/_index.md, docs/_backlog.md — frontmatter + wikilinks only
//   - 'other'       : subdirs not part of the vault convention (e.g. session-review/,
//                     superpowers/, adr/) — skipped entirely. README.md is also skipped.

function classify(relPosix) {
  const segments = relPosix.split('/');
  if (segments[0] === '_archive') return 'skip';
  if (segments[0] === '_meta') return 'meta';
  if (segments[0] === '_cheatsheets') return 'cheatsheet';
  if (segments.length === 1) {
    const name = segments[0];
    if (name === '_index.md' || name === '_backlog.md') return 'index-like';
    if (name.toLowerCase() === 'readme.md') return 'skip';
    if (name.startsWith('_')) return 'index-like';
    return 'domain';
  }
  // .md files in subdirs the convention doesn't bless: not linted, but no
  // longer invisible — surfaced as per-dir info lines (ascendai's
  // docs/regions/ hid 5 abandoned ungoverned docs for 6 weeks).
  return 'stray';
}

// ---- Checks -------------------------------------------------------------

const REQUIRED_FRONTMATTER_KEYS = ['title', 'domain', 'status', 'last-reviewed'];

function checkFrontmatter(doc) {
  const violations = [];
  if (!doc.frontmatter.found) {
    violations.push({ check: 'frontmatter', detail: 'missing frontmatter block (file must start with `---`)' });
    return violations;
  }
  if (doc.frontmatter.error) {
    violations.push({ check: 'frontmatter', detail: `parse error: ${doc.frontmatter.error}` });
    return violations;
  }
  for (const key of REQUIRED_FRONTMATTER_KEYS) {
    if (!(key in doc.frontmatter.data)) {
      violations.push({ check: 'frontmatter', detail: `missing required key \`${key}\`` });
    }
  }
  return violations;
}

const DATE_RE = /^\d{4}-\d{2}-\d{2}$/;

function checkStaleness(doc, defaultMaxAge, today) {
  if (!doc.frontmatter.data) return [];
  const value = doc.frontmatter.data['last-reviewed'];
  if (!value) return []; // already reported by frontmatter check
  if (!DATE_RE.test(value)) {
    return [{ check: 'stale', detail: `last-reviewed \`${value}\` is not a valid YYYY-MM-DD date` }];
  }
  // Per-doc max-age overrides the CLI default. Useful when one runbook needs
  // tighter freshness than the rest of the vault (or looser).
  const docMaxAge = doc.frontmatter.data['max-age'];
  let maxAge = defaultMaxAge;
  if (docMaxAge !== undefined) {
    const n = parseInt(docMaxAge, 10);
    if (!Number.isFinite(n) || n < 0) {
      return [{ check: 'stale', detail: `frontmatter max-age \`${docMaxAge}\` is not a non-negative integer` }];
    }
    maxAge = n;
  }
  const ageMs = today.getTime() - Date.parse(value + 'T00:00:00Z');
  const ageDays = Math.floor(ageMs / (1000 * 60 * 60 * 24));
  if (ageDays > maxAge) {
    return [{ check: 'stale', detail: `last-reviewed ${value} is ${ageDays} days old (max ${maxAge})` }];
  }
  return [];
}

const CALLOUT_IN_RE = /What['’]s in this doc:/;
const CALLOUT_NOT_RE = /What['’]s NOT:/;

function checkCallout(doc) {
  // Scan the first ~40 body lines for both phrases inside a blockquote.
  const body = doc.body.split(/\r?\n/).slice(0, 60).join('\n');
  if (!CALLOUT_IN_RE.test(body)) {
    return [{ check: 'callout', detail: 'missing "What’s in this doc:" line near top of doc' }];
  }
  if (!CALLOUT_NOT_RE.test(body)) {
    return [{ check: 'callout', detail: 'missing "What’s NOT:" line near top of doc' }];
  }
  return [];
}

function checkDomainStem(doc) {
  if (!doc.frontmatter.data) return [];
  const domain = doc.frontmatter.data['domain'];
  if (!domain) return [];
  const stem = path.basename(doc.relPath, '.md');
  if (domain !== stem) {
    return [{ check: 'domain-stem', detail: `frontmatter domain \`${domain}\` does not match filename stem \`${stem}\`` }];
  }
  return [];
}

// status: archived docs must live under _archive/ (which the walk skips).
// Any doc we can see that claims archived status is therefore misplaced —
// agents reading the flat root treat everything there as live.
function checkArchivedLocation(doc) {
  if (doc.frontmatter.data?.['status'] !== 'archived') return [];
  return [{
    check: 'archived-location',
    detail: 'status is `archived` but file is not under docs/_archive/ — move it (keep `superseded-by:` frontmatter) so agents don\'t read it as live',
  }];
}

// Doc length is a context-efficiency check, not a hallucination check. A
// 600+ line domain doc forces the agent to load lots of mostly-irrelevant
// content. The fix is usually one of: (a) split into two narrower docs,
// (b) extract a cheatsheet for the highest-frequency lookup, or
// (c) move stale sections to `_archive/`. Per-doc override via frontmatter
// `max-doc-lines: N` (use 0 to disable for that doc).
function checkDocLength(doc, defaultMax) {
  const docMax = doc.frontmatter.data?.['max-doc-lines'];
  let max = defaultMax;
  if (docMax !== undefined) {
    const n = parseInt(docMax, 10);
    if (!Number.isFinite(n) || n < 0) {
      return [{ check: 'length', detail: `frontmatter max-doc-lines \`${docMax}\` is not a non-negative integer` }];
    }
    max = n;
  }
  if (max === 0) return []; // explicit opt-out per-doc
  const lineCount = doc.text.split(/\r?\n/).length;
  if (lineCount > max) {
    return [{
      check: 'length',
      detail: `${lineCount} lines exceeds limit ${max} — split, extract a cheatsheet, or archive stale sections`,
    }];
  }
  return [];
}

// Byte-size companion to checkDocLength — same override pattern
// (frontmatter `max-doc-bytes: N`, 0 disables).
function checkDocBytes(doc, defaultMax) {
  const docMax = doc.frontmatter.data?.['max-doc-bytes'];
  let max = defaultMax;
  if (docMax !== undefined) {
    const n = parseInt(docMax, 10);
    if (!Number.isFinite(n) || n < 0) {
      return [{ check: 'bytes', detail: `frontmatter max-doc-bytes \`${docMax}\` is not a non-negative integer` }];
    }
    max = n;
  }
  if (max === 0) return [];
  const bytes = Buffer.byteLength(doc.text, 'utf8');
  if (bytes > max) {
    return [{
      check: 'bytes',
      detail: `${Math.round(bytes / 1024)} KB exceeds limit ${Math.round(max / 1024)} KB — long lines defeat the line cap; split, extract a cheatsheet, or archive stale sections`,
    }];
  }
  return [];
}

// Coverage check: for a domain doc with declared `paths` in
// doc-ownership.yml, count how many files matching those paths are cited
// (by literal path substring) anywhere in the doc body. Reports uncovered
// files so an author can either document them or remove them from the doc's
// territory.
//
// Citation detection is a literal substring match against the doc body
// (after frontmatter). This is robust to formatting variation:
//   - `src/api/auth/route.ts:42` (backticks + line)
//   - `src/api/auth/route.ts` (backticks, no line)
//   - src/api/auth/route.ts (bare in prose)
// all match. False-positive risk is low; documents don't accidentally embed
// realistic file paths.
function checkCoverage(doc, ownedFiles) {
  const total = ownedFiles.length;
  if (total === 0) {
    return { total: 0, cited: 0, uncovered: [], percent: 100 };
  }
  const cited = [];
  const uncovered = [];
  for (const f of ownedFiles) {
    if (doc.body.includes(f)) {
      cited.push(f);
    } else {
      uncovered.push(f);
    }
  }
  const percent = Math.round((cited.length / total) * 100);
  return { total, cited: cited.length, uncovered, percent };
}

const DEFAULT_COVERAGE_SKIP_EXTS = new Set([
  '.md', '.json', '.yml', '.yaml', '.svg', '.png', '.jpg', '.jpeg',
  '.gif', '.ico', '.css', '.scss', '.lock', '.txt',
]);

// Filter a list of repo-relative file paths down to those that should
// require doc coverage. By default, drops configs/assets (see
// DEFAULT_COVERAGE_SKIP_EXTS). When `opts.allow` is provided, switches to
// allowlist mode: only files with those extensions are kept.
function filterCoverageEligible(files, opts = {}) {
  const allow = opts.allow
    ? new Set(opts.allow.map(e => e.toLowerCase()))
    : null;
  return files.filter(f => {
    const dot = f.lastIndexOf('.');
    if (dot === -1 || dot < f.lastIndexOf('/')) return true; // no ext = keep
    const ext = f.slice(dot).toLowerCase();
    if (allow) return allow.has(ext);
    return !DEFAULT_COVERAGE_SKIP_EXTS.has(ext);
  });
}

// GitHub-style heading slug: lowercase, drop most punctuation, EACH whitespace
// char → one hyphen (no collapsing — that's how GitHub renders "X & Y" as
// `x--y`). Tested against real headings like:
//   "What's NOT:"                 → whats-not
//   "Known Quirks & Gotchas"      → known-quirks--gotchas
//   "Next.js version"             → nextjs-version
function slugifyHeading(h) {
  return h
    .toLowerCase()
    .replace(/[’']/g, '')              // smart and dumb apostrophes
    .replace(/[^\w\sÀ-ɏ-]/g, '')  // drop punctuation but keep Latin extended
    .replace(/^\s+|\s+$/g, '')         // trim only — don't collapse interior whitespace
    .replace(/\s/g, '-');              // each space → one hyphen (matches GitHub)
}

function extractHeadings(body) {
  const slugs = new Set();
  const seen = {};
  for (const line of body.split(/\r?\n/)) {
    const m = line.match(/^#{1,6}\s+(.+?)\s*#*\s*$/);
    if (!m) continue;
    let slug = slugifyHeading(m[1]);
    if (slug in seen) {
      seen[slug]++;
      slug = `${slug}-${seen[slug]}`;
    } else {
      seen[slug] = 0;
    }
    slugs.add(slug);
  }
  return slugs;
}

const WIKILINK_RE = /\[\[([^\]\|#]+?)(?:#([^\]\|]+?))?(?:\|[^\]]+)?\]\]/g;

// Strip fenced (``` / ~~~) and inline-code (`...`) spans from body before
// extracting wikilinks, so example syntax inside docs doesn't fire wikilink
// resolution. Convention docs intentionally show `[[filename]]` in code blocks.
function stripCode(body) {
  const lines = body.split(/\r?\n/);
  let inFence = false;
  let fenceMarker = '';
  const out = [];
  for (const line of lines) {
    const fence = line.match(/^\s*(```|~~~)/);
    if (fence) {
      if (!inFence) { inFence = true; fenceMarker = fence[1]; out.push(''); continue; }
      if (line.includes(fenceMarker)) { inFence = false; fenceMarker = ''; out.push(''); continue; }
    }
    if (inFence) { out.push(''); continue; }
    // Strip inline code spans.
    out.push(line.replace(/`[^`]*`/g, ''));
  }
  return out.join('\n');
}

// Inline ignore marker — placed on the same line as a wikilink to suppress
// just that wikilink's resolution check. Useful for known-broken links you
// haven't fixed yet, or links pointing at planned-but-unwritten docs.
const INLINE_IGNORE_RE = /<!--\s*vault-doctor:\s*ignore\s*-->/;

function checkWikilinks(doc, docsByStem) {
  const violations = [];
  const seen = new Set();
  const scannable = stripCode(doc.body);
  // Iterate line-by-line so we can honor inline ignores adjacent to the link.
  for (const line of scannable.split(/\r?\n/)) {
    if (INLINE_IGNORE_RE.test(line)) continue;
    for (const match of line.matchAll(WIKILINK_RE)) {
      const target = match[1].trim();
      const anchor = match[2] ? match[2].trim() : null;
      const key = `${target}#${anchor || ''}`;
      if (seen.has(key)) continue;
      seen.add(key);

      const targetDoc = docsByStem.get(target);
      if (!targetDoc) {
        violations.push({
          check: 'wikilink',
          detail: `[[${target}${anchor ? '#' + anchor : ''}]] — target file not found in vault`,
        });
        continue;
      }
      if (anchor) {
        const anchorSlug = slugifyHeading(anchor);
        if (!targetDoc.headings.has(anchorSlug)) {
          violations.push({
            check: 'wikilink',
            detail: `[[${target}#${anchor}]] — anchor \`${anchorSlug}\` not found in ${targetDoc.relPath}`,
          });
        }
      }
    }
  }
  return violations;
}

// ---- Pipeline -----------------------------------------------------------

async function loadDoc(absPath, rootAbs) {
  const text = await fs.readFile(absPath, 'utf8');
  const relPath = path.relative(rootAbs, absPath).split(path.sep).join('/');
  const fm = parseFrontmatter(text);
  // Body is everything after the frontmatter block (or the whole file if none).
  let body = text;
  if (fm.found && fm.bodyStartLine) {
    body = text.split(/\r?\n/).slice(fm.bodyStartLine).join('\n');
  }
  return {
    absPath,
    relPath,
    text,
    frontmatter: fm,
    body,
    headings: extractHeadings(body),
    klass: classify(relPath),
  };
}

async function audit({ root, maxAge, maxDocLines, maxDocBytes = DEFAULT_MAX_DOC_BYTES, checkCoverage: coverageEnabled, enforceCoverage, repoRoot }) {
  const rootAbs = path.resolve(root);
  const repoRootAbs = repoRoot
    ? path.resolve(repoRoot)
    : path.resolve(rootAbs, '..');
  let stat;
  try {
    stat = await fs.stat(rootAbs);
  } catch {
    die(`Vault root not found: ${rootAbs}`, 2);
  }
  if (!stat.isDirectory()) die(`Vault root is not a directory: ${rootAbs}`, 2);

  const files = await walk(rootAbs, new Set(['_archive']));
  const docs = await Promise.all(files.map(f => loadDoc(f, rootAbs)));

  // Build per-doc owned-files lists once. Cheap when coverage is off (skipped
  // entirely); when on, we walk the repo once and run each doc's globs.
  let coverageByDoc = null; // Map<slug, string[]> (eligible owned files)
  if (coverageEnabled) {
    const ownershipPath = path.join(rootAbs, '_meta', 'doc-ownership.yml');
    let ownershipLoad;
    try {
      ownershipLoad = await loadOwnership(ownershipPath);
    } catch (err) {
      die(`Coverage check enabled but could not load ownership file:\n  ${err.message}`, 2);
    }
    const compiled = compileOwnership(ownershipLoad.ownership);
    const allFiles = await walkRepo(repoRootAbs);
    coverageByDoc = new Map();
    for (const { doc, pathRegexes } of compiled) {
      const matched = allFiles.filter(f => pathRegexes.some(({ re }) => re.test(f)));
      coverageByDoc.set(doc, matched);
    }
  }

  // Index by stem (filename without .md, relative path components joined with /).
  // Wikilinks use bare stems like [[auth]] or [[vault-conventions]] (without path),
  // so register by stem only. Last-wins is fine because vault filenames should be
  // globally unique.
  const docsByStem = new Map();
  for (const d of docs) {
    const stem = path.basename(d.relPath, '.md');
    docsByStem.set(stem, d);
  }

  // Stray .md accounting (info, not violations). Silenceable per-dir via
  // `vault-doctor-ignore-dirs:` list in _index.md frontmatter.
  const indexDoc = docs.find(d => d.relPath === '_index.md');
  const rawIgnore = indexDoc?.frontmatter.data?.['vault-doctor-ignore-dirs'];
  const ignoreDirs = new Set(Array.isArray(rawIgnore) ? rawIgnore.map(s => String(s).trim()) : []);
  const strayByDir = new Map();
  for (const d of docs) {
    if (d.klass !== 'stray') continue;
    const top = d.relPath.split('/')[0];
    if (ignoreDirs.has(top)) continue;
    strayByDir.set(top, (strayByDir.get(top) || 0) + 1);
  }

  const today = new Date();
  const report = []; // { relPath, klass, violations: [], skipped: bool }

  for (const d of docs) {
    if (d.klass === 'skip' || d.klass === 'stray') continue;

    // File-level escape hatches via frontmatter:
    //   vault-doctor: skip                          # skip ALL checks
    //   vault-doctor: skip-checks: [stale, callout] # skip a subset
    // Both shapes are honored. `skip` short-circuits; the list form filters.
    const vdValue = d.frontmatter.data?.['vault-doctor'];
    const vdSkipChecks = d.frontmatter.data?.['vault-doctor-skip-checks'];
    if (vdValue === 'skip') {
      report.push({ relPath: d.relPath, klass: d.klass, violations: [], skipped: true, coverageResult: null });
      continue;
    }
    const skipChecks = new Set();
    if (Array.isArray(vdSkipChecks)) {
      for (const c of vdSkipChecks) skipChecks.add(String(c).trim());
    }

    const violations = [];

    // Frontmatter applies to everything except 'skip'.
    if (!skipChecks.has('frontmatter')) {
      violations.push(...checkFrontmatter(d));
    }

    // Staleness applies if frontmatter parsed.
    if (d.frontmatter.data && !skipChecks.has('stale')) {
      violations.push(...checkStaleness(d, maxAge, today));
    }

    // Archived docs must live in _archive/ (applies to every linted class).
    if (d.frontmatter.data && !skipChecks.has('archived-location')) {
      violations.push(...checkArchivedLocation(d));
    }

    // Callout only on 'domain'.
    if (d.klass === 'domain' && !skipChecks.has('callout')) {
      violations.push(...checkCallout(d));
    }

    // Domain stem only on 'domain'.
    if (d.klass === 'domain' && !skipChecks.has('domain-stem')) {
      violations.push(...checkDomainStem(d));
    }

    // Doc length only on 'domain'. Context-efficiency signal — too-long docs
    // burn the agent's budget on irrelevant sections.
    if (d.klass === 'domain' && !skipChecks.has('length')) {
      violations.push(...checkDocLength(d, maxDocLines));
    }

    // Byte budget only on 'domain' — same rationale as length, but honest
    // about long lines.
    if (d.klass === 'domain' && !skipChecks.has('bytes')) {
      violations.push(...checkDocBytes(d, maxDocBytes));
    }

    // Coverage (opt-in, domain docs only).
    let coverageResult = null;
    if (coverageEnabled && d.klass === 'domain' && !skipChecks.has('coverage')) {
      const slug = path.basename(d.relPath, '.md');
      const ownedRaw = coverageByDoc.get(slug) || [];
      // Per-doc extension allowlist via frontmatter.
      const allow = d.frontmatter.data?.['coverage-extensions'];
      const filtered = filterCoverageEligible(
        ownedRaw,
        Array.isArray(allow) ? { allow } : {},
      );
      // Per-doc file-level exclusions.
      const excludes = new Set(
        Array.isArray(d.frontmatter.data?.['coverage-exclude'])
          ? d.frontmatter.data['coverage-exclude']
          : [],
      );
      const owned = filtered.filter(f => !excludes.has(f));
      coverageResult = checkCoverage(d, owned);
      if (enforceCoverage !== null && owned.length > 0 && coverageResult.percent < enforceCoverage) {
        violations.push({
          check: 'coverage',
          detail: `${coverageResult.cited}/${coverageResult.total} files cited (${coverageResult.percent}%) — below threshold ${enforceCoverage}% — uncovered: ${coverageResult.uncovered.slice(0, 5).join(', ')}${coverageResult.uncovered.length > 5 ? `, +${coverageResult.uncovered.length - 5} more` : ''}`,
        });
      }
    }

    // Wikilinks apply to everything except 'skip'.
    if (!skipChecks.has('wikilink')) {
      violations.push(...checkWikilinks(d, docsByStem));
    }

    report.push({ relPath: d.relPath, klass: d.klass, violations, skipped: false, coverageResult });
  }

  return {
    rootAbs,
    report,
    strayDirs: [...strayByDir.entries()].map(([dir, count]) => ({ dir, count })),
  };
}

// ---- Output -------------------------------------------------------------

function printHuman(rootAbs, report, strayDirs = []) {
  let totalViolations = 0;
  let skippedCount = 0;
  const sorted = [...report].sort((a, b) => a.relPath.localeCompare(b.relPath));
  for (const r of sorted) {
    if (r.skipped) {
      console.log(`· ${r.relPath} (skipped via frontmatter vault-doctor: skip)`);
      skippedCount++;
      continue;
    }
    if (r.violations.length === 0) {
      console.log(`✓ ${r.relPath}`);
    } else {
      for (const v of r.violations) {
        console.log(`✗ ${r.relPath}: [${v.check}] ${v.detail}`);
        totalViolations++;
      }
    }
    // Coverage INFO line (informational, doesn't count as a violation).
    // Skipped for docs whose owned-files filter dropped everything (total 0).
    if (r.coverageResult && r.coverageResult.total > 0) {
      const cr = r.coverageResult;
      const tail = cr.uncovered.length === 0
        ? ''
        : ` — uncovered: ${cr.uncovered.slice(0, 5).join(', ')}${cr.uncovered.length > 5 ? `, +${cr.uncovered.length - 5} more` : ''}`;
      console.log(`i ${r.relPath}: [coverage] ${cr.cited}/${cr.total} files cited (${cr.percent}%)${tail}`);
    }
  }
  for (const s of strayDirs) {
    console.log(`i ${s.dir}/ — ${s.count} .md file(s) outside vault governance (not linted). Adopt into the vault, move to _archive/, or silence via \`vault-doctor-ignore-dirs: [${s.dir}]\` in _index.md frontmatter.`);
  }
  console.log('');
  const checked = report.length - skippedCount;
  if (totalViolations === 0) {
    console.log(`${checked} docs checked${skippedCount ? `, ${skippedCount} skipped` : ''}, 0 violations.`);
  } else {
    console.log(`${totalViolations} violation(s) across ${checked} doc(s)${skippedCount ? ` (${skippedCount} skipped)` : ''} in ${rootAbs}.`);
  }
  return totalViolations;
}

function printJson(rootAbs, report, strayDirs = []) {
  let totalViolations = 0;
  const violations = [];
  const coverage = [];
  for (const r of report) {
    for (const v of r.violations) {
      violations.push({ file: r.relPath, check: v.check, detail: v.detail });
      totalViolations++;
    }
    if (r.coverageResult && r.coverageResult.total > 0) {
      coverage.push({
        file: r.relPath,
        cited: r.coverageResult.cited,
        total: r.coverageResult.total,
        percent: r.coverageResult.percent,
        uncovered: r.coverageResult.uncovered,
      });
    }
  }
  const out = {
    summary: {
      root: rootAbs,
      totalDocs: report.length,
      violations: totalViolations,
      exitCode: totalViolations === 0 ? 0 : 1,
    },
    violations,
    coverage,
    strays: strayDirs,
  };
  console.log(JSON.stringify(out, null, 2));
  return totalViolations;
}

// ---- Entry --------------------------------------------------------------

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) {
    console.log(HELP);
    return 0;
  }
  const root = args.root || path.join(process.cwd(), 'docs');
  const { rootAbs, report, strayDirs } = await audit({
    root,
    maxAge: args.maxAge,
    maxDocLines: args.maxDocLines,
    maxDocBytes: args.maxDocBytes,
    checkCoverage: args.checkCoverage,
    enforceCoverage: args.enforceCoverage,
    repoRoot: args.repoRoot,
  });
  const violations = args.json ? printJson(rootAbs, report, strayDirs) : printHuman(rootAbs, report, strayDirs);
  return violations === 0 ? 0 : 1;
}

// Run main() only when executed directly (not when imported as a module
// for tests). The path comparison handles Windows + symlinked entrypoints.
const invokedDirectly = (() => {
  try { return fileURLToPath(import.meta.url) === path.resolve(process.argv[1] || ''); }
  catch { return false; }
})();
if (invokedDirectly) {
  main().then(code => process.exit(code)).catch(err => {
    console.error(`vault-doctor failed: ${err.message}`);
    if (process.env.DEBUG) console.error(err.stack);
    process.exit(2);
  });
}

// Test-only exports. Not part of the CLI contract; do not depend on these
// from outside `tests/`.
export {
  audit,
  parseFrontmatter,
  unquote,
  classify,
  slugifyHeading,
  extractHeadings,
  stripCode,
  checkFrontmatter,
  checkStaleness,
  checkCallout,
  checkDomainStem,
  checkArchivedLocation,
  checkDocLength,
  checkDocBytes,
  checkCoverage,
  checkWikilinks,
  WIKILINK_RE,
  walkRepo,
  DEFAULT_REPO_EXCLUDES,
  filterCoverageEligible,
  DEFAULT_COVERAGE_SKIP_EXTS,
  DEFAULT_MAX_DOC_BYTES,
};
