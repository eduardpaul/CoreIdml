#!/usr/bin/env bash
# Publishes the staged npm tarballs in the order installs need: every platform package, then the packages that list
# them as optional dependencies (@coreidml/node, @coreidml/cli), and @coreidml/wasm. Used by the release workflow for
# npmjs.org and by its rehearsal against a local registry, so both run the same steps.
#
#   bash eng/publish-npm.sh <folder of .tgz files> <dist-tag> [extra npm publish arguments...]
set -euo pipefail

# An absolute path: npm reads "folder/file.tgz" as GitHub shorthand (user/repo) and tries to clone it.
folder="$(cd "$1" && pwd)"
tag="$2"
shift 2

# A platform package is <scope>-<package>-<platform>-<arch>-<version>.tgz; the version is what starts with a digit.
shopt -s nullglob
platform=("$folder"/coreidml-node-*-*-[0-9]*.tgz "$folder"/coreidml-cli-*-*-[0-9]*.tgz)
node=("$folder"/coreidml-node-[0-9]*.tgz)
wasm=("$folder"/coreidml-wasm-[0-9]*.tgz)
cli=("$folder"/coreidml-cli-[0-9]*.tgz)
if (( ${#platform[@]} == 0 || ${#node[@]} != 1 || ${#wasm[@]} != 1 || ${#cli[@]} != 1 )); then
  echo "Expected platform packages and one @coreidml/node, @coreidml/wasm and @coreidml/cli tarball in $folder." >&2
  ls -l "$folder" >&2
  exit 1
fi

# A version that is on the registry already is skipped, so a release that stopped halfway (one package without its
# trusted publisher, say) can be run again: npm never accepts the same version twice.
for tarball in "${platform[@]}" "${node[@]}" "${wasm[@]}" "${cli[@]}"; do
  spec="$(tar -xzOf "$tarball" package/package.json | node -e 'const p = JSON.parse(require("fs").readFileSync(0, "utf8")); console.log(`${p.name}@${p.version}`)')"
  if [[ -n "$(npm view "$spec" version 2>/dev/null)" ]]; then
    echo "skipping $spec: already published"
    continue
  fi
  echo "publishing $(basename "$tarball") (tag $tag)"
  npm publish "$tarball" --access public --tag "$tag" "$@"
done
