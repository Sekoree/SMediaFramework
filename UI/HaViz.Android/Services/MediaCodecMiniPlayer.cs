using Android.Content;
using Android.Media;
using HaViz.Core;
using System.Runtime.InteropServices;
using AUri = Android.Net.Uri;

namespace HaViz.Android.Services;

/// <summary>
/// Mini player on the platform decoders: MediaExtractor + MediaCodec decode one track on a
/// dedicated thread, output is converted to float32 and written to an AudioTrack, and the SAME
/// floats go to the <see cref="IPcmSink"/> so the visualizer/NDI audio is exactly what is
/// audible. Interface calls come from the UI thread; events fire on the decode thread.
/// </summary>
public sealed class MediaCodecMiniPlayer : IMiniPlayer
{
    private readonly Context _context;
    private readonly IPcmSink _sink;
    private readonly AudioManager? _audioManager;
    private readonly object _lock = new();
    private Session? _session;
    private int? _preferredDeviceId;
    private volatile bool _localOutputEnabled; // default false: NDI/visualizer feed only

    public event Action<TrackInfo>? TrackStarted;
    public event Action? PlaybackEnded;
    public event Action<string>? PlaybackError;

    public MediaCodecMiniPlayer(Context context, IPcmSink sink)
    {
        _context = context;
        _sink = sink;
        _audioManager = (AudioManager?)context.GetSystemService(Context.AudioService);
    }

    public bool IsPlaying
    {
        get
        {
            lock (_lock)
                return _session is { IsPaused: false, HasEnded: false };
        }
    }

    public void Play(TrackInfo track)
    {
        lock (_lock)
        {
            _session?.Shutdown();
            _session = new Session(this, track);
            _session.Start();
        }
    }

    public void Pause()
    {
        lock (_lock)
            _session?.Pause();
    }

    public void Resume()
    {
        lock (_lock)
            _session?.Resume();
    }

    public void Stop()
    {
        lock (_lock)
        {
            _session?.Shutdown();
            _session = null;
        }
    }

    public IReadOnlyList<AudioOutputDeviceInfo> GetOutputDevices()
    {
        var result = new List<AudioOutputDeviceInfo>();
        foreach (var device in _audioManager?.GetDevices(GetDevicesTargets.Outputs) ?? [])
        {
            // Telephony/submix are "outputs" only to the audio framework, not user-routable sinks.
            // (The RemoteSubmix constant is API 31+; older releases keep it hidden.)
            if (!device.IsSink || device.Type == AudioDeviceType.Telephony)
                continue;
            if (OperatingSystem.IsAndroidVersionAtLeast(31) && device.Type == AudioDeviceType.RemoteSubmix)
                continue;
            var product = device.ProductName?.ToString();
            var label = TypeLabel(device.Type);
            var name = string.IsNullOrWhiteSpace(product) ? label : $"{product} ({label})";
            result.Add(new AudioOutputDeviceInfo(device.Id, name));
        }

        return result;
    }

    public void SetOutputDevice(int? deviceId)
    {
        lock (_lock)
        {
            _preferredDeviceId = deviceId;
            _session?.ApplyPreferredDevice();
        }
    }

    public void SetLocalOutputEnabled(bool enabled)
    {
        lock (_lock)
        {
            _localOutputEnabled = enabled;
            _session?.ApplyLocalOutputVolume();
        }
    }

    public void Dispose() => Stop();

    private AudioDeviceInfo? ResolvePreferredDevice()
    {
        if (_preferredDeviceId is not { } id)
            return null;
        var devices = _audioManager?.GetDevices(GetDevicesTargets.Outputs) ?? [];
        return devices.FirstOrDefault(d => d.Id == id);
    }

