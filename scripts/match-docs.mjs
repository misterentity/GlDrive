#!/usr/bin/env node
// match-docs.mjs — route a git diff to the docs that should be updated.
//
// Usage:
//   node scripts/match-docs.mjs <base-ref> [--out-dir <path>]
//
// Examples:
//   node scripts/match-docs.mjs main           # diff main...HEAD
//   node scripts/match-docs.mjs HEAD~1         # last commit
//   node scripts/match-docs.mjs origin/master  # diff origin/master...HEAD
//   node scripts/match-docs.mjs main --out-dir ./out  # pinned output location
//
// Reads:
//   docs/_meta/doc-ownership.yml
// Writes:
//   <out-dir>/affected-docs.txt          newline-separated doc filenames
//   <out-dir>/match-summary.md           human-readable summary
//
// Default <out-dir> is <tmpdir>; the filenames include a short hash of CWD
// so parallel runs (monorepo, worktrees, CI matrix) don't clobber each other.
// Pass --out-dir to pin the location (useful in CI when downstream steps
// need to read the files at a known path).
//
// Zero runtime deps — uses only Node built-ins. The doc-ownership.yml parser
// handles the specific subset of YAML this file uses (docs: / paths: /
// concepts: lists). It is NOT a general YAML parser.

import { execSync } from 'node:child_process';
import { promises as fs } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import os from 'node:os';

const CWD = process.cwd();
const OWNERSHIP_PATH = path.join(CWD, 'docs', '_meta', 'doc-ownership.yml');

function die(msg, code = 1) {
  console.error(msg);
  process.exit(code);
}

// ---- YAML subset parser --------------------------------------------------
//
// Handles exactly the shape doc-ownership.yml uses:
//
//   docs:
//     <slug>:
//       paths:
//         - 'glob'
//         - "glob"
//         - glob
//       concepts:
//         - 'string'
//       db_objects:                # opt-in, used to route SQL migration diffs
//         - table: places
//         - function: find_nearest_place
//         - rls: "all tables"
//
// Indentation is detected per-file (2-space, 4-space, etc.) — not hardcoded.
// Tabs are normalized to 2 spaces. UTF-8 BOM is stripped.
//
// Returns: {
//   docs: { <slug>: {
//     paths: string[],
//     concepts: string[],
//     db_objects: Array<{table?:string, function?:string, rls?:string}>,
//   } },
//   warnings: string[]
// }
// `warnings` is non-empty if the parser saw tokens it couldn't make sense of.

