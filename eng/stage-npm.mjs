// Lays out the npm packages for publishing in artifacts/npm/, from what is already built:
//
//   cd bindings/node && npm ci && npm run build
//   cd bindings/wasm && npm ci && npm run build          (after: dotnet publish src/CoreIdml.Wasm -c Release)
//   dotnet publish src/CoreIdml.Cli -c Release -r <rid>  (each platform there is; the release uses its cli-<rid> builds)
//   node eng/stage-npm.mjs [--version 1.2.3] [--only node,wasm,cli]
//
//   artifacts/npm/node/                      @coreidml/node: dist/, README.md, LICENSE
//   artifacts/npm/node-<platform>-<arch>/    one package per native library found in artifacts/native/<rid>/
//   artifacts/npm/wasm/                      @coreidml/wasm: dist/ with the WebAssembly build inside
//   artifacts/npm/cli/                       @coreidml/cli: the launcher (bin/coreidml.js), README.md, LICENSE
//   artifacts/npm/cli-<platform>-<arch>/     one package per command line found in artifacts/cli/<rid>/: the binary
//                                            and the PDFium library next to it
//
// @coreidml/node and @coreidml/cli list the platform packages that were staged as optional dependencies, at exactly
// this version, so npm installs the one that matches the machine. Publish the platform packages first, then node,
// wasm and cli (npm publish <folder>; eng/publish-npm.sh does it for the packed tarballs). --only stages some of the
// packages, for example just the command line on a machine that has built nothing else.
// The package.json files in the repository stay "private"; only the staged copies can be published.
import { chmodSync, cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

const root = fileURLToPath(new URL('../', import.meta.url));
const out = join(root, 'artifacts', 'npm');

// Node's process.platform and process.arch, the .NET runtime identifier of the build, the native library file and
// the command line's executable.
const platforms = [
  { name: 'win32-x64', os: 'win32', cpu: 'x64', rid: 'win-x64', library: 'coreidml.dll', executable: 'coreidml.exe' },
  { name: 'win32-arm64', os: 'win32', cpu: 'arm64', rid: 'win-arm64', library: 'coreidml.dll', executable: 'coreidml.exe' },
  { name: 'linux-x64', os: 'linux', cpu: 'x64', rid: 'linux-x64', library: 'libcoreidml.so', executable: 'coreidml', libc: 'glibc' },
  { name: 'linux-arm64', os: 'linux', cpu: 'arm64', rid: 'linux-arm64', library: 'libcoreidml.so', executable: 'coreidml', libc: 'glibc' },
  { name: 'darwin-x64', os: 'darwin', cpu: 'x64', rid: 'osx-x64', library: 'libcoreidml.dylib', executable: 'coreidml' },
  { name: 'darwin-arm64', os: 'darwin', cpu: 'arm64', rid: 'osx-arm64', library: 'libcoreidml.dylib', executable: 'coreidml' },
];

const { values } = parseArgs({ options: { version: { type: 'string' }, only: { type: 'string' } } });
const selected = new Set((values.only ?? 'node,wasm,cli').split(',').map((name) => name.trim()));
for (const name of selected) {
  if (!['node', 'wasm', 'cli'].includes(name)) {
    console.error(`--only takes node, wasm and cli; got ${name}.`);
    process.exit(2);
  }
}
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
// Every manifest carries the same version (eng/set-version.mjs); without --version, it is theirs.
const version = values.version ?? nodeManifest.version;

if (selected.has('node')) {
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
}

// ---- @coreidml/wasm ---------------------------------------------------------------------------------------------

if (selected.has('wasm')) {
  const wasmFolder = join(root, 'bindings', 'wasm');
  const wasmManifest = readJson(join(wasmFolder, 'package.json'));
  requireBuilt(wasmFolder, join('dist', '_framework', 'dotnet.js'), 'Run `dotnet publish src/CoreIdml.Wasm -c Release`, then `npm run build` in bindings/wasm.');

  const wasmTarget = folder('wasm');
  cpSync(join(wasmFolder, 'dist'), join(wasmTarget, 'dist'), { recursive: true });
  cpSync(join(wasmFolder, 'README.md'), join(wasmTarget, 'README.md'));
  cpSync(join(wasmFolder, 'LICENSE'), join(wasmTarget, 'LICENSE'));
  writeJson(join(wasmTarget, 'package.json'), publishable(wasmManifest, version));
  console.log(`staged   ${wasmManifest.name}@${version}`);
}

// ---- @coreidml/cli and its platform packages ------------------------------------------------------------------
//
// The command line is one native binary per platform (Native AOT), with the PDFium library it loads from its own
// folder for page previews. Each platform package carries both; @coreidml/cli carries only a launcher that finds
// the installed platform package and runs its binary.

if (selected.has('cli')) {
  const cliFolder = join(root, 'bindings', 'cli');
  const cliManifest = readJson(join(cliFolder, 'package.json'));
  const cliDependencies = {};
  for (const platform of platforms) {
    const build = join(root, 'artifacts', 'cli', platform.rid);
    if (!existsSync(join(build, platform.executable))) {
      console.log(`skipped  ${cliManifest.name}-${platform.name}: no ${platform.executable} in artifacts/cli/${platform.rid}`);
      continue;
    }
    // pdfium.dll, libpdfium.so or libpdfium.dylib: whatever the build put next to the binary.
    const pdfium = readdirSync(build).filter((file) => /pdfium/i.test(file));
    if (pdfium.length === 0) {
      console.error(`${build} has no PDFium library next to ${platform.executable}; page previews would not work.`);
      process.exit(1);
    }
    const name = `${cliManifest.name}-${platform.name}`;
    const target = folder(`cli-${platform.name}`);
    for (const file of [platform.executable, ...pdfium]) {
      cpSync(join(build, file), join(target, file));
    }
    // npm packs the file mode it finds, and a binary that came from a download, or is staged on Windows, may not
    // have the executable bit. The tarball must have it: npm keeps the mode on install.
    if (platform.os !== 'win32') {
      chmodSync(join(target, platform.executable), 0o755);
    }
    cpSync(join(cliFolder, 'LICENSE'), join(target, 'LICENSE'));
    cpSync(join(root, 'THIRD-PARTY-NOTICES.md'), join(target, 'THIRD-PARTY-NOTICES.md'));
    writeFileSync(
      join(target, 'README.md'),
      `# ${name}\n\nThe coreidml command line and MCP server (\`${platform.executable}\`, with PDFium for page previews) ` +
        `for ${platform.os} ${platform.cpu}.\n\n` +
        `This package is installed automatically with [\`${cliManifest.name}\`](https://www.npmjs.com/package/${cliManifest.name}), ` +
        'whose `coreidml` command runs it.\n',
    );
    writeJson(join(target, 'package.json'), {
      name,
      version,
      description: `The coreidml command line (${platform.executable}) for ${platform.os} ${platform.cpu}; installed with ${cliManifest.name}.`,
      license: cliManifest.license,
      repository: cliManifest.repository,
      homepage: cliManifest.homepage,
      os: [platform.os],
      cpu: [platform.cpu],
      ...(platform.libc ? { libc: [platform.libc] } : {}),
      files: [platform.executable, ...pdfium, 'THIRD-PARTY-NOTICES.md'],
    });
    cliDependencies[name] = version;
    console.log(`staged   ${name}@${version}`);
  }

  const cliTarget = folder('cli');
  cpSync(join(cliFolder, 'bin'), join(cliTarget, 'bin'), { recursive: true });
  cpSync(join(cliFolder, 'README.md'), join(cliTarget, 'README.md'));
  cpSync(join(cliFolder, 'LICENSE'), join(cliTarget, 'LICENSE'));
  writeJson(join(cliTarget, 'package.json'), { ...publishable(cliManifest, version), optionalDependencies: cliDependencies });
  console.log(`staged   ${cliManifest.name}@${version} (platform packages: ${Object.keys(cliDependencies).length})`);
  if (Object.keys(cliDependencies).length === 0) {
    console.warn('warning: no command line build was found, so the staged package cannot work on its own. Publish one first.');
  }
}

console.log(`\nPackages are in ${out}. Publish the platform packages first, then node, wasm and cli.`);
