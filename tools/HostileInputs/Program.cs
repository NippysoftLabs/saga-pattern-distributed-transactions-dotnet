using System.Text.Json;
using OrderSaga;
using OrderSaga.Orchestration;
using OrderSaga.Services;

// Feeds the saga inputs a careless or hostile caller could send, and prints what comes out.
var good = new OrderRequest("order-1001", "mug", 2, 30m);
var n = 0;

Shop NewShop(int mugs = 5) => new(new Dictionary<string, int> { ["mug"] = mugs });
RetryPolicy Retry() => new(3, TimeSpan.FromSeconds(1), _ => Task.CompletedTask);
SagaOrchestrator Saga(Shop shop, ISagaLog log) => new(log, OrderSagaSteps.For(shop), Retry());

async Task Case(string input, Func<Task<string>> run)
{
    string result;
    try { result = await run(); }
    catch (Exception ex) { result = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}"; }
    Console.WriteLine($"{++n,2}. {input}\n    -> {result}");
}

string State(Shop s, SagaOutcome o) =>
    $"{o.Status}{(o.Reason is null ? "" : $" ({o.Reason})")} | held {s.Payments.Captured} | charges {s.Payments.ChargesMade} | refunds {s.Payments.RefundsIssued} | order {s.Orders.StatusOf(good.OrderId)?.ToString() ?? "none"}";

async Task<string> RunOnce(OrderRequest order, string sagaId = "saga-1", int mugs = 5, Action<Faults>? faults = null)
{
    var shop = NewShop(mugs);
    faults?.Invoke(shop.Faults);
    var o = await Saga(shop, new InMemorySagaLog()).RunAsync(sagaId, order);
    return State(shop, o);
}

await Case("amount 0", () => RunOnce(good with { Amount = 0m }));
await Case("amount -30", () => RunOnce(good with { Amount = -30m }));
await Case("quantity 0", () => RunOnce(good with { Quantity = 0 }));
await Case("order id \"   \"", () => RunOnce(good with { OrderId = "   " }));
await Case("saga id \"\"", () => RunOnce(good, sagaId: ""));
await Case("quantity int.MaxValue with 5 in stock", () => RunOnce(good with { Quantity = int.MaxValue }));
await Case("amount 0.001 (a tenth of a cent)", () => RunOnce(good with { Amount = 0.001m }));
await Case("saga id with quotes and a newline, file log", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"hostile-{Guid.NewGuid():N}.jsonl");
    try
    {
        var shop = NewShop();
        var id = "a\"b\nc";
        var o = await Saga(shop, new FileSagaLog(path)).RunAsync(id, good);
        return $"{State(shop, o)} | lines in file {File.ReadAllLines(path).Length} | read back {new FileSagaLog(path).Read(id).Count}";
    }
    finally { File.Delete(path); }
});
await Case("same saga id, same order, run twice", async () =>
{
    var shop = NewShop();
    var log = new InMemorySagaLog();
    await Saga(shop, log).RunAsync("saga-1", good);
    var o = await Saga(shop, log).RunAsync("saga-1", good);
    return $"{State(shop, o)} | log entries {log.Read("saga-1").Count}";
});
await Case("same saga id, amount changed to 300", async () =>
{
    var shop = NewShop();
    var log = new InMemorySagaLog();
    await Saga(shop, log).RunAsync("saga-1", good);
    var o = await Saga(shop, log).RunAsync("saga-1", good with { Amount = 300m });
    return State(shop, o);
});
await Case("resume a saga that is not in the log", async () =>
    State(NewShop(), await Saga(NewShop(), new InMemorySagaLog()).ResumeAsync("saga-404")));
await Case("log entry for a step this version does not have", async () =>
{
    var log = new InMemorySagaLog();
    log.Append(new SagaEvent("saga-1", SagaEventType.Started, null, JsonSerializer.Serialize(good), DateTimeOffset.UnixEpoch));
    log.Append(new SagaEvent("saga-1", SagaEventType.StepStarted, "check-fraud", null, DateTimeOffset.UnixEpoch));
    var shop = NewShop();
    return State(shop, await Saga(shop, log).ResumeAsync("saga-1"));
});
await Case("Started entry whose order says amount -1", async () =>
{
    var log = new InMemorySagaLog();
    log.Append(new SagaEvent("saga-1", SagaEventType.Started, null, JsonSerializer.Serialize(good with { Amount = -1m }), DateTimeOffset.UnixEpoch));
    var shop = NewShop();
    return State(shop, await Saga(shop, log).ResumeAsync("saga-1"));
});
await Case("log file whose last line was cut by a crash", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"hostile-{Guid.NewGuid():N}.jsonl");
    try
    {
        var shop = NewShop();
        var log = new FileSagaLog(path);
        log.Append(new SagaEvent("saga-1", SagaEventType.Started, null, JsonSerializer.Serialize(good), DateTimeOffset.UnixEpoch));
        File.AppendAllText(path, "{\"SagaId\":\"saga-1\",\"Type\":\"StepSta");
        var o = await Saga(shop, log).ResumeAsync("saga-1");
        return $"{State(shop, o)} | entries {log.Read("saga-1").Count}";
    }
    finally { File.Delete(path); }
});
await Case("log file with a broken line in the middle", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"hostile-{Guid.NewGuid():N}.jsonl");
    try
    {
        var log = new FileSagaLog(path);
        log.Append(new SagaEvent("saga-1", SagaEventType.Started, null, JsonSerializer.Serialize(good), DateTimeOffset.UnixEpoch));
        File.AppendAllText(path, "garbage\n");
        log.Append(new SagaEvent("saga-1", SagaEventType.StepStarted, "create-order", null, DateTimeOffset.UnixEpoch));
        var shop = NewShop();
        return State(shop, await Saga(shop, log).ResumeAsync("saga-1"));
    }
    finally { File.Delete(path); }
});
await Case("card declined (business error on the charge)", () => RunOnce(good, faults: f => f.Next("payments.charge", Fault.Business)));
await Case("order service rejects the cancel during the undo", () => RunOnce(good, mugs: 1, faults: f => f.Next("orders.cancel", Fault.Business)));
await Case("email service rejects the message after the point of no return", () => RunOnce(good, faults: f => f.Next("email.send", Fault.Business)));
await Case("retry policy with 0 attempts", () => { _ = new RetryPolicy(0, TimeSpan.Zero); return Task.FromResult("built"); });
await Case("two steps with the same name", () =>
{
    _ = new SagaOrchestrator(new InMemorySagaLog(), [new("a", _ => { }, _ => { }), new("a", _ => { }, _ => { })], Retry());
    return Task.FromResult("built");
});
await Case("a step that can be undone placed after one that cannot", () =>
{
    _ = new SagaOrchestrator(new InMemorySagaLog(), [new("email", _ => { }), new("charge", _ => { }, _ => { })], Retry());
    return Task.FromResult("built");
});
