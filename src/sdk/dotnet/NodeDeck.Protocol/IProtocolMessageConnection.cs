namespace NodeDeck.Protocol;

/// <summary>Exchanges complete NodeDeck messages over a connected transport.</summary>
public interface IProtocolMessageConnection : IAsyncDisposable
{
    /// <summary>Gets the current limit on the size of a serialized message in bytes.</summary>
    int MaxMessageBytes { get; }

    /// <summary>Applies the negotiated message size limit to subsequent sends and receives.</summary>
    void SetMaxMessageBytes(int maxMessageBytes);

    /// <summary>Serializes and sends one message.</summary>
    Task SendAsync<T>(T message, CancellationToken cancellationToken = default);

    /// <summary>Receives and deserializes the next complete message from the peer.</summary>
    Task<T?> ReceiveAsync<T>(CancellationToken cancellationToken = default);
}
