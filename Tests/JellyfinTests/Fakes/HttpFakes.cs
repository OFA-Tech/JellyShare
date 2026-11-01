using Xunit;

namespace JellyShare.JellyfinTests.Fakes;

/// <summary>Request lifetime that cancels <c>RequestAborted</c> on <c>Abort()</c>, as Kestrel does.</summary>
internal sealed class RecordingLifetime : Microsoft.AspNetCore.Http.Features.IHttpRequestLifetimeFeature
{
    private readonly CancellationTokenSource _cts;

    public RecordingLifetime(CancellationToken shared, CancellationToken extra)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(shared, extra);
        RequestAborted = _cts.Token;
    }

    public bool Aborted { get; private set; }

    public CancellationToken RequestAborted { get; set; }

    public void Abort()
    {
        Aborted = true;
        _cts.Cancel();
    }
}

/// <summary>Response feature that records OnCompleted callbacks so tests can finish a response.</summary>
internal sealed class CompletableResponseFeature : Microsoft.AspNetCore.Http.Features.HttpResponseFeature
{
    private readonly List<(Func<object, Task> Callback, object State)> _completed = new();

    public override void OnCompleted(Func<object, Task> callback, object state) => _completed.Add((callback, state));

    public async Task CompleteAsync()
    {
        var callbacks = _completed.ToList();
        _completed.Clear();
        foreach (var (callback, state) in callbacks)
        {
            await callback(state);
        }
    }
}
