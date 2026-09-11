namespace ProgramFlowTracer.Core.Rewriting;

/// <summary>
/// Hands out identifier names for injected locals (call tokens, exception variables, captured
/// return values). One instance is shared across an entire source file so that names never
/// collide, no matter how deeply members are nested (outer method, nested local function, nested
/// local function within that, etc.) - simpler and safer than reasoning about C#'s scoping rules
/// for shadowing across local-function boundaries.
///
/// Names also carry a random per-instance salt, not just the counter. The counter alone is only
/// collision-free if this exact file's syntax tree is walked by exactly one
/// <see cref="UniqueNameGenerator"/> for its entire lifetime; a per-instance salt means that even
/// if two separate instances ever end up injecting into the same physical scope (e.g. a shared/
/// linked source file processed by more than one project, or a future rewriter change that
/// revisits a subtree), their generated names still can't collide with each other - the failure
/// mode becomes visibly wrong instrumentation, not an opaque CS0136 the person then has to
/// diagnose from a raw compiler error.
/// </summary>
internal sealed class UniqueNameGenerator
{
    private readonly string _salt = Guid.NewGuid().ToString("N")[..8];
    private int _counter;

    public string NextCallToken() => $"__ftCall_{_salt}_{_counter++}";

    public string NextExceptionVar() => $"__ftEx_{_salt}_{_counter++}";

    public string NextReturnVar() => $"__ftRet_{_salt}_{_counter++}";
}
