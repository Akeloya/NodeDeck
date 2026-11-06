using System.Text.Json;
using NodeDeck.Core.Protocol;

namespace NodeDeck.Gateway;

public delegate ValueTask<JsonElement> GatewayCommandHandler(JsonElement arguments, CancellationToken cancellationToken);

/// <summary>Evaluates current gateway state. Implementations must support concurrent calls and avoid side effects.</summary>
public interface ICommandAvailability
{
    CommandAvailability GetAvailability();
}

public sealed class DelegateCommandAvailability : ICommandAvailability
{
    private readonly Func<CommandAvailability> _evaluate;

    public DelegateCommandAvailability(Func<CommandAvailability> evaluate)
    {
        ArgumentNullException.ThrowIfNull(evaluate);
        _evaluate = evaluate;
    }

    public CommandAvailability GetAvailability() => _evaluate();
}

/// <summary>A complete executable command. Reuse a handler or availability policy across multiple commands.</summary>
public sealed class GatewayCommand
{
    private static readonly ICommandAvailability AlwaysAvailable =
        new DelegateCommandAvailability(() => new CommandAvailability(true));

    public GatewayCommand(string id, string title, string? description, ValueDescriptor parameters,
        ValueDescriptor result, GatewayCommandHandler handler, ICommandAvailability? availability = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(handler);
        Id = id;
        Title = title;
        Description = description;
        Parameters = parameters;
        Result = result;
        Handler = handler;
        Availability = availability ?? AlwaysAvailable;
        ProtocolValidator.ValidateCommandDefinition(new CommandDefinition(id, title, description, parameters, result, new(true)));
    }

    public string Id { get; }
    public string Title { get; }
    public string? Description { get; }
    public ValueDescriptor Parameters { get; }
    public ValueDescriptor Result { get; }
    public GatewayCommandHandler Handler { get; }
    public ICommandAvailability Availability { get; }

    internal CommandAvailability GetAvailability() => Availability.GetAvailability()
        ?? throw new InvalidOperationException("An availability policy returned null.");

    internal CommandDefinition GetDefinition() => new(Id, Title, Description, Parameters, Result, GetAvailability());
}
