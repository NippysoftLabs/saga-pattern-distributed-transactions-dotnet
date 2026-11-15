namespace OrderSaga;

public sealed record OrderRequest(string OrderId, string Sku, int Quantity, decimal Amount)
{
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OrderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Sku);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Quantity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Amount);
    }
}
