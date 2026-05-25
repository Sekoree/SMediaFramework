using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HaPlay.Desktop;

/// <summary>
/// Minimal background-writer file logger provider. One file per process invocation, named
/// <c>haplay-YYYYMMDD-HHmmss.log</c> in the configured directory. Old logs older than
/// <see cref="RollingFileLoggerOptions.RetainCount"/> are removed at startup so the directory
/// does not grow unbounded across sessions.
/// </summary>
/// <remarks>
/// Designed to never block the producer: <see cref="ILogger.Log{TState}"/> formats the line and
/// hands it to a concurrent queue; a single background task drains the queue and writes to disk.
/// File I/O failures fall back to <see cref="Console.Error"/> rather than throwing into framework
/// hot paths.
/// </remarks>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly RollingFileLoggerOptions _options;
    private readonly BlockingCollectionDrain _drain;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private readonly string _filePath;
    private FileStream? _stream;
    private bool _disposed;

    public RollingFileLoggerProvider(RollingFileLoggerOptions options)
    {
        _options = options;
        Directory.CreateDirectory(options.Directory);
        TryPruneOld(options);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _filePath = Path.Combine(options.Directory, $"{options.FileNamePrefix}-{stamp}.log");
        try
        {
            _stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RollingFileLogger: cannot open {_filePath}: {ex.Message}");
            _stream = null;
        }

        _drain = new BlockingCollectionDrain(options.QueueCapacity);
        _writerTask = Task.Run(() => DrainLoop(_cts.Token));
    }

    /// <summary>Absolute path of the file this provider opened on startup.</summary>
    public string FilePath => _filePath;

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(this, name, _options.MinimumLevel));

    internal void Enqueue(string line)
    {
        if (_disposed) return;
        _drain.TryEnqueueDropOldest(line);
    }

    private void DrainLoop(CancellationToken token)
    {
        try
        {
            foreach (var line in _drain.Consume(token))
            {
                if (_stream is null) continue;
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(line);
                    _stream.Write(bytes, 0, bytes.Length);
                    _stream.WriteByte((byte)'\n');
                    _stream.Flush();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"RollingFileLogger: write failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
    }

    private static void TryPruneOld(RollingFileLoggerOptions options)
    {
        if (options.RetainCount <= 0) return;
        try
        {
            var files = new DirectoryInfo(options.Directory)
                .EnumerateFiles($"{options.FileNamePrefix}-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(options.RetainCount)
                .ToList();
            foreach (var f in files)
            {
                try { f.Delete(); }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort — pruning is not critical */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _drain.CompleteAdding();
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch { /* best effort */ }
        finally
        {
            try { _stream?.Dispose(); }
            catch { /* best effort */ }
            _cts.Cancel();
            _cts.Dispose();
        }
    }

    private sealed class BlockingCollectionDrain
    {
        private readonly ConcurrentQueue<string> _queue = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly int _capacity;
        private volatile bool _completed;

        public BlockingCollectionDrain(int capacity) => _capacity = Math.Max(64, capacity);

        public void TryEnqueueDropOldest(string line)
        {
            if (_completed) return;
            while (_queue.Count >= _capacity && _queue.TryDequeue(out _)) { /* drop oldest */ }
            _queue.Enqueue(line);
            try { _signal.Release(); } catch (ObjectDisposedException) { /* shutdown race */ }
            catch (SemaphoreFullException) { /* signal already saturated */ }
        }

        public IEnumerable<string> Consume(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { _signal.Wait(token); }
                catch (OperationCanceledException) { yield break; }
                while (_queue.TryDequeue(out var line))
                    yield return line;
                if (_completed && _queue.IsEmpty) yield break;
            }
        }

        public void CompleteAdding()
        {
            _completed = true;
            try { _signal.Release(); } catch { /* best effort */ }
        }
    }
}

/// <summary>Configuration knobs for <see cref="RollingFileLoggerProvider"/>.</summary>
public sealed class RollingFileLoggerOptions
{
    /// <summary>Directory log files live in (created on demand).</summary>
    public string Directory { get; init; } = ".";
    /// <summary>Filename prefix; combined with a timestamp produces e.g. <c>haplay-20260520-141503.log</c>.</summary>
    public string FileNamePrefix { get; init; } = "haplay";
    /// <summary>Discard log records below this severity.</summary>
    public LogLevel MinimumLevel { get; init; } = LogLevel.Information;
    /// <summary>Queue capacity before drop-oldest kicks in.</summary>
    public int QueueCapacity { get; init; } = 8192;
    /// <summary>Number of historical log files to keep beyond the new one — older are deleted at startup.</summary>
    public int RetainCount { get; init; } = 10;
}

internal sealed class RollingFileLogger : ILogger
{
    private readonly RollingFileLoggerProvider _provider;
    private readonly string _category;
    private readonly LogLevel _min;

    public RollingFileLogger(RollingFileLoggerProvider provider, string category, LogLevel min)
    {
        _provider = provider;
        _category = category;
        _min = min;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= _min && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;
        var msg = formatter(state, exception);
        var sb = new StringBuilder(128 + msg.Length);
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff"))
            .Append(' ').Append(Abbrev(logLevel))
            .Append(' ').Append(_category)
            .Append(": ").Append(msg);
        if (exception is not null)
            sb.Append(' ').Append(exception);
        _provider.Enqueue(sb.ToString());
    }

    [SuppressMessage("Style", "IDE0072:Add missing cases", Justification = "Exhaustive switch with default")]
    private static string Abbrev(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };
}
