namespace S.Media.Routing;

/// <summary>
/// Multi-client fan-in for a physical <see cref="IAudioOutput"/>. Each lease has its own
/// single-producer buffer; one persistent <see cref="AudioRouter"/> mixes those buffers and is
/// the only producer that ever submits to the terminal output.
/// </summary>
/// <remarks>
/// This is the ownership boundary required for hardware outputs such as PortAudio, whose native
/// ring is deliberately single-producer. Sharing the terminal instance itself would corrupt that
/// contract, while opening one terminal per player creates duplicate operating-system audio nodes.
/// </remarks>
public sealed class SharedAudioOutput : IDisposable
{
    private const string TerminalId = "__shared_terminal";
    private const string SilenceId = "__shared_silence";

    private readonly Lock _gate = new();
    private readonly IAudioOutput _terminal;
    private readonly AudioRouter _mixer;
    private readonly TimeSpan _clientBufferDuration;
    private readonly bool _disposeTerminalOutput;
    private readonly Dictionary<long, ClientInput> _clients = [];
    private long _nextClientId;
    private bool _disposed;

    /// <param name="terminalOutput">The one physical/backend output owned by the mixer.</param>
    /// <param name="disposeTerminalOutput">
    /// Whether disposing this owner also disposes <paramref name="terminalOutput"/>.
    /// </param>
    /// <param name="chunkSamples">Mixer chunk size in samples per channel.</param>
    /// <param name="pumpCapacityChunks">Short jitter queue in front of the physical output.</param>
    /// <param name="clientBufferDuration">Maximum buffer available to each independent producer.</param>
    public SharedAudioOutput(
        IAudioOutput terminalOutput,
        bool disposeTerminalOutput = false,
        int chunkSamples = 480,
        int pumpCapacityChunks = 2,
        TimeSpan? clientBufferDuration = null)
    {
        ArgumentNullException.ThrowIfNull(terminalOutput);
        terminalOutput.Format.Validate(nameof(terminalOutput));

        _terminal = terminalOutput;
        _disposeTerminalOutput = disposeTerminalOutput;
        _clientBufferDuration = clientBufferDuration ?? TimeSpan.FromMilliseconds(60);
        if (_clientBufferDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(clientBufferDuration), "must be > 0");

        _mixer = new AudioRouter(terminalOutput.Format.SampleRate, chunkSamples, pumpCapacityChunks)
        {
            AutoWirePrimary = true,
            // The permanent silence source keeps the device alive; natural EOF is not part of this
            // owner's lifecycle and must never flush a terminal used by a newly acquired client.
            FlushOutputsOnNaturalEof = false,
        };

        try
        {
            _mixer.AddOutput(terminalOutput, TerminalId, pumpCapacityChunks);
            _mixer.AddSource(new SilenceSource(terminalOutput.Format), SilenceId, autoResample: false);
            _mixer.AddRoute(SilenceId, TerminalId, ChannelMap.Identity(terminalOutput.Format.Channels));
            _mixer.Start();
        }
        catch
        {
            try { _mixer.Dispose(); }
            catch { /* preserve the construction failure */ }
            if (_disposeTerminalOutput)
            {
                try { (terminalOutput as IDisposable)?.Dispose(); }
                catch { /* preserve the construction failure */ }
            }
            throw;
        }
    }

    public AudioFormat Format => _terminal.Format;

    /// <summary>Number of independent playback clients currently feeding the terminal.</summary>
    public int ActiveLeaseCount
    {
        get { lock (_gate) return _clients.Count; }
    }

