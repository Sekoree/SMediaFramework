using System.Collections.Concurrent;

namespace S.Media.OpenGL;

/// <summary>
/// Ref-counted linked programs keyed by a stable shader pair id (typically
/// <c>vertFile + fragFile</c>). Use when many <see cref="YuvVideoRenderer"/>
/// instances share the same sources within one OpenGL context.
/// </summary>
/// <remarks>
/// Program objects are not portable across GL contexts. Only use one current
/// context per cache key in a process, or disable sharing.
/// </remarks>
internal static class SharedGlProgramCache
{
    private sealed class Entry
    {
        public uint Program;
        public int RefCount;
        public readonly Lock Gate = new();
    }

    private static readonly ConcurrentDictionary<string, Entry> Programs = new(StringComparer.Ordinal);

    internal static uint Acquire(string cacheKey, GL gl, Func<GL, uint> link)
    {
        var entry = Programs.GetOrAdd(cacheKey, static _ => new Entry());
        lock (entry.Gate)
        {
            if (entry.Program == 0)
                entry.Program = link(gl);
            entry.RefCount++;
            return entry.Program;
        }
    }

    internal static void Release(GL gl, string cacheKey)
    {
        if (!Programs.TryGetValue(cacheKey, out var entry)) return;
        lock (entry.Gate)
        {
            if (--entry.RefCount > 0) return;
            if (entry.Program != 0)
            {
                gl.DeleteProgram(entry.Program);
                entry.Program = 0;
            }
            Programs.TryRemove(cacheKey, out _);
        }
    }
}
