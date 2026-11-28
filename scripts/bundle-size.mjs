// Size of what the browser downloads (VIN-131): measured on the built front end and compared with the budgets of docs/performance.md.
// Sizes are gzip (level 9) in bytes; budgets are in kilobytes (1 KB = 1024 bytes).
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { gzipSync } from 'node:zlib';

/** The budgets, in KB of gzip. Raise one only with a reason written in docs/performance.md. */
export const BUDGET_KB = {
  /** The JavaScript of the first load: the entry chunk named in index.html (and what it loads at once). */
  entryJs: 230,
  /** The biggest chunk that is loaded later (the editor of the compose screen). */
  lazyJs: 160,
  /** All the JavaScript of the application together. */
  totalJs: 400,
  /** All the CSS. */
  css: 12,
};

const gzipSize = (path) => gzipSync(readFileSync(path), { level: 9 }).length;

function files(dir) {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });
}

/** The entry scripts (and modulepreloaded chunks) that index.html loads at once, as paths relative to the dist directory. */
export function firstLoad(html) {
  const found = new Set();
  for (const match of html.matchAll(/<(?:script|link)\b[^>]*?(?:src|href)="\/?([^"]+\.js)"[^>]*>/g)) found.add(match[1]);
  return found;
}

/** Measures a dist directory: { entryJs, lazyJs, totalJs, css } in bytes, and the list of chunks. */
export function measure(dist) {
  const html = readFileSync(join(dist, 'index.html'), 'utf8');
  const first = firstLoad(html);
  let entryJs = 0;
  let lazyJs = 0;
  let totalJs = 0;
  let css = 0;
  const chunks = [];
  for (const path of files(dist)) {
    const rel = path.slice(dist.length + 1).replaceAll('\\', '/');
    if (rel.endsWith('.js')) {
      const size = gzipSize(path);
      totalJs += size;
      const entry = first.has(rel);
      if (entry) entryJs += size;
      else lazyJs = Math.max(lazyJs, size);
      chunks.push({ file: rel, size, entry });
    } else if (rel.endsWith('.css')) {
      css += gzipSize(path);
    }
  }
  return { sizes: { entryJs, lazyJs, totalJs, css }, chunks };
}

/** Which budgets are exceeded: [{ name, size, budget }] (sizes in KB). */
export function exceeded(sizes, budget = BUDGET_KB) {
  return Object.entries(budget)
    .map(([name, kb]) => ({ name, size: sizes[name] / 1024, budget: kb }))
    .filter((x) => x.size > x.budget);
}

export function report(sizes, budget = BUDGET_KB) {
  return Object.entries(budget)
    .map(([name, kb]) => {
      const size = sizes[name] / 1024;
      return `  ${name.padEnd(8)} ${size.toFixed(1).padStart(7)} KB gz   budget ${String(kb).padStart(4)} KB   ${size > kb ? 'OVER' : 'ok'}`;
    })
    .join('\n');
}
