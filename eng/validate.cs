// Builds and tests every target of the repository, the way CI does, on the machine it runs on:
//
//   dotnet run eng/validate.cs                 everything the installed toolchains allow
//   dotnet run eng/validate.cs -- --coreclr    also the opt-in CoreCLR flavor of the WebAssembly build
//
// A step whose toolchain is missing (Dart, a C compiler) is reported as skipped, never as passed.
// This is a file-based app (.NET 10+): no project file, only the SDK is needed.

#:property TreatWarningsAsErrors=false

using System.Diagnostics;
using System.Runtime.InteropServices;

var root = FindRoot();
var rid = $"{(OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")}-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
var coreclr = args.Contains("--coreclr");
List<(string Step, string Result)> results = [];

Step("build (warnings are errors)", "dotnet", "build CoreIdml.slnx -c Release -nologo -v q");
Step(".NET tests: core, interop contract, ABI conformance", "dotnet", "test --solution CoreIdml.slnx -c Release --no-build");
Step($"native library, Native AOT, {rid}", "dotnet", $"publish src/CoreIdml.Native -c Release -r {rid} -nologo -v q");
Step("WebAssembly build (Mono, the default runtime)", "dotnet", "publish src/CoreIdml.Wasm -c Release -nologo -v q");

Step("Python binding (ctypes -> native)", Has("python3") ? "python3" : "python", "-m unittest discover -s bindings/python/tests");
var node = Has("node");
if (node)
{
    if (!Directory.Exists(Path.Combine(root, "bindings", "node", "node_modules")))
    {
        Step("Node binding: install dependencies", "npm", "ci --no-audit --no-fund", "bindings/node");
    }
    Step("Node binding (koffi -> native)", "node", "--test \"test/**/*.test.ts\"", "bindings/node");
    Step("JavaScript binding (WebAssembly, under Node)", "node", "--test \"test/**/*.test.ts\"", "bindings/wasm");
    if (!Directory.Exists(Path.Combine(root, "bindings", "wasm", "node_modules")))
    {
        Step("JavaScript binding: install dependencies", "npm", "ci --no-audit --no-fund", "bindings/wasm");
    }
    // Compiles the package with tsc, then runs it in headless Chrome, Edge or Chromium; exit code 3 = no browser.
    Step("JavaScript binding (WebAssembly, in a real browser)", "npm", "run test:browser", "bindings/wasm", skippedExitCode: 3);
}
else
{
    results.Add(("Node binding (koffi -> native): no node on the PATH", "skipped"));
    results.Add(("JavaScript binding (WebAssembly, under Node): no node on the PATH", "skipped"));
}

if (Has("dart"))
{
    Step("Dart binding: get packages", "dart", "pub get", "bindings/dart/coreidml");
    Step("Dart binding: analyze", "dart", "analyze --fatal-infos", "bindings/dart/coreidml");
    Step("Dart binding (dart:ffi -> native)", "dart", "test", "bindings/dart/coreidml");
}
else
{
    results.Add(("Dart binding (dart:ffi -> native): no dart on the PATH", "skipped"));
}

var smokeDirectory = Path.Combine(root, "artifacts", "smoke", rid);
var nativeDirectory = Path.Combine(root, "artifacts", "native", rid);
if (!OperatingSystem.IsWindows() && Has("cc"))
{
    Directory.CreateDirectory(smokeDirectory);
    var smoke = Path.Combine(smokeDirectory, "smoke");
    Step("C smoke test: compile", "cc", $"tests/native/smoke.c -Wall -Wextra -Werror -Iinclude -L{nativeDirectory} -lcoreidml -Wl,-rpath,{nativeDirectory} -o {smoke}");
    Step("C smoke test (header + linking)", smoke, "tests/fixtures/article-1photo.idml");
}
else if (OperatingSystem.IsWindows() && rid == "win-x64" && FindVcVars() is { } vcvars)
{
    // MSVC needs its environment; the program runs with the library next to it.
    Directory.CreateDirectory(smokeDirectory);
    var smoke = Path.Combine(smokeDirectory, "smoke.exe");
    Step("C smoke test: compile (MSVC)", $"call \"{vcvars}\" >nul &&", $"cl /nologo /W4 /WX /D_CRT_SECURE_NO_WARNINGS /Iinclude tests\\native\\smoke.c \"{Path.Combine(nativeDirectory, "coreidml.lib")}\" /Fo\"{smokeDirectory}/\" /Fe\"{smoke}\"");
    if (File.Exists(Path.Combine(nativeDirectory, "coreidml.dll")))
    {
        File.Copy(Path.Combine(nativeDirectory, "coreidml.dll"), Path.Combine(smokeDirectory, "coreidml.dll"), overwrite: true);
    }
    Step("C smoke test (header + linking)", $"\"{smoke}\"", "tests/fixtures/article-1photo.idml");
}
else
{
    results.Add(("C smoke test (header + linking): no C compiler found", "skipped"));
}

