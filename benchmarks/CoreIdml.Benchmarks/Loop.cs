using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace CoreIdml.Benchmarks;

/// <summary>Runs one benchmark method repeatedly outside BenchmarkDotNet, for a profiler to sample.</summary>
public static class LoopRunner
{
    public static int Run(string typeName, string methodName, string? parameter, int count)
    {
        var type = typeof(LoopRunner).Assembly.GetType("CoreIdml.Benchmarks." + typeName) ?? throw new ArgumentException("No benchmark class " + typeName);
        var instance = Activator.CreateInstance(type)!;
        if (!string.IsNullOrEmpty(parameter) && parameter != "-")
        {
            var property = type.GetProperties().Single(p => p.GetCustomAttribute<BenchmarkDotNet.Attributes.ParamsSourceAttribute>() is not null);
            property.SetValue(instance, property.PropertyType == typeof(int) ? int.Parse(parameter, CultureInfo.InvariantCulture) : parameter);
        }
        Invoke<BenchmarkDotNet.Attributes.GlobalSetupAttribute>(type, instance);
        var method = type.GetMethod(methodName) ?? throw new ArgumentException("No method " + methodName);
        method.Invoke(instance, null);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            method.Invoke(instance, null);
        }
        Console.WriteLine($"{typeName}.{methodName}({parameter}): {watch.Elapsed.TotalMilliseconds / count:F2} ms per call");
        Invoke<BenchmarkDotNet.Attributes.GlobalCleanupAttribute>(type, instance);
        return 0;
    }

    private static void Invoke<TAttribute>(Type type, object instance)
        where TAttribute : Attribute
    {
        foreach (var method in type.GetMethods().Where(m => m.GetCustomAttribute<TAttribute>() is not null))
        {
            method.Invoke(instance, null);
        }
    }
}
