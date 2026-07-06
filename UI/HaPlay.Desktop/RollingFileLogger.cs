using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
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
/// Designed to never block the producer (LOG-01): <see cref="ILogger.Log{TState}"/> formats a
/// length-bounded line and hands it to a <see cref="Channel.CreateBounded{T}(BoundedChannelOptions)"/>
/// (single-reader, drop-oldest); a single background task drains and writes. Writes are batched — the
/// stream is flushed at most every <see cref="RollingFileLoggerOptions.FlushIntervalMs"/>, and
/// immediately on a warning/error/critical line or shutdown — instead of once per line. Dropped lines
/// (queue overflow) are counted and periodically surfaced into the log. File I/O failures fall back to
/// <see cref="Console.Error"/> rather than throwing into framework hot paths.
/// </remarks>
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly RollingFileLoggerOptions _options;
    private readonly Channel<LogEntry> _channel;
    private readonly ConcurrentDictionary<string, RollingFileLogger> _loggers = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _writerTask;
    private readonly string _filePath;
    private FileStream? _stream;
    private long _dropped;
    private long _reportedDropped;
    private volatile bool _disposed;

    private readonly record struct LogEntry(string Line, bool ForceFlush);

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

        _channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(Math.Max(64, options.QueueCapacity))
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            _ => Interlocked.Increment(ref _dropped));
        _writerTask = Task.Run(() => DrainLoopAsync(_cts.Token));
    }

    /// <summary>Absolute path of the file this provider opened on startup.</summary>
    public string FilePath => _filePath;

    /// <summary>Number of log lines discarded because the bounded queue was full.</summary>
    public long DroppedLineCount => Interlocked.Read(ref _dropped);

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new RollingFileLogger(this, name, _options.MinimumLevel, _options.MaxLineLength));

    internal void Enqueue(string line, bool forceFlush)
    {
        if (_disposed) return;
        _channel.Writer.TryWrite(new LogEntry(line, forceFlush)); // drop-oldest on overflow (counted)
    }

    private async Task DrainLoopAsync(CancellationToken token)
    {
        var reader = _channel.Reader;
        var lastFlush = Environment.TickCount64;
        var dirty = false;
        var flushIntervalMs = Math.Max(1, _options.FlushIntervalMs);
        try
        {
            while (true)
            {
                if (reader.TryRead(out var entry))
                {
                    WriteLine(entry.Line);
                    dirty = true;
                    var now = Environment.TickCount64;
                    if (entry.ForceFlush || now - lastFlush >= flushIntervalMs)
                    {
                        FlushStream();
                        dirty = false;
                        lastFlush = now;
                    }

                    continue;
                }

                if (dirty)
                {
                    // Queue drained with unflushed writes: flush after a short grace unless new data arrives first.
                    var ready = reader.WaitToReadAsync(token).AsTask();
                    var winner = await Task.WhenAny(ready, Task.Delay(flushIntervalMs, token)).ConfigureAwait(false);
                    if (winner != ready)
                    {
                        FlushStream();
                        dirty = false;
                        lastFlush = Environment.TickCount64;
                        continue;
                    }

                    if (!await ready.ConfigureAwait(false))
                        break; // channel completed
                    continue;
                }

                if (!await reader.WaitToReadAsync(token).ConfigureAwait(false))
                    break;
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        finally
        {
            FlushStream();
        }
    }

    private void WriteLine(string line)
    {
        MaybeReportDrops();
        if (_stream is null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.WriteByte((byte)'\n');
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RollingFileLogger: write failed: {ex.Message}");
        }
    }

    /// <summary>Emit a single notice line when the dropped-line count has advanced, so overflow is visible
    /// in the log itself without one notice per drop.</summary>
    private void MaybeReportDrops()
    {
        var dropped = Interlocked.Read(ref _dropped);
        if (dropped == _reportedDropped || _stream is null)
            return;
        var delta = dropped - _reportedDropped;
        _reportedDropped = dropped;
        try
        {
            var notice = $"{DateTime.Now:HH:mm:ss.fff} WRN HaPlay.Desktop.RollingFileLogger: dropped {delta} log line(s) (queue full; total {dropped})";
            var bytes = Encoding.UTF8.GetBytes(notice);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.WriteByte((byte)'\n');
        }
        catch { /* best effort */ }
    }

    private void FlushStream()
    {
        if (_stream is null) return;
        try { _stream.Flush(); }
        catch (Exception ex) { Console.Error.WriteLine($"RollingFileLogger: flush failed: {ex.Message}"); }
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
            _channel.Writer.TryComplete();
            _writerTask.Wait(TimeSpan.FromSeconds(2)); // drains + final flush in the loop's finally
        }
        catch { /* best effort */ }
        finally
        {
            try { _stream?.Flush(); } catch { /* best effort */ }
            try { _stream?.Dispose(); }
            catch { /* best effort */ }
            _cts.Cancel();
            _cts.Dispose();
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
    /// <summary>Queue capacity before drop-oldest kicks in (LOG-01: lowered from 8192).</summary>
    public int QueueCapacity { get; init; } = 2048;
    /// <summary>Number of historical log files to keep beyond the new one — older are deleted at startup.</summary>
    public int RetainCount { get; init; } = 10;
    /// <summary>Maximum interval between disk flushes for buffered info/debug/trace lines (LOG-01: batching).
    /// Warning/error/critical lines and shutdown flush immediately regardless.</summary>
    public int FlushIntervalMs { get; init; } = 250;
    /// <summary>Upper bound on a single formatted log line; longer lines are truncated (LOG-01).</summary>
    public int MaxLineLength { get; init; } = 8192;
}

internal sealed class RollingFileLogger : ILogger
{
    private readonly RollingFileLoggerProvider _provider;
    private readonly string _category;
    private readonly LogLevel _min;
    private readonly int _maxLineLength;

    public RollingFileLogger(RollingFileLoggerProvider provider, string category, LogLevel min, int maxLineLength = 8192)
    {
        _provider = provider;
        _category = category;
        _min = min;
        _maxLineLength = Math.Max(256, maxLineLength);
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

        var line = sb.ToString();
        if (line.Length > _maxLineLength)
            line = string.Concat(line.AsSpan(0, _maxLineLength - 14), "…[truncated]");

        // Warning and above flush immediately so the last words before a crash survive; info/debug/trace batch.
        _provider.Enqueue(line, forceFlush: logLevel >= LogLevel.Warning);
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
