namespace PicoMediator;

public sealed class Mediator(ISvcScope scope) : IMediator
{
    /// <summary>
    /// Optional callback invoked when a notification has no registered handlers.
    /// Receives the notification type name. Silent drop by default (PUB/SUB semantics).
    /// </summary>
    public Action<string>? OnNoSubscribers { get; set; }

    public ValueTask<TResponse> Send<TCommand, TResponse>(
        TCommand command,
        CancellationToken ct = default
    )
        where TCommand : ICommand<TResponse> =>
        GeneratedDispatch.Send<TCommand, TResponse>(scope, command, ct);

    public async ValueTask Publish<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IEvent
    {
        if (!scope.TryGetServices(typeof(ISubscriber<TEvent>), out var rawServices))
        {
            OnNoSubscribers?.Invoke(typeof(TEvent).FullName!);
            return;
        }

        List<Exception>? exceptions = null;
        foreach (var raw in rawServices)
        {
            var h = (ISubscriber<TEvent>)raw;
            try
            {
                await h.Handle(@event, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                exceptions ??= [];
                exceptions.Add(ex);
            }
        }

        if (exceptions is { Count: > 0 })
            throw new AggregateException(exceptions);
    }

    public async ValueTask PublishParallel<TEvent>(TEvent @event, CancellationToken ct = default)
        where TEvent : IEvent
    {
        if (!scope.TryGetServices(typeof(ISubscriber<TEvent>), out var rawServices))
        {
            OnNoSubscribers?.Invoke(typeof(TEvent).FullName!);
            return;
        }

        var count = rawServices.Count;
        var tasks = new Task[count];
        var exceptions = new Exception?[count];

        for (var i = 0; i < count; i++)
        {
            var idx = i;
            var handler = (ISubscriber<TEvent>)rawServices[idx];
            tasks[i] = HandleSafelyAsync(handler, @event, ct, exceptions, idx);
        }

        await Task.WhenAll(tasks);

        var actual = new List<Exception>(count);
        foreach (var e in exceptions)
        {
            if (e is not null)
                actual.Add(e);
        }

        if (actual.Count > 0)
            throw new AggregateException(actual);
    }

    private static async Task HandleSafelyAsync<TEvent>(
        ISubscriber<TEvent> subscriber,
        TEvent @event,
        CancellationToken ct,
        Exception?[] exceptions,
        int index
    )
        where TEvent : IEvent
    {
        try
        {
            await subscriber.Handle(@event, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            exceptions[index] = ex;
        }
    }
}
