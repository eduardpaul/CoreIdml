using System.Reflection;
using System.Runtime.InteropServices;
using CoreIdml.AbiGen;
using CoreIdml.Interop;
using CoreIdml.Native;
using CoreIdml.TestSupport;

namespace CoreIdml.Abi.Tests;

/// <summary>
/// One contract, written once in <c>include/coreidml.h</c>. These tests fail when anything else disagrees with it:
/// the functions the native library exports, the status codes of the interop layer, or a generated binding file.
/// </summary>
public class AbiTests
{
    private static readonly AbiDefinition Header = Repository.ReadAbi(TestFiles.RepositoryRoot);

    [Fact]
    public void The_header_and_the_library_declare_the_same_abi_version()
    {
        Assert.Equal(IdmlInterop.AbiVersion, Header.Version);
    }

    [Fact]
    public void The_header_and_the_interop_layer_declare_the_same_status_codes()
    {
        var header = Header.Statuses.ToDictionary(status => Naming.Pascal(status.Words), status => status.Value);
        var library = Enum.GetValues<IdmlStatus>().ToDictionary(status => status.ToString(), status => (int)status);

        Assert.Equal(library.OrderBy(pair => pair.Value), header.OrderBy(pair => pair.Value));
    }

    [Fact]
    public void The_header_and_the_native_library_declare_the_same_functions()
    {
        var exports = Exports();

        Assert.Equal(Header.Functions.Select(function => function.CName).Order(), exports.Keys.Order());
        foreach (var function in Header.Functions)
        {
            var method = exports[function.CName];
            var expected = string.Join(", ", function.Parameters.Select(parameter => $"{ManagedType(parameter.Type)} {Naming.Camel(parameter.Words)}"));
            var actual = string.Join(", ", method.GetParameters().Select(parameter => $"{parameter.ParameterType} {parameter.Name}"));
            Assert.True(expected == actual, $"{function.CName}: the header declares ({expected}) but the library exports ({actual}).");
            Assert.True(ManagedType(function.ReturnType) == method.ReturnType.ToString(), $"{function.CName}: the header returns {ManagedType(function.ReturnType)} but the library returns {method.ReturnType}.");
        }
    }

    [Fact]
    public void Every_function_of_the_header_is_documented()
    {
        Assert.All(Header.Functions, function => Assert.True(function.Documentation.Count > 0, $"{function.CName} has no comment above it."));
    }

    [Fact]
    public void The_generated_binding_files_are_up_to_date()
    {
        var stale = Repository.FindStale(TestFiles.RepositoryRoot, Emitters.Generate(Header));

        Assert.True(stale.Count == 0, $"Out of date: {string.Join(", ", stale)}. Run: dotnet run --project tools/CoreIdml.AbiGen");
    }

    [Fact]
    public void A_header_that_uses_an_unknown_type_is_refused_rather_than_guessed()
    {
        const string Text = "#define COREIDML_ABI_VERSION 1\n/* Doc. */\nCOREIDML_API double coreidml_ratio(void);\n";

        var exception = Assert.Throws<FormatException>(() => HeaderParser.Parse(Text));

        Assert.Contains("double", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declaration_the_parser_cannot_read_is_an_error_not_a_silent_omission()
    {
        const string Text = "#define COREIDML_ABI_VERSION 1\nCOREIDML_API int32_t coreidml_ok(void);\nCOREIDML_API int32_t (*coreidml_callback)(void);\n";

        Assert.Throws<FormatException>(() => HeaderParser.Parse(Text));
    }

    /// <summary>The exported functions of the native library, by symbol name.</summary>
    private static Dictionary<string, MethodInfo> Exports()
    {
        Dictionary<string, MethodInfo> exports = new(StringComparer.Ordinal);
        foreach (var method in typeof(NativeExports).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<UnmanagedCallersOnlyAttribute>() is { EntryPoint: { } symbol })
            {
                exports.Add(symbol, method);
            }
        }
        return exports;
    }

    /// <summary>The managed type an export must use for a C type, as <see cref="Type.ToString"/> prints it.</summary>
    private static string ManagedType(AbiType type) => type switch
    {
        AbiType.Nothing => "System.Void",
        AbiType.Number => "System.Int32",
        AbiType.Length => "System.UIntPtr",
        AbiType.Text or AbiType.Data => "System.Byte*",
        AbiType.TextOut or AbiType.DataOut => "System.Byte**",
        AbiType.LengthOut => "System.UIntPtr*",
        AbiType.Memory => "System.Void*",
        AbiType.Document => "System.IntPtr",
        AbiType.DocumentOut => "System.IntPtr*",
        _ => throw new NotSupportedException($"{type} has no managed type yet."),
    };
}
