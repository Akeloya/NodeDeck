using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using NodeDeck.Core.Protocol;
using ProtocolValueType = NodeDeck.Core.Protocol.ValueType;

namespace NodeDeck.Gateway;

/// <summary>Resolves attributed instance methods and registers them as one atomic batch.</summary>
public sealed class CommandResolver
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly GatewayCommandRegistry _registry;

    public CommandResolver(GatewayCommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>
    /// Registers all attributed methods. Duplicate IDs or unsupported signatures leave the registry unchanged.
    /// The registry retains the owner through the generated handlers until the commands are removed or replaced.
    /// </summary>
    public IReadOnlyList<GatewayCommand> Resolve(object owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var commands = owner.GetType().GetMethods(Members)
            .Select(method => (Method: method, Attribute: method.GetCustomAttribute<NodeCommandAttribute>(inherit: true)))
            .Where(entry => entry.Attribute is not null)
            .Select(entry => ResolveMethod(owner, entry.Method, entry.Attribute!)).ToArray();
        _registry.AddRange(commands);
        return commands;
    }

    private static GatewayCommand ResolveMethod(object owner, MethodInfo method, NodeCommandAttribute attribute)
    {
        if (method.IsStatic || method.ContainsGenericParameters || method.IsSpecialName ||
            method.ReturnType.IsByRef || method.ReturnType == typeof(void) && method.IsDefined(typeof(AsyncStateMachineAttribute)))
            throw Invalid(method, "Use a non-generic instance method; async methods must return Task or ValueTask, not void.");

        var parameters = method.GetParameters();
        if (parameters.Any(parameter => parameter.ParameterType.IsByRef || parameter.IsOut) ||
            parameters.Count(parameter => parameter.ParameterType == typeof(CancellationToken)) > 1)
            throw Invalid(method, "ref/out parameters and multiple CancellationToken parameters are unsupported.");

        var properties = new Dictionary<string, ValueDescriptor>(StringComparer.Ordinal);
        var required = new List<string>();
        foreach (var parameter in parameters.Where(parameter => parameter.ParameterType != typeof(CancellationToken)))
        {
            var name = parameter.Name ?? throw Invalid(method, "Parameter names must be available.");
            properties.Add(name, Describe(parameter.ParameterType, method));
            if (!parameter.HasDefaultValue)
                required.Add(name);
            else if (parameter.DefaultValue is null ||
                ProtocolValidator.ValidateArguments(properties[name], JsonSerializer.SerializeToElement(
                    parameter.DefaultValue, parameter.ParameterType, ProtocolJson.Options)).Count != 0)
                throw Invalid(method, $"Default value for '{name}' is not supported by the protocol.");
        }

        var resultType = UnwrapReturnType(method.ReturnType);
        var resultProperties = new Dictionary<string, ValueDescriptor>(StringComparer.Ordinal);
        if (resultType != typeof(void))
            resultProperties.Add("value", Describe(resultType, method));

        var (title, description) = ResolveText(owner, method, attribute);
        return new GatewayCommand(attribute.Id, title, description,
            ObjectDescriptor(properties, required), ObjectDescriptor(resultProperties, resultProperties.Keys.ToArray()),
            async (arguments, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = parameters.Select(parameter => parameter.ParameterType == typeof(CancellationToken)
                    ? (object)cancellationToken
                    : arguments.TryGetProperty(parameter.Name!, out var value)
                        ? value.Deserialize(parameter.ParameterType, ProtocolJson.Options)
                        : parameter.DefaultValue).ToArray();
                var invocation = Invoke(method, owner, values);
                var output = await AwaitResult(invocation, method.ReturnType).ConfigureAwait(false);
                return resultType == typeof(void)
                    ? JsonSerializer.SerializeToElement(new { }, ProtocolJson.Options)
                    : JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["value"] = output }, ProtocolJson.Options);
            }, ResolveAvailability(owner, method, attribute));
    }

    private static (string Title, string? Description) ResolveText(object owner, MethodInfo method, NodeCommandAttribute attribute)
    {
        if (attribute.ResourceType is not { } resourceType)
            return (attribute.Title!, attribute.Description);

        var prefix = $"{owner.GetType().Name}_{method.Name}";
        // Strongly typed resources can expose exact-case properties even when the .resx
        // compiler cannot preserve keys that differ only in case.
        var titleProperty = resourceType.GetProperty($"{prefix}_name", Members);
        if (titleProperty is not null)
        {
            var title = ReadResourceProperty(titleProperty, method);
            if (string.IsNullOrWhiteSpace(title))
                throw Invalid(method, $"Resource '{resourceType.FullName}' must provide a non-empty string '{prefix}_name'.");
            var descriptionProperty = resourceType.GetProperty($"{prefix}_description", Members);
            var description = attribute.Description ?? (descriptionProperty is null ? null : ReadResourceProperty(descriptionProperty, method));
            return (title, description);
        }

        // Never mutate a resource class's shared ResourceManager or its IgnoreCase setting.
        var resources = new ResourceManager(resourceType) { IgnoreCase = false };
        try
        {
            var title = resources.GetString($"{prefix}_name", CultureInfo.CurrentUICulture);
            if (string.IsNullOrWhiteSpace(title))
                throw Invalid(method, $"Resource '{resourceType.FullName}' must contain a non-empty string '{prefix}_name' (case-sensitive).");
            var description = attribute.Description ?? resources.GetString($"{prefix}_description", CultureInfo.CurrentUICulture);
            return (title, description);
        }
        catch (Exception ex) when (ex is MissingManifestResourceException or MissingSatelliteAssemblyException or InvalidOperationException)
        {
            throw new ArgumentException($"Cannot resolve command method '{method.DeclaringType?.FullName}.{method.Name}' " +
                $"from resource '{resourceType.FullName}' using keys '{prefix}_name' and '{prefix}_description'.", ex);
        }
        finally
        {
            resources.ReleaseAllResources();
        }
    }

    private static string? ReadResourceProperty(PropertyInfo property, MethodInfo command)
    {
        if (property.PropertyType != typeof(string) || property.GetGetMethod(nonPublic: true) is not { IsStatic: true } getter ||
            property.GetIndexParameters().Length != 0)
            throw Invalid(command, $"Resource property '{property.DeclaringType?.FullName}.{property.Name}' must be a readable static string property.");
        return getter.CreateDelegate<Func<string?>>()();
    }

    private static ICommandAvailability? ResolveAvailability(object owner, MethodInfo command, NodeCommandAttribute attribute)
    {
        var name = attribute.CanExecute ?? $"Can{command.Name}";
        var properties = owner.GetType().GetProperties(Members).Where(property => property.Name == name).ToArray();
        Func<bool> evaluate;
        if (properties.Length == 1)
        {
            var property = properties[0];
            var getter = property.GetGetMethod(nonPublic: true);
            if (getter is null || property.GetIndexParameters().Length != 0)
                throw Invalid(command, $"Availability property '{name}' needs a parameterless getter.");
            if (property.PropertyType == typeof(bool))
                evaluate = Bind<Func<bool>>(getter, owner);
            else if (property.PropertyType == typeof(Func<bool>))
            {
                var getDelegate = Bind<Func<Func<bool>>>(getter, owner);
                evaluate = () => (getDelegate() ?? throw new InvalidOperationException($"Availability '{name}' returned null."))();
            }
            else
                throw Invalid(command, $"Availability property '{name}' must return bool or Func<bool>.");
        }
        else if (properties.Length > 1)
            throw Invalid(command, $"Availability property '{name}' is ambiguous.");
        else if (attribute.CanExecute is not null)
        {
            var methods = owner.GetType().GetMethods(Members).Where(method => method.Name == name &&
                method.ReturnType == typeof(bool) && !method.ContainsGenericParameters && method.GetParameters().Length == 0).ToArray();
            if (methods.Length != 1)
                throw Invalid(command, $"Availability '{name}' must name a bool/Func<bool> property or a parameterless bool method.");
            evaluate = Bind<Func<bool>>(methods[0], owner);
        }
        else
            return null;

        return new DelegateCommandAvailability(() => evaluate()
            ? new CommandAvailability(true)
            : new CommandAvailability(false, attribute.UnavailableReason));
    }

    private static T Bind<T>(MethodInfo method, object owner) where T : Delegate =>
        method.IsStatic ? method.CreateDelegate<T>() : method.CreateDelegate<T>(owner);

    private static Type UnwrapReturnType(Type type) =>
        type == typeof(Task) || type == typeof(ValueTask) ? typeof(void) :
        type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>))
            ? type.GetGenericArguments()[0] : type;

    private static async ValueTask<object?> AwaitResult(object? invocation, Type returnType)
    {
        if (invocation is null && returnType != typeof(void))
            throw new InvalidOperationException("A command returned null; the protocol does not support null results.");
        if (invocation is Task task)
        {
            await task.ConfigureAwait(false);
            return returnType.IsGenericType ? returnType.GetProperty(nameof(Task<object>.Result))!.GetValue(task) : null;
        }
        if (returnType == typeof(ValueTask))
        {
            await ((ValueTask)invocation!).ConfigureAwait(false);
            return null;
        }
        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var valueTask = (Task)returnType.GetMethod(nameof(ValueTask<object>.AsTask))!.Invoke(invocation, null)!;
            await valueTask.ConfigureAwait(false);
            return valueTask.GetType().GetProperty(nameof(Task<object>.Result))!.GetValue(valueTask);
        }
        return invocation;
    }

    private static object? Invoke(MethodInfo method, object owner, object?[] arguments)
    {
        try
        {
            return method.Invoke(owner, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }

    private static ValueDescriptor Describe(Type type, MethodInfo method)
    {
        if (type == typeof(string))
            return new() { Type = ProtocolValueType.String };
        if (type == typeof(bool))
            return new() { Type = ProtocolValueType.Boolean };
        if (type == typeof(int))
            return new() { Type = ProtocolValueType.Integer, Minimum = int.MinValue, Maximum = int.MaxValue };
        if (type == typeof(long))
            return new() { Type = ProtocolValueType.Integer, Minimum = -ProtocolConstants.MaxSafeInteger, Maximum = ProtocolConstants.MaxSafeInteger };
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return new() { Type = ProtocolValueType.Number,
                Minimum = type == typeof(float) ? -float.MaxValue : type == typeof(decimal) ? (double)decimal.MinValue : null,
                Maximum = type == typeof(float) ? float.MaxValue : type == typeof(decimal) ? (double)decimal.MaxValue : null };
        if (type.IsArray && type.GetArrayRank() == 1 && type == type.GetElementType()!.MakeArrayType())
            return new() { Type = ProtocolValueType.Array, Items = Describe(type.GetElementType()!, method) };
        throw Invalid(method, $"Type '{type}' is unsupported. Use string, bool, int, long, float, double, decimal, or arrays of these types.");
    }

    private static ValueDescriptor ObjectDescriptor(IReadOnlyDictionary<string, ValueDescriptor> properties, IReadOnlyList<string> required) =>
        new() { Type = ProtocolValueType.Object, Properties = properties, Required = required, AdditionalProperties = false };

    private static ArgumentException Invalid(MethodInfo method, string message) =>
        new($"Cannot resolve command method '{method.DeclaringType?.FullName}.{method.Name}': {message}");
}
