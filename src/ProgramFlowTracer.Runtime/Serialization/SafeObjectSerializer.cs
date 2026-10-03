using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ProgramFlowTracer.Runtime.Configuration;
using ProgramFlowTracer.Runtime.Models;

namespace ProgramFlowTracer.Runtime.Serialization;

/// <summary>
/// Turns arbitrary CLR values into <see cref="CapturedValue"/> instances without ever throwing.
/// This is the single choke point that guarantees rule #7 from the spec: a value ProgramFlowTracer
/// cannot serialize must never crash - or even visibly disturb - the traced application.
///
/// Walks the object graph itself (reflection, depth/cycle/collection-limited) instead of handing
/// the whole thing to <see cref="JsonSerializer"/> in one call. The previous implementation did the
/// latter, which meant a single unserializable property anywhere in a large graph (e.g. an
/// interop type that happens to expose a <c>ref struct</c>-returning property) failed the capture
/// of the *entire* value - losing every sibling property along with it. Here, only the specific
/// node that can't be captured is marked as failed; everything reachable around it is still
/// recorded, and the overall result is <see cref="SerializationStatus.Partial"/> rather than
/// <see cref="SerializationStatus.Failed"/>.
/// </summary>
public sealed class SafeObjectSerializer
{
    private const string ErrorKey = "$error";
    private const string ErrorTypeKey = "$errorType";
    private const string NoteKey = "$note";

    private readonly FlowTracerConfig _config;

