using System.Text.Json;

namespace OrderSaga.Orchestration;

public enum SagaStatus { Completed, Compensated, Stuck }

public sealed record SagaOutcome(string SagaId, SagaStatus Status, string? Reason = null);

public sealed record SagaAlert(string SagaId, string Reason);

/// <summary>
/// Runs the steps in order and writes each move to the log before going on.
/// If a step that can be undone fails, it undoes every step it started, newest first.
/// If an undo fails after its retries, or a step after the point of no return fails,
/// the saga is marked Stuck and an alert goes out: a person has to look at it.
/// </summary>
public sealed class SagaOrchestrator
{
    private readonly ISagaLog _log;
    private readonly IReadOnlyList<SagaStep> _steps;
    private readonly RetryPolicy _retry;
    private readonly TimeProvider _clock;
    private readonly Action<SagaAlert> _alert;

    public SagaOrchestrator(ISagaLog log, IReadOnlyList<SagaStep> steps, RetryPolicy retry,
        TimeProvider? clock = null, Action<SagaAlert>? alert = null)
    {
        if (steps.Count == 0)
            throw new ArgumentException("a saga needs at least one step", nameof(steps));
        if (steps.Select(s => s.Name).Distinct().Count() != steps.Count)
            throw new ArgumentException("step names must be unique", nameof(steps));
        // Once a step cannot be undone, no later step may need undoing.
        var firstForwardOnly = steps.ToList().FindIndex(s => !s.IsCompensable);
        if (firstForwardOnly >= 0 && steps.Skip(firstForwardOnly).Any(s => s.IsCompensable))
            throw new ArgumentException("a step that can be undone cannot come after the point of no return", nameof(steps));

        _log = log;
        _steps = steps;
        _retry = retry;
        _clock = clock ?? TimeProvider.System;
        _alert = alert ?? (_ => { });
    }