    private static string TypeLabel(AudioDeviceType type) => type switch
    {
        AudioDeviceType.BuiltinSpeaker => "Speaker",
        AudioDeviceType.BuiltinEarpiece => "Earpiece",
        AudioDeviceType.WiredHeadphones => "Wired headphones",
        AudioDeviceType.WiredHeadset => "Wired headset",
        AudioDeviceType.BluetoothA2dp => "Bluetooth",
        AudioDeviceType.BluetoothSco => "Bluetooth SCO",
        AudioDeviceType.UsbDevice => "USB",
        AudioDeviceType.UsbHeadset => "USB headset",
        AudioDeviceType.UsbAccessory => "USB accessory",
        AudioDeviceType.Hdmi => "HDMI",
        AudioDeviceType.HdmiArc => "HDMI ARC",
        AudioDeviceType.Dock => "Dock",
        AudioDeviceType.LineAnalog => "Line out",
        AudioDeviceType.LineDigital => "Digital line out",
        AudioDeviceType.AuxLine => "Aux",
        AudioDeviceType.HearingAid => "Hearing aid",
        _ => type.ToString(),
    };

    /// <summary>One playing track: owns the extractor/codec/AudioTrack and the decode thread.</summary>
    private sealed class Session
    {
        private const long DequeueTimeoutUs = 10_000;

        private readonly MediaCodecMiniPlayer _owner;
        private readonly TrackInfo _track;
        private readonly Thread _thread;
        private readonly CancellationTokenSource _cts = new();
        // Set = running; Pause() resets it to park the decode thread without tearing anything down.
        private readonly ManualResetEventSlim _resumeGate = new(true);

        private MediaExtractor? _extractor;
        private MediaCodec? _codec;
        private volatile AudioTrack? _audioTrack;
        private int _sampleRate;
        private int _channels;
        private Encoding _pcmEncoding = Encoding.Pcm16bit;
        private byte[] _byteScratch = [];
        private float[] _floatScratch = [];
        private long _framesWritten;

        public volatile bool IsPaused;
        public volatile bool HasEnded;

        public Session(MediaCodecMiniPlayer owner, TrackInfo track)
        {
            _owner = owner;
            _track = track;
            _thread = new Thread(Run) { IsBackground = true, Name = "haviz-decode" };
        }

        public void Start() => _thread.Start();

        public void Pause()
        {
            IsPaused = true;
            _resumeGate.Reset();
            _audioTrack?.Pause();
        }

        public void Resume()
        {
            IsPaused = false;
            _audioTrack?.Play();
            _resumeGate.Set();
        }

        public void ApplyPreferredDevice() => _audioTrack?.SetPreferredDevice(_owner.ResolvePreferredDevice());

        /// <summary>Local monitoring is a volume gate, not a write gate: the blocking AudioTrack
        /// write stays as the loop's clock and the tap keeps feeding NDI/visualizer regardless.</summary>
        public void ApplyLocalOutputVolume() => _audioTrack?.SetVolume(_owner._localOutputEnabled ? 1f : 0f);

        /// <summary>Cancels and joins the decode thread. Called at most once (under the owner lock,
        /// which also drops the reference), so no events fire after this returns.</summary>
        public void Shutdown()
        {
            _cts.Cancel();
            // Unblock a decode thread stuck in a blocking Write (flush makes it return early).
            var track = _audioTrack;
            try
            {
                track?.Pause();
                track?.Flush();
            }
            catch (Exception)
            {
                // Track released concurrently by the decode thread's teardown - nothing to unblock.
            }

            if (_thread.IsAlive)
                _thread.Join();
        }

