#!/usr/bin/env node
// The `coreidml` command of the npm package @coreidml/cli. It only starts the real program: a native binary that npm
// installed with this package, from the platform package that matches this machine (@coreidml/cli-<platform>-<arch>,
// an optional dependency, so npm skips the ones for other systems).
//
//   npx @coreidml/cli summary brochure.idml
//   npx -y @coreidml/cli@<version> mcp            the MCP server over stdio, as MCP clients start it
//
// Arguments, standard input and output, the exit code and signals pass straight through, so the launcher is invisible
// to whoever runs it; an MCP client talks to the binary itself.
'use strict';

const { spawn } = require('node:child_process');
const { accessSync, chmodSync, constants, existsSync } = require('node:fs');
const { constants: os } = require('node:os');
const { dirname, join } = require('node:path');

const platformPackage = `@coreidml/cli-${process.platform}-${process.arch}`;
const executable = process.platform === 'win32' ? 'coreidml.exe' : 'coreidml';

// The platforms that have a build. Kept here only for the error message; npm decides what to install from the
// os, cpu and libc fields of each platform package.
const supported = ['win32-x64', 'win32-arm64', 'darwin-arm64', 'darwin-x64', 'linux-x64 (glibc)', 'linux-arm64 (glibc)'];

/** Path of the binary in the installed platform package, or a message that says why there is none. */
function findBinary() {
  let packageFolder;
  try {
    // Resolved from this file, so the package is found wherever npm put it (nested, hoisted, or in the npx cache).
    packageFolder = dirname(require.resolve(`${platformPackage}/package.json`));
  } catch {
    return { error: notInstalled() };
  }
  const binary = join(packageFolder, executable);
  if (!existsSync(binary)) {
    return { error: `${platformPackage} is installed at ${packageFolder} but has no ${executable}. Reinstall @coreidml/cli.` };
  }
  return { binary };
}

function notInstalled() {
  const here = `${process.platform}-${process.arch}`;
  const lines = [`coreidml: the package ${platformPackage}, which holds the program for this machine, is not installed.`];
  if (!supported.some((name) => name.startsWith(here))) {
    lines.push(`There is no build for ${here}. Builds exist for: ${supported.join(', ')}.`);
  } else if (process.platform === 'linux' && isMusl()) {
    lines.push('This Linux uses musl (Alpine, for example); the Linux builds need glibc 2.28 or later.');
  } else {
    lines.push(
      'npm installs it as an optional dependency of @coreidml/cli. It is skipped when optional dependencies are',
      'omitted (--omit=optional, --no-optional) or when a lockfile was made on another platform. Reinstall with',
      'optional dependencies, or run: npx -y @coreidml/cli@<version> ...',
    );
  }
  lines.push('Other ways to get coreidml: https://github.com/eduardpaul/CoreIdml/blob/main/src/CoreIdml.Cli/README.md');
  return lines.join('\n');
}

/** Whether this Linux runs on musl instead of glibc (Node reports the glibc version only when there is one). */
function isMusl() {
  try {
    return !process.report.getReport().header.glibcVersionRuntime;
  } catch {
    return false;
  }
}

/**
 * Makes sure a Unix binary can be run. npm keeps the executable bit the package was published with, but an install
 * that went through another tool or a copy can lose it; then this puts it back, once, where the user may write.
 */
function ensureExecutable(binary) {
  if (process.platform === 'win32') return;
  try {
    accessSync(binary, constants.X_OK);
  } catch {
    try {
      chmodSync(binary, 0o755);
    } catch {
      // Not ours to change; spawning reports the real problem.
    }
  }
}

const found = findBinary();
if (found.error) {
  console.error(found.error);
  process.exit(1);
}
ensureExecutable(found.binary);

const child = spawn(found.binary, process.argv.slice(2), { stdio: 'inherit', windowsHide: true });

// Signals sent to this process (an MCP client stopping the server, Ctrl+C in a terminal) go to the binary, and this
// process stays alive until the binary has exited, so the exit status below is the binary's. On Windows there is
// nothing to forward: Ctrl+C reaches every process of the console, the binary included, and killing it from here
// would only cut its shutdown short, so the handlers just keep this process waiting for it.
const handlers = new Map();
for (const signal of ['SIGINT', 'SIGTERM', 'SIGHUP', 'SIGQUIT', 'SIGBREAK']) {
  if (!(signal in os.signals)) continue;
  const handler = () => {
    if (process.platform === 'win32' || child.exitCode !== null || child.signalCode !== null) return;
    try {
      child.kill(signal);
    } catch {
      // The binary is already gone; its exit event follows.
    }
  };
  process.on(signal, handler);
  handlers.set(signal, handler);
}

child.on('error', (error) => {
  console.error(`coreidml: could not start ${found.binary}: ${error.message}`);
  process.exit(1);
});

child.on('exit', (code, signal) => {
  for (const [name, handler] of handlers) process.removeListener(name, handler);
  if (signal) {
    // End the same way the binary did, so a parent sees "killed by SIGTERM" rather than an exit code; the exit code
    // a shell would show (128 + signal number) is the fallback should this process outlive the signal.
    process.exitCode = 128 + (os.signals[signal] ?? 0);
    process.kill(process.pid, signal);
    return;
  }
  process.exit(code ?? 1);
});
