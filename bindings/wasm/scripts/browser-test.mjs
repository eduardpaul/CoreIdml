// Runs test/browser in a real browser: serves the compiled package and the shared fixtures over HTTP, opens the test
// page in a headless Chromium-family browser (Chrome, Edge or Chromium) and waits for the page to post its results.
// The page runs twice: as an ordinary page, and cross-origin isolated (COOP + COEP headers), where IdmlWorker can
// cancel a running call without terminating the worker.
//
//   npm run test:browser            (builds dist/ first)
//   COREIDML_BROWSER=/path/to/browser picks the browser; otherwise the usual install locations are tried.
import { spawn } from 'node:child_process';
import { createReadStream, existsSync, mkdtempSync, rmSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { extname, join, normalize, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const packageRoot = fileURLToPath(new URL('../', import.meta.url));
const fixtures = fileURLToPath(new URL('../../../tests/fixtures/', import.meta.url));
const timeoutMilliseconds = 240_000;

const contentTypes = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json',
  // Streaming compilation of WebAssembly requires exactly this type.
  '.wasm': 'application/wasm',
  '.idml': 'application/octet-stream',
  '.xml': 'application/xml',
  '.ttf': 'font/ttf',
};

// What makes a page cross-origin isolated; every response carries them, so the worker and its scripts qualify too.
const isolationHeaders = {
  'cross-origin-opener-policy': 'same-origin',
  'cross-origin-embedder-policy': 'require-corp',
  'cross-origin-resource-policy': 'same-origin',
};

function findBrowser() {
  const candidates = [
    process.env.COREIDML_BROWSER,
    process.env.CHROME_EXECUTABLE,
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge',
    '/usr/bin/google-chrome',
    '/usr/bin/google-chrome-stable',
    '/usr/bin/chromium',
    '/usr/bin/chromium-browser',
    '/usr/bin/microsoft-edge',
  ];
  return candidates.find((candidate) => candidate && existsSync(candidate));
}

/** The file a URL path maps to, kept inside the folder it is served from. */
function resolveFile(urlPath) {
  const [base, relative] = urlPath.startsWith('/fixtures/') ? [fixtures, urlPath.slice('/fixtures/'.length)] : [packageRoot, urlPath.slice(1)];
  const file = normalize(join(base, decodeURIComponent(relative)));
  return file.startsWith(normalize(base).replace(/[\\/]$/, '') + sep) && existsSync(file) && statSync(file).isFile() ? file : undefined;
}

/** Serves the page, runs it in the browser, and returns what it posted (undefined on a timeout) and what was served. */
async function runPage(browser, isolated) {
  let finish;
  const finished = new Promise((resolve) => (finish = resolve));
  const requests = [];

  const server = createServer((request, response) => {
    const path = new URL(request.url, 'http://localhost').pathname;
    if (request.method === 'POST' && path === '/results') {
      let body = '';
      request.on('data', (chunk) => (body += chunk));
      request.on('end', () => {
        response.writeHead(204).end();
        finish(JSON.parse(body));
      });
      return;
    }
    const file = resolveFile(path);
    requests.push(`${file ? 200 : 404} ${path}`);
    if (!file) {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, {
      'content-type': contentTypes[extname(file)] ?? 'application/octet-stream',
      'cache-control': 'no-store',
      ...(isolated ? isolationHeaders : {}),
    });
    createReadStream(file).pipe(response);
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const url = `http://127.0.0.1:${server.address().port}/test/browser/index.html`;

  const profile = mkdtempSync(join(tmpdir(), 'coreidml-browser-'));
  const child = spawn(browser, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', `--user-data-dir=${profile}`, url], {
    stdio: 'ignore',
  });
  const timer = setTimeout(() => finish(undefined), timeoutMilliseconds);

  const report = await finished;
  clearTimeout(timer);
  child.kill();
  server.close();
  await new Promise((resolve) => setTimeout(resolve, 500)); // let the browser release its profile folder
  try {
    rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  } catch {
    // A leftover temporary profile is not worth failing the run for.
  }
  return { report, requests };
}

if (!existsSync(join(packageRoot, 'dist', '_framework', 'dotnet.js'))) {
  console.error('No dist/_framework: run `npm run build:wasm` and `npm run build` first.');
  process.exit(1);
}
const browser = findBrowser();
if (!browser) {
  console.error('No Chrome, Edge or Chromium found. Set COREIDML_BROWSER to the browser to use.');
  process.exit(3); // "could not run", which eng/validate.cs reports as skipped rather than failed
}

let total = 0;
let failed = 0;
for (const isolated of [false, true]) {
  const { report, requests } = await runPage(browser, isolated);
  console.log(`\n${isolated ? 'cross-origin isolated page' : 'ordinary page'}`);
  if (!report) {
    console.error(`The page did not report within ${timeoutMilliseconds / 1000}s (${browser}). Requests served:\n  ${requests.join('\n  ')}`);
    process.exit(1);
  }
  console.log(report.userAgent);
  console.log(`loaded in ${report.loadMilliseconds} ms; ${requests.filter((line) => line.endsWith('.wasm')).length} .wasm files fetched`);
  for (const note of report.notes ?? []) {
    console.log(`  ${note}`);
  }
  if (report.isolated !== isolated) {
    console.log(`✖ the page was ${report.isolated ? '' : 'not '}cross-origin isolated`);
    failed++;
  }
  for (const result of report.results) {
    console.log(`${result.ok ? '✔' : '✖'} ${result.name}${result.ok ? '' : `\n${result.error}`}`);
  }
  total += report.results.length;
  failed += report.results.filter((result) => !result.ok).length;
  const missing = requests.filter((line) => line.startsWith('404') && !line.endsWith('/favicon.ico'));
  if (missing.length > 0) {
    console.log(`not found:\n  ${missing.join('\n  ')}`);
  }
}
console.log(`\n${total} tests, ${failed} failed`);
process.exit(failed === 0 && total > 0 ? 0 : 1);
