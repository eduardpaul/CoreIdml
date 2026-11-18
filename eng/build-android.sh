#!/usr/bin/env bash
# Builds the native library for Android (arm64-v8a and x86_64) and lays it out the way Flutter and Gradle expect:
#
#   bash eng/build-android.sh [extra dotnet publish arguments, e.g. -p:Version=1.2.0 -p:CoreIdmlRelease=true]
#     -> artifacts/native/linux-bionic-arm64/libcoreidml.so, artifacts/native/linux-bionic-x64/libcoreidml.so
#     -> artifacts/native/jniLibs/{arm64-v8a,x86_64}/libcoreidml.so   (copy into android/app/src/main/jniLibs/)
#
# Run it on Linux (or macOS): Native AOT does not cross-compile between operating systems, and Android is Linux.
# Android is .NET's "linux-bionic" target, which .NET labels experimental for Native AOT.
#
# Builds are pinned to NDK r27d (27.3.13750724), the version they were verified with:
#   sdkmanager "ndk;27.3.13750724"
# It is taken from $ANDROID_HOME/ndk/<version>, or from ANDROID_NDK_ROOT when that points at the same version.
# To try another NDK on purpose, set COREIDML_ALLOW_OTHER_NDK=1 together with ANDROID_NDK_ROOT.
set -euo pipefail

readonly NDK_VERSION="27.3.13750724"

root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/native"

# The NDK's Pkg.Revision (e.g. 27.3.13750724), or nothing when the folder is not an NDK.
ndk_revision() {
  sed -n 's/^Pkg\.Revision *= *//p' "$1/source.properties" 2>/dev/null | tr -d '[:space:]' || true
}

# Prints the pinned NDK's path, or explains what is wrong and exits.
find_ndk() {
  local ndk
  if [[ -n "${ANDROID_NDK_ROOT:-}" ]]; then
    ndk="$ANDROID_NDK_ROOT"
  else
    ndk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}}/ndk/$NDK_VERSION"
  fi
  if [[ ! -d "$ndk" ]]; then
    echo "Android NDK $NDK_VERSION not found at $ndk. Install it: sdkmanager \"ndk;$NDK_VERSION\"" >&2
    exit 1
  fi
  local revision
  revision="$(ndk_revision "$ndk")"
  if [[ "$revision" != "$NDK_VERSION" && "${COREIDML_ALLOW_OTHER_NDK:-}" != "1" ]]; then
    echo "Android NDK at $ndk is ${revision:-not an NDK (no source.properties)}; builds are pinned to $NDK_VERSION (r27d)." >&2
    echo "Install it with sdkmanager \"ndk;$NDK_VERSION\", or set COREIDML_ALLOW_OTHER_NDK=1 to use this one anyway." >&2
    exit 1
  fi
  if [[ "$revision" != "$NDK_VERSION" ]]; then
    echo "warning: building with untested Android NDK ${revision:-unknown} (pinned version is $NDK_VERSION)" >&2
  fi
  echo "$ndk"
}

ndk="$(find_ndk)"
case "$(uname -s)" in
  Linux) host_tag=linux-x86_64 ;;
  Darwin) host_tag=darwin-x86_64 ;;
  *) echo "Build Android on Linux or macOS." >&2; exit 1 ;;
esac
toolchain="$ndk/toolchains/llvm/prebuilt/$host_tag"
echo "Android NDK $(ndk_revision "$ndk") at $ndk"
export PATH="$toolchain/bin:$PATH"

declare -A abis=([linux-bionic-arm64]=arm64-v8a [linux-bionic-x64]=x86_64)
for rid in linux-bionic-arm64 linux-bionic-x64; do
  echo "==> $rid (${abis[$rid]})"
  # CoreIdml.Native.csproj sets what every linux-bionic build needs; the NDK's compiler and sysroot come from here.
  dotnet publish "$root/src/CoreIdml.Native" -c Release -r "$rid" -nologo -v q \
    -p:CppCompilerAndLinker=clang \
    -p:SysRoot="$toolchain/sysroot" "$@"
  mkdir -p "$out/jniLibs/${abis[$rid]}"
  cp "$out/$rid/libcoreidml.so" "$out/jniLibs/${abis[$rid]}/"
done
echo "Android libraries -> $out/jniLibs/"
