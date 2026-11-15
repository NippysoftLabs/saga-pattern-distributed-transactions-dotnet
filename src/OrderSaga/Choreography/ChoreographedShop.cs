using OrderSaga.Services;

namespace OrderSaga.Choreography;

public abstract record ShopEvent(string OrderId);
public sealed record OrderPlaced(string OrderId, string Sku, int Quantity, decimal Amount) : ShopEvent(OrderId);
public sealed record PaymentCharged(string OrderId, string Sku, int Quantity) : ShopEvent(OrderId);
public sealed record PaymentDeclined(string OrderId, string Reason) : ShopEvent(OrderId);
public sealed record StockReserved(string OrderId) : ShopEvent(OrderId);
public sealed record StockRejected(string OrderId, string Reason) : ShopEvent(OrderId);
public sealed record PaymentRefunded(string OrderId) : ShopEvent(OrderId);

/// <summary>
/// In-memory bus that hands each event to its subscribers right away. It has no outbox,
/// no persistence and no retries: it only shows who reacts to what.
/// </summary>
public sealed class EventBus
{
    private readonly Dictionary<Type, List<Action<ShopEvent>>> _handlers = new();

    public List<ShopEvent> History { get; } = new();

    public void Subscribe<T>(Action<T> handler) where T : ShopEvent
    {
        if (!_handlers.TryGetValue(typeof(T), out var list))
            _handlers[typeof(T)] = list = new();
        list.Add(e => handler((T)e));
    }

    public void Publish(ShopEvent e)
    {
        History.Add(e);
        foreach (var handler in _handlers.GetValueOrDefault(e.GetType()) ?? [])
            handler(e);
    }
}

/// <summary>
/// The same order without an orchestrator: each service listens for an event and publishes
/// the next one. The flow, including the undo path, is spread over these handlers.
/// </summary>
public static class ChoreographedShop
{
    public static EventBus Wire(Shop shop)
    {
        var bus = new EventBus();

        // Orders
        bus.Subscribe<OrderPlaced>(e => shop.Orders.Create($"{e.OrderId}:create", e.OrderId));
        bus.Subscribe<StockReserved>(e => shop.Orders.Confirm($"{e.OrderId}:confirm", e.OrderId));
        bus.Subscribe<PaymentDeclined>(e => shop.Orders.Cancel($"{e.OrderId}:cancel", e.OrderId));
        bus.Subscribe<PaymentRefunded>(e => shop.Orders.Cancel($"{e.OrderId}:cancel", e.OrderId));

        // Payments
        bus.Subscribe<OrderPlaced>(e =>
        {
            try
            {
                shop.Payments.Charge($"{e.OrderId}:charge", e.OrderId, e.Amount);
                bus.Publish(new PaymentCharged(e.OrderId, e.Sku, e.Quantity));
            }
            catch (BusinessRuleException ex)
            {
                bus.Publish(new PaymentDeclined(e.OrderId, ex.Message));
            }
        });
        bus.Subscribe<StockRejected>(e =>
        {
            shop.Payments.Refund($"{e.OrderId}:refund", e.OrderId);
            bus.Publish(new PaymentRefunded(e.OrderId));
        });

        // Inventory
        bus.Subscribe<PaymentCharged>(e =>
        {
            try
            {
                shop.Inventory.Reserve($"{e.OrderId}:reserve", e.OrderId, e.Sku, e.Quantity);
                bus.Publish(new StockReserved(e.OrderId));
            }
            catch (BusinessRuleException ex)
            {
                bus.Publish(new StockRejected(e.OrderId, ex.Message));
            }
        });

        // Email
        bus.Subscribe<StockReserved>(e => shop.Email.SendConfirmation($"{e.OrderId}:email", e.OrderId));

        return bus;
    }
}
