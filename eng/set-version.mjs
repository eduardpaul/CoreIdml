// One version for every package. Directory.Build.props (VersionPrefix) holds the last release; the manifests of the
// language packages and the Claude plugin repeat it because their tools read it from there.
//
//   node eng/set-version.mjs --print            the version of Directory.Build.props
//   node eng/set-version.mjs --next             the next patch version (main's dry runs build <next>-ci.<run>)
//   node eng/set-version.mjs --check            fails when a manifest disagrees with Directory.Build.props
//   node eng/set-version.mjs 1.2.0[-rc.1]       writes that version into every manifest (CI, before packaging)
//   node eng/set-version.mjs --release 1.2.0    writes it into Directory.Build.props and every manifest
//
// A release is only a tag: the builds take its version (-p:Version=..., and this script before packaging), and once
// the release is published the release workflow commits it to main with --release.
//
// Python spells pre-releases its own way (PEP 440): 1.2.0-rc.1 becomes 1.2.0rc1, and the CI builds of main,
// 1.2.0-ci.<n>, become 1.2.0.dev<n>.
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
const read = (path) => readFileSync(join(root, path), 'utf8');
const write = (path, text) => writeFileSync(join(root, path), text);

const semver = /^(\d+)\.(\d+)\.(\d+)(?:-(alpha|beta|rc|ci)\.(\d+))?$/;

/** The version as Python writes it (PEP 440). */
function pythonVersion(version) {
  const [, major, minor, patch, kind, number] = semver.exec(version);
  const base = `${major}.${minor}.${patch}`;
  switch (kind) {
    case undefined:
      return base;
    case 'alpha':
      return `${base}a${number}`;
    case 'beta':
      return `${base}b${number}`;
    case 'rc':
      return `${base}rc${number}`;
    default:
      return `${base}.dev${number}`;
  }
}

/** Every manifest: how to read its version and how to write a new one. */
const manifests = [
  { path: 'bindings/node/package.json', pattern: /^(  "version": ")([^"]+)(",)$/m, format: (v) => v },
  { path: 'bindings/wasm/package.json', pattern: /^(  "version": ")([^"]+)(",)$/m, format: (v) => v },
  { path: 'bindings/cli/package.json', pattern: /^(  "version": ")([^"]+)(",)$/m, format: (v) => v },
  { path: 'bindings/python/pyproject.toml', pattern: /^(version = ")([^"]+)(")$/m, format: pythonVersion },
  { path: 'bindings/dart/coreidml/pubspec.yaml', pattern: /^(version: )(\S+)()$/m, format: (v) => v },
  { path: 'bindings/mcpb/manifest.json', pattern: /^(  "version": ")([^"]+)(",)$/m, format: (v) => v },
  // The Claude plugin: its own version, the npm package its MCP server runs (pinned exactly: npx would otherwise
  // fetch whatever is newest), and its entry in the marketplace at the repository root.
  { path: 'plugins/coreidml/.claude-plugin/plugin.json', pattern: /^(  "version": ")([^"]+)(",)$/m, format: (v) => v },
  { path: 'plugins/coreidml/.mcp.json', pattern: /("@coreidml\/cli@)([^"]+)(")/, format: (v) => v },
  { path: '.claude-plugin/marketplace.json', pattern: /^(      "version": ")([^"]+)(",)$/m, format: (v) => v },
];

const props = read('Directory.Build.props');
const prefix = /<VersionPrefix>([^<]+)<\/VersionPrefix>/.exec(props)?.[1];
if (!prefix) {
  console.error('Directory.Build.props has no <VersionPrefix>.');
  process.exit(1);
}

/** Writes the version into every manifest. */
function writeManifests(version) {
  for (const manifest of manifests) {
    const text = read(manifest.path);
    if (!manifest.pattern.test(text)) {
      console.error(`${manifest.path}: no version field found.`);
      process.exit(1);
    }
    write(manifest.path, text.replace(manifest.pattern, (_, before, _old, after) => `${before}${manifest.format(version)}${after}`));
    console.log(`${manifest.path}: ${manifest.format(version)}`);
  }
}

const argument = process.argv[2];
if (argument === '--print') {
  console.log(prefix);
} else if (argument === '--next') {
  const [, major, minor, patch] = semver.exec(prefix) ?? [];
  if (patch === undefined) {
    console.error(`Directory.Build.props: '${prefix}' is not a version.`);
    process.exit(1);
  }
  console.log(`${major}.${minor}.${Number(patch) + 1}`);
} else if (argument === '--release') {
  const version = process.argv[3] ?? '';
  if (!/^\d+\.\d+\.\d+$/.test(version)) {
    console.error(`--release takes a release version (major.minor.patch), not '${version}'.`);
    process.exit(2);
  }
  write('Directory.Build.props', props.replace(/<VersionPrefix>[^<]+<\/VersionPrefix>/, `<VersionPrefix>${version}</VersionPrefix>`));
  console.log(`Directory.Build.props: ${version}`);
  writeManifests(version);
} else if (argument === '--check') {
  let wrong = 0;
  for (const manifest of manifests) {
    const found = manifest.pattern.exec(read(manifest.path))?.[2];
    if (found !== manifest.format(prefix)) {
      console.error(`${manifest.path}: version ${found ?? '(none)'}, expected ${manifest.format(prefix)} (Directory.Build.props)`);
      wrong++;
    }
  }
  if (wrong > 0) {
    process.exit(1);
  }
  console.log(`Every manifest is at ${prefix}.`);
} else if (argument && semver.test(argument)) {
  writeManifests(argument);
} else {
  console.error('usage: node eng/set-version.mjs --print | --next | --check | <major.minor.patch[-alpha|beta|rc|ci.N]> | --release <major.minor.patch>');
  process.exit(2);
}
