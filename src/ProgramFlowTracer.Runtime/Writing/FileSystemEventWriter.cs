using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ProgramFlowTracer.Runtime.Configuration;
using ProgramFlowTracer.Runtime.Models;

namespace ProgramFlowTracer.Runtime.Writing;

/// <summary>
/// Persists trace events and spilled object records to disk on a single dedicated background
/// task, reading from a bounded <see cref="Channel{T}"/>. This keeps concurrent writes from
/// multiple traced threads/tasks safe (only the background task ever touches the files) and keeps
/// tracing overhead on the hot path down to "serialize + enqueue".
///
/// If the queue is full, or if any I/O error occurs, events are silently dropped (and counted) -
/// tracing must never apply backpressure to, or throw out of, the traced application.
/// </summary>
public sealed class FileSystemEventWriter : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly Channel<TraceQueueItem> _channel;
    private readonly Task _pump;
    private readonly string _runDirectory;
    private readonly string _eventsPath;
    private readonly string _objectsDirectory;
    private readonly string _runJsonPath;
    private readonly RunMetadata _metadata;
    private long _eventCount;
    private long _droppedCount;
    private readonly CancellationTokenSource _cts = new();

    private FileSystemEventWriter(string runDirectory, RunMetadata metadata, int capacity)
    {
        _runDirectory = runDirectory;
        _eventsPath = Path.Combine(runDirectory, "events.jsonl");
        _objectsDirectory = Path.Combine(runDirectory, "objects");
        _runJsonPath = Path.Combine(runDirectory, "run.json");
        _metadata = metadata;

        _channel = Channel.CreateBounded<TraceQueueItem>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

        _pump = Task.Run(PumpAsync);
    }

    public long EventCount => Interlocked.Read(ref _eventCount);

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public string RunDirectory => _runDirectory;

    /// <summary>
    /// Creates the run directory structure (<c>run.json</c>, <c>events.jsonl</c>, <c>objects/</c>)
    /// under <paramref name="outputDirectory"/>/runs/{runId} and starts the background writer.
    /// </summary>
    public static FileSystemEventWriter Start(string outputDirectory, string runId, string applicationName, string? commandLine, FlowTracerConfig config)
    {
        var runDirectory = Path.Combine(outputDirectory, "runs", runId);
        Directory.CreateDirectory(runDirectory);
        Directory.CreateDirectory(Path.Combine(runDirectory, "objects"));

        var metadata = new RunMetadata
        {
            RunId = runId,
            Application = applicationName,
            CommandLine = commandLine,
            StartedAtUtc = DateTime.UtcNow.ToString("O")
        };

        var writer = new FileSystemEventWriter(runDirectory, metadata, config.WriterQueueCapacity);
        writer.WriteRunMetadata();
        return writer;
    }

    public bool TryEnqueue(TraceEvent traceEvent)
    {
        Interlocked.Increment(ref _eventCount);
        if (_channel.Writer.TryWrite(new TraceQueueItem.Event { Value = traceEvent }))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    public bool TryEnqueueObject(ObjectRecord record)
    {
        if (_channel.Writer.TryWrite(new TraceQueueItem.Object { Value = record }))
        {
            return true;
        }

        Interlocked.Increment(ref _droppedCount);
        return false;
    }

    private async Task PumpAsync()
    {
        StreamWriter? eventWriter = null;
        try
        {
            eventWriter = new StreamWriter(new FileStream(_eventsPath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 64 * 1024, useAsync: true), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = false
            };

            var sinceFlush = 0;
            await foreach (var item in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    switch (item)
                    {
                        case TraceQueueItem.Event e:
                            var line = JsonSerializer.Serialize(e.Value, JsonOptions);
                            await eventWriter.WriteLineAsync(line).ConfigureAwait(false);
                            break;
                        case TraceQueueItem.Object o:
                            await WriteObjectRecordAsync(o.Value).ConfigureAwait(false);
                            break;
                    }
                }
                catch
                {
                    // A single bad record must never take down the writer loop.
                    Interlocked.Increment(ref _droppedCount);
                }

                sinceFlush++;
                if (sinceFlush >= 50)
                {
                    await eventWriter.FlushAsync().ConfigureAwait(false);
                    sinceFlush = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown drain.
        }
        catch
        {
            // Never let a writer-loop fault escape onto an unobserved task exception.
        }
        finally
        {
            if (eventWriter is not null)
            {
                try
                {
                    await eventWriter.FlushAsync().ConfigureAwait(false);
                    await eventWriter.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // best-effort
                }
            }
        }
    }

    private async Task WriteObjectRecordAsync(ObjectRecord record)
    {
        var path = Path.Combine(_objectsDirectory, record.ObjectId + ".json");
        var json = JsonSerializer.Serialize(record, JsonOptions);
        await File.WriteAllTextAsync(path, json).ConfigureAwait(false);
    }

    private void WriteRunMetadata()
    {
        try
        {
            var json = JsonSerializer.Serialize(_metadata, IndentedJsonOptions);
            File.WriteAllText(_runJsonPath, json);
        }
        catch
        {
            // best-effort; run.json is metadata, not the event stream itself.
        }
    }

    /// <summary>
    /// Signals no more items will be enqueued, drains whatever is already queued, and finalizes
    /// <c>run.json</c> with the end time and final counters. Bounded by
    /// <paramref name="timeout"/> so a stuck disk can't hang process shutdown forever.
    /// </summary>
    public async Task FlushAndCompleteAsync(TimeSpan timeout)
    {
        _channel.Writer.TryComplete();

        var completed = await Task.WhenAny(_pump, Task.Delay(timeout)).ConfigureAwait(false);
        if (completed != _pump)
        {
            _cts.Cancel();
        }

        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch
        {
            // already logged/dropped internally
        }

        _metadata.EndedAtUtc = DateTime.UtcNow.ToString("O");
        _metadata.EventCount = EventCount;
        _metadata.DroppedEventCount = DroppedCount;
        WriteRunMetadata();
    }

    public async ValueTask DisposeAsync()
    {
        await FlushAndCompleteAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _cts.Dispose();
    }
}
