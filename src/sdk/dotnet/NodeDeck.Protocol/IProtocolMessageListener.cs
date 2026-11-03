namespace NodeDeck.Protocol;

/// <summary>Accepts incoming NodeDeck message connections.</summary>
public interface IProtocolMessageListener : IAsyncDisposable
{
    /// <summary>Starts listening for connections.</summary>
    void Start();

    /// <summary>Waits for the next connection and returns its message transport.</summary>
    Task<IProtocolMessageConnection> AcceptAsync(CancellationToken cancellationToken = default);
}
