using OrderSaga.Services;

namespace OrderSaga.Orchestration;

/// <summary>
/// Tries a call again when it fails with <see cref="TransientException"/>, waiting
/// <c>baseDelay</c>, then twice that, and so on. Any other exception goes straight up.
/// </summary>
public sealed class RetryPolicy
{
    private readonly Func<TimeSpan, Task> _wait;

    public RetryPolicy(int maxAttempts, TimeSpan baseDelay, Func<TimeSpan, Task>? wait = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(baseDelay, TimeSpan.Zero);
        MaxAttempts = maxAttempts;
        BaseDelay = baseDelay;
        _wait = wait ?? (d => Task.Delay(d));
    }

    public int MaxAttempts { get; }
    public TimeSpan BaseDelay { get; }

    /// <summary>Every wait this policy asked for, in order (handy in tests and demos).</summary>
    public List<TimeSpan> Waits { get; } = new();

    public async Task ExecuteAsync(Action call)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                call();
                return;
            }
            catch (TransientException) when (attempt < MaxAttempts)
            {
                var delay = BaseDelay * Math.Pow(2, attempt - 1);
                Waits.Add(delay);
                await _wait(delay);
            }
        }
    }
}