function parseOwnershipYaml(text) {
  if (text.charCodeAt(0) === 0xFEFF) text = text.slice(1);
  text = text.replace(/\t/g, '  ');
  const lines = text.split(/\r?\n/);
  const warnings = [];

  // Tokenize: [{ lineNo, indent, content }] for non-comment, non-blank lines.
  const tokens = [];
  for (let i = 0; i < lines.length; i++) {
    const stripped = lines[i].replace(/#.*$/, '').replace(/\s+$/, '');
    if (!stripped.trim()) continue;
    const indent = stripped.length - stripped.trimStart().length;
    tokens.push({ lineNo: i + 1, indent, content: stripped.trimStart() });
  }

  if (tokens.length === 0) return { docs: {}, warnings };

  // Find the `docs:` block at indent 0.
  const docsTok = tokens.find(t => t.indent === 0 && t.content === 'docs:');
  if (!docsTok) {
    warnings.push(`No top-level \`docs:\` key found.`);
    return { docs: {}, warnings };
  }

  // Tokens inside the docs: block (until the next indent-0 line, if any).
  const docsStart = tokens.indexOf(docsTok) + 1;
  let docsEnd = tokens.length;
  for (let i = docsStart; i < tokens.length; i++) {
    if (tokens[i].indent === 0) { docsEnd = i; break; }
  }
  const inner = tokens.slice(docsStart, docsEnd);
  if (inner.length === 0) return { docs: {}, warnings };

  // Detect the indent unit (smallest non-zero indent inside the block).
  // For a well-formed file, the first child of `docs:` is a doc slug at
  // exactly one indent unit.
  const unit = inner[0].indent;
  if (unit <= 0) {
    warnings.push(`Lines inside \`docs:\` block have indent ≤0 — file appears malformed.`);
    return { docs: {}, warnings };
  }

  const result = {};
  let currentSlug = null;
  let currentList = null; // 'paths' | 'concepts'

  for (const tok of inner) {
    const { lineNo, indent, content } = tok;
    if (indent % unit !== 0) {
      warnings.push(`Line ${lineNo}: indent ${indent} is not a multiple of detected unit ${unit}. Skipped.`);
      continue;
    }
    const level = indent / unit; // 1=slug, 2=paths/concepts key, 3=list item

    if (level === 1 && content.endsWith(':')) {
      currentSlug = content.slice(0, -1).trim();
      result[currentSlug] = { paths: [], concepts: [], db_objects: [] };
      currentList = null;
    } else if (level === 2 && content.endsWith(':') && currentSlug) {
      const key = content.slice(0, -1).trim();
      currentList = (key === 'paths' || key === 'concepts' || key === 'db_objects') ? key : null;
      if (currentList === null) {
        warnings.push(`Line ${lineNo}: unknown key "${key}" under doc "${currentSlug}" (expected "paths", "concepts", or "db_objects").`);
      }
    } else if (level === 3 && currentSlug && currentList && content.startsWith('-')) {
      const itemText = content.slice(1).trim();
      if (currentList === 'db_objects') {
        // Each item is `<key>: <value>`. Allowed keys: table, function, rls.
        const m = itemText.match(/^(table|function|rls)\s*:\s*(.+)$/);
        if (!m) {
          warnings.push(`Line ${lineNo}: db_objects item "${itemText}" — expected \`table: <name>\`, \`function: <name>\`, or \`rls: <value>\`.`);
          continue;
        }
        let value = m[2].trim();
        if (
          (value.startsWith("'") && value.endsWith("'")) ||
          (value.startsWith('"') && value.endsWith('"'))
        ) {
          value = value.slice(1, -1);
        }
        result[currentSlug].db_objects.push({ [m[1]]: value });
      } else {
        let value = itemText;
        if (
          (value.startsWith("'") && value.endsWith("'")) ||
          (value.startsWith('"') && value.endsWith('"'))
        ) {
          value = value.slice(1, -1);
        }
        if (value) result[currentSlug][currentList].push(value);
      }
    } else {
      warnings.push(`Line ${lineNo}: could not interpret "${content}" at level ${level}. Skipped.`);
    }
  }

  return { docs: result, warnings };
}

// ---- SQL migration parsing ----------------------------------------------
//
// Extract db objects (tables, functions, policy-changed flag) referenced by
// CREATE/ALTER/DROP statements in a Postgres migration. Used to route SQL
// diffs to the docs that own those db objects.
//
// Comments (`-- line` and `/* block */`) are stripped before scanning so DDL
// in commentary is ignored. DDL inside string literals is NOT stripped — that
// would require a real parser and is a rarer case.
//
// Optional schema prefix `<schema>.` before identifiers is accepted and
// discarded; we match the bare object name.

function extractDbObjectsFromMigration(sqlContent) {
  const cleaned = sqlContent
    .replace(/--[^\n]*/g, '')
    .replace(/\/\*[\s\S]*?\*\//g, '');

  const tables = new Set();
  const functions = new Set();
  let hasPolicyChange = false;

  const createTable = /\bCREATE\s+(?:OR\s+REPLACE\s+)?TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:["']?\w+["']?\.)?["']?(\w+)["']?/gi;
  const alterTable = /\bALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:["']?\w+["']?\.)?["']?(\w+)["']?/gi;
  const dropTable = /\bDROP\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:["']?\w+["']?\.)?["']?(\w+)["']?/gi;
  const createFunction = /\bCREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?:["']?\w+["']?\.)?["']?(\w+)["']?/gi;
  const createPolicy = /\bCREATE\s+POLICY\b/gi;
  const alterPolicy = /\bALTER\s+POLICY\b/gi;
  const dropPolicy = /\bDROP\s+POLICY\b/gi;

  for (const pattern of [createTable, alterTable, dropTable]) {
    let m;
    while ((m = pattern.exec(cleaned)) !== null) tables.add(m[1]);
  }
  let fm;
  while ((fm = createFunction.exec(cleaned)) !== null) functions.add(fm[1]);
  if (createPolicy.test(cleaned) || alterPolicy.test(cleaned) || dropPolicy.test(cleaned)) {
    hasPolicyChange = true;
  }

  return { tables: [...tables], functions: [...functions], hasPolicyChange };
}

// ---- Routing helpers (composable) ---------------------------------------
//
// These are extracted from main() so other tools (e.g. check-docs-freshness)
// can compose the same routing logic without duplicating it.

function compileOwnership(ownership) {
  return Object.entries(ownership).map(([doc, spec]) => ({
    doc,
    pathRegexes: spec.paths.map(p => ({ glob: p, re: globToRegExp(p) })),
    concepts: spec.concepts || [],
    dbObjects: spec.db_objects || [],
  }));
}

function matchPathToDocs(file, compiled) {
  const out = [];
  for (const { doc, pathRegexes } of compiled) {
    for (const { re } of pathRegexes) {
      if (re.test(file)) {
        out.push(doc);
        break;
      }
    }
  }
  return out;
}

// Returns Map<doc, hits[]> where each hit string is "table:foo" /
// "function:bar" / "rls:all tables" — used both for routing and for the
// human-readable match summary.
function matchSqlToDocs(sqlText, compiled) {
  const { tables, functions, hasPolicyChange } = extractDbObjectsFromMigration(sqlText);
  const hitsByDoc = new Map();
  for (const { doc, dbObjects } of compiled) {
    if (!dbObjects.length) continue;
    const hits = [];
    for (const obj of dbObjects) {
      if (obj.table && tables.includes(obj.table)) hits.push(`table:${obj.table}`);
      if (obj.function && functions.includes(obj.function)) hits.push(`function:${obj.function}`);
      if (obj.rls === 'all tables' && hasPolicyChange) hits.push('rls:all tables');
    }
    if (hits.length) hitsByDoc.set(doc, hits);
  }
  return hitsByDoc;
}

// Read and parse doc-ownership.yml from <cwd>/docs/_meta/doc-ownership.yml
// (or a custom path). Returns { ownership, warnings }. Throws if the file
// is missing or contains zero valid doc entries.
async function loadOwnership(ownershipPath = OWNERSHIP_PATH) {
  let text;
  try {
    text = await fs.readFile(ownershipPath, 'utf8');
  } catch (err) {
    throw new Error(`Could not read ${ownershipPath}: ${err.message}`);
  }
  const { docs: ownership, warnings } = parseOwnershipYaml(text);
  if (Object.keys(ownership).length === 0) {
    const hasContent = text.replace(/#.*$/gm, '').replace(/^\s*$/gm, '').trim().length > 0;
    if (hasContent) {
      throw new Error(`${ownershipPath} has content but the parser found zero docs. Most common cause: inconsistent indentation.`);
    }
    throw new Error(`${ownershipPath} has no docs entries.`);
  }
  return { ownership, warnings };
}

// ---- Glob → RegExp -------------------------------------------------------
//
// Supports: **, *, ?, literal strings, file extensions. Does not support
// brace expansion ({a,b}) or character classes.

function globToRegExp(glob) {
  // Normalize Windows-style backslashes to forward slashes.
  const normalized = glob.replace(/\\/g, '/');
  let re = '';
  for (let i = 0; i < normalized.length; i++) {
    const c = normalized[i];
    const next = normalized[i + 1];
    if (c === '*' && next === '*') {
      re += '.*';
      i++;
      // Skip the slash after ** if present (so 'src/**/foo' matches 'src/foo' too).
      if (normalized[i + 1] === '/') i++;
    } else if (c === '*') {
      re += '[^/]*';
    } else if (c === '?') {
      re += '.';
    } else if ('.+^$|()[]{}\\'.includes(c)) {
      re += '\\' + c;
    } else {
      re += c;
    }
  }
  return new RegExp('^' + re + '$');
}

// ---- Git diff ------------------------------------------------------------

function getChangedFiles(baseRef) {
  // Try 3-dot diff first (covers the "common ancestor" case for feature branches).
  // Fall back to 2-dot if 3-dot fails (e.g. baseRef is a single commit like HEAD~1).
  const tryCommands = [
    ['diff', '--name-only', `${baseRef}...HEAD`],
    ['diff', '--name-only', baseRef],
  ];
  for (const args of tryCommands) {
    try {
      const out = execSync(`git ${args.join(' ')}`, { cwd: CWD, stdio: ['ignore', 'pipe', 'pipe'] }).toString();
      return out.split('\n').map(s => s.trim()).filter(Boolean);
    } catch {
      // try next
    }
  }
  die(`Could not run \`git diff\` against base ref "${baseRef}". Is this a git repo, and is the ref valid?`);
}

// ---- Concept matching (optional) -----------------------------------------

async function fileContains(filePath, needles) {
  if (!needles.length) return [];
  const abs = path.join(CWD, filePath);
  try {
    const content = await fs.readFile(abs, 'utf8');
    return needles.filter(n => content.includes(n));
  } catch {
    return [];
  }
}

// ---- Main ----------------------------------------------------------------

function parseArgs(argv) {
  const args = { baseRef: null, outDir: null };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--out-dir') args.outDir = argv[++i];
    else if (!a.startsWith('--') && args.baseRef === null) args.baseRef = a;
    else die(`Unknown argument: ${a}\nUsage: node scripts/match-docs.mjs <base-ref> [--out-dir <path>]`);
  }
  return args;
}

function resolveOutPaths(outDir) {
  if (outDir) {
    const abs = path.resolve(outDir);
    return {
      affected: path.join(abs, 'affected-docs.txt'),
      summary: path.join(abs, 'match-summary.md'),
    };
  }
  // Default: tmpdir + short CWD hash so parallel runs don't clobber.
  const cwdHash = createHash('sha1').update(CWD).digest('hex').slice(0, 8);
  return {
    affected: path.join(os.tmpdir(), `affected-docs-${cwdHash}.txt`),
    summary: path.join(os.tmpdir(), `match-summary-${cwdHash}.md`),
  };
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const baseRef = args.baseRef;
  if (!baseRef) {
    die(`Usage: node scripts/match-docs.mjs <base-ref> [--out-dir <path>]\nExample: node scripts/match-docs.mjs main`);
  }
  const { affected: AFFECTED_PATH, summary: SUMMARY_PATH } = resolveOutPaths(args.outDir);
  if (args.outDir) await fs.mkdir(path.dirname(AFFECTED_PATH), { recursive: true });

  let ownershipText;
  try {
    ownershipText = await fs.readFile(OWNERSHIP_PATH, 'utf8');
  } catch (err) {
    die(`Could not read ${OWNERSHIP_PATH}: ${err.message}\n\nDid you run \`npx wts-ai-docs init\` to bootstrap the vault?`);
  }

  const { docs: ownership, warnings } = parseOwnershipYaml(ownershipText);
  if (warnings.length) {
    console.error(`!! doc-ownership.yml parse warnings:`);
    for (const w of warnings) console.error(`   ${w}`);
    console.error('');
  }
  if (Object.keys(ownership).length === 0) {
    const hasContent = ownershipText.replace(/#.*$/gm, '').replace(/^\s*$/gm, '').trim().length > 0;
    if (hasContent) {
      die(`${OWNERSHIP_PATH} has content but the parser found zero docs.\n\nMost common cause: inconsistent indentation. The parser auto-detects the indent unit (2-space, 4-space, ...) but it must be consistent within the file. See parse warnings above and the example in templates/doc-ownership.yml.\n\nFailing closed rather than producing wrong routing output.`);
    }
    die(`${OWNERSHIP_PATH} has no docs entries. Add at least one doc with paths: before running this.`);
  }

  // Pre-compile regexes.
  const compiled = compileOwnership(ownership);

  // SQL routing is opt-in: only active when at least one doc declares
  // `db_objects:`. Avoids the cost of reading every .sql diff on projects
  // that don't care.
  const sqlRoutingEnabled = compiled.some(c => c.dbObjects.length > 0);

  const changedFiles = getChangedFiles(baseRef);
  if (changedFiles.length === 0) {
    console.log(`No files changed against ${baseRef}.`);
    await fs.writeFile(AFFECTED_PATH, '', 'utf8');
    await fs.writeFile(SUMMARY_PATH, `# Doc Routing Summary\n\nNo files changed against \`${baseRef}\`.\n`, 'utf8');
    return;
  }

  const matchesByDoc = {};         // doc -> { paths, concepts, dbObjects: Map<file, string[]> }
  const fileToMatches = {};        // file -> Set<doc>
  for (const { doc } of compiled) {
    matchesByDoc[doc] = { paths: new Set(), concepts: new Map(), dbObjects: new Map() };
  }

  // Path matches.
  for (const file of changedFiles) {
    for (const doc of matchPathToDocs(file, compiled)) {
      matchesByDoc[doc].paths.add(file);
      if (!fileToMatches[file]) fileToMatches[file] = new Set();
      fileToMatches[file].add(doc);
    }
  }

  // Concept matches (only on files that exist on disk after the diff — i.e. not deleted).
  for (const file of changedFiles) {
    for (const { doc, concepts } of compiled) {
      if (!concepts.length) continue;
      const hits = await fileContains(file, concepts);
      if (hits.length) {
        matchesByDoc[doc].concepts.set(file, hits);
        if (!fileToMatches[file]) fileToMatches[file] = new Set();
        fileToMatches[file].add(doc);
      }
    }
  }

  // SQL migration routing — parse changed *.sql files and route them to docs
  // that claim the db objects (tables, functions, policies) the migration
  // touches. Opt-in: only fires when at least one doc declares db_objects:.
  if (sqlRoutingEnabled) {
    for (const file of changedFiles) {
      if (!file.endsWith('.sql')) continue;
      let sql;
      try {
        sql = await fs.readFile(path.join(CWD, file), 'utf8');
      } catch {
        continue; // deleted in diff, or unreadable — skip silently
      }
      for (const [doc, hits] of matchSqlToDocs(sql, compiled)) {
        matchesByDoc[doc].dbObjects.set(file, hits);
        if (!fileToMatches[file]) fileToMatches[file] = new Set();
        fileToMatches[file].add(doc);
      }
    }
  }

  const unmatched = changedFiles.filter(f => !fileToMatches[f]);
  const affectedDocs = Object.entries(matchesByDoc)
    .filter(([, m]) => m.paths.size > 0 || m.concepts.size > 0 || m.dbObjects.size > 0)
    .map(([doc]) => doc)
    .sort();

  // affected-docs.txt — one filename per line (e.g. "auth.md")
  await fs.writeFile(
    AFFECTED_PATH,
    affectedDocs.map(d => `${d}.md`).join('\n') + (affectedDocs.length ? '\n' : ''),
    'utf8',
  );

  // match-summary.md — human-readable.
  const lines = [];
  lines.push(`# Doc Routing Summary`);
  lines.push('');
  lines.push(`Diff base: \`${baseRef}\``);
  lines.push(`Changed files: ${changedFiles.length}`);
  lines.push(`Affected docs: ${affectedDocs.length}`);
  lines.push(`Unmatched files: ${unmatched.length}`);
  if (warnings.length) {
    lines.push(`Parse warnings: ${warnings.length}`);
  }
  lines.push('');

  if (warnings.length) {
    lines.push(`## ⚠ doc-ownership.yml parse warnings`);
    lines.push('');
    lines.push(`> The parser saw lines it couldn't interpret. Fix these — partial parsing means some docs may be silently skipped.`);
    lines.push('');
    for (const w of warnings) lines.push(`- ${w}`);
    lines.push('');
  }

  if (affectedDocs.length) {
    lines.push(`## Affected docs`);
    lines.push('');
    for (const doc of affectedDocs) {
      const m = matchesByDoc[doc];
      lines.push(`### \`docs/${doc}.md\``);
      if (m.paths.size) {
        lines.push('');
        lines.push(`**Path matches** (${m.paths.size}):`);
        for (const f of [...m.paths].sort()) lines.push(`- \`${f}\``);
      }
      if (m.concepts.size) {
        lines.push('');
        lines.push(`**Concept matches** (${m.concepts.size}):`);
        for (const [f, hits] of [...m.concepts.entries()].sort()) {
          lines.push(`- \`${f}\` — ${hits.map(h => '`' + h + '`').join(', ')}`);
        }
      }
      if (m.dbObjects.size) {
        lines.push('');
        lines.push(`**DB object matches** (${m.dbObjects.size}):`);
        for (const [f, hits] of [...m.dbObjects.entries()].sort()) {
          lines.push(`- \`${f}\` — ${hits.map(h => '`' + h + '`').join(', ')}`);
        }
      }
      lines.push('');
    }
  }

  if (unmatched.length) {
    lines.push(`## Unmatched files`);
    lines.push('');
    lines.push(`> These files changed but no doc claims them. Either add them to a doc's \`paths:\` in \`docs/_meta/doc-ownership.yml\` and re-run, or confirm they legitimately have no doc owner.`);
    lines.push('');
    for (const f of unmatched.sort()) lines.push(`- \`${f}\``);
    lines.push('');
  }

  await fs.writeFile(SUMMARY_PATH, lines.join('\n'), 'utf8');

  // Console report.
  console.log(`Diff base:        ${baseRef}`);
  console.log(`Changed files:    ${changedFiles.length}`);
  console.log(`Affected docs:    ${affectedDocs.length}${affectedDocs.length ? ' (' + affectedDocs.join(', ') + ')' : ''}`);
  console.log(`Unmatched files:  ${unmatched.length}`);
  console.log('');
  console.log(`Wrote: ${AFFECTED_PATH}`);
  console.log(`Wrote: ${SUMMARY_PATH}`);
  if (unmatched.length) {
    console.log('');
    console.log(`!! ${unmatched.length} unmatched file(s). See match-summary.md and resolve before merging.`);
  }

  // Exit non-zero on parse warnings so CI catches partial parses.
  if (warnings.length) process.exit(2);
}

const invokedDirectly = (() => {
  try { return fileURLToPath(import.meta.url) === path.resolve(process.argv[1] || ''); }
  catch { return false; }
})();
if (invokedDirectly) {
  main().catch(err => {
    console.error(`match-docs failed: ${err.message}`);
    if (process.env.DEBUG) console.error(err.stack);
    process.exit(1);
  });
}

// Exports — used by tests and by other tools that compose this routing logic
// (e.g. scripts/check-docs-freshness.mjs).
export {
  parseOwnershipYaml,
  globToRegExp,
  extractDbObjectsFromMigration,
  compileOwnership,
  matchPathToDocs,
  matchSqlToDocs,
  loadOwnership,
};