        private void Run()
        {
            try
            {
                Setup();
                _owner.TrackStarted?.Invoke(_track);
                if (DecodeLoop())
                {
                    DrainToEnd();
                    _owner.PlaybackEnded?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                // Stop()/skip - deliberate teardown, no events.
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                    _owner.PlaybackError?.Invoke(ex.Message);
            }
            finally
            {
                HasEnded = true;
                ReleaseAll();
            }
        }

        private void Setup()
        {
            _extractor = new MediaExtractor();
            // TrackInfo.Uri is a SAF content:// URI on Android, but plain file paths stay playable
            // (adb-pushed files, tests).
            if (_track.Uri.Contains("://"))
                _extractor.SetDataSource(_owner._context, AUri.Parse(_track.Uri)!, null);
            else
                _extractor.SetDataSource(_track.Uri);

            MediaFormat? format = null;
            string? mime = null;
            for (var i = 0; i < _extractor.TrackCount; i++)
            {
                var candidate = _extractor.GetTrackFormat(i);
                var candidateMime = candidate.GetString(MediaFormat.KeyMime);
                if (candidateMime?.StartsWith("audio/", StringComparison.Ordinal) != true)
                    continue;
                _extractor.SelectTrack(i);
                format = candidate;
                mime = candidateMime;
                break;
            }

            if (format is null || mime is null)
                throw new InvalidOperationException($"no audio track in '{_track.DisplayName}'");

            _sampleRate = format.GetInteger(MediaFormat.KeySampleRate);
            _channels = format.GetInteger(MediaFormat.KeyChannelCount);

            _codec = MediaCodec.CreateDecoderByType(mime);
            _codec.Configure(format, null, null, MediaCodecConfigFlags.None);
            _codec.Start();
        }

        /// <summary>True on natural EOS; throws OperationCanceledException on Stop()/skip.</summary>
        private bool DecodeLoop()
        {
            var info = new MediaCodec.BufferInfo();
            var inputDone = false;
            while (true)
            {
                _resumeGate.Wait(_cts.Token);
                _cts.Token.ThrowIfCancellationRequested();

                if (!inputDone)
                    inputDone = FeedInput();

                var index = _codec!.DequeueOutputBuffer(info, DequeueTimeoutUs);
                if (index == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    ApplyOutputFormat(_codec.OutputFormat);
                    continue;
                }

                if (index < 0)
                    continue; // try-again-later / buffers-changed - nothing to drain this pass

                if (info.Size > 0)
                    DeliverPcm(_codec.GetOutputBuffer(index)!, info.Offset, info.Size);
                var endOfStream = (info.Flags & MediaCodecBufferFlags.EndOfStream) != 0;
                _codec.ReleaseOutputBuffer(index, false);
                if (endOfStream)
                    return true;
            }
        }

        /// <summary>True once the end-of-stream input has been queued.</summary>
        private bool FeedInput()
        {
            var index = _codec!.DequeueInputBuffer(DequeueTimeoutUs);
            if (index < 0)
                return false;

            var buffer = _codec.GetInputBuffer(index)!;
            var size = _extractor!.ReadSampleData(buffer, 0);
            if (size < 0)
            {
                _codec.QueueInputBuffer(index, 0, 0, 0, MediaCodecBufferFlags.EndOfStream);
                return true;
            }

            _codec.QueueInputBuffer(index, 0, size, _extractor.SampleTime, MediaCodecBufferFlags.None);
            _extractor.Advance();
            return false;
        }

        /// <summary>Decoders may change rate/layout/encoding mid-stream (e.g. chained Ogg); the
        /// AudioTrack must follow or playback comes out pitch-shifted.</summary>
        private void ApplyOutputFormat(MediaFormat format)
        {
            var sampleRate = format.GetInteger(MediaFormat.KeySampleRate);
            var channels = format.GetInteger(MediaFormat.KeyChannelCount);
            _pcmEncoding = format.ContainsKey(MediaFormat.KeyPcmEncoding)
                ? (Encoding)format.GetInteger(MediaFormat.KeyPcmEncoding)
                : Encoding.Pcm16bit;

            if (sampleRate == _sampleRate && channels == _channels && _audioTrack is not null)
                return;

            _sampleRate = sampleRate;
            _channels = channels;
            if (_audioTrack is { } old)
            {
                old.Pause();
                old.Flush();
                old.Release();
                _audioTrack = null;
                _framesWritten = 0;
            }
            // The replacement is created lazily by the next DeliverPcm with the new parameters.
        }

        private void DeliverPcm(Java.Nio.ByteBuffer buffer, int offset, int size)
        {
            EnsureAudioTrack();

            if (_byteScratch.Length < size)
                _byteScratch = new byte[Math.Max(size, _byteScratch.Length * 2)];
            buffer.Position(offset);
            buffer.Get(_byteScratch, 0, size);

            int samples;
            if (_pcmEncoding == Encoding.PcmFloat)
            {
                samples = size / sizeof(float);
                EnsureFloatScratch(samples);
                Buffer.BlockCopy(_byteScratch, 0, _floatScratch, 0, size);
            }
            else
            {
                // 16-bit little-endian (all Android ABIs are LE) -> normalized float.
                samples = size / sizeof(short);
                EnsureFloatScratch(samples);
                var shorts = MemoryMarshal.Cast<byte, short>(_byteScratch.AsSpan(0, size));
                for (var i = 0; i < shorts.Length; i++)
                    _floatScratch[i] = shorts[i] * (1f / 32768f);
            }

            // Same samples to the tap and the speaker; the blocking write paces the whole loop.
            _owner._sink.SubmitPcm(new ReadOnlySpan<float>(_floatScratch, 0, samples), _sampleRate, _channels);
            var written = _audioTrack!.Write(_floatScratch, 0, samples, WriteMode.Blocking);
            if (written > 0)
                _framesWritten += written / _channels;
        }

        private void EnsureFloatScratch(int samples)
        {
            if (_floatScratch.Length < samples)
                _floatScratch = new float[Math.Max(samples, _floatScratch.Length * 2)];
        }

        private void EnsureAudioTrack()
        {
            if (_audioTrack is not null)
                return;

            var formatBuilder = new AudioFormat.Builder()
                .SetEncoding(Encoding.PcmFloat)!
                .SetSampleRate(_sampleRate)!;
            // Positional masks for the common layouts (they route best); the index mask covers
            // exotic channel counts without a lookup table.
            switch (_channels)
            {
                case 1:
                    formatBuilder.SetChannelMask(ChannelOut.Mono);
                    break;
                case 2:
                    formatBuilder.SetChannelMask(ChannelOut.Stereo);
                    break;
                default:
                    formatBuilder.SetChannelIndexMask((1 << _channels) - 1);
                    break;
            }

            var attributes = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Music)!
                .Build()!;
            // GetMinBufferSize has no index-mask overload; scale the stereo minimum instead.
            var minBytes = AudioTrack.GetMinBufferSize(
                _sampleRate, _channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo, Encoding.PcmFloat);
            if (_channels > 2)
                minBytes = minBytes * _channels / 2;

            var track = new AudioTrack.Builder()
                .SetAudioAttributes(attributes)!
                .SetAudioFormat(formatBuilder.Build()!)!
                .SetTransferMode(AudioTrackMode.Stream)!
                .SetBufferSizeInBytes(Math.Max(minBytes * 2, 16 * 1024))!
                .Build();
            track.SetPreferredDevice(_owner.ResolvePreferredDevice());
            track.SetVolume(_owner._localOutputEnabled ? 1f : 0f);
            if (!IsPaused)
                track.Play();
            _audioTrack = track;
        }

        /// <summary>Lets the buffered tail play out before PlaybackEnded so the playlist advance
        /// (which releases this AudioTrack) does not clip the end of the song.</summary>
        private void DrainToEnd()
        {
            if (_audioTrack is not { } track || _framesWritten == 0)
                return;
            track.Stop(); // stream mode: keeps playing until everything written has been played
            var deadline = Environment.TickCount64 + 3_000;
            while (!_cts.IsCancellationRequested
                   && Environment.TickCount64 < deadline
                   && track.PlaybackHeadPosition < _framesWritten)
                Thread.Sleep(20);
        }

        private void ReleaseAll()
        {
            // Each release is independent - a codec in the error state must not leak the track.
            try
            {
                _codec?.Stop();
                _codec?.Release();
            }
            catch (Exception)
            {
                // Codec already in the released/error state.
            }

            try
            {
                _extractor?.Release();
            }
            catch (Exception)
            {
                // Extractor never got a data source.
            }

            try
            {
                _audioTrack?.Release();
            }
            catch (Exception)
            {
                // Track already released by a format change.
            }

            _audioTrack = null;
        }
    }
}
