namespace NodeDeck.Gateway;

/// <summary>Exposes a method as a gateway command through <see cref="CommandResolver"/>.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class NodeCommandAttribute : Attribute
{
    public NodeCommandAttribute(string id, string title)
    {
        Id = id;
        Title = title;
    }

    /// <summary>
    /// Resolves {ClassName}_{MethodName}_name and {ClassName}_{MethodName}_description
    /// from the resource type, using exact case and the current UI culture at resolution time.
    /// </summary>
    public NodeCommandAttribute(string id, Type resourceType)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        Id = id;
        ResourceType = resourceType;
    }

    public string Id { get; }
    public string? Title { get; }
    public Type? ResourceType { get; }
    public string? Description { get; set; }

    /// <summary>
    /// Name of a bool property, parameterless bool method, or Func&lt;bool&gt; property on the command owner.
    /// Defaults to the optional Can{MethodName} property. Use nameof for explicit bindings.
    /// </summary>
    public string? CanExecute { get; set; }
    public string? UnavailableReason { get; set; }
}
