using System.Buffers;

namespace S.Media.FFmpeg.Internal;

/// <summary>
/// <see cref="MemoryManager{T}"/> that wraps an unmanaged pointer + length so it
/// can be exposed as <see cref="ReadOnlyMemory{T}"/>. The pointed-to memory must
/// outlive every <see cref="Memory"/> handle handed out — typically the producer
/// keeps the underlying buffer alive (e.g. via a refcounted FFmpeg AVFrame) and
/// frees it explicitly when consumers are done.
/// </summary>
internal sealed unsafe class UnmanagedMemoryManager<T> : MemoryManager<T> where T : unmanaged
{
    private readonly T* _ptr;
    private readonly int _length;

    public UnmanagedMemoryManager(T* ptr, int length)
    {
        if (ptr == null) throw new ArgumentNullException(nameof(ptr));
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        _ptr = ptr;
        _length = length;
    }

    public override Span<T> GetSpan() => new(_ptr, _length);

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_length)
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        return new MemoryHandle(_ptr + elementIndex);
    }

    public override void Unpin() { /* not pinned: pointer is already fixed */ }

    protected override void Dispose(bool disposing) { /* lifetime managed by producer */ }
}
