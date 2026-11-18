#!/usr/bin/env bash
# Puts what the app bundles into place (none of it is committed):
#   - the Android libraries      artifacts/native/jniLibs/*     -> android/app/src/main/jniLibs/
#   - the Linux desktop library  artifacts/native/linux-<arch>/ -> linux/native/
#   - the template the app opens tests/fixtures/                -> assets/
# Build the libraries first: eng/build-android.sh, and dotnet publish src/CoreIdml.Native -c Release -r linux-x64.
set -euo pipefail

app="$(cd "$(dirname "$0")/.." && pwd)"
root="$(cd "$app/../.." && pwd)"
native="$root/artifacts/native"

mkdir -p "$app/assets"
cp "$root/tests/fixtures/article-1photo.idml" "$app/assets/"

if [[ -d "$native/jniLibs" ]]; then
  rm -rf "$app/android/app/src/main/jniLibs"
  cp -r "$native/jniLibs" "$app/android/app/src/main/jniLibs"
  echo "Android: $(cd "$app/android/app/src/main/jniLibs" && ls | tr '\n' ' ')"
else
  echo "Android: no libraries in $native/jniLibs (run eng/build-android.sh)"
fi

case "$(uname -m)" in
  x86_64) rid=linux-x64 ;;
  aarch64) rid=linux-arm64 ;;
  *) rid= ;;
esac
if [[ -n "$rid" && -f "$native/$rid/libcoreidml.so" ]]; then
  mkdir -p "$app/linux/native"
  cp "$native/$rid/libcoreidml.so" "$app/linux/native/"
  echo "Linux: $rid"
else
  echo "Linux: no library in $native/${rid:-linux-<arch>} (run dotnet publish src/CoreIdml.Native -c Release -r ${rid:-linux-x64})"
fi
