#!/usr/bin/env node
// The product version has one source of truth: VersionPrefix in backend/Directory.Build.props.
// frontend/package.json and the Helm chart (version and appVersion in deploy/helm/vinette/Chart.yaml) must carry the same version (AGENTS.md section 6).
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');

const props = readFileSync(join(root, 'backend', 'Directory.Build.props'), 'utf8');
const backend = props.match(/<VersionPrefix>\s*([^<\s]+)\s*<\/VersionPrefix>/)?.[1];
const frontend = JSON.parse(readFileSync(join(root, 'frontend', 'package.json'), 'utf8')).version;
const chart = readFileSync(join(root, 'deploy', 'helm', 'vinette', 'Chart.yaml'), 'utf8');
const chartField = (name) => chart.match(new RegExp(String.raw`^${name}:\s*"?([^"\s#]+)"?`, 'm'))?.[1];
const chartVersion = chartField('version');
const chartAppVersion = chartField('appVersion');

if (!backend || !frontend) {
  console.error(`Could not read versions (backend: ${backend ?? 'missing'}, frontend: ${frontend ?? 'missing'}).`);
  process.exit(2);
}

if (!chartVersion || !chartAppVersion) {
  console.error('Could not read version and appVersion from deploy/helm/vinette/Chart.yaml.');
  process.exit(2);
}

if (chartVersion !== backend || chartAppVersion !== backend) {
  console.error(
    `Version mismatch: backend/Directory.Build.props has ${backend}, deploy/helm/vinette/Chart.yaml has version ${chartVersion} and appVersion ${chartAppVersion}. ` +
      'Set both to the same version as the product.',
  );
  process.exit(1);
}

if (backend !== frontend) {
  console.error(
    `Version mismatch: backend/Directory.Build.props has ${backend}, frontend/package.json has ${frontend}. ` +
      'Set both to the same version.',
  );
  process.exit(1);
}

console.log(`Version check passed (${backend}).`);
