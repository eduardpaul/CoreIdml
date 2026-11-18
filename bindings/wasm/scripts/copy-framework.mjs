// Copies the WebAssembly build (artifacts/wasm/_framework) next to the compiled wrapper, so the package is
// self-contained: dist/index.js loads dist/_framework/dotnet.js by default.
import { cpSync, existsSync, rmSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const source = fileURLToPath(new URL('../../../artifacts/wasm/_framework/', import.meta.url));
const target = fileURLToPath(new URL('../dist/_framework/', import.meta.url));

if (!existsSync(source)) {
  console.error(`No WebAssembly build at ${source}. Run: npm run build:wasm`);
  process.exit(1);
}
rmSync(target, { recursive: true, force: true });
cpSync(source, target, { recursive: true });
console.log(`_framework -> ${target}`);
