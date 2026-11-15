# Saga pattern in .NET: the payment went through, the stock did not

Companion code for the Nippysoft post on the Saga pattern
([English](https://nippysoft.com/en/blog/software-architecture/saga-pattern-distributed-transactions-dotnet),
[Spanish](https://nippysoft.com/es/blog/arquitectura-de-software/patron-saga-transacciones-distribuidas-dotnet)).

An online store order touches four services: orders, payments, inventory and email. Each one has
its own database, so there is no single transaction that can be rolled back. This repo shows what
goes wrong without a saga, and a small orchestrated saga that undoes the earlier steps when a
later one fails.

Everything runs in memory. There is no broker, no database and no network: the "services" are
classes with planned failures, so every scenario is repeatable.

## Run it

```bash
dotnet test                                  # 20 tests
dotnet run --project tools/SagaDemo          # seven scenarios, with the saga log
dotnet run --project tools/HostileInputs     # 21 bad or odd inputs and what comes out
```

Requires the .NET 10 SDK.

## What is where

| Path | What it shows |
|---|---|
| `src/OrderSaga/Before/NaiveCheckout.cs` | Five calls in a row. When the stock runs out, the customer has already paid. |
| `src/OrderSaga/Orchestration/SagaStep.cs` | The five steps of the order. The first three can be undone; the last two come after the point of no return. |
| `src/OrderSaga/Orchestration/SagaOrchestrator.cs` | Runs the steps, logs each move, undoes started steps newest first, marks the saga Stuck when an undo keeps failing. |
| `src/OrderSaga/Orchestration/SagaLog.cs` | In-memory log and a JSON-lines file log that survives a crash. |
| `src/OrderSaga/Orchestration/RetryPolicy.cs` | Retries only transient failures, waiting 1 s, 2 s, ... |
| `src/OrderSaga/Orchestration/SagaMonitor.cs` | Lists stuck sagas and sagas nobody resumed. |
| `src/OrderSaga/Choreography/ChoreographedShop.cs` | The same order with events and no orchestrator. |
| `src/OrderSaga/Services/` | The fake services. Each remembers the idempotency keys it applied. |

## What this code does not do

- Two processes working on the same saga at once are not prevented. That covers two `ResumeAsync`
  calls, and also two `RunAsync` calls with the same saga id: checking that the saga is not in the
  log and writing its `Started` entry are two separate operations, so both calls can start it. Use
  a lease, a row lock or a unique constraint on the saga id.
- Only `TransientException` and `BusinessRuleException` are handled. Any other exception from a
  service escapes `RunAsync` or `ResumeAsync`: the saga is not marked Stuck and no alert goes out.
  It stays in the log without a final entry, so `SagaMonitor` lists it only after `maxRunning`.
- The retry policy has no cancellation (`CancellationToken`), no timeout per call, no jitter and no
  limit on the total time spent retrying.
- `RetryPolicy.Waits` is a plain `List<TimeSpan>` for the demo and the tests. It is not safe for
  concurrent writes and it keeps growing with every retry of every saga that shares the policy.
- The in-memory event bus has no outbox: a service can save its change and lose the event.
- The fake services keep the change and the idempotency key together in memory. A real one has to
  save both in the same database transaction.
- A call that timed out can still reach a real service after its compensation ran (a charge landing
  after the refund). The fake services answer at once, so this cannot happen here; a real service
  has to remember the compensation and reject the late call.
- Nothing hides a half-finished saga from other readers (no isolation): another request can see a
  charged order whose stock is not reserved yet.
- Amounts are not checked for currency precision.

For production, look at a workflow engine or a messaging library with saga support (MassTransit,
NServiceBus, Dapr Workflow, Durable Functions, Temporal) and check its license first.

## License

MIT. See [LICENSE](LICENSE).