    public SafeObjectSerializer(FlowTracerConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Redacted placeholder used for parameters/fields identified as sensitive. Never attempts to
    /// serialize the real value at all, so a sensitive value can never accidentally leak through a
    /// serializer bug or a custom <c>ToString()</c> override.
    /// </summary>
    public static CapturedValue Redacted(string? typeName) => new()
    {
        Type = typeName,
        SerializationStatus = SerializationStatus.Redacted,
        Value = "***REDACTED***"
    };

    public static CapturedValue Unavailable(string? typeName, string reason) => new()
    {
        Type = typeName,
        SerializationStatus = SerializationStatus.Unavailable,
        Error = reason
    };

    /// <summary>
    /// Attempts to capture <paramref name="value"/>. Every code path is wrapped so that any
    /// exception - out of memory pressure aside - results in a <see cref="SerializationStatus.Failed"/>
    /// record rather than a thrown exception. When part, but not all, of the graph can be captured,
    /// returns <see cref="SerializationStatus.Partial"/> with the rest inlined as normal.
    /// </summary>
    public CapturedValue Capture(object? value, Type? staticType)
    {
        var typeName = DescribeType(value, staticType);

        if (value is null)
        {
            return new CapturedValue
            {
                Type = typeName,
                SerializationStatus = SerializationStatus.Null,
                Value = null
            };
        }

        try
        {
            if (value is string s && s.Length > _config.MaxStringLength)
            {
                return new CapturedValue
                {
                    Type = typeName,
                    SerializationStatus = SerializationStatus.Truncated,
                    Value = s[.._config.MaxStringLength] + "...(truncated)"
                };
            }

            var walker = new GraphWalker(_config);
            var node = walker.Walk(value, depth: 0, visiting: new HashSet<object>(ReferenceEqualityComparer.Instance));

            // The size estimate is accumulated by the walk itself. Re-serializing the finished
            // tree just to measure it meant every captured value was traversed twice.
            var sizeEstimate = walker.EstimatedBytes;
            if (sizeEstimate > _config.MaxObjectSizeBytes)
            {
                // Still refuse to inline something this large: it would go straight into the
                // event stream and bloat every reader downstream.
                return new CapturedValue
                {
                    Type = typeName,
                    SerializationStatus = SerializationStatus.Truncated,
                    Error = $"Serialized size (~{sizeEstimate} bytes) exceeds maxObjectSizeBytes ({_config.MaxObjectSizeBytes}).",
                    ToStringFallback = SafeToString(value)
                };
            }

            if (walker.BudgetExhausted)
            {
                // Unlike the size limit, this keeps what was captured. The budget stopped the walk
                // early, so the tree is bounded by construction - a partial object is far more
                // use than the nothing this case used to yield.
                return new CapturedValue
                {
                    Type = typeName,
                    SerializationStatus = SerializationStatus.Truncated,
                    Value = node,
                    Error = $"Stopped after {walker.NodeCount} nodes (maxCapturedNodes = {_config.MaxCapturedNodes}); " +
                            "the value is larger than this and has been captured only in part.",
                    ToStringFallback = SafeToString(value)
                };
            }

            return new CapturedValue
            {
                Type = typeName,
                SerializationStatus = walker.HadAnyError
                    ? SerializationStatus.Partial
                    : SerializationStatus.Success,
                Value = node,
                Error = walker.HadAnyError
                    ? $"{walker.ErrorCount} node(s) inside this value could not be captured; see \"{ErrorKey}\" entries below."
                    : null
            };
        }
        catch (Exception ex)
        {
            return new CapturedValue
            {
                Type = typeName,
                SerializationStatus = SerializationStatus.Failed,
                Error = SafeMessage(ex),
                ErrorType = ex.GetType().FullName,
                ToStringFallback = SafeToString(value)
            };
        }
    }

    private static string? DescribeType(object? value, Type? staticType)
    {
        var runtimeType = value?.GetType();
        var type = runtimeType ?? staticType;
        return type is null ? null : FriendlyTypeName(type);
    }

    /// <summary>Produces a readable name for generic types, e.g. <c>List&lt;Int32&gt;</c> instead
    /// of the raw <c>List`1</c> reflection name.</summary>
    public static string FriendlyTypeName(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var genericArgs = string.Join(", ", type.GetGenericArguments().Select(FriendlyTypeName));
        var baseName = type.GetGenericTypeDefinition().FullName ?? type.Name;
        var backtick = baseName.IndexOf('`');
        if (backtick >= 0)
        {
            baseName = baseName[..backtick];
        }

        return $"{baseName}<{genericArgs}>";
    }

    private static string? SafeToString(object value)
    {
        try
        {
            return value.ToString();
        }
        catch (Exception ex)
        {
            return $"<ToString() threw {ex.GetType().Name}>";
        }
    }

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

    /// <summary>
    /// Does the actual recursive walk for one <see cref="Capture"/> call. Instantiated fresh per
    /// call (it carries per-call error-tracking state) but is cheap - all its real config comes
    /// from the enclosing <see cref="SafeObjectSerializer"/>'s <see cref="FlowTracerConfig"/>.
    /// </summary>
    private sealed class GraphWalker
    {
        /// <summary>Shared across every walk: property sets are immutable per type.</summary>
        private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();
        private static readonly ConcurrentDictionary<Type, bool> MaterializedCache = new();
        private static readonly ConcurrentDictionary<PropertyInfo, bool> AutoPropertyCache = new();

        private readonly FlowTracerConfig _config;

        public GraphWalker(FlowTracerConfig config)
        {
            _config = config;
        }

        public bool HadAnyError { get; private set; }

        public int ErrorCount { get; private set; }

        /// <summary>How many nodes the walk visited. Bounded by <c>maxCapturedNodes</c>.</summary>
        public int NodeCount { get; private set; }

        /// <summary>True when the walk stopped early because the node budget ran out.</summary>
        public bool BudgetExhausted { get; private set; }

        /// <summary>Running estimate of the JSON size of the tree built so far, accumulated as we
        /// go so the finished tree never has to be traversed a second time to measure it.</summary>
        public long EstimatedBytes { get; private set; }

        public object? Walk(object? value, int depth, HashSet<object> visiting)
        {
            if (value is null)
            {
                EstimatedBytes += 4;
                return null;
            }

            // Counted before anything else, so every node - leaf, collection or object - costs
            // budget. Checking only composites would let a million-element array of primitives
            // through untouched.
            if (++NodeCount > _config.MaxCapturedNodes)
            {
                BudgetExhausted = true;
                return Note($"...(stopped at maxCapturedNodes = {_config.MaxCapturedNodes})");
            }

            var type = value.GetType();

            if (TryCaptureLeaf(value, type, out var leaf))
            {
                EstimatedBytes += EstimateLeafBytes(leaf);
                return leaf;
            }

            if (depth >= _config.MaxObjectGraphDepth)
            {
                return Note($"...(max object graph depth {_config.MaxObjectGraphDepth} reached)");
            }

            // Reference-type cycle guard: only meaningful for reference types (value types can't
            // participate in a reference cycle), and scoped to the current branch (a "visiting"
            // stack, not a whole-graph "already seen" set) so the same object reachable via two
            // different, non-cyclical paths (a diamond, not a loop) is still captured both times.
            if (!type.IsValueType)
            {
                if (!visiting.Add(value))
                {
                    return Note("<circular reference>");
                }

                try
                {
                    return WalkComposite(value, type, depth, visiting);
                }
                finally
                {
                    visiting.Remove(value);
                }
            }

            return WalkComposite(value, type, depth, visiting);
        }

        private object? WalkComposite(object value, Type type, int depth, HashSet<object> visiting)
        {
            if (value is IDictionary dictionary)
            {
                return WalkDictionary(dictionary, depth, visiting);
            }

            if (value is IEnumerable enumerable)
            {
                // Only walk sequences that have already been materialised.
                //
                // Enumerating anything else *runs the user's program*: a LINQ chain or IQueryable
                // executes (firing database round trips), a `yield return` iterator runs its body
                // and whatever side effects it has, and a single-pass sequence is consumed - so the
                // caller then receives an empty one. Tracing must never do any of that, and no
                // amount of value detail is worth it, so deferred sequences are described rather
                // than read.
                if (!IsMaterialized(value))
                {
                    AddStructureBytes(48);
                    return Note($"<{FriendlyTypeName(type)}: deferred sequence, not enumerated>");
                }

                return WalkEnumerable(enumerable, depth, visiting);
            }

            return WalkObject(value, type, depth, visiting);
        }

        private object WalkDictionary(IDictionary dictionary, int depth, HashSet<object> visiting)
        {
            var result = new Dictionary<string, object?>();
            var count = 0;

            foreach (DictionaryEntry entry in dictionary)
            {
                if (BudgetExhausted) { break; }
                if (count >= _config.MaxCollectionItems)
                {
                    result["..."] = Note($"(truncated at {_config.MaxCollectionItems} items)");
                    break;
                }

                var key = entry.Key?.ToString() ?? "null";
                AddStructureBytes(key.Length + 4L);
                try
                {
                    result[key] = Walk(entry.Value, depth + 1, visiting);
                }
                catch (Exception ex)
                {
                    result[key] = Error(ex);
                }

                count++;
            }

            return result;
        }

        /// <summary>
        /// Whether a sequence is already in memory, so that reading it cannot run user code or
        /// consume anything. Arrays and the BCL collections all carry a count - <see
        /// cref="ICollection"/>, <c>ICollection&lt;T&gt;</c> or <c>IReadOnlyCollection&lt;T&gt;</c>
        /// - whereas iterator state machines, LINQ operators and <c>IQueryable</c> carry none,
        /// which is exactly the distinction that matters here.
        /// </summary>
        private static bool IsMaterialized(object value)
        {
            if (value is ICollection || value is Array)
            {
                return true;
            }

            return MaterializedCache.GetOrAdd(value.GetType(), static t =>
                t.GetInterfaces().Any(i => i.IsGenericType &&
                    (i.GetGenericTypeDefinition() == typeof(ICollection<>) ||
                     i.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>))));
        }

        private object WalkEnumerable(IEnumerable enumerable, int depth, HashSet<object> visiting)
        {
            var result = new List<object?>();
            var count = 0;

            IEnumerator? enumerator = null;
            try
            {
                enumerator = enumerable.GetEnumerator();
            }
            catch (Exception ex)
            {
                return Error(ex);
            }

            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = enumerator.MoveNext();
                    }
                    catch (Exception ex)
                    {
                        result.Add(Error(ex));
                        break;
                    }

                    if (!moved)
                    {
                        break;
                    }

                    if (count >= _config.MaxCollectionItems)
                    {
                        result.Add(Note($"...(truncated at {_config.MaxCollectionItems} items)"));
                        break;
                    }

                    // Stop pulling from the sequence the moment the budget is gone: enumerating a
                    // lazy sequence can itself be expensive, so there is no point walking it just
                    // to discard every element.
                    if (BudgetExhausted) { break; }
                    AddStructureBytes(1);

                    try
                    {
                        result.Add(Walk(enumerator.Current, depth + 1, visiting));
                    }
                    catch (Exception ex)
                    {
                        result.Add(Error(ex));
                    }

                    count++;
                }
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }

            return result;
        }

        private object WalkObject(object value, Type type, int depth, HashSet<object> visiting)
        {
            var result = new Dictionary<string, object?>();

            foreach (var property in GetCapturableProperties(type))
            {
                if (BudgetExhausted) { break; }
                AddStructureBytes(property.Name.Length + 4L);

                if (property.PropertyType.IsByRefLike || property.PropertyType.IsPointer)
                {
                    // Never even attempt to invoke these - reflection cannot box a ref struct or
                    // pointer return value at all, so trying is guaranteed to throw. This is
                    // exactly the shape of the Encoding.Preamble (ReadOnlySpan<byte>) case: skip
                    // it immediately instead of paying for a doomed reflection call and losing the
                    // rest of the object when it throws.
                    result[property.Name] = Note($"<{FriendlyTypeName(property.PropertyType)}: ref struct/pointer, not capturable>");
                    continue;
                }

                if (!_config.CaptureComputedProperties && !IsAutoProperty(property))
                {
                    // Reading this would run user code - see CaptureComputedProperties.
                    result[property.Name] = Note("<computed property, not read>");
                    continue;
                }

                object? propertyValue;
                try
                {
                    propertyValue = property.GetValue(value);
                }
                catch (Exception ex)
                {
                    result[property.Name] = Error(ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
                    continue;
                }

                try
                {
                    result[property.Name] = Walk(propertyValue, depth + 1, visiting);
                }
                catch (Exception ex)
                {
                    result[property.Name] = Error(ex);
                }
            }

            return result;
        }

        /// <summary>
        /// The readable instance properties of a type, resolved once and reused.
        /// <para>
        /// This used to run <c>GetProperties</c> plus a LINQ filter for every <em>instance</em>
        /// walked, so capturing a hundred-element list of one type did the same reflection query a
        /// hundred times and allocated a hundred iterators. The set can never change for a given
        /// <see cref="Type"/>, so it is cached for the process lifetime.
        /// </para>
        /// </summary>
        /// <summary>
        /// Whether a property is a plain auto-property, so reading it only returns a field.
        /// <para>
        /// The C# compiler names an auto-property's backing field <c>&lt;Name&gt;k__BackingField</c>
        /// and declares it on the same type as the property, which makes the check exact rather
        /// than a guess. Anything else - an expression-bodied property, a lazy loader, a navigation
        /// property on an ORM proxy - runs code, and is left alone.
        /// </para>
        /// </summary>
        private static bool IsAutoProperty(PropertyInfo property) =>
            AutoPropertyCache.GetOrAdd(property, static p =>
                p.DeclaringType?.GetField(
                    $"<{p.Name}>k__BackingField",
                    BindingFlags.NonPublic | BindingFlags.Instance) is not null);

        private static PropertyInfo[] GetCapturableProperties(Type type) =>
            PropertyCache.GetOrAdd(type, static t => t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .ToArray());

        /// <summary>Rough JSON byte cost of one captured leaf. Deliberately approximate - it only
        /// has to decide whether a value is near the size limit, not report it exactly.</summary>
        private static long EstimateLeafBytes(object? leaf) => leaf switch
        {
            null => 4,
            string s => s.Length + 2L,
            bool => 5,
            _ => 12
        };

        /// <summary>Structural cost of a JSON object/array: braces, commas, and the key text.</summary>
        private void AddStructureBytes(long bytes) => EstimatedBytes += bytes;

        private static bool TryCaptureLeaf(object value, Type type, out object? leaf)
        {
            switch (value)
            {
                case string s:
                    leaf = s;
                    return true;
                case bool b:
                    leaf = b;
                    return true;
                case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    leaf = value;
                    return true;
                case char c:
                    leaf = c.ToString();
                    return true;
                case Guid g:
                    leaf = g.ToString();
                    return true;
                case DateTime dt:
                    leaf = dt.ToString("O");
                    return true;
                case DateTimeOffset dto:
                    leaf = dto.ToString("O");
                    return true;
                case TimeSpan ts:
                    leaf = ts.ToString();
                    return true;
                case Enum:
                    leaf = value.ToString();
                    return true;
                case Type t:
                    // The reflection metadata graph reachable from a live Type object is enormous
                    // and not meaningful to trace consumers - always represent it as its name.
                    leaf = t.FullName ?? t.Name;
                    return true;
            }

            if (type.IsPrimitive)
            {
                leaf = value;
                return true;
            }

            leaf = null;
            return false;
        }

        private object Error(Exception ex)
        {
            HadAnyError = true;
            ErrorCount++;
            return new Dictionary<string, object?>
            {
                [ErrorKey] = SafeMessage(ex),
                [ErrorTypeKey] = ex.GetType().FullName
            };
        }

        private static object Note(string text) => new Dictionary<string, object?> { [NoteKey] = text };

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
    }
}
