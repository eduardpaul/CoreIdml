import { existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

/** Environment variable with the full path of the native library; it wins over every other location. */
export const LIBRARY_ENV = 'COREIDML_LIBRARY';

/** Name of this package; the platform packages that carry the library are `<name>-<platform>-<arch>`. */
export const PACKAGE_NAME = '@coreidml/node';

/** File name of the native library on a platform: `coreidml.dll`, `libcoreidml.dylib` or `libcoreidml.so`. */
export function libraryFileName(platform: NodeJS.Platform = process.platform): string {
  switch (platform) {
    case 'win32':
      return 'coreidml.dll';
    case 'darwin':
      return 'libcoreidml.dylib';
    default:
      return 'libcoreidml.so';
  }
}

/** The .NET runtime identifier of a Node platform and architecture (the folder name under artifacts/native), if any. */
export function dotnetRid(platform: NodeJS.Platform = process.platform, arch: string = process.arch): string | undefined {
  const os = platform === 'win32' ? 'win' : platform === 'darwin' ? 'osx' : platform === 'linux' ? 'linux' : undefined;
  const cpu = arch === 'x64' || arch === 'arm64' ? arch : undefined;
  return os && cpu ? `${os}-${cpu}` : undefined;
}

/** Where a library path came from, for error messages. */
export interface LibraryLocation {
  path: string;
  source: string;
}

export interface FindLibraryOptions {
  explicit?: string | undefined;
  env?: Record<string, string | undefined>;
  platform?: NodeJS.Platform;
  arch?: string;
  /** Directory of an installed package, or undefined when it is not installed. */
  resolvePackage?: (name: string) => string | undefined;
  /** Root of a source checkout (has include/coreidml.h), for the local build in artifacts/native. */
  sourceRoot?: string | undefined;
  exists?: (path: string) => boolean;
}

export interface FindLibraryResult {
  /** Locations to load, in order. */
  candidates: LibraryLocation[];
  /** Locations that were looked at and skipped, with the reason, for the error when nothing loads. */
  skipped: string[];
}

/**
 * Where to look for the native library, in order:
 *   1. the path given to `CoreIdml.load({ libraryPath })`;
 *   2. the `COREIDML_LIBRARY` environment variable;
 *   3. the platform package `@coreidml/node-<platform>-<arch>` (an optional dependency of the published package);
 *   4. a local build in `artifacts/native/<rid>/` when running from a source checkout;
 *   5. the bare file name, left to the operating system's library search path.
 * An explicit path or the environment variable is the only candidate: if it does not load, that is the error.
 */
export function findLibrary(options: FindLibraryOptions = {}): FindLibraryResult {
  const env = options.env ?? process.env;
  const platform = options.platform ?? process.platform;
  const arch = options.arch ?? process.arch;
  const exists = options.exists ?? existsSync;
  const fileName = libraryFileName(platform);
  const skipped: string[] = [];

  if (options.explicit) {
    return { candidates: [{ path: options.explicit, source: 'the libraryPath option' }], skipped };
  }
  const fromEnv = env[LIBRARY_ENV];
  if (fromEnv) {
    return { candidates: [{ path: fromEnv, source: `the ${LIBRARY_ENV} environment variable` }], skipped };
  }
  skipped.push(`${LIBRARY_ENV} is not set`);

  const candidates: LibraryLocation[] = [];
  const platformPackage = `${PACKAGE_NAME}-${platform}-${arch}`;
  const directory = options.resolvePackage?.(platformPackage);
  if (directory === undefined) {
    skipped.push(`package ${platformPackage} is not installed`);
  } else if (!exists(join(directory, fileName))) {
    skipped.push(`package ${platformPackage} has no ${fileName}`);
  } else {
    candidates.push({ path: join(directory, fileName), source: `package ${platformPackage}` });
  }

  const rid = dotnetRid(platform, arch);
  if (options.sourceRoot && rid) {
    const path = join(options.sourceRoot, 'artifacts', 'native', rid, fileName);
    if (exists(path)) {
      candidates.push({ path, source: 'the local build' });
    } else {
      skipped.push(`no local build at ${path}`);
    }
  }

  candidates.push({ path: fileName, source: "the operating system's library search path" });
  return { candidates, skipped };
}

/** The repository root when this package runs from a source checkout (bindings/node inside it), else undefined. */
export function sourceCheckoutRoot(): string | undefined {
  const root = join(dirname(fileURLToPath(import.meta.url)), '..', '..', '..');
  return existsSync(join(root, 'include', 'coreidml.h')) ? root : undefined;
}

/** Directory of an installed package, resolved from this package (so optional dependencies are found). */
export function resolveInstalledPackage(name: string): string | undefined {
  try {
    return dirname(createRequire(import.meta.url).resolve(`${name}/package.json`));
  } catch {
    return undefined;
  }
}

/** How to get the library on this platform, added to "not found" errors. */
export function buildHint(platform: NodeJS.Platform = process.platform, arch: string = process.arch): string {
  const rid = dotnetRid(platform, arch);
  return rid
    ? `Build it with: dotnet publish src/CoreIdml.Native -c Release -r ${rid}`
    : `There is no build of the CoreIdml native library for ${platform}-${arch}.`;
}
