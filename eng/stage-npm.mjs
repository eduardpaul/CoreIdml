// Lays out the npm packages for publishing in artifacts/npm/, from what is already built:
//
//   cd bindings/node && npm ci && npm run build
//   cd bindings/wasm && npm ci && npm run build        (after: dotnet publish src/CoreIdml.Wasm -c Release)
//   node eng/stage-npm.mjs [--version 1.2.3]
//
//   artifacts/npm/node/                      @coreidml/node: dist/, README.md, LICENSE
//   artifacts/npm/node-<platform>-<arch>/    one package per native library found in artifacts/native/<rid>/
//   artifacts/npm/wasm/                      @coreidml/wasm: dist/ with the WebAssembly build inside
//
// @coreidml/node lists the platform packages that were staged as optional dependencies, so npm installs the one
// that matches the machine. Publish the platform packages first, then node, then wasm (npm publish <folder>).
// The package.json files in the repository stay "private"; only the staged copies can be published.
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

const root = fileURLToPath(new URL('../', import.meta.url));
const out = join(root, 'artifacts', 'npm');

// Node's process.platform and process.arch, the .NET runtime identifier of the build, and the library file.
const platforms = [
  { name: 'win32-x64', os: 'win32', cpu: 'x64', rid: 'win-x64', library: 'coreidml.dll' },
  { name: 'win32-arm64', os: 'win32', cpu: 'arm64', rid: 'win-arm64', library: 'coreidml.dll' },
  { name: 'linux-x64', os: 'linux', cpu: 'x64', rid: 'linux-x64', library: 'libcoreidml.so', libc: 'glibc' },
  { name: 'linux-arm64', os: 'linux', cpu: 'arm64', rid: 'linux-arm64', library: 'libcoreidml.so', libc: 'glibc' },
  { name: 'darwin-x64', os: 'darwin', cpu: 'x64', rid: 'osx-x64', library: 'libcoreidml.dylib' },
  { name: 'darwin-arm64', os: 'darwin', cpu: 'arm64', rid: 'osx-arm64', library: 'libcoreidml.dylib' },
];

const { values } = parseArgs({ options: { version: { type: 'string' } } });
const readJson = (path) => JSON.parse(readFileSync(path, 'utf8'));
const writeJson = (path, value) => writeFileSync(path, JSON.stringify(value, null, 2) + '\n');

/** A fresh folder under artifacts/npm. */
function folder(name) {
  const path = join(out, name);
  rmSync(path, { recursive: true, force: true });
  mkdirSync(path, { recursive: true });
  return path;
}

/** The manifest as it is published: no development-only fields, and not private. */
function publishable(manifest, version) {
  const { private: _private, scripts: _scripts, devDependencies: _devDependencies, ...rest } = manifest;
  return { ...rest, version };
}

function requireBuilt(packageFolder, file, hint) {
  if (!existsSync(join(packageFolder, file))) {
    console.error(`${join(packageFolder, file)} is missing. ${hint}`);
    process.exit(1);
  }
}

// ---- @coreidml/node and its platform packages -----------------------------------------------------------------

const nodeFolder = join(root, 'bindings', 'node');
const nodeManifest = readJson(join(nodeFolder, 'package.json'));
const version = values.version ?? nodeManifest.version;
requireBuilt(nodeFolder, join('dist', 'index.js'), 'Run `npm run build` in bindings/node.');

const optionalDependencies = {};
for (const platform of platforms) {
  const library = join(root, 'artifacts', 'native', platform.rid, platform.library);
  if (!existsSync(library)) {
    console.log(`skipped  ${nodeManifest.name}-${platform.name}: no ${platform.library} in artifacts/native/${platform.rid}`);
    continue;
  }
  const name = `${nodeManifest.name}-${platform.name}`;
  const target = folder(`node-${platform.name}`);
  cpSync(library, join(target, platform.library));
  cpSync(join(nodeFolder, 'LICENSE'), join(target, 'LICENSE'));
  writeFileSync(
    join(target, 'README.md'),
    `# ${name}\n\nThe CoreIdml native library (\`${platform.library}\`) for ${platform.os} ${platform.cpu}.\n\n` +
      `This package is installed automatically with [\`${nodeManifest.name}\`](https://www.npmjs.com/package/${nodeManifest.name}); ` +
      'there is nothing to import from it.\n',
  );
  writeJson(join(target, 'package.json'), {
    name,
    version,
    description: `The CoreIdml native library (${platform.library}) for ${platform.os} ${platform.cpu}; installed with ${nodeManifest.name}.`,
    license: nodeManifest.license,
    repository: nodeManifest.repository,
    homepage: nodeManifest.homepage,
    os: [platform.os],
    cpu: [platform.cpu],
    ...(platform.libc ? { libc: [platform.libc] } : {}),
    files: [platform.library],
  });
  optionalDependencies[name] = version;
  console.log(`staged   ${name}@${version}`);
}

const nodeTarget = folder('node');
cpSync(join(nodeFolder, 'dist'), join(nodeTarget, 'dist'), { recursive: true });
cpSync(join(nodeFolder, 'README.md'), join(nodeTarget, 'README.md'));
cpSync(join(nodeFolder, 'LICENSE'), join(nodeTarget, 'LICENSE'));
writeJson(join(nodeTarget, 'package.json'), { ...publishable(nodeManifest, version), optionalDependencies });
console.log(`staged   ${nodeManifest.name}@${version} (platform packages: ${Object.keys(optionalDependencies).length})`);
if (Object.keys(optionalDependencies).length === 0) {
  console.warn('warning: no native library was found, so the staged package cannot work on its own. Publish a native build first.');
}

// ---- @coreidml/wasm ---------------------------------------------------------------------------------------------

const wasmFolder = join(root, 'bindings', 'wasm');
const wasmManifest = readJson(join(wasmFolder, 'package.json'));
requireBuilt(wasmFolder, join('dist', '_framework', 'dotnet.js'), 'Run `dotnet publish src/CoreIdml.Wasm -c Release`, then `npm run build` in bindings/wasm.');

const wasmTarget = folder('wasm');
cpSync(join(wasmFolder, 'dist'), join(wasmTarget, 'dist'), { recursive: true });
cpSync(join(wasmFolder, 'README.md'), join(wasmTarget, 'README.md'));
cpSync(join(wasmFolder, 'LICENSE'), join(wasmTarget, 'LICENSE'));
writeJson(join(wasmTarget, 'package.json'), publishable(wasmManifest, version));
console.log(`staged   ${wasmManifest.name}@${version}`);

console.log(`\nPackages are in ${out}. Publish the platform packages first, then node, then wasm.`);
