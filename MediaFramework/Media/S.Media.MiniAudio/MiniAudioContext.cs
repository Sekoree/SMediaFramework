using S.Media.Core.Audio;
using S.Media.MiniAudio.Runtime;

namespace S.Media.MiniAudio;

internal sealed unsafe class MiniAudioContext : IDisposable
{
    private nint _handle;

    private MiniAudioContext(nint handle) => _handle = handle;

    public static MiniAudioContext Create()
    {
        MiniAudioLibraryResolver.Install();
        MiniAudioException.ThrowIfError(MiniAudioNative.ContextCreate(out var handle), "sma_context_create");
        return new MiniAudioContext(handle);
    }

    public IReadOnlyList<AudioDeviceInfo> Enumerate(MiniAudioDeviceType deviceType)
    {
        ObjectDisposedException.ThrowIf(_handle == nint.Zero, this);

        MiniAudioException.ThrowIfError(
            MiniAudioNative.ContextDeviceCount(_handle, (int)deviceType, out var count),
            "sma_context_device_count");

        var devices = new AudioDeviceInfo[count];
        var idCapacity = Math.Max(1, MiniAudioNative.DeviceIdHexCapacity());
        for (var i = 0; i < devices.Length; i++)
        {
            var idBuffer = new byte[idCapacity];
            var nameBuffer = new byte[512];
            uint isDefault;
            uint maxChannels;
            uint defaultSampleRate;

            fixed (byte* idPtr = idBuffer)
            fixed (byte* namePtr = nameBuffer)
            {
                MiniAudioException.ThrowIfError(
                    MiniAudioNative.ContextDeviceGet(
                        _handle,
                        (int)deviceType,
                        (uint)i,
                        idPtr,
                        idBuffer.Length,
                        namePtr,
                        nameBuffer.Length,
                        out isDefault,
                        out maxChannels,
                        out defaultSampleRate),
                    "sma_context_device_get");
            }

            devices[i] = new AudioDeviceInfo(
                MiniAudioNative.FromUtf8NullTerminated(idBuffer),
                MiniAudioNative.FromUtf8NullTerminated(nameBuffer),
                checked((int)Math.Max(1, maxChannels)),
                defaultSampleRate == 0 ? 48000 : defaultSampleRate,
                isDefault != 0);
        }

        return devices;
    }

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, nint.Zero);
        if (handle != nint.Zero)
            MiniAudioNative.ContextDestroy(handle);
    }
}
