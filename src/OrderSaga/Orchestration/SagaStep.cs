using OrderSaga.Services;

namespace OrderSaga.Orchestration;

/// <summary>What a step receives: the saga, its order, and a key that is the same on every retry.</summary>
public sealed record StepContext(string SagaId, string Step, OrderRequest Order)
{
    public string KeyFor(string action) => $"{SagaId}:{Step}:{action}";
}

/// <summary>
/// One local transaction of the saga. A step with <see cref="Compensate"/> can be undone;
/// a step without it comes after the point of no return and is only ever retried.
/// </summary>
public sealed record SagaStep(string Name, Action<StepContext> Execute, Action<StepContext>? Compensate = null)
{
    public bool IsCompensable => Compensate is not null;
}

/// <summary>The steps of the online store order, in order.</summary>
public static class OrderSagaSteps
{
    public static IReadOnlyList<SagaStep> For(Shop shop) =>
    [
        new("create-order",
            c => shop.Orders.Create(c.KeyFor("do"), c.Order.OrderId),
            c => shop.Orders.Cancel(c.KeyFor("undo"), c.Order.OrderId)),
        new("charge-payment",
            c => shop.Payments.Charge(c.KeyFor("do"), c.Order.OrderId, c.Order.Amount),
            c => shop.Payments.Refund(c.KeyFor("undo"), c.Order.OrderId)),
        new("reserve-stock",
            c => shop.Inventory.Reserve(c.KeyFor("do"), c.Order.OrderId, c.Order.Sku, c.Order.Quantity),
            c => shop.Inventory.Release(c.KeyFor("undo"), c.Order.OrderId)),
        // Point of no return: from here on the saga only moves forward.
        new("confirm-order",
            c => shop.Orders.Confirm(c.KeyFor("do"), c.Order.OrderId)),
        new("send-email",
            c => shop.Email.SendConfirmation(c.KeyFor("do"), c.Order.OrderId)),
    ];
}
