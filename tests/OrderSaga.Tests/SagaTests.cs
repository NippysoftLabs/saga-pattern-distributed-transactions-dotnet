using OrderSaga.Before;
using OrderSaga.Choreography;
using OrderSaga.Orchestration;
using OrderSaga.Services;
using static OrderSaga.Tests.TestKit;

namespace OrderSaga.Tests;

public class BeforeTests
{
    [Fact]
    public void Without_a_saga_the_customer_pays_for_an_order_that_never_ships()
    {
        var shop = NewShop(mugsInStock: 1);

        Assert.Throws<BusinessRuleException>(() => NaiveCheckout.PlaceOrder(shop, Mug));

        Assert.Equal(30m, shop.Payments.Captured);
        Assert.Equal(OrderStatus.Pending, shop.Orders.StatusOf(Mug.OrderId));
    }
}

public class OrchestrationTests
{
    [Fact]
    public async Task Happy_path_runs_the_five_steps_in_order()
    {
        var shop = NewShop();
        var log = new InMemorySagaLog();

        var outcome = await Orchestrator(shop, log).RunAsync("saga-1", Mug);

        Assert.Equal(SagaStatus.Completed, outcome.Status);
        Assert.Equal(OrderStatus.Confirmed, shop.Orders.StatusOf(Mug.OrderId));
        Assert.Equal(30m, shop.Payments.Captured);
        Assert.Equal(3, shop.Inventory.StockOf("mug"));
        Assert.Equal([Mug.OrderId], shop.Email.Sent);
        Assert.Equal(12, log.Read("saga-1").Count);
    }

    [Fact]
    public async Task Out_of_stock_refunds_the_payment_and_cancels_the_order_newest_first()
    {
        var shop = NewShop(mugsInStock: 1);
        var log = new InMemorySagaLog();

        var outcome = await Orchestrator(shop, log).RunAsync("saga-1", Mug);

        Assert.Equal(SagaStatus.Compensated, outcome.Status);
        Assert.Equal("inventory.reserve: only 1 of mug left", outcome.Reason);
        Assert.Equal(0m, shop.Payments.Captured);
        Assert.Equal(1, shop.Payments.RefundsIssued);
        Assert.Equal(OrderStatus.Cancelled, shop.Orders.StatusOf(Mug.OrderId));
        Assert.Equal(1, shop.Inventory.StockOf("mug"));
        Assert.Equal(
            ["Started", "StepStarted create-order", "StepCompleted create-order",
             "StepStarted charge-payment", "StepCompleted charge-payment",
             "StepStarted reserve-stock", "CompensationRequested reserve-stock",
             "StepCompensated reserve-stock", "StepCompensated charge-payment",
             "StepCompensated create-order", "Compensated"],
            Trail(log, "saga-1"));
    }

