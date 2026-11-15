namespace OrderSaga.Services;

public enum OrderStatus { Pending, Confirmed, Cancelled }

public sealed class OrdersService(Faults faults) : FakeService(faults)
{
    private readonly Dictionary<string, OrderStatus> _orders = new();

    public OrderStatus? StatusOf(string orderId) => _orders.TryGetValue(orderId, out var s) ? s : null;

    public void Create(string key, string orderId) =>
        Run("orders.create", key, () => _orders[orderId] = OrderStatus.Pending);

    /// <summary>Cancels a pending order. An order that does not exist is left alone.</summary>
    public void Cancel(string key, string orderId) =>
        Run("orders.cancel", key, () =>
        {
            if (_orders.TryGetValue(orderId, out var s) && s == OrderStatus.Pending)
                _orders[orderId] = OrderStatus.Cancelled;
        });

    public void Confirm(string key, string orderId) =>
        Run("orders.confirm", key, () => _orders[orderId] = OrderStatus.Confirmed);
}

public sealed class PaymentsService(Faults faults) : FakeService(faults)
{
    private readonly Dictionary<string, decimal> _charges = new();

    /// <summary>Money currently held from customers (charged and not refunded).</summary>
    public decimal Captured => _charges.Values.Sum();
    public int ChargesMade { get; private set; }
    public int RefundsIssued { get; private set; }

    public void Charge(string key, string orderId, decimal amount) =>
        Run("payments.charge", key, () =>
        {
            _charges[orderId] = amount;
            ChargesMade++;
        });

    /// <summary>Refunds the charge of an order. With no charge on record there is nothing to refund.</summary>
    public void Refund(string key, string orderId) =>
        Run("payments.refund", key, () =>
        {
            if (_charges.Remove(orderId))
                RefundsIssued++;
        });
}

public sealed class InventoryService(Faults faults, IDictionary<string, int> stock) : FakeService(faults)
{
    private readonly Dictionary<string, (string Sku, int Quantity)> _reservations = new();

    public int StockOf(string sku) => stock.TryGetValue(sku, out var n) ? n : 0;
    public bool IsReserved(string orderId) => _reservations.ContainsKey(orderId);

    public void Reserve(string key, string orderId, string sku, int quantity) =>
        Run("inventory.reserve", key, () =>
        {
            if (StockOf(sku) < quantity)
                throw new BusinessRuleException($"inventory.reserve: only {StockOf(sku)} of {sku} left");
            stock[sku] = StockOf(sku) - quantity;
            _reservations[orderId] = (sku, quantity);
        });

    /// <summary>Puts reserved units back on the shelf. With no reservation there is nothing to release.</summary>
    public void Release(string key, string orderId) =>
        Run("inventory.release", key, () =>
        {
            if (_reservations.Remove(orderId, out var r))
                stock[r.Sku] = StockOf(r.Sku) + r.Quantity;
        });
}

public sealed class EmailService(Faults faults) : FakeService(faults)
{
    public List<string> Sent { get; } = new();

    public void SendConfirmation(string key, string orderId) =>
        Run("email.send", key, () => Sent.Add(orderId));
}

/// <summary>The four services of the online store, sharing one fault plan.</summary>
public sealed class Shop
{
    public Shop(IDictionary<string, int> stock, Faults? faults = null)
    {
        Faults = faults ?? new Faults();
        Orders = new OrdersService(Faults);
        Payments = new PaymentsService(Faults);
        Inventory = new InventoryService(Faults, stock);
        Email = new EmailService(Faults);
    }

    public Faults Faults { get; }
    public OrdersService Orders { get; }
    public PaymentsService Payments { get; }
    public InventoryService Inventory { get; }
    public EmailService Email { get; }
}
