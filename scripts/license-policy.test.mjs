import assert from 'node:assert/strict';
import { test } from 'node:test';
import { evaluateExpression, isLicenseAcceptable } from './license-policy.mjs';

const shipped = { shipped: true };
const dev = { shipped: false };

test('permissive licenses are accepted everywhere', () => {
  for (const id of ['MIT', 'Apache-2.0', 'BSD-3-Clause', 'ISC', 'MIT-0', 'BlueOak-1.0.0']) {
    assert.equal(isLicenseAcceptable(id, shipped), true, id);
    assert.equal(isLicenseAcceptable(id, dev), true, id);
  }
});

test('comparison is case-insensitive', () => {
  assert.equal(isLicenseAcceptable('mit', shipped), true);
});

test('copyleft licenses are rejected', () => {
  for (const id of ['GPL-3.0-only', 'GPL-2.0-or-later', 'AGPL-3.0', 'LGPL-2.1', 'SSPL-1.0', 'UNKNOWN', '']) {
    assert.equal(isLicenseAcceptable(id, shipped), false, id);
    assert.equal(isLicenseAcceptable(id, dev), false, id);
  }
});

test('weak copyleft and attribution licenses are accepted only for non-shipped tooling', () => {
  assert.equal(isLicenseAcceptable('MPL-2.0', dev), true);
  assert.equal(isLicenseAcceptable('MPL-2.0', shipped), false);
  assert.equal(isLicenseAcceptable('CC-BY-4.0', dev), true);
  assert.equal(isLicenseAcceptable('CC-BY-4.0', shipped), false);
});

test('OR passes when one side is acceptable, AND needs both', () => {
  assert.equal(isLicenseAcceptable('(MIT OR CC0-1.0)', shipped), true);
  assert.equal(isLicenseAcceptable('MIT OR GPL-3.0-only', shipped), true);
  assert.equal(isLicenseAcceptable('GPL-3.0-only OR AGPL-3.0', shipped), false);
  assert.equal(isLicenseAcceptable('MIT AND BSD-3-Clause', shipped), true);
  assert.equal(isLicenseAcceptable('MIT AND GPL-3.0-only', shipped), false);
});

test('precedence: AND binds tighter than OR; parentheses override', () => {
  assert.equal(isLicenseAcceptable('GPL-3.0-only AND MIT OR Apache-2.0', shipped), true);
  assert.equal(isLicenseAcceptable('GPL-3.0-only AND (MIT OR Apache-2.0)', shipped), false);
});

test('"+" suffix is judged by the base license', () => {
  assert.equal(isLicenseAcceptable('Apache-2.0+', shipped), true);
  assert.equal(isLicenseAcceptable('GPL-2.0+', shipped), false);
});

test('malformed expressions are rejected', () => {
  for (const expression of ['(MIT', 'MIT OR', 'OR MIT', 'MIT MIT', ')']) {
    assert.equal(isLicenseAcceptable(expression, shipped), false, expression);
  }
});

test('evaluateExpression delegates single ids to the callback', () => {
  const seen = [];
  evaluateExpression('A OR (B AND C)', (id) => (seen.push(id), id === 'C'));
  assert.deepEqual(seen, ['A', 'B', 'C']);
});
