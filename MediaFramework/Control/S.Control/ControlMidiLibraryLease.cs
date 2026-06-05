using PMLib;
using PMLib.Types;

namespace S.Control;

/// <summary>
/// Ref-counted lease over the global PortMidi library: the first <see cref="Acquire"/> initializes it and
/// the last <see cref="Dispose"/> terminates it, so multiple control sessions can share one initialization.
/// </summary>
internal sealed class ControlMidiLibraryLease : IDisposable
{
    private static readonly object Gate = new();
    private static int _refCount;
    private bool _disposed;

    private ControlMidiLibraryLease()
    {
    }

    public static ControlMidiLibraryLease Acquire()
    {
        lock (Gate)
        {
            if (_refCount == 0)
            {
                var err = PMUtil.Initialize();
                if (err != PmError.NoError)
                    throw new InvalidOperationException(PMUtil.GetErrorText(err) ?? err.ToString());
            }

            _refCount++;
            return new ControlMidiLibraryLease();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        lock (Gate)
        {
            if (_refCount <= 0)
                return;

            _refCount--;
            if (_refCount == 0)
                PMUtil.Terminate();
        }
    }
}
