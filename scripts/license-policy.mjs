// License policy for dependencies. Vinette is Apache-2.0 (see docs/adr/0003-license.md).
// Pure functions, no I/O: tested in license-policy.test.mjs and used by check-licenses.mjs.

/** Permissive licenses: always fine. Compared case-insensitively. */
export const ALLOWED = [
  'MIT',
  'MIT-0',
  'Apache-2.0',
  'BSD-2-Clause',
  'BSD-3-Clause',
  '0BSD',
  'ISC',
  'CC0-1.0',
  'Unlicense',
  'Zlib',
  'BlueOak-1.0.0',
  'Python-2.0',
];

/**
 * Weak-copyleft / attribution licenses that are fine only for tools that are NOT distributed with the product
 * (build, test and lint tooling). Anything shipped needs an explicit exception.
 */
export const DEV_ONLY_ALLOWED = ['MPL-2.0', 'CC-BY-4.0'];

const allowedSet = new Set(ALLOWED.map((id) => id.toLowerCase()));
const devOnlySet = new Set(DEV_ONLY_ALLOWED.map((id) => id.toLowerCase()));

/** Tokenizes an SPDX license expression: ids, AND, OR, WITH, parentheses. */
export function tokenize(expression) {
  return expression.match(/\(|\)|[^\s()]+/g) ?? [];
}

/**
 * Evaluates an SPDX expression: `A OR B` passes when either side passes, `A AND B` when both do.
 * `isOk(id)` decides a single license id. Unknown or malformed input never passes.
 */
export function evaluateExpression(expression, isOk) {
  const tokens = tokenize(expression ?? '');
  let position = 0;

  const peek = () => tokens[position];
  const next = () => tokens[position++];

  function parseOr() {
    let result = parseAnd();
    while (peek()?.toUpperCase() === 'OR') {
      next();
      const right = parseAnd();
      result = result || right;
    }
    return result;
  }

  function parseAnd() {
    let result = parseAtom();
    while (peek()?.toUpperCase() === 'AND') {
      next();
      const right = parseAtom();
      result = result && right;
    }
    return result;
  }

  function parseAtom() {
    const token = next();
    if (token === undefined) return false;
    if (token === '(') {
      const value = parseOr();
      if (next() !== ')') return false;
      return value;
    }
    if (token === ')' || ['AND', 'OR', 'WITH'].includes(token.toUpperCase())) return false;
    if (peek()?.toUpperCase() === 'WITH') {
      next();
      next();
      // "<license> WITH <exception>": judge the base license only if the whole expression is not a copyleft one.
      return isOk(token.replace(/\+$/, ''));
    }
    return isOk(token.replace(/\+$/, ''));
  }

  const value = parseOr();
  return position === tokens.length && value;
}

/**
 * @param {string} expression SPDX expression or a free-form license string
 * @param {{ shipped: boolean }} options shipped: part of what is distributed to users
 * @returns {boolean}
 */
export function isLicenseAcceptable(expression, { shipped }) {
  return evaluateExpression(expression, (id) => {
    const key = id.toLowerCase();
    return allowedSet.has(key) || (!shipped && devOnlySet.has(key));
  });
}
