import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { test } from 'node:test';
import { BUDGET_KB, exceeded, firstLoad, measure } from './bundle-size.mjs';

/** A dist directory with an entry chunk, a lazy chunk and a stylesheet; the content is random so that it does not compress away. */
function dist({ entryKb = 10, lazyKb = 20, cssKb = 2 } = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'bundle-'));
  mkdirSync(join(dir, 'assets'));
  const noise = (kb) => Buffer.from(Array.from({ length: kb * 1024 }, () => Math.floor(Math.random() * 256)));
  writeFileSync(join(dir, 'index.html'), '<html><head><script type="module" crossorigin src="/assets/index-AAA.js"></script><link rel="modulepreload" href="/assets/vendor-BBB.js"><link rel="stylesheet" href="/assets/index-CCC.css"></head></html>');
  writeFileSync(join(dir, 'assets', 'index-AAA.js'), noise(entryKb));
  writeFileSync(join(dir, 'assets', 'vendor-BBB.js'), noise(entryKb));
  writeFileSync(join(dir, 'assets', 'Compose-DDD.js'), noise(lazyKb));
  writeFileSync(join(dir, 'assets', 'index-CCC.css'), noise(cssKb));
  writeFileSync(join(dir, 'assets', 'font.woff2'), noise(50)); // fonts are not part of the budget
  return dir;
}

test('the entry scripts and preloaded chunks of index.html are the first load', () => {
  const html = '<script type="module" src="/assets/a.js"></script><link rel="modulepreload" href="/assets/b.js"><link rel="stylesheet" href="/assets/c.css">';
  assert.deepEqual([...firstLoad(html)].sort(), ['assets/a.js', 'assets/b.js']);
});

test('the sizes split into the first load, the biggest later chunk, the total and the styles; fonts are left out', () => {
  const dir = dist({ entryKb: 10, lazyKb: 20, cssKb: 2 });
  try {
    const { sizes } = measure(dir);
    const kb = (n) => n / 1024;
    assert.ok(kb(sizes.entryJs) > 19 && kb(sizes.entryJs) < 22, 'entry = two chunks of 10 KB');
    assert.ok(kb(sizes.lazyJs) > 19.5 && kb(sizes.lazyJs) < 21, 'lazy = the 20 KB chunk');
    assert.ok(kb(sizes.totalJs) > 39 && kb(sizes.totalJs) < 43);
    assert.ok(kb(sizes.css) > 1.9 && kb(sizes.css) < 2.2);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('nothing is over the budget for a small build, and what is over is named', () => {
  const small = dist();
  const big = dist({ entryKb: 140, lazyKb: 170, cssKb: 20 }); // entry 280, lazy 170, total 450, css 20
  try {
    assert.deepEqual(exceeded(measure(small).sizes), []);
    assert.deepEqual(exceeded(measure(big).sizes).map((x) => x.name).sort(), ['css', 'entryJs', 'lazyJs', 'totalJs']);
  } finally {
    rmSync(small, { recursive: true, force: true });
    rmSync(big, { recursive: true, force: true });
  }
});

test('the budgets are the ones the documentation states', () => {
  assert.deepEqual(BUDGET_KB, { entryJs: 230, lazyJs: 160, totalJs: 400, css: 12 });
});