    [Fact]
    public async Task Out_of_stock_is_not_retried_but_a_timeout_is()
    {
        var shop = NewShop(mugsInStock: 1);
        var retry = Retry();
        await Orchestrator(shop, new InMemorySagaLog(), retry).RunAsync("saga-1", Mug);
        Assert.Empty(retry.Waits);

        var shop2 = NewShop();
        shop2.Faults.Next("inventory.reserve", Fault.Transient, Fault.Transient);
        var retry2 = Retry();
        var outcome = await Orchestrator(shop2, new InMemorySagaLog(), retry2).RunAsync("saga-2", Mug);

        Assert.Equal(SagaStatus.Completed, outcome.Status);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], retry2.Waits);
    }

    [Fact]
    public async Task A_charge_that_timed_out_after_charging_is_retried_without_charging_twice()
    {
        var shop = NewShop();
        shop.Faults.Next("payments.charge", Fault.ApplyThenTimeout);

        var outcome = await Orchestrator(shop, new InMemorySagaLog()).RunAsync("saga-1", Mug);

        Assert.Equal(SagaStatus.Completed, outcome.Status);
        Assert.Equal(1, shop.Payments.AppliedCount);
        Assert.Equal(30m, shop.Payments.Captured);
    }

    [Fact]
    public async Task A_step_that_failed_is_undone_too_because_it_may_have_applied_its_change()
    {
        var shop = NewShop();
        // The charge goes through, but every answer is a timeout: retries run out.
        shop.Faults.Next("payments.charge", Fault.ApplyThenTimeout, Fault.Transient, Fault.Transient);

        var outcome = await Orchestrator(shop, new InMemorySagaLog()).RunAsync("saga-1", Mug);

        Assert.Equal(SagaStatus.Compensated, outcome.Status);
        Assert.Equal(0m, shop.Payments.Captured);
        Assert.Equal(1, shop.Payments.RefundsIssued);
    }

    [Fact]
    public async Task A_refund_that_fails_twice_is_retried_and_the_saga_still_ends_compensated()
    {
        var shop = NewShop(mugsInStock: 1);
        shop.Faults.Next("payments.refund", Fault.Transient, Fault.Transient);

        var outcome = await Orchestrator(shop, new InMemorySagaLog()).RunAsync("saga-1", Mug);

        Assert.Equal(SagaStatus.Compensated, outcome.Status);
        Assert.Equal(0m, shop.Payments.Captured);
    }

    [Fact]
    public async Task A_refund_that_keeps_failing_marks_the_saga_stuck_and_raises_an_alert()
    {
        var shop = NewShop(mugsInStock: 1);
        shop.Faults.Always("payments.refund", Fault.Transient);
        var log = new InMemorySagaLog();
        var alerts = new List<SagaAlert>();

        var outcome = await Orchestrator(shop, log, alerts: alerts).RunAsync("saga-1", Mug);

        Assert.Equal(SagaStatus.Stuck, outcome.Status);
        Assert.Equal("undo of charge-payment failed: payments.refund: service unavailable", outcome.Reason);
        Assert.Single(alerts);
        Assert.Equal(30m, shop.Payments.Captured);
        // The stock was already released; the order stays pending until the refund goes through.
        Assert.Equal(1, shop.Inventory.StockOf("mug"));
        Assert.Equal(OrderStatus.Pending, shop.Orders.StatusOf(Mug.OrderId));
    }

    [Fact]
    public async Task A_stuck_saga_finishes_its_undo_when_resumed_after_the_fix_without_repeating_done_work()
    {
        var shop = NewShop(mugsInStock: 1);
        shop.Faults.Always("payments.refund", Fault.Transient);
        var log = new InMemorySagaLog();
        await Orchestrator(shop, log).RunAsync("saga-1", Mug);

        shop.Faults.Heal("payments.refund");
        var outcome = await Orchestrator(shop, log).ResumeAsync("saga-1");

        Assert.Equal(SagaStatus.Compensated, outcome.Status);
        Assert.Equal(0m, shop.Payments.Captured);
        // The reserve was rejected and the release, done before the refund got stuck, was not repeated.
        Assert.Equal(1, shop.Inventory.AppliedCount);
        Assert.Equal(OrderStatus.Cancelled, shop.Orders.StatusOf(Mug.OrderId));
    }

    [Fact]
    public async Task A_failure_after_the_point_of_no_return_is_never_undone()
    {
        var shop = NewShop();
        shop.Faults.Always("email.send", Fault.Transient);
        var alerts = new List<SagaAlert>();

        var outcome = await Orchestrator(shop, new InMemorySagaLog(), alerts: alerts).RunAsync("saga-1", Mug);

        Assert.Equal(SagaStatus.Stuck, outcome.Status);
        Assert.Equal(30m, shop.Payments.Captured);
        Assert.Equal(OrderStatus.Confirmed, shop.Orders.StatusOf(Mug.OrderId));
        Assert.Single(alerts);
    }

    [Fact]
    public async Task Starting_the_same_saga_twice_does_not_charge_twice()
    {
        var shop = NewShop();
        var log = new InMemorySagaLog();
        var orchestrator = Orchestrator(shop, log);

        await orchestrator.RunAsync("saga-1", Mug);
        var second = await orchestrator.RunAsync("saga-1", Mug);

        Assert.Equal(SagaStatus.Completed, second.Status);
        Assert.Equal(1, shop.Payments.ChargesMade);
        Assert.Equal(12, log.Read("saga-1").Count);
    }

    [Fact]
    public async Task Reusing_a_saga_id_for_a_different_order_is_refused()
    {
        var shop = NewShop();
        var log = new InMemorySagaLog();
        await Orchestrator(shop, log).RunAsync("saga-1", Mug);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Orchestrator(shop, log).RunAsync("saga-1", Mug with { Amount = 300m }));
        Assert.Equal(30m, shop.Payments.Captured);
    }
}

