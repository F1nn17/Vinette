#!/usr/bin/env node
// Fails when the built front end is bigger than its budget (docs/performance.md). Run after `make build`.
import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { exceeded, measure, report } from './bundle-size.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const dist = process.argv[2] ? resolve(process.argv[2]) : join(root, 'frontend', 'dist');

if (!existsSync(join(dist, 'index.html'))) {
  console.error(`No built front end in ${dist}: run "make build" first.`);
  process.exit(2);
}

const { sizes, chunks } = measure(dist);
console.log('Front end size (gzip):');
console.log(report(sizes));
const over = exceeded(sizes);
if (over.length > 0) {
  const biggest = [...chunks].sort((a, b) => b.size - a.size).slice(0, 3).map((c) => `${c.file} ${(c.size / 1024).toFixed(1)} KB`).join(', ');
  console.error(
    `\nOver the budget: ${over.map((o) => `${o.name} ${o.size.toFixed(1)} KB > ${o.budget} KB`).join('; ')}.\n` +
      `Biggest chunks: ${biggest}.\nFind what grew (a library, an import from a barrel that pulls a heavy module into the first load) or, with a reason, raise the budget in scripts/bundle-size.mjs and docs/performance.md.`,
  );
  process.exit(1);
}
console.log('Bundle size check passed.');