    /// <summary>
    /// Creates an isolated endpoint for one producer. Disposing the returned lease removes only
    /// that producer; the physical output and every other client remain live.
    /// </summary>
    public SharedAudioOutputLease Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var clientId = ++_nextClientId;
            var sourceId = $"client_{clientId}";
            var input = new ClientInput(Format, _clientBufferDuration, _mixer.ChunkSamples);
            _mixer.AddSource(input, sourceId, autoResample: false);
            try
            {
                _mixer.AddRoute(sourceId, TerminalId, ChannelMap.Identity(Format.Channels));
                _clients.Add(clientId, input);
                return new SharedAudioOutputLease(input, () => Release(clientId, sourceId, input));
            }
            catch
            {
                _mixer.RemoveSource(sourceId);
                input.Dispose();
                throw;
            }
        }
    }

    private void Release(long clientId, string sourceId, ClientInput input)
    {
        lock (_gate)
        {
            if (!_clients.Remove(clientId))
                return;

            input.Dispose();
            if (!_disposed)
                _mixer.RemoveSource(sourceId); // also removes the client's route atomically
        }
    }

    public void Dispose()
    {
        ClientInput[] clients;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            clients = _clients.Values.ToArray();
            _clients.Clear();
        }

        foreach (var client in clients)
            client.Dispose();
        try
        {
            _mixer.Dispose();
        }
        finally
        {
            if (_disposeTerminalOutput)
                (_terminal as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// One SPSC client endpoint. It is clocked from the mixer's consumption of its private bus so
    /// an upstream router applies backpressure instead of slowly overflowing the buffer. It does
    /// not implement IPlaybackClock: the physical device has one continuous clock, while each
    /// client has an independent transport epoch.
    /// </summary>
    private sealed class ClientInput :
        IAudioOutput,
        IAudioOutputChannelCapabilities,
        IAudioSource,
        IClockedOutput,
        IFlushableOutput,
        IDisposable
    {
        private readonly AudioBus _bus;
        private readonly int _targetQueueSamples;
        private int _disposed;

        public ClientInput(AudioFormat format, TimeSpan bufferDuration, int chunkSamples)
        {
            _bus = new AudioBus(format, bufferDuration);
            _targetQueueSamples = Math.Min(_bus.CapacitySamples, Math.Max(chunkSamples * 3, chunkSamples));
        }

        public AudioFormat Format => _bus.Format;
        public AudioOutputChannelCapabilities ChannelCapabilities => _bus.ChannelCapabilities;
        public bool IsExhausted => false;

        public void Submit(ReadOnlySpan<float> packedSamples)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;
            _bus.Submit(packedSamples);
        }

        public int ReadInto(Span<float> destination) =>
            Volatile.Read(ref _disposed) != 0 ? 0 : _bus.ReadInto(destination);

        public bool WaitForCapacity(int chunkSamples, CancellationToken token)
        {
            if (chunkSamples <= 0)
                return Volatile.Read(ref _disposed) == 0 && !token.IsCancellationRequested;

            var deadline = Environment.TickCount64 + (long)TimeSpan.FromSeconds(5).TotalMilliseconds;
            while (Volatile.Read(ref _disposed) == 0 && !token.IsCancellationRequested)
            {
                var queued = _bus.BufferedSamples;
                if (queued + chunkSamples <= _targetQueueSamples)
                    return true;
                if (Environment.TickCount64 >= deadline)
                    return false;

                var excess = queued + chunkSamples - _targetQueueSamples;
                var waitMs = Math.Max(1, (int)Math.Ceiling(1000.0 * excess / Format.SampleRate));
                if (token.WaitHandle.WaitOne(waitMs))
                    return false;
            }

            return false;
        }

        public void Flush() => _bus.Flush();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                _bus.Flush();
        }
    }

    private sealed class SilenceSource(AudioFormat format) : IAudioSource
    {
        public AudioFormat Format { get; } = format;
        public bool IsExhausted => false;

        public int ReadInto(Span<float> destination)
        {
            destination.Clear();
            return destination.Length;
        }
    }
}

/// <summary>A single producer's borrowed endpoint into a <see cref="SharedAudioOutput"/>.</summary>
public sealed class SharedAudioOutputLease : IDisposable
{
    private Action? _release;

    internal SharedAudioOutputLease(IAudioOutput output, Action release)
    {
        Output = output;
        _release = release;
    }

    public IAudioOutput Output { get; }

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
