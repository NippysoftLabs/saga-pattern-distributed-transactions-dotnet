using OrderSaga;
using OrderSaga.Before;
using OrderSaga.Orchestration;
using OrderSaga.Services;

// Prints what happens to one order in each scenario: the money held, the stock, the order status
// and the saga log. No real waits: the retry delays are recorded, not slept.
var order = new OrderRequest("order-1001", "mug", 2, 30m);

Shop NewShop(int mugs) => new(new Dictionary<string, int> { ["mug"] = mugs });
RetryPolicy Retry() => new(3, TimeSpan.FromSeconds(1), _ => Task.CompletedTask);

void Show(string title, Shop shop, InMemorySagaLog? log, SagaOutcome? outcome, RetryPolicy? retry)
{
    Console.WriteLine($"## {title}");
    if (outcome is not null)
        Console.WriteLine($"outcome: {outcome.Status}{(outcome.Reason is null ? "" : $" ({outcome.Reason})")}");
    Console.WriteLine($"money held: {shop.Payments.Captured} | charges made: {shop.Payments.ChargesMade} | refunds: {shop.Payments.RefundsIssued}");
    Console.WriteLine($"mugs in stock: {shop.Inventory.StockOf("mug")} | order: {shop.Orders.StatusOf(order.OrderId)?.ToString() ?? "none"} | emails: {shop.Email.Sent.Count}");
    if (retry is not null && retry.Waits.Count > 0)
        Console.WriteLine($"retry waits: {string.Join(", ", retry.Waits.Select(w => $"{w.TotalSeconds}s"))}");
    if (log is not null)
        foreach (var e in log.Read("saga-1"))
            Console.WriteLine($"  log: {e.Type}{(e.Step is null ? "" : " " + e.Step)}{(e.Detail is null || e.Type == SagaEventType.Started ? "" : " | " + e.Detail)}");
    Console.WriteLine();
}

async Task Scenario(string title, int mugs, Action<Faults>? faults = null)
{
    var shop = NewShop(mugs);
    faults?.Invoke(shop.Faults);
    var log = new InMemorySagaLog();
    var retry = Retry();
    var outcome = await new SagaOrchestrator(log, OrderSagaSteps.For(shop), retry).RunAsync("saga-1", order);
    Show(title, shop, log, outcome, retry);
}

// Before: no saga.
var naive = NewShop(1);
try { NaiveCheckout.PlaceOrder(naive, order); }
catch (BusinessRuleException ex) { Console.WriteLine($"naive checkout threw: {ex.Message}"); }
Show("1. Before: no saga, 1 mug in stock", naive, null, null, null);

await Scenario("2. Saga, 5 mugs in stock", 5);
await Scenario("3. Saga, 1 mug in stock (the post's case)", 1);
await Scenario("4. Saga, stock service times out twice", 5, f => f.Next("inventory.reserve", Fault.Transient, Fault.Transient));
await Scenario("5. Saga, charge applied but the answer is lost", 5, f => f.Next("payments.charge", Fault.ApplyThenTimeout));
await Scenario("6. Saga, 1 mug, refund service down", 1, f => f.Always("payments.refund", Fault.Transient));
await Scenario("7. Saga, email service down after the point of no return", 5, f => f.Always("email.send", Fault.Transient));
