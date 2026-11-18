namespace CoreIdml.Pdf;

/// <summary>
/// Android detection that also covers the Native AOT linux-bionic build (the Flutter/dart:ffi path). There
/// <see cref="OperatingSystem.IsAndroid"/> is false (to .NET it is "Linux"), yet the process runs on Android: no
/// /usr/share/fonts, no OpenSSL. Every Android process has ANDROID_ROOT set.
/// </summary>
internal static class AndroidRuntime
{
    /// <summary>Running on Android, whether as .NET for Android (MAUI) or as the linux-bionic native library.</summary>
    public static bool IsAndroid { get; } =
        OperatingSystem.IsAndroid() || Environment.GetEnvironmentVariable("ANDROID_ROOT") is { Length: > 0 };

    /// <summary>
    /// The linux-bionic native library on Android. Here .NET's System.Security.Cryptography expects OpenSSL, Android
    /// only has BoringSSL, and the first crypto call aborts the whole process instead of throwing. .NET for Android
    /// (MAUI) has its own crypto and is not affected.
    /// </summary>
    public static bool IsNativeLibraryOnAndroid { get; } = IsAndroid && !OperatingSystem.IsAndroid();
}
