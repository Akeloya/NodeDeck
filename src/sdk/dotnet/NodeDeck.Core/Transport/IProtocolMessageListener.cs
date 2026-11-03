namespace NodeDeck.Core.Transport;

/// <summary>Accepts incoming NodeDeck message connections.</summary>
public interface IProtocolMessageListener : IAsyncDisposable
{
    /// <summary>Completes once the transport is ready after Start, including asynchronous discovery.</summary>
    Task Ready => Task.CompletedTask;

    /// <summary>Starts listening for connections.</summary>
    void Start();

    /// <summary>Waits for the next connection and returns its message transport.</summary>
    Task<IProtocolMessageConnection> AcceptAsync(CancellationToken cancellationToken = default);
}
