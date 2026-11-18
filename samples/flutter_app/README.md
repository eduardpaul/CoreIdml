# CoreIdml in a Flutter app

A small app that opens a template, edits a story, saves and reopens it, all through `bindings/dart/coreidml` and
the native library bundled with the app. Its integration test runs on the device, so it proves the whole chain:
Native AOT library → app bundle → dart:ffi → Dart API.

```sh
bash eng/build-android.sh                                         # Android libraries (from the repository root)
dotnet publish src/CoreIdml.Native -c Release -r linux-x64        # Linux desktop library
cd samples/flutter_app
bash tool/prepare.sh                                              # copies libraries and the template into the app
flutter test integration_test -d <device>                         # an Android device or emulator, or: -d linux
```

The Android build needs a JDK with a compiler (17 or 21); Flutter uses `JAVA_HOME` when it is set. The build warns
that Flutter's plugins prefer a newer NDK than the pinned one the library is built with; that is expected.

How the library gets into the app:

- **Android**: `android/app/src/main/jniLibs/<abi>/libcoreidml.so`; Gradle packages it and the binding loads it by name.
- **Linux desktop**: `linux/CMakeLists.txt` installs `linux/native/libcoreidml.so` into the bundle's `lib/`, where
  the binding looks first.

Flutter web is not supported: the Dart package uses dart:ffi.
