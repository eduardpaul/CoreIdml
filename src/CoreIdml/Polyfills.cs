// .NET 11 types the library uses, for older target frameworks. Internal, so they never reach the public API.
#if !NET11_0_OR_GREATER
using System.Runtime.InteropServices;

namespace System.Runtime.CompilerServices
{
    /// <summary>Marks a C# 15 closed hierarchy; the compiler needs it, the runtime never reads it.</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    internal sealed class IsClosedTypeAttribute : Attribute;
}

namespace System.IO
{
    /// <summary>.NET 11's ReadOnlyMemoryStream: a read-only stream over the same memory (copied only if it is not an array).</summary>
    internal sealed class ReadOnlyMemoryStream : MemoryStream
    {
        public ReadOnlyMemoryStream(ReadOnlyMemory<byte> data)
            : this(MemoryMarshal.TryGetArray(data, out var segment) ? segment : new ArraySegment<byte>(data.ToArray()))
        {
        }

        private ReadOnlyMemoryStream(ArraySegment<byte> segment)
            : base(segment.Array!, segment.Offset, segment.Count, writable: false)
        {
        }
    }
}
#endif
