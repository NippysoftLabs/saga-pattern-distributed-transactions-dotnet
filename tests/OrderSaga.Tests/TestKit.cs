using OrderSaga.Orchestration;
using OrderSaga.Services;

namespace OrderSaga.Tests;

internal static class TestKit
{
    public static readonly OrderRequest Mug = new("order-1001", "mug", 2, 30m);

    public static Shop NewShop(int mugsInStock = 5) => new(new Dictionary<string, int> { ["mug"] = mugsInStock });

    /// <summary>Three attempts, waits recorded but not slept.</summary>
    public static RetryPolicy Retry(int maxAttempts = 3) => new(maxAttempts, TimeSpan.FromSeconds(1), _ => Task.CompletedTask);

    public static SagaOrchestrator Orchestrator(Shop shop, ISagaLog log, RetryPolicy? retry = null,
        List<SagaAlert>? alerts = null, TimeProvider? clock = null) =>
        new(log, OrderSagaSteps.For(shop), retry ?? Retry(), clock, a => alerts?.Add(a));

    public static List<string> Trail(ISagaLog log, string sagaId) =>
        log.Read(sagaId).Select(e => e.Step is null ? e.Type.ToString() : $"{e.Type} {e.Step}").ToList();
}

/// <summary>A log that dies on the n-th write, like a process killed in the middle of the saga.</summary>
internal sealed class CrashingLog(ISagaLog inner, int crashOnWrite) : ISagaLog
{
    private int _writes;

    public void Append(SagaEvent e)
    {
        if (++_writes == crashOnWrite)
            throw new IOException("process killed");
        inner.Append(e);
    }

    public IReadOnlyList<SagaEvent> Read(string sagaId) => inner.Read(sagaId);
    public IReadOnlyList<string> SagaIds() => inner.SagaIds();
}

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
}
