namespace OrderSaga.Services;

/// <summary>A failure that may go away if the same call is tried again (timeout, service down).</summary>
public sealed class TransientException(string message) : Exception(message);

/// <summary>A failure that will repeat on every retry (out of stock, card declined).</summary>
public sealed class BusinessRuleException(string message) : Exception(message);

public enum Fault
{
    /// <summary>Throws <see cref="TransientException"/> without applying the change.</summary>
    Transient,
    /// <summary>Throws <see cref="BusinessRuleException"/> without applying the change.</summary>
    Business,
    /// <summary>Applies the change, then throws <see cref="TransientException"/>: the caller cannot tell it worked.</summary>
    ApplyThenTimeout,
}

/// <summary>Planned failures per operation name, used by the fake services in tests and demos.</summary>
public sealed class Faults
{
    private readonly Dictionary<string, Queue<Fault>> _once = new();
    private readonly Dictionary<string, Fault> _always = new();

    /// <summary>The next calls to <paramref name="operation"/> fail with these faults, in order, then succeed.</summary>
    public Faults Next(string operation, params Fault[] faults)
    {
        if (!_once.TryGetValue(operation, out var queue))
            _once[operation] = queue = new Queue<Fault>();
        foreach (var fault in faults)
            queue.Enqueue(fault);
        return this;
    }

    /// <summary>Every call to <paramref name="operation"/> fails with <paramref name="fault"/>.</summary>
    public Faults Always(string operation, Fault fault)
    {
        _always[operation] = fault;
        return this;
    }

    /// <summary>Stops the permanent fault of <paramref name="operation"/>, as when someone fixes the service.</summary>
    public void Heal(string operation) => _always.Remove(operation);

    internal Fault? Take(string operation)
    {
        if (_always.TryGetValue(operation, out var permanent))
            return permanent;
        if (_once.TryGetValue(operation, out var queue) && queue.Count > 0)
            return queue.Dequeue();
        return null;
    }
}
