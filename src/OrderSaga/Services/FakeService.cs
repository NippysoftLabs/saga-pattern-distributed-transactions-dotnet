namespace OrderSaga.Services;

/// <summary>
/// Base for the in-memory services. Each one remembers the idempotency keys it already
/// applied, so a retry with the same key does nothing and reports success.
/// </summary>
public abstract class FakeService(Faults faults)
{
    private readonly HashSet<string> _appliedKeys = new();

    /// <summary>How many times a change was really applied (retries with a known key do not count).</summary>
    public int AppliedCount { get; private set; }

    protected void Run(string operation, string idempotencyKey, Action apply)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        // The planned fault plays the network or the service being down, so it comes first.
        var fault = faults.Take(operation);
        if (fault == Fault.Transient)
            throw new TransientException($"{operation}: service unavailable");
        if (fault == Fault.Business)
            throw new BusinessRuleException($"{operation}: rejected");

        // Same key seen before: the change is already there, so it is not applied again.
        // In a real service the change and the key are saved in the same database transaction.
        if (!_appliedKeys.Contains(idempotencyKey))
        {
            apply();
            _appliedKeys.Add(idempotencyKey);
            AppliedCount++;
        }

        if (fault == Fault.ApplyThenTimeout)
            throw new TransientException($"{operation}: timeout after the change was applied");
    }
}
