using System.Collections.Concurrent;
using Proctoring.Application.Events;

namespace Proctoring.Infrastructure.Notifications;

/// <summary>
/// Wakes SSE streams of the same process immediately after a change. Streams also poll the database, so this is
/// purely a latency optimisation; with several backend instances the Redis notifier forwards signals between them.
/// </summary>
public sealed class InProcessEventUpdateNotifier : IEventUpdateNotifier
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiters = new(StringComparer.Ordinal);

    public void Notify(string sessionId)
    {
        if (_waiters.TryRemove(sessionId, out var waiter)) waiter.TrySetResult();
    }

    public async Task WaitAsync(string sessionId, TimeSpan timeout, CancellationToken ct)
    {
        var waiter = _waiters.GetOrAdd(sessionId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await waiter.Task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            // No change within the timeout: the caller polls the database anyway.
        }
        finally
        {
            if (!waiter.Task.IsCompleted) _waiters.TryRemove(KeyValuePair.Create(sessionId, waiter));
        }
    }
}
