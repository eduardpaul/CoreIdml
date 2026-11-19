// Serves the web demo (and the repository it reads the template and fonts from) on http://localhost:8080.
//
//   npm install && node serve.mjs            then open http://localhost:8080/samples/packages/web/
//   node serve.mjs --check                   runs the page in headless Chrome, Edge or Chromium and exits with its
//                                            result (COREIDML_BROWSER picks the browser)
import { spawn } from 'node:child_process';
import { createReadStream, existsSync, mkdtempSync, rmSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { extname, join, normalize, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = normalize(fileURLToPath(new URL('../../../', import.meta.url)));
const checking = process.argv.includes('--check');
const page = '/samples/packages/web/';
const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.json': 'application/json',
  '.wasm': 'application/wasm', // streaming compilation of WebAssembly requires exactly this type
  '.ttf': 'font/ttf',
};

let finish;
const finished = new Promise((resolve) => (finish = resolve));

const server = createServer((request, response) => {
  const path = decodeURIComponent(new URL(request.url, 'http://localhost').pathname);
  if (request.method === 'POST' && path === '/results') {
    let body = '';
    request.on('data', (chunk) => (body += chunk));
    request.on('end', () => {
      response.writeHead(204).end();
      finish(JSON.parse(body));
    });
    return;
  }
  let file = normalize(join(root, path.endsWith('/') ? `${path}index.html` : path));
  if (!file.startsWith(root.replace(/[\\/]$/, '') + sep) || !existsSync(file) || !statSync(file).isFile()) {
    response.writeHead(404).end();
    return;
  }
  response.writeHead(200, {
    'content-type': types[extname(file)] ?? 'application/octet-stream',
    // Cross-origin isolated, so IdmlWorker can cancel a running call without stopping the worker.
    'cross-origin-opener-policy': 'same-origin',
    'cross-origin-embedder-policy': 'require-corp',
  });
  createReadStream(file).pipe(response);
});
await new Promise((resolve) => server.listen(checking ? 0 : 8080, '127.0.0.1', resolve));
const url = `http://localhost:${server.address().port}${page}`;

if (!checking) {
  console.log(`Open ${url}`);
} else {
  const browser = [
    process.env.COREIDML_BROWSER,
    'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe',
    'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',
    'C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe',
    '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/usr/bin/google-chrome',
    '/usr/bin/chromium',
    '/usr/bin/chromium-browser',
  ].find((candidate) => candidate && existsSync(candidate));
  if (!browser) {
    console.error('No Chrome, Edge or Chromium found; set COREIDML_BROWSER.');
    process.exit(3);
  }
  const profile = mkdtempSync(join(tmpdir(), 'coreidml-demo-'));
  const child = spawn(browser, ['--headless=new', '--disable-gpu', '--no-first-run', `--user-data-dir=${profile}`, `${url}?check`], { stdio: 'ignore' });
  const timer = setTimeout(() => finish(undefined), 180_000);
  const result = await finished;
  clearTimeout(timer);
  child.kill();
  server.close();
  await new Promise((resolve) => setTimeout(resolve, 500));
  try {
    rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  } catch {
    // a leftover temporary profile is harmless
  }
  if (!result) {
    console.error('The page did not report within 180 s.');
    process.exit(1);
  }
  console.log(result.userAgent);
  console.log(result.lines.join('\n'));
  process.exit(result.failures.length === 0 ? 0 : 1);
}
