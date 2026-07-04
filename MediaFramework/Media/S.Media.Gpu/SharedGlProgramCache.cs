using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace S.Media.Gpu;

/// <summary>
/// Ref-counted linked programs keyed by a stable shader pair id (typically
/// <c>vertFile + fragFile</c>) <strong>within one OpenGL context</strong>. Use when many
/// <see cref="YuvVideoRenderer"/> / compositor instances share the same sources in the same context.
/// </summary>
/// <remarks>
/// Program objects are not portable across GL contexts, so the cache is scoped per <see cref="GL"/>
/// instance (one <see cref="GL"/> per context in this codebase): two compositors in <em>different</em>
/// contexts never share a program, which would otherwise hand a program linked in one context to another
/// (every <c>glGetUniformLocation</c> then returns -1). Entries for a context are reclaimed when its
/// <see cref="GL"/> is collected; programs are freed by the driver when the context is destroyed.
/// </remarks>
internal static class SharedGlProgramCache
{
    private sealed class Entry
    {
        public uint Program;
        public int RefCount;
        public readonly Lock Gate = new();
    }

    // Per-GL-context program tables (weak keys so a destroyed context's entries don't pin or leak).
    private static readonly ConditionalWeakTable<GL, ConcurrentDictionary<string, Entry>> PerContext = new();

    internal static uint Acquire(string cacheKey, GL gl, Func<GL, uint> link)
    {
        var programs = PerContext.GetValue(gl, static _ => new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal));
        var entry = programs.GetOrAdd(cacheKey, static _ => new Entry());
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
        if (!PerContext.TryGetValue(gl, out var programs)) return;
        if (!programs.TryGetValue(cacheKey, out var entry)) return;
        lock (entry.Gate)
        {
            if (--entry.RefCount > 0) return;
            if (entry.Program != 0)
            {
                gl.DeleteProgram(entry.Program);
                entry.Program = 0;
            }
            programs.TryRemove(cacheKey, out _);
        }
    }
}
