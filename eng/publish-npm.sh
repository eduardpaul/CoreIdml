#!/usr/bin/env bash
# Publishes the staged npm tarballs in the order installs need: every platform package, then @coreidml/node (which
# lists them as optional dependencies), then @coreidml/wasm. Used by the release workflow for npmjs.org and by its
# rehearsal against a local registry, so both run the same steps.
#
#   bash eng/publish-npm.sh <folder of .tgz files> <dist-tag> [extra npm publish arguments...]
set -euo pipefail

# An absolute path: npm reads "folder/file.tgz" as GitHub shorthand (user/repo) and tries to clone it.
folder="$(cd "$1" && pwd)"
tag="$2"
shift 2

shopt -s nullglob
platform=("$folder"/coreidml-node-*-*-[0-9]*.tgz)
main=("$folder"/coreidml-node-[0-9]*.tgz)
wasm=("$folder"/coreidml-wasm-[0-9]*.tgz)
if (( ${#platform[@]} == 0 || ${#main[@]} != 1 || ${#wasm[@]} != 1 )); then
  echo "Expected platform packages, one @coreidml/node and one @coreidml/wasm tarball in $folder." >&2
  ls -l "$folder" >&2
  exit 1
fi

for tarball in "${platform[@]}" "${main[@]}" "${wasm[@]}"; do
  echo "publishing $(basename "$tarball") (tag $tag)"
  npm publish "$tarball" --access public --tag "$tag" "$@"
done
