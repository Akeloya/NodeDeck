namespace NodeDeck.Gateway;

/// <summary>Thread-safe command registration. Changes affect new invocations; running handlers finish normally.</summary>
public sealed class GatewayCommandRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, GatewayCommand> _commands = new(StringComparer.Ordinal);
    private long _revision = 1;

    public void Add(GatewayCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        AddRange([command]);
    }

    /// <summary>Adds a batch atomically. Duplicate IDs or null entries leave the registry unchanged.</summary>
    public void AddRange(IEnumerable<GatewayCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var batch = commands.ToArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in batch)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!ids.Add(command.Id))
                throw new ArgumentException($"Command '{command.Id}' already exists in the batch.", nameof(commands));
        }
        lock (_sync)
        {
            foreach (var command in batch)
                if (_commands.ContainsKey(command.Id))
                    throw new ArgumentException($"Command '{command.Id}' already exists.", nameof(commands));
            foreach (var command in batch)
                _commands.Add(command.Id, command);
            if (batch.Length > 0)
                _revision++;
        }
    }

    public void Replace(GatewayCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_sync)
        {
            if (!_commands.ContainsKey(command.Id))
                throw new KeyNotFoundException($"Command '{command.Id}' does not exist.");
            _commands[command.Id] = command;
            _revision++;
        }
    }

    public bool Remove(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        lock (_sync)
        {
            if (!_commands.Remove(commandId))
                return false;
            _revision++;
            return true;
        }
    }

    public IReadOnlyList<GatewayCommand> GetCommands() => GetSnapshot().Commands.Values.ToArray();

    internal Snapshot GetSnapshot()
    {
        lock (_sync)
            return new Snapshot(_revision, new Dictionary<string, GatewayCommand>(_commands, StringComparer.Ordinal));
    }

    internal sealed record Snapshot(long Revision, IReadOnlyDictionary<string, GatewayCommand> Commands)
    {
        public string UiRevision => $"ui-{Revision}";
    }
}
