using System.Runtime.CompilerServices;
using System.Diagnostics;
using ProgramFlowTracer.Runtime.Configuration;
using ProgramFlowTracer.Runtime.Context;
using ProgramFlowTracer.Runtime.Models;
using ProgramFlowTracer.Runtime.Serialization;
using ProgramFlowTracer.Runtime.Writing;

namespace ProgramFlowTracer.Runtime;

/// <summary>
/// Entry point instrumented code calls into. This is the only ProgramFlowTracer type an
/// instrumented application needs to know about - everything else in this assembly is
/// implementation detail reachable transitively but not meant to be called directly.
///
/// Every public method here is designed to never throw and to be cheap when tracing is disabled,
/// so that instrumentation can never change the observable behavior (beyond timing) of the traced
/// program. See rule #7 ("fail-safe tracing") and rule #19 ("performance") in the spec this was
/// built against.
/// </summary>
public static class FlowTracer
{
    private static FlowTracerConfig _config = FlowTracerConfig.Default;
    private static SafeObjectSerializer _serializer = new(FlowTracerConfig.Default);
    private static FileSystemEventWriter? _writer;
    private static string _runId = string.Empty;
    private static readonly object InitLock = new();
    private static volatile bool _initialized;
    private static readonly Random SamplingRandom = new();

    public static bool IsEnabled => _initialized && _config.Enabled && _writer is not null;

    public static string RunId => _runId;

