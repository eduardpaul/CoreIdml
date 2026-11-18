// One version for every package. Directory.Build.props (VersionPrefix) is the source; the manifests of the language
// packages repeat it because their tools read it from there.
//
//   node eng/set-version.mjs --print            the version of Directory.Build.props
//   node eng/set-version.mjs --check            fails when a manifest disagrees with it
//   node eng/set-version.mjs 1.2.0[-rc.1]       writes that version into every manifest (CI, before packaging)
//
// The .NET projects take the version on the command line (-p:Version=...), so Directory.Build.props itself is only
// ever changed by hand, in the commit that prepares a release.
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
  { path: 'bindings/python/pyproject.toml', pattern: /^(version = ")([^"]+)(")$/m, format: pythonVersion },
  { path: 'bindings/dart/coreidml/pubspec.yaml', pattern: /^(version: )(\S+)()$/m, format: (v) => v },
  { path: 'bindings/mcpb/manifest.json', pattern: /^(  "version": ")([^"]+)(",)$/m, format: (v) => v },
];

const props = read('Directory.Build.props');
const prefix = /<VersionPrefix>([^<]+)<\/VersionPrefix>/.exec(props)?.[1];
if (!prefix) {
  console.error('Directory.Build.props has no <VersionPrefix>.');
  process.exit(1);
}

const argument = process.argv[2];
if (argument === '--print') {
  console.log(prefix);
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
  for (const manifest of manifests) {
    const text = read(manifest.path);
    if (!manifest.pattern.test(text)) {
      console.error(`${manifest.path}: no version field found.`);
      process.exit(1);
    }
    write(manifest.path, text.replace(manifest.pattern, (_, before, _old, after) => `${before}${manifest.format(argument)}${after}`));
    console.log(`${manifest.path}: ${manifest.format(argument)}`);
  }
} else {
  console.error('usage: node eng/set-version.mjs --print | --check | <major.minor.patch[-alpha|beta|rc|ci.N]>');
  process.exit(2);
}
