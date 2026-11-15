namespace OrderSaga.Orchestration;

/// <summary>
/// Reads the log and lists the sagas a person should look at: the ones marked Stuck,
/// and the ones that started long ago and never finished (nobody resumed them).
/// </summary>
public static class SagaMonitor
{
    public static IReadOnlyList<SagaAlert> NeedsAttention(ISagaLog log, DateTimeOffset now, TimeSpan maxRunning)
    {
        var alerts = new List<SagaAlert>();
        foreach (var sagaId in log.SagaIds())
        {
            var events = log.Read(sagaId);
            var last = events[^1];
            if (last.Type is SagaEventType.Completed or SagaEventType.Compensated)
                continue;
            if (last.Type == SagaEventType.Stuck)
                alerts.Add(new SagaAlert(sagaId, $"stuck: {last.Detail}"));
            else if (now - events[0].At > maxRunning)
                alerts.Add(new SagaAlert(sagaId, $"running for {(now - events[0].At).TotalMinutes:0} min, last entry {last.Type} {last.Step}".TrimEnd()));
        }
        return alerts;
    }
}
