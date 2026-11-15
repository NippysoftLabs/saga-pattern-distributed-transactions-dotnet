using OrderSaga.Services;

namespace OrderSaga.Before;

/// <summary>
/// The version without a saga: five calls in a row, as if they were one transaction.
/// When a later call fails, nothing undoes the earlier ones.
/// </summary>
public static class NaiveCheckout
{
    public static void PlaceOrder(Shop shop, OrderRequest order)
    {
        shop.Orders.Create($"{order.OrderId}:create", order.OrderId);
        shop.Payments.Charge($"{order.OrderId}:charge", order.OrderId, order.Amount);
        shop.Inventory.Reserve($"{order.OrderId}:reserve", order.OrderId, order.Sku, order.Quantity);
        shop.Orders.Confirm($"{order.OrderId}:confirm", order.OrderId);
        shop.Email.SendConfirmation($"{order.OrderId}:email", order.OrderId);
    }
}
