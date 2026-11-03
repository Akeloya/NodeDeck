using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NodeDeck.Protocol;

namespace NodeDeck.Transport.Tcp;

/// <summary>Newline-delimited JSON connection used by the initial local-development TCP profile.</summary>
public sealed class TcpJsonConnection : IProtocolMessageConnection
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _maxMessageBytes;
    private readonly byte[] _readBuffer = new byte[4096];
    private int _readOffset;
    private int _readCount;

    private TcpJsonConnection(TcpClient client, int maxMessageBytes)
    {
        _client = client;
        _stream = client.GetStream();
        _maxMessageBytes = maxMessageBytes;
    }

    public static async Task<TcpJsonConnection> ConnectAsync(string host, int port, int maxMessageBytes = ProtocolConstants.InitialMaxMessageBytes,
        CancellationToken cancellationToken = default)
    {
        ValidateArguments(host, port, maxMessageBytes);
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            return new TcpJsonConnection(client, maxMessageBytes);
        }
        catch { client.Dispose(); throw; }
    }

    public static TcpJsonConnection FromAcceptedClient(TcpClient client, int maxMessageBytes = ProtocolConstants.InitialMaxMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (maxMessageBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        client.NoDelay = true;
        return new TcpJsonConnection(client, maxMessageBytes);
    }

    public int MaxMessageBytes => _maxMessageBytes;
    public void SetMaxMessageBytes(int maxMessageBytes)
    {
        if (maxMessageBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        _maxMessageBytes = maxMessageBytes;
    }

    public async Task SendAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        var bytes = ProtocolJson.Serialize(message);
        if (bytes.Length > _maxMessageBytes)
            throw new MessageTooLargeException("Message exceeds the configured TCP frame limit.");
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(new byte[] { ProtocolConstants.FrameDelimiter }, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<T?> ReceiveAsync<T>(CancellationToken cancellationToken = default)
    {
        using var frame = new MemoryStream();
        while (true)
        {
            if (_readOffset == _readCount)
            {
                _readCount = await _stream.ReadAsync(_readBuffer, cancellationToken).ConfigureAwait(false);
                _readOffset = 0;
                if (_readCount == 0)
                {
                    if (frame.Length == 0)
                        return default;
                    throw new InvalidRequestException("TCP connection ended in the middle of a frame.");
                }
            }
            var newline = Array.IndexOf(_readBuffer, ProtocolConstants.FrameDelimiter, _readOffset, _readCount - _readOffset);
            var end = newline < 0 ? _readCount : newline;
            var count = end - _readOffset;
            if (frame.Length + count > _maxMessageBytes)
                throw new MessageTooLargeException("TCP frame exceeds the configured limit.");
            frame.Write(_readBuffer, _readOffset, count);
            _readOffset = newline < 0 ? _readCount : newline + 1;
            if (newline < 0)
                continue;
            if (frame.Length == 0)
                throw new InvalidRequestException("Empty TCP frames are not allowed.");
            var bytes = frame.ToArray();
            if (bytes[^1] == ProtocolConstants.CarriageReturn)
                Array.Resize(ref bytes, bytes.Length - 1);
            return ProtocolJson.Deserialize<T>(bytes, _maxMessageBytes);
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _client.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }

    private static void ValidateArguments(string host, int port, int maxMessageBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (maxMessageBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
    }
}

public sealed class TcpJsonServer : IProtocolMessageListener
{
    private readonly TcpListener _listener;
    private readonly int _maxMessageBytes;

    public TcpJsonServer(System.Net.IPAddress address, int port, int maxMessageBytes = ProtocolConstants.InitialMaxMessageBytes)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (maxMessageBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxMessageBytes));
        _listener = new TcpListener(address, port);
        _maxMessageBytes = maxMessageBytes;
    }

    public int Port => ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
    public void Start(int backlog = 100) => _listener.Start(backlog);
    void IProtocolMessageListener.Start() => Start();
    public async Task<IProtocolMessageConnection> AcceptAsync(CancellationToken cancellationToken = default) =>
        TcpJsonConnection.FromAcceptedClient(await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false),
            Math.Min(_maxMessageBytes, ProtocolConstants.InitialMaxMessageBytes));
    public ValueTask DisposeAsync() { _listener.Stop(); return ValueTask.CompletedTask; }
}