public class RecoveryTests
{
    [Fact]
    public async Task After_a_crash_between_charging_and_logging_it_a_new_process_finishes_with_one_charge()
    {
        var shop = NewShop();
        var path = Path.Combine(Path.GetTempPath(), $"saga-{Guid.NewGuid():N}.jsonl");
        try
        {
            // Write 5 would be "StepCompleted charge-payment": the charge happened, the log does not say so.
            var dying = Orchestrator(shop, new CrashingLog(new FileSagaLog(path), crashOnWrite: 5));
            await Assert.ThrowsAsync<IOException>(() => dying.RunAsync("saga-1", Mug));
            Assert.Equal(30m, shop.Payments.Captured);

            var outcome = await Orchestrator(shop, new FileSagaLog(path)).ResumeAsync("saga-1");

            Assert.Equal(SagaStatus.Completed, outcome.Status);
            Assert.Equal(1, shop.Payments.AppliedCount);
            Assert.Equal(OrderStatus.Confirmed, shop.Orders.StatusOf(Mug.OrderId));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task After_a_crash_in_the_middle_of_the_undo_the_refund_is_not_issued_twice()
    {
        var shop = NewShop(mugsInStock: 1);
        var log = new InMemorySagaLog();
        // Writes: 1 Started ... 7 CompensationRequested, 8 StepCompensated reserve-stock, 9 StepCompensated charge-payment.
        var dying = Orchestrator(shop, new CrashingLog(log, crashOnWrite: 9));
        await Assert.ThrowsAsync<IOException>(() => dying.RunAsync("saga-1", Mug));
        Assert.Equal(1, shop.Payments.RefundsIssued);

        var outcome = await Orchestrator(shop, log).ResumeAsync("saga-1");

        Assert.Equal(SagaStatus.Compensated, outcome.Status);
        Assert.Equal(1, shop.Payments.RefundsIssued);
        Assert.Equal(2, shop.Payments.AppliedCount); // one charge, one refund
        Assert.Equal(OrderStatus.Cancelled, shop.Orders.StatusOf(Mug.OrderId));
    }

    [Fact]
    public async Task The_monitor_lists_stuck_sagas_and_sagas_nobody_resumed()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var log = new InMemorySagaLog();

        var stuckShop = NewShop(mugsInStock: 1);
        stuckShop.Faults.Always("payments.refund", Fault.Transient);
        await Orchestrator(stuckShop, log, clock: clock).RunAsync("saga-stuck", Mug);

        var abandoned = Orchestrator(NewShop(), new CrashingLog(log, crashOnWrite: 5), clock: clock);
        await Assert.ThrowsAsync<IOException>(() => abandoned.RunAsync("saga-abandoned", Mug));

        await Orchestrator(NewShop(), log, clock: clock).RunAsync("saga-ok", Mug);

        clock.Now = clock.Now.AddMinutes(20);
        var alerts = SagaMonitor.NeedsAttention(log, clock.Now, maxRunning: TimeSpan.FromMinutes(15));

        Assert.Equal(["saga-stuck", "saga-abandoned"], alerts.Select(a => a.SagaId));
        Assert.StartsWith("stuck: undo of charge-payment", alerts[0].Reason);
        Assert.Equal("running for 20 min, last entry StepStarted charge-payment", alerts[1].Reason);
    }
}

public class ChoreographyTests
{
    [Fact]
    public void Without_an_orchestrator_the_same_undo_happens_through_events()
    {
        var shop = NewShop(mugsInStock: 1);
        var bus = ChoreographedShop.Wire(shop);

        bus.Publish(new OrderPlaced(Mug.OrderId, Mug.Sku, Mug.Quantity, Mug.Amount));

        Assert.Equal(0m, shop.Payments.Captured);
        Assert.Equal(OrderStatus.Cancelled, shop.Orders.StatusOf(Mug.OrderId));
        Assert.Equal(
            ["OrderPlaced", "PaymentCharged", "StockRejected", "PaymentRefunded"],
            bus.History.Select(e => e.GetType().Name));
    }

    [Fact]
    public void Happy_path_through_events_confirms_and_emails()
    {
        var shop = NewShop();
        var bus = ChoreographedShop.Wire(shop);

        bus.Publish(new OrderPlaced(Mug.OrderId, Mug.Sku, Mug.Quantity, Mug.Amount));

        Assert.Equal(OrderStatus.Confirmed, shop.Orders.StatusOf(Mug.OrderId));
        Assert.Equal([Mug.OrderId], shop.Email.Sent);
    }
}

public class GuardTests
{
    [Fact]
    public void A_step_that_can_be_undone_after_the_point_of_no_return_is_rejected()
    {
        SagaStep[] steps =
        [
            new("email", _ => { }),
            new("charge", _ => { }, _ => { }),
        ];
        Assert.Throws<ArgumentException>(() => new SagaOrchestrator(new InMemorySagaLog(), steps, Retry()));
    }

    [Fact]
    public async Task A_log_from_another_version_of_the_saga_is_not_resumed()
    {
        var log = new InMemorySagaLog();
        log.Append(new SagaEvent("saga-1", SagaEventType.Started, null, System.Text.Json.JsonSerializer.Serialize(Mug), DateTimeOffset.UnixEpoch));
        log.Append(new SagaEvent("saga-1", SagaEventType.StepStarted, "check-fraud", null, DateTimeOffset.UnixEpoch));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Orchestrator(NewShop(), log).ResumeAsync("saga-1"));
        Assert.Contains("check-fraud", ex.Message);
    }

    [Fact]
    public void A_write_cut_short_by_a_crash_is_ignored_and_trimmed_but_a_broken_middle_line_stops_the_read()
    {
        var path = Path.Combine(Path.GetTempPath(), $"saga-{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new FileSagaLog(path);
            log.Append(new SagaEvent("saga-1", SagaEventType.Started, null, "{}", DateTimeOffset.UnixEpoch));
            File.AppendAllText(path, "{\"SagaId\":\"saga-1\",\"Ty");
            Assert.Single(log.Read("saga-1"));

            log.Append(new SagaEvent("saga-1", SagaEventType.Completed, null, null, DateTimeOffset.UnixEpoch));
            Assert.Equal(2, log.Read("saga-1").Count);

            File.AppendAllText(path, "not json\n");
            Assert.Throws<InvalidDataException>(() => log.Read("saga-1"));
        }
        finally { File.Delete(path); }
    }
}