    /// <summary>
    /// Starts the saga. If the log already has it with the same order, this resumes it instead
    /// of starting twice; with a different order it refuses, because the id is being reused.
    /// </summary>
    public Task<SagaOutcome> RunAsync(string sagaId, OrderRequest order)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sagaId);
        order.Validate();
        var existing = _log.Read(sagaId);
        if (existing.Count > 0)
        {
            if (existing[0].Detail != JsonSerializer.Serialize(order))
                throw new InvalidOperationException($"saga '{sagaId}' already exists with a different order");
            return ResumeAsync(sagaId);
        }

        Write(sagaId, SagaEventType.Started, detail: JsonSerializer.Serialize(order));
        return ContinueAsync(sagaId, order, SagaState.Empty);
    }

    /// <summary>Picks up a saga from its log, for example after the process that ran it died.</summary>
    public Task<SagaOutcome> ResumeAsync(string sagaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sagaId);
        var events = _log.Read(sagaId);
        if (events.Count == 0 || events[0].Type != SagaEventType.Started)
            throw new InvalidOperationException($"saga '{sagaId}' has no Started entry in the log");

        var order = JsonSerializer.Deserialize<OrderRequest>(events[0].Detail ?? "")
            ?? throw new InvalidDataException($"saga '{sagaId}' has no order in its Started entry");
        order.Validate();
        return ContinueAsync(sagaId, order, SagaState.From(events, _steps));
    }

    private async Task<SagaOutcome> ContinueAsync(string sagaId, OrderRequest order, SagaState state)
    {
        if (state.Final is { } final)
            return final with { SagaId = sagaId };

        if (!state.Compensating)
        {
            foreach (var step in _steps.Where(s => !state.Completed.Contains(s.Name)))
            {
                if (!state.Started.Contains(step.Name))
                {
                    Write(sagaId, SagaEventType.StepStarted, step.Name);
                    state.Started.Add(step.Name);
                }

                var failure = await TryAsync(() => step.Execute(new StepContext(sagaId, step.Name, order)));
                if (failure is null)
                {
                    Write(sagaId, SagaEventType.StepCompleted, step.Name);
                    state.Completed.Add(step.Name);
                    continue;
                }

                if (!step.IsCompensable)
                    return Stuck(sagaId, $"{step.Name} failed after the point of no return: {failure.Message}");

                Write(sagaId, SagaEventType.CompensationRequested, step.Name, failure.Message);
                state.Compensating = true;
                state.Reason = failure.Message;
                break;
            }

            if (!state.Compensating)
            {
                Write(sagaId, SagaEventType.Completed);
                return new SagaOutcome(sagaId, SagaStatus.Completed);
            }
        }

        // Undo every step that was started, newest first. A step that failed is undone too:
        // it may have applied its change before the error (a timeout after the charge).
        foreach (var step in _steps.Where(s => s.IsCompensable && state.Started.Contains(s.Name)).Reverse())
        {
            if (state.Compensated.Contains(step.Name))
                continue;
            var failure = await TryAsync(() => step.Compensate!(new StepContext(sagaId, step.Name, order)));
            if (failure is not null)
                return Stuck(sagaId, $"undo of {step.Name} failed: {failure.Message}");
            Write(sagaId, SagaEventType.StepCompensated, step.Name);
            state.Compensated.Add(step.Name);
        }

        Write(sagaId, SagaEventType.Compensated, detail: state.Reason);
        return new SagaOutcome(sagaId, SagaStatus.Compensated, state.Reason);
    }

    // Exceptions from the log are not caught here: if the log cannot be written, the saga stops.
    private async Task<Exception?> TryAsync(Action call)
    {
        try
        {
            await _retry.ExecuteAsync(call);
            return null;
        }
        catch (Exception ex) when (ex is Services.TransientException or Services.BusinessRuleException)
        {
            return ex;
        }
    }

    private SagaOutcome Stuck(string sagaId, string reason)
    {
        Write(sagaId, SagaEventType.Stuck, detail: reason);
        _alert(new SagaAlert(sagaId, reason));
        return new SagaOutcome(sagaId, SagaStatus.Stuck, reason);
    }

    private void Write(string sagaId, SagaEventType type, string? step = null, string? detail = null) =>
        _log.Append(new SagaEvent(sagaId, type, step, detail, _clock.GetUtcNow()));

    /// <summary>What the log says has happened so far.</summary>
    private sealed class SagaState
    {
        public HashSet<string> Started { get; } = new();
        public HashSet<string> Completed { get; } = new();
        public HashSet<string> Compensated { get; } = new();
        public bool Compensating { get; set; }
        public string? Reason { get; set; }
        public SagaOutcome? Final { get; private set; }

        public static SagaState Empty => new();

        public static SagaState From(IReadOnlyList<SagaEvent> events, IReadOnlyList<SagaStep> steps)
        {
            var known = steps.Select(s => s.Name).ToHashSet();
            var state = new SagaState();
            foreach (var e in events)
            {
                // A log written by another version of the saga cannot be resumed safely.
                if (e.Step is not null && !known.Contains(e.Step))
                    throw new InvalidOperationException($"the log mentions step '{e.Step}', which this version does not define");

                switch (e.Type)
                {
                    case SagaEventType.StepStarted: state.Started.Add(e.Step!); break;
                    case SagaEventType.StepCompleted: state.Completed.Add(e.Step!); break;
                    case SagaEventType.CompensationRequested: state.Compensating = true; state.Reason = e.Detail; break;
                    case SagaEventType.StepCompensated: state.Compensated.Add(e.Step!); break;
                    case SagaEventType.Completed: state.Final = new SagaOutcome("", SagaStatus.Completed); break;
                    case SagaEventType.Compensated: state.Final = new SagaOutcome("", SagaStatus.Compensated, e.Detail); break;
                    // Stuck is not final: after someone fixes the cause, Resume tries again.
                    case SagaEventType.Stuck: break;
                }
            }
            return state;
        }
    }
}