    /// <summary>
    /// Sets up the tracer for this process. Safe to call multiple times (subsequent calls are
    /// ignored) and safe to never call at all - <see cref="Enter"/> lazily initializes with
    /// defaults discovered from the current directory if instrumented code runs before this is
    /// called explicitly (which the generated bootstrap normally prevents).
    /// </summary>
    public static void Initialize(FlowTracerConfig? config = null, string? applicationName = null, string? baseDirectory = null)
    {
        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            // Declared outside the try so the catch block can still describe what was attempted
            // even if resolution of the config itself is what went wrong.
            var resolvedConfig = config ?? FlowTracerConfig.Default;

            try
            {
                resolvedConfig = config ?? FlowTracerConfig.LoadFromNearestOrDefault(baseDirectory ?? Directory.GetCurrentDirectory());
                _config = resolvedConfig;
                _serializer = new SafeObjectSerializer(resolvedConfig);

                if (!resolvedConfig.Enabled)
                {
                    _initialized = true;
                    return;
                }

                _runId = Guid.NewGuid().ToString();
                var appName = applicationName ?? AppDomain.CurrentDomain.FriendlyName;
                var commandLine = SafeCommandLine();

                _writer = FileSystemEventWriter.Start(resolvedConfig.OutputDirectory, _runId, appName, commandLine, resolvedConfig);
                _initialized = true;

                AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownSync();
            }
            catch (Exception ex)
            {
                // If initialization fails for any reason (e.g. cannot create output directory),
                // tracing is simply off for this run - the traced application must still start.
                // But silently vanishing here is exactly what makes "there's no .flowtrace folder"
                // reports impossible to diagnose, so make a best-effort attempt to leave a record
                // of what happened before giving up.
                _writer = null;
                _initialized = true;
                TryWriteInitDiagnostics(resolvedConfig, baseDirectory, ex);
            }
        }
    }

    /// <summary>
    /// Best-effort diagnostics for a failed <see cref="Initialize"/> call. Writes a small report
    /// to two places: alongside the trace output directory that was supposed to be created (so
    /// it's easy to find if that directory could at least be created), and to a fixed, well-known
    /// location under the OS temp directory that doesn't depend on anything about the traced
    /// application's own directories (so there's still somewhere to look if the failure was the
    /// output directory itself being uncreatable). Both writes are best-effort; this method can
    /// never throw, and never blocks/affects the traced application beyond the two file writes.
    /// </summary>
    private static void TryWriteInitDiagnostics(FlowTracerConfig config, string? baseDirectory, Exception ex)
    {
        try
        {
            var report = BuildDiagnosticsReport(config, baseDirectory, ex);

            var primaryPath = Path.Combine(config.OutputDirectory, "init-error.log");
            var primaryWritten = TryWriteReport(primaryPath, report);

            var fallbackPath = Path.Combine(Path.GetTempPath(), "ProgramFlowTracer", "last-init-error.log");
            TryWriteReport(fallbackPath, report);

            if (IsDebugEnabled())
            {
                Console.Error.WriteLine(report);
            }
            else if (!primaryWritten)
            {
                Console.Error.WriteLine($"[ProgramFlowTracer] tracing failed to initialize for this run; see '{fallbackPath}' (set PROGRAMFLOWTRACER_DEBUG=1 to print this immediately instead).");
            }
        }
        catch
        {
            // Diagnostics must never be able to affect the traced application either.
        }
    }

    private static bool TryWriteReport(string path, string report)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, report);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildDiagnosticsReport(FlowTracerConfig config, string? baseDirectory, Exception ex)
    {
        string resolvedOutputDir;
        try
        {
            resolvedOutputDir = Path.GetFullPath(config.OutputDirectory);
        }
        catch
        {
            resolvedOutputDir = config.OutputDirectory;
        }

        return
            "ProgramFlowTracer failed to initialize tracing for this process.\n" +
            $"Time (UTC):          {DateTime.UtcNow:O}\n" +
            $"Command line:        {SafeCommandLine()}\n" +
            $"Current directory:   {SafeCurrentDirectory()}\n" +
            $"Base directory arg:  {baseDirectory ?? "(not provided)"}\n" +
            $"AppDomain base dir:  {SafeAppBaseDirectory()}\n" +
            $"Configured output:   {config.OutputDirectory}\n" +
            $"Resolved output dir: {resolvedOutputDir}\n" +
            $"Exception type:      {ex.GetType().FullName}\n" +
            $"Exception message:   {ex.Message}\n" +
            $"Stack trace:\n{ex}\n\n" +
            "Common causes: the resolved output directory above doesn't exist and couldn't be " +
            "created (check antivirus/EDR policies that block new folders under this path), the " +
            "path is too long for the OS (common with deeply nested repo directories on Windows - " +
            "the .flowtrace/runs/{runId}/objects/{objectId}.json paths underneath it add over 80 " +
            "characters on top of this one), or the process lacks permission to write here. Set " +
            "the PROGRAMFLOWTRACER_DEBUG=1 environment variable before running the traced app to " +
            "print this report to its stderr as soon as it happens, instead of only to this file.";
    }

    private static bool IsDebugEnabled()
    {
        try
        {
            var val = Environment.GetEnvironmentVariable("PROGRAMFLOWTRACER_DEBUG");
            return !string.IsNullOrEmpty(val) && val != "0" && !val.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string SafeCurrentDirectory()
    {
        try
        {
            return Directory.GetCurrentDirectory();
        }
        catch
        {
            return "(unavailable)";
        }
    }

    private static string SafeAppBaseDirectory()
    {
        try
        {
            return AppDomain.CurrentDomain.BaseDirectory;
        }
        catch
        {
            return "(unavailable)";
        }
    }

    private static void EnsureInitialized()
    {
        if (!_initialized)
        {
            Initialize();
        }
    }

    private static string? SafeCommandLine()
    {
        try
        {
            return Environment.CommandLine;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Records that a method was entered, and nothing else.
    /// <para>
    /// Deliberately returns <c>void</c> and pushes nothing onto the call chain, so the injected
    /// code is a single statement: no local to declare, and no <c>finally</c> needed to balance
    /// it. That is the whole point of entry-only instrumentation - the traced method keeps its
    /// original control flow exactly, because nothing wraps it.
    /// </para>
    /// <para>
    /// The trade is that the trace is a flat, ordered list of entries rather than a call tree:
    /// with no scope pushed there is no parent to attribute the next call to. Buffered events are
    /// still flushed by the <see cref="AppDomain.ProcessExit"/> hook registered in
    /// <see cref="Initialize"/>.
    /// </para>
    /// </summary>
    public static void EnterOnly(
        string methodName,
        string declaringType,
        string? filePath,
        int? line,
        int? column,
        FlowTraceParameter[]? parameters)
    {
        try
        {
            EnsureInitialized();

            if (!IsEnabled)
            {
                return;
            }

            if (_config.SamplingRate < 1.0 && SamplingRandom.NextDouble() > _config.SamplingRate)
            {
                return;
            }

            var traceId = Guid.NewGuid();

            var evt = new TraceEvent
            {
                EventType = Models.TraceEventType.MethodEnter,
                RunId = _runId,
                TraceId = traceId.ToString(),
                ParentTraceId = TraceScope.CurrentContext?.TraceId.ToString(),
                Method = methodName,
                DeclaringType = declaringType,
                File = filePath,
                Line = line,
                Column = column,
                TimestampUtc = DateTime.UtcNow.ToString("O")
            };

            ApplyThreadInfo(evt);

            if (_config.CaptureParameters && parameters is { Length: > 0 })
            {
                evt.Parameters = CaptureParameters(traceId, parameters);
            }

            _writer!.TryEnqueue(evt);
        }
        catch
        {
            // Recording must never prevent the real method body from running.
        }
    }

    /// <summary>
    /// Records a method-enter event and pushes a new node onto the logical call chain (see
    /// <see cref="TraceScope"/>). Must be paired with exactly one call to <see cref="Leave"/> in a
    /// <c>finally</c> block, regardless of whether <see cref="Exit"/>/<see cref="ExitVoid"/> or
    /// <see cref="Exception"/> was also called.
    /// </summary>
    public static FlowTraceCall Enter(
        string methodName,
        string declaringType,
        string? filePath,
        int? line,
        int? column,
        FlowTraceParameter[]? parameters)
    {
        try
        {
            EnsureInitialized();

            if (!IsEnabled)
            {
                return FlowTraceCall.Disabled;
            }

            if (_config.SamplingRate < 1.0 && SamplingRandom.NextDouble() > _config.SamplingRate)
            {
                return FlowTraceCall.Disabled;
            }

            var traceId = Guid.NewGuid();
            var pushed = TraceScope.Push(traceId);

            var evt = new TraceEvent
            {
                EventType = Models.TraceEventType.MethodEnter,
                RunId = _runId,
                TraceId = traceId.ToString(),
                ParentTraceId = pushed.ParentTraceId?.ToString(),
                Method = methodName,
                DeclaringType = declaringType,
                File = filePath,
                Line = line,
                Column = column,
                TimestampUtc = DateTime.UtcNow.ToString("O")
            };

            ApplyThreadInfo(evt);

            if (_config.CaptureParameters && parameters is { Length: > 0 })
            {
                evt.Parameters = CaptureParameters(traceId, parameters);
            }

            _writer!.TryEnqueue(evt);

            // The self-time clock starts here, right before control returns to the instrumented
            // method body - not before parameter capture above. Capture (JSON-serializing
            // arguments, including ones that ultimately fail) can take real, sometimes large,
            // amounts of time, and none of that is the traced method's own execution - starting
            // the clock earlier misattributed all of it as "self" time on the call.
            var call = new FlowTraceCall(true, traceId, pushed, Stopwatch.GetTimestamp(), methodName, declaringType, filePath, line, column);
            return call;
        }
        catch
        {
            // Entering trace bookkeeping must never prevent the real method body from running.
            return FlowTraceCall.Disabled;
        }
    }

    /// <summary>Records a method-exit event with a captured return value.</summary>
    public static void Exit(in FlowTraceCall call, object? returnValue, Type? returnType, FlowTraceParameter[]? outParameters = null)
    {
        if (!call.Enabled)
        {
            return;
        }

        try
        {
            var evt = BuildExitEvent(call);
            if (_config.CaptureReturnValues)
            {
                evt.ReturnValue = _serializer.Capture(returnValue, returnType);
                SpillIfLarge(evt.ReturnValue, call.TraceId, "$return");
            }

            if (outParameters is { Length: > 0 })
            {
                evt.OutParameters = CaptureParameters(call.TraceId, outParameters);
            }

            _writer!.TryEnqueue(evt);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Records a method-exit event for a <c>void</c>/<c>Task</c>-returning method.</summary>
    public static void ExitVoid(in FlowTraceCall call, FlowTraceParameter[]? outParameters = null)
    {
        if (!call.Enabled)
        {
            return;
        }

        try
        {
            var evt = BuildExitEvent(call);
            if (outParameters is { Length: > 0 })
            {
                evt.OutParameters = CaptureParameters(call.TraceId, outParameters);
            }

            _writer!.TryEnqueue(evt);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// Records a propagating exception from an <c>when (...)</c> exception filter, and always
    /// returns <see langword="false"/> so the exception is never caught.
    /// <para>
    /// This exists so instrumentation can observe exceptions without changing how they behave.
    /// A <c>catch (Exception) { record; throw; }</c> makes the method a real handler: the runtime
    /// ends its first pass there and unwinds, so any <c>catch ... when (filter)</c> further up the
    /// stack runs its filter <em>after</em> inner <c>finally</c> blocks instead of before, and the
    /// rethrow makes the exception look handled to the debugger. A filter that returns false is
    /// invisible - the first pass simply continues to the next frame, exactly as if the traced
    /// method had no handler at all.
    /// </para>
    /// <para>
    /// Never throws: an exception escaping a filter is swallowed by the runtime and treated as
    /// <see langword="false"/>, which would silently lose the trace event.
    /// </para>
    /// </summary>
    public static bool ObserveException(in FlowTraceCall call, Exception exception)
    {
        Exception(call, exception);
        return false;
    }

    /// <summary>Records that <paramref name="exception"/> is propagating out of the traced method.
    /// The instrumented code must always rethrow the original exception unchanged after calling
    /// this - ProgramFlowTracer never swallows exceptions.</summary>
    public static void Exception(in FlowTraceCall call, Exception exception)
    {
        if (!call.Enabled)
        {
            return;
        }

        try
        {
            if (!_config.CaptureExceptions)
            {
                return;
            }

            var evt = new TraceEvent
            {
                EventType = Models.TraceEventType.Exception,
                RunId = _runId,
                TraceId = call.TraceId.ToString(),
                ParentTraceId = call.PushedContext?.ParentTraceId?.ToString(),
                Method = call.MethodName,
                DeclaringType = call.DeclaringType,
                File = call.File,
                Line = call.Line,
                Column = call.Column,
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                ExceptionType = exception.GetType().FullName,
                Message = SafeMessage(exception),
                StackTrace = SafeStackTrace(exception)
            };

            ApplyThreadInfo(evt);
            _writer!.TryEnqueue(evt);
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>Restores the logical call chain to what it was before <see cref="Enter"/> pushed
    /// this call. Must be called exactly once per <see cref="Enter"/>, from a <c>finally</c> block,
    /// so the parent/child relationship stays correct even when exceptions unwind past several
    /// nested traced calls at once.</summary>
    public static void Leave(in FlowTraceCall call)
    {
        if (!call.Enabled || call.PushedContext is null)
        {
            return;
        }

        try
        {
            TraceScope.Restore(call.PushedContext);
        }
        catch
        {
            // best-effort
        }
    }

    private static TraceEvent BuildExitEvent(in FlowTraceCall call)
    {
        var elapsedTicks = Stopwatch.GetTimestamp() - call.StartTimestamp;
        var elapsedMicroseconds = elapsedTicks * 1_000_000.0 / Stopwatch.Frequency;

        var evt = new TraceEvent
        {
            EventType = Models.TraceEventType.MethodExit,
            RunId = _runId,
            TraceId = call.TraceId.ToString(),
            ParentTraceId = call.PushedContext?.ParentTraceId?.ToString(),
            Method = call.MethodName,
            DeclaringType = call.DeclaringType,
            File = call.File,
            Line = call.Line,
            Column = call.Column,
            TimestampUtc = DateTime.UtcNow.ToString("O"),
            DurationMicroseconds = elapsedMicroseconds
        };

        ApplyThreadInfo(evt);
        return evt;
    }

    private static void ApplyThreadInfo(TraceEvent evt)
    {
        if (!_config.CaptureThreadInfo)
        {
            return;
        }

        try
        {
            evt.ThreadId = Environment.CurrentManagedThreadId;
            evt.TaskId = Task.CurrentId;
            evt.IsThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
        }
        catch
        {
            // best-effort
        }
    }

    private static Dictionary<string, CapturedValue> CaptureParameters(Guid traceId, FlowTraceParameter[] parameters)
    {
        var result = new Dictionary<string, CapturedValue>(parameters.Length);
        foreach (var p in parameters)
        {
            CapturedValue captured;
            if (!p.IsAvailable)
            {
                captured = SafeObjectSerializer.Unavailable(
                    p.StaticType is null ? null : SafeObjectSerializer.FriendlyTypeName(p.StaticType),
                    "Value not available at this point in execution.");
            }
            else if (p.IsSensitive)
            {
                captured = SafeObjectSerializer.Redacted(
                    p.Value?.GetType() is { } rt ? SafeObjectSerializer.FriendlyTypeName(rt) : (p.StaticType is null ? null : SafeObjectSerializer.FriendlyTypeName(p.StaticType)));
            }
            else
            {
                captured = _serializer.Capture(p.Value, p.StaticType);
                SpillIfLarge(captured, traceId, p.Name);
            }

            result[p.Name] = captured;
        }

        return result;
    }

    /// <summary>
    /// When a captured value's inline JSON representation would be large, this replaces its
    /// inline <see cref="CapturedValue.Value"/> with an <see cref="ObjectRecord"/> reference
    /// (rule #8 in the spec: "don't put huge parameter objects directly into the main execution
    /// JSON"). Small values stay inlined for readability/locality.
    /// </summary>
    private static void SpillIfLarge(CapturedValue captured, Guid traceId, string parameterName)
    {
        if ((captured.SerializationStatus != SerializationStatus.Success &&
             captured.SerializationStatus != SerializationStatus.Partial) ||
            captured.Value is null)
        {
            return;
        }

        var approxSize = EstimateSize(captured.Value);
        if (approxSize < _config.InlineObjectThresholdBytes)
        {
            return;
        }

        var objectId = Guid.NewGuid().ToString();
        var record = new ObjectRecord
        {
            ObjectId = objectId,
            RunId = _runId,
            TraceId = traceId.ToString(),
            ParameterName = parameterName,
            Type = captured.Type,
            SerializationStatus = captured.SerializationStatus,
            Value = captured.Value
        };

        _writer!.TryEnqueueObject(record);

        captured.ObjectId = objectId;
        captured.Value = null;
    }

    private static long EstimateSize(object value) => value switch
    {
        string s => s.Length,
        System.Collections.ICollection c => c.Count * 32L,
        _ => 64L
    };

    private static string SafeMessage(Exception ex)
    {
        try
        {
            return ex.Message;
        }
        catch
        {
            return "<no message available>";
        }
    }

    private static string? SafeStackTrace(Exception ex)
    {
        try
        {
            return ex.StackTrace;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Flushes and finalizes the trace. Instrumented <c>Main</c> methods call this (via the
    /// generated bootstrap) before the process exits so the last batch of buffered events is not
    /// lost. Also wired up to <see cref="AppDomain.ProcessExit"/> as a safety net.
    /// </summary>
    public static async Task ShutdownAsync(TimeSpan? timeout = null)
    {
        var writer = _writer;
        if (writer is null)
        {
            return;
        }

        try
        {
            await writer.FlushAndCompleteAsync(timeout ?? TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }

    private static void ShutdownSync()
    {
        try
        {
            ShutdownAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        }
        catch
        {
            // best-effort
        }
    }

    /// <summary>
    /// Resets all static state so a fresh <see cref="Initialize"/> call takes effect. Not for
    /// production use - <see cref="FlowTracer"/> is deliberately a single process-wide singleton
    /// there. This exists solely so in-process test suites (which share one process, and
    /// therefore one static <see cref="FlowTracer"/>, across many independent test cases) can get
    /// isolated tracer state per test.
    /// </summary>
    internal static void ResetForTesting()
    {
        lock (InitLock)
        {
            _initialized = false;
            _writer = null;
            _config = FlowTracerConfig.Default;
            _serializer = new SafeObjectSerializer(FlowTracerConfig.Default);
            _runId = string.Empty;
        }
    }
}