if (coreclr)
{
    // The two wasm flavors share intermediate files; a mixed build does not start, hence the clean builds.
    var obj = Path.Combine(root, "artifacts", "obj", "CoreIdml.Wasm");
    Directory.Delete(obj, recursive: true);
    Step("WebAssembly build (CoreCLR, opt-in)", "dotnet", "publish src/CoreIdml.Wasm -c Release -nologo -v q -p:CoreIdmlWasmRuntime=CoreCLR");
    Step("JavaScript binding on CoreCLR WebAssembly", "node", "--test \"test/**/*.test.ts\"", "bindings/wasm");
    Directory.Delete(obj, recursive: true);
    Step("WebAssembly build back to Mono", "dotnet", "publish src/CoreIdml.Wasm -c Release -nologo -v q");
}

Console.WriteLine();
Console.WriteLine("==== CoreIdml validation ====");
foreach (var (step, result) in results)
{
    Console.WriteLine($"  {result,-8} {step}");
}
return results.Any(r => r.Result == "FAILED") ? 1 : 0;

void Step(string name, string command, string arguments, string directory = "", int? skippedExitCode = null)
{
    Console.WriteLine($"\n---- {name}\n     {command} {arguments}");
    var watch = Stopwatch.StartNew();
    int exitCode;
    try
    {
        // npm and friends are .cmd shims on Windows, which only the shell can start.
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command} {arguments}\"")
            : new ProcessStartInfo(command, arguments);
        start.WorkingDirectory = Path.Combine(root, directory);
        start.UseShellExecute = false;
        using var process = Process.Start(start)!;
        process.WaitForExit();
        exitCode = process.ExitCode;
    }
    catch (System.ComponentModel.Win32Exception exception)
    {
        Console.WriteLine($"     could not start: {exception.Message}");
        exitCode = -1;
    }
    results.Add(($"{name} ({watch.Elapsed.TotalSeconds:0.0}s)", exitCode == 0 ? "ok" : exitCode == skippedExitCode ? "skipped" : "FAILED"));
}

bool Has(string tool)
{
    var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : [""];
    return (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Any(folder => extensions.Any(extension => File.Exists(Path.Combine(folder, tool + extension))));
}

// vcvars64.bat of the newest Visual Studio (or Build Tools) that has the C++ x64 tools, if any.
static string? FindVcVars()
{
    var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
    if (!File.Exists(vswhere))
    {
        return null;
    }
    var start = new ProcessStartInfo(vswhere, "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath")
    {
        RedirectStandardOutput = true,
        UseShellExecute = false,
    };
    using var process = Process.Start(start)!;
    var installation = process.StandardOutput.ReadToEnd().Trim();
    process.WaitForExit();
    var vcvars = Path.Combine(installation, "VC", "Auxiliary", "Build", "vcvars64.bat");
    return installation.Length > 0 && File.Exists(vcvars) ? vcvars : null;
}

static string FindRoot()
{
    for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "CoreIdml.slnx")))
        {
            return directory.FullName;
        }
    }
    throw new DirectoryNotFoundException("Run this from inside the repository (no CoreIdml.slnx above the current directory).");
}
