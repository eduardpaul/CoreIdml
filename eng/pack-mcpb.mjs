// Packs the command line into an MCP Bundle (.mcpb) for one platform: the one-click install of the MCP server in
// Claude Desktop (open the file, choose the documents folder, done; no JSON configuration).
//
//   node eng/pack-mcpb.mjs --rid win-x64 --cli artifacts/cli/win-x64 --tools-from <coreidml that runs here> \
//        [--version 1.2.0] [--out artifacts/release/coreidml-1.2.0-win-x64.mcpb]
//
// --cli is a folder with coreidml(.exe) and the PDFium library, as the release builds it. The manifest comes from
// bindings/mcpb/manifest.json with the platform's command and the tool list filled in; --tools-from is any coreidml
// that runs on this machine (the list is the same on every platform). The bundle is made by the official packer
// (@anthropic-ai/mcpb, pinned below), then checked: manifest inside, binary present and, outside Windows, executable.
// mcpb keeps Unix file modes only when it runs on Linux or macOS: pack macOS bundles there (the release does).
import { spawnSync } from 'node:child_process';
import { chmodSync, cpSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const MCPB = '@anthropic-ai/mcpb@2.1.2';
const root = fileURLToPath(new URL('../', import.meta.url));

const options = {};
for (let i = 2; i < process.argv.length; i += 2) {
  options[process.argv[i].replace(/^--/, '')] = process.argv[i + 1];
}
for (const required of ['rid', 'cli', 'tools-from']) {
  if (!options[required]) {
    console.error('usage: node eng/pack-mcpb.mjs --rid <rid> --cli <folder> --tools-from <coreidml> [--version <v>] [--out <file.mcpb>]');
    process.exit(2);
  }
}

const rid = options.rid;
const windows = rid.startsWith('win-');
const platform = windows ? 'win32' : rid.startsWith('osx-') ? 'darwin' : rid.startsWith('linux-') ? 'linux' : null;
if (!platform) {
  console.error(`Unknown runtime identifier ${rid}.`);
  process.exit(2);
}
const executable = windows ? 'coreidml.exe' : 'coreidml';
const cli = resolve(options.cli);
if (!existsSync(join(cli, executable))) {
  console.error(`${join(cli, executable)} does not exist.`);
  process.exit(1);
}

// The manifest for this platform.
const manifest = JSON.parse(readFileSync(join(root, 'bindings/mcpb/manifest.json'), 'utf8'));
const version = options.version ?? manifest.version;
manifest.version = version;
manifest.server.entry_point = `server/${executable}`;
manifest.server.mcp_config.command = `\${__dirname}/server/${executable}`;
manifest.compatibility = { ...manifest.compatibility, platforms: [platform] };

// The tools, as Claude Desktop lists them before installing: name and the first sentence of the description (not
// ended by "e.g."), without its parenthetical asides and, when still long, without what follows a colon.
const listed = spawnSync(resolve(options['tools-from']), ['tools'], { encoding: 'utf8' });
if (listed.status !== 0) {
  console.error(`${options['tools-from']} tools failed: ${listed.stderr}`);
  process.exit(1);
}
const summary = (text) => {
  const sentence = (/^.*?\.(?=\s+[A-Z{"']|$)/s.exec(text)?.[0] ?? text).replace(/ \([^()]*\)/g, '');
  const colon = sentence.indexOf(': ');
  return sentence.length > 140 && colon > 0 ? `${sentence.slice(0, colon)}.` : sentence;
};
manifest.tools = JSON.parse(listed.stdout).map((tool) => ({ name: tool.name, description: summary(tool.description) }));

// Stage: manifest.json and server/ (everything the CLI build has).
const stage = join(root, 'artifacts', 'mcpb', rid);
rmSync(stage, { recursive: true, force: true });
mkdirSync(join(stage, 'server'), { recursive: true });
for (const file of readdirSync(cli)) {
  cpSync(join(cli, file), join(stage, 'server', file), { recursive: true });
}
if (!windows) {
  chmodSync(join(stage, 'server', executable), 0o755);
}
writeFileSync(join(stage, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);

// Pack with the official CLI (it validates the manifest first).
const out = resolve(options.out ?? join(root, 'artifacts', 'mcpb', `coreidml-${version}-${rid}.mcpb`));
mkdirSync(dirname(out), { recursive: true });
rmSync(out, { force: true });
// npx is a .cmd script on Windows, which only a shell runs; the paths are quoted for it.
const packed = spawnSync(`npx --yes ${MCPB} pack "${stage}" "${out}"`, { stdio: 'inherit', shell: true });
if (packed.status !== 0 || !existsSync(out)) {
  console.error('mcpb pack failed.');
  process.exit(1);
}

// Check the archive: the entries we need, and the binary's Unix mode where it matters.
const entries = zipEntries(readFileSync(out));
const binary = entries.find((entry) => entry.name === `server/${executable}`);
const problems = [];
if (!entries.some((entry) => entry.name === 'manifest.json')) problems.push('manifest.json is missing');
if (!binary) problems.push(`server/${executable} is missing`);
if (binary && !windows && (binary.mode & 0o111) === 0) problems.push(`server/${executable} is not executable in the archive (mode ${binary.mode.toString(8)})`);
if (problems.length > 0) {
  console.error(`${out}: ${problems.join('; ')}.`);
  process.exit(1);
}
console.log(`${out}: ${entries.length} files, ${manifest.tools.length} tools, ${platform}.`);

/** Names and Unix modes of the entries of a ZIP file, from its central directory. */
function zipEntries(zip) {
  let end = zip.length - 22;
  while (end >= 0 && zip.readUInt32LE(end) !== 0x06054b50) end--;
  if (end < 0) throw new Error('not a ZIP file');
  const count = zip.readUInt16LE(end + 10);
  let at = zip.readUInt32LE(end + 16);
  const result = [];
  for (let i = 0; i < count; i++) {
    const nameLength = zip.readUInt16LE(at + 28);
    const extraLength = zip.readUInt16LE(at + 30);
    const commentLength = zip.readUInt16LE(at + 32);
    const external = zip.readUInt32LE(at + 38);
    result.push({ name: zip.toString('utf8', at + 46, at + 46 + nameLength), mode: external >>> 16 });
    at += 46 + nameLength + extraLength + commentLength;
  }
  return result;
}
