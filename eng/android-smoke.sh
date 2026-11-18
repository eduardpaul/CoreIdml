#!/usr/bin/env bash
# Runs the C smoke test (tests/native/smoke.c) on a connected Android device or emulator, once per ABI the device
# can run, against the libraries built by eng/build-android.sh:
#
#   bash eng/build-android.sh && bash eng/android-smoke.sh
#
# Needs adb with one device attached (ANDROID_SERIAL picks one of several) and the same NDK as the build.
set -euo pipefail

readonly NDK_VERSION="27.3.13750724"
readonly DEVICE_DIR="/data/local/tmp/coreidml-smoke"

root="$(cd "$(dirname "$0")/.." && pwd)"
sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Android/Sdk}}"
ndk="${ANDROID_NDK_ROOT:-$sdk/ndk/$NDK_VERSION}"
adb="$(command -v adb || echo "$sdk/platform-tools/adb")"
case "$(uname -s)" in
  Darwin) toolchain="$ndk/toolchains/llvm/prebuilt/darwin-x86_64" ;;
  *) toolchain="$ndk/toolchains/llvm/prebuilt/linux-x86_64" ;;
esac

device_abis="$("$adb" shell getprop ro.product.cpu.abilist | tr -d '\r')"
echo "Device: $("$adb" shell getprop ro.product.model | tr -d '\r'), Android $("$adb" shell getprop ro.build.version.release | tr -d '\r'), ABIs $device_abis"

declare -A triples=([arm64-v8a]=aarch64-linux-android [x86_64]=x86_64-linux-android)
ran=0
for abi in x86_64 arm64-v8a; do
  library="$root/artifacts/native/jniLibs/$abi/libcoreidml.so"
  if [[ ",$device_abis," != *",$abi,"* ]]; then
    echo "==> $abi: skipped, the device does not run it"
    continue
  fi
  if [[ ! -f "$library" ]]; then
    echo "==> $abi: no library at $library; run eng/build-android.sh first" >&2
    exit 1
  fi
  echo "==> $abi"
  build="$root/artifacts/smoke/android-$abi"
  mkdir -p "$build"
  # API 24 is the lowest level the test is built for; the library itself decides what it needs at load time.
  "$toolchain/bin/${triples[$abi]}24-clang" "$root/tests/native/smoke.c" -Wall -Wextra -Werror \
    -I"$root/include" -L"$(dirname "$library")" -lcoreidml -Wl,-rpath,'$ORIGIN' -o "$build/smoke"
  "$adb" shell "rm -rf $DEVICE_DIR/$abi && mkdir -p $DEVICE_DIR/$abi"
  if ! pushed="$("$adb" push "$build/smoke" "$library" "$root/tests/fixtures/article-1photo.idml" "$DEVICE_DIR/$abi/" 2>&1)"; then
    echo "$pushed" >&2
    exit 1
  fi
  # The library is found through the program's $ORIGIN rpath: the linker of a translated ABI (arm64 on an x86_64
  # emulator) does not read LD_LIBRARY_PATH.
  "$adb" shell "cd $DEVICE_DIR/$abi && chmod 755 smoke && ./smoke article-1photo.idml"
  ran=$((ran + 1))
done
"$adb" shell "rm -rf $DEVICE_DIR"
if [[ $ran -eq 0 ]]; then
  echo "No ABI of the device was tested." >&2
  exit 1
fi
echo "Android smoke test passed on $ran ABI(s)."
