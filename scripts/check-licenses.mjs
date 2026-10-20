#!/usr/bin/env node
// Fails when a NuGet or npm dependency has a license that is not compatible with Apache-2.0
// (policy in license-policy.mjs, approved exceptions in license-exceptions.json).
// Needs restored packages: run `make setup` first. Node only, no dependencies.
import { execSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { isLicenseAcceptable } from './license-policy.mjs';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const exceptions = JSON.parse(readFileSync(join(root, 'scripts', 'license-exceptions.json'), 'utf8')).exceptions;

/** @type {{ ecosystem: string, name: string, version: string, license: string, shipped: boolean }[]} */
const dependencies = [];

// ---- NuGet: every package resolved for any project (transitive included), license from its .nuspec ----
function nugetLicense(nuspecPath) {
  const xml = readFileSync(nuspecPath, 'utf8');
  const expression = xml.match(/<license\s+type="expression"\s*>([^<]+)<\/license>/i);
  if (expression) return expression[1].trim();
  const file = xml.match(/<license\s+type="file"\s*>([^<]+)<\/license>/i);
  if (file) return `file:${file[1].trim()}`;
  const url = xml.match(/<licenseUrl>([^<]+)<\/licenseUrl>/i);
  return url ? `url:${url[1].trim()}` : 'UNKNOWN';
}

function collectNuget() {
  const backend = join(root, 'backend');
  const shippedPackages = new Set();
  const allPackages = new Map(); // "id/version" -> { name, version, nuspec }

  for (const group of ['src', 'tests']) {
    const groupDir = join(backend, group);
    if (!existsSync(groupDir)) continue;
    for (const project of readdirSync(groupDir, { withFileTypes: true })) {
      const assetsPath = join(groupDir, project.name, 'obj', 'project.assets.json');
      if (!project.isDirectory() || !existsSync(assetsPath)) continue;
      const assets = JSON.parse(readFileSync(assetsPath, 'utf8'));
      const folder = Object.keys(assets.packageFolders ?? {})[0] ?? join(homedir(), '.nuget', 'packages');
      for (const [key, library] of Object.entries(assets.libraries ?? {})) {
        if (library.type !== 'package') continue;
        const [name, version] = key.split('/');
        const nuspec = join(folder, library.path ?? `${name.toLowerCase()}/${version}`, `${name.toLowerCase()}.nuspec`);
        allPackages.set(key, { name, version, nuspec });
        if (group === 'src') shippedPackages.add(key);
      }
    }
  }

  if (allPackages.size === 0) {
    console.error('No NuGet packages found. Run `make setup` (dotnet restore) first.');
    process.exit(2);
  }

  for (const [key, { name, version, nuspec }] of allPackages) {
    const license = existsSync(nuspec) ? nugetLicense(nuspec) : 'UNKNOWN';
    dependencies.push({ ecosystem: 'nuget', name, version, license, shipped: shippedPackages.has(key) });
  }
}

// ---- npm: pnpm knows the license of every installed package; --prod is what ends up in the bundle ----
function pnpmLicenses(flag) {
  const output = execSync(`corepack pnpm licenses list --json ${flag}`, {
    cwd: join(root, 'frontend'),
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
    env: { ...process.env, COREPACK_ENABLE_DOWNLOAD_PROMPT: '0' },
  });
  return JSON.parse(output.slice(output.indexOf('{')));
}

function collectNpm() {
  const shippedNames = new Set();
  for (const packages of Object.values(pnpmLicenses('--prod'))) {
    for (const entry of packages) shippedNames.add(entry.name);
  }
  for (const [license, packages] of Object.entries(pnpmLicenses(''))) {
    for (const entry of packages) {
      for (const version of entry.versions ?? ['?']) {
        dependencies.push({ ecosystem: 'npm', name: entry.name, version, license, shipped: shippedNames.has(entry.name) });
      }
    }
  }
}

collectNuget();
collectNpm();

// ---- Evaluate ----
const exceptionFor = (dependency) =>
  exceptions.find((e) => e.ecosystem === dependency.ecosystem && e.name === dependency.name && e.license === dependency.license);

const violations = [];
const usedExceptions = new Set();
const counts = new Map();

for (const dependency of dependencies) {
  const key = `${dependency.ecosystem}: ${dependency.license}`;
  counts.set(key, (counts.get(key) ?? 0) + 1);
  if (isLicenseAcceptable(dependency.license, { shipped: dependency.shipped })) continue;
  const exception = exceptionFor(dependency);
  if (exception) {
    usedExceptions.add(exception);
    continue;
  }
  violations.push(dependency);
}

console.log(`Checked ${dependencies.length} dependencies (NuGet + npm).`);
for (const [key, count] of [...counts].sort()) console.log(`  ${String(count).padStart(4)}  ${key}`);

const stale = exceptions.filter((e) => !usedExceptions.has(e));
for (const e of stale) {
  console.warn(`Warning: exception for ${e.ecosystem}:${e.name} (${e.license}) is unused; remove it from scripts/license-exceptions.json.`);
}

if (violations.length > 0) {
  console.error('\nLicense check failed. These dependencies are not compatible with Apache-2.0 under the project policy:');
  for (const v of violations) {
    console.error(`  ${v.ecosystem}:${v.name}@${v.version}  license: ${v.license}  (${v.shipped ? 'shipped' : 'dev/test only'})`);
  }
  console.error(
    '\nGPL/AGPL and similar need the owner\'s approval (AGENTS.md section 13). If approved, add an entry with a reason to scripts/license-exceptions.json.',
  );
  process.exit(1);
}

console.log('License check passed.');
