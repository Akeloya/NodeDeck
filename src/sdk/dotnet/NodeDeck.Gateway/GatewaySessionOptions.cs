namespace NodeDeck.Gateway;

public sealed record GatewaySessionOptions
{
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan InactivityTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ActiveUsersLogInterval { get; init; } = TimeSpan.FromMinutes(1);

    internal void Validate()
    {
        if (PingInterval <= TimeSpan.Zero || InactivityTimeout <= PingInterval || ActiveUsersLogInterval <= TimeSpan.Zero)
            throw new ArgumentException("Session intervals must be positive and the inactivity timeout must exceed the ping interval.");
    }
}

/// <summary>A session snapshot. Authentication tokens are deliberately excluded.</summary>
public sealed record GatewaySessionInfo(string SessionId, string SubjectId, string DeviceId, string DeviceName,
    string Transport, DateTimeOffset ConnectedAt, DateTimeOffset LastSeenAt, DateTimeOffset ExpiresAt);
