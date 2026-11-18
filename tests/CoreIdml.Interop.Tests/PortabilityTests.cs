using System.Reflection;

namespace CoreIdml.Interop.Tests;

/// <summary>
/// Guards the Runtime Async policy of Directory.Build.targets. An assembly that Mono may load (the browser build)
/// must not contain runtime-async methods: Mono throws "Runtime Async is not supported on this platform" the
/// moment one awaits (verified on .NET 11.0.0).
/// </summary>
public class PortabilityTests
{
    /// <summary>MethodImplAttributes.Async: the method is compiled for runtime async instead of a state machine.</summary>
    private const MethodImplAttributes RuntimeAsync = (MethodImplAttributes)0x2000;

    [Fact]
    public void The_portable_assemblies_have_no_runtime_async_methods()
    {
        Assert.Empty(RuntimeAsyncMethods(typeof(IdmlDocument).Assembly));
        Assert.Empty(RuntimeAsyncMethods(typeof(IdmlInterop).Assembly));
    }

    [Fact]
    public async Task This_test_assembly_is_compiled_with_runtime_async()
    {
        // Proves the check above can see such methods: assemblies that never run on Mono get runtime async,
        // and this method is one of them.
        await Task.Yield();

        Assert.Contains(nameof(This_test_assembly_is_compiled_with_runtime_async), RuntimeAsyncMethods(typeof(PortabilityTests).Assembly));
    }

    [Fact]
    public async Task The_async_api_of_the_core_library_awaits_correctly_from_runtime_async_callers()
    {
        // A runtime-async caller (this test) awaiting state-machine async code (the portable library).
        using var stream = File.OpenRead(TestSupport.TestFiles.Article);

        var document = await IdmlDocument.OpenAsync(stream, TestContext.Current.CancellationToken);
        using var target = new MemoryStream();
        await document.SaveAsync(target, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(3, document.StoryPaths.Count);
        Assert.Equal(document.ToArray(), target.ToArray());
    }

    private static List<string> RuntimeAsyncMethods(Assembly assembly)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return [.. assembly.GetTypes()
            .SelectMany(type => type.GetMethods(All))
            .Where(method => (method.MethodImplementationFlags & RuntimeAsync) != 0)
            .Select(method => method.Name)];
    }
}
