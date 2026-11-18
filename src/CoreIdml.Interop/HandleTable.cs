using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace CoreIdml.Interop;

/// <summary>
/// Maps opaque handles to objects. Handles count up from 1 and are never reused, so a stale handle (a finalizer
/// that runs late in Dart or Node, a double close) can only miss; it can never reach somebody else's object.
/// </summary>
internal sealed class HandleTable<T>
    where T : class
{
    private readonly ConcurrentDictionary<nint, T> items = new();
    private long last;

    public int Count => items.Count;

    public nint Add(T item)
    {
        var handle = (nint)Interlocked.Increment(ref last);
        items[handle] = item;
        return handle;
    }

    public bool TryGet(nint handle, [NotNullWhen(true)] out T? item) => items.TryGetValue(handle, out item);

    public bool Remove(nint handle) => items.TryRemove(handle, out _);
}
