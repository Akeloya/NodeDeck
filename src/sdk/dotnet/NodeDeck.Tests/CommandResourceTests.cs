using NodeDeck.Gateway;
using NodeDeck.Tests.Resources;
using NUnit.Framework;

namespace NodeDeck.Tests;

public class CommandResourceTests
{
    [Test]
    [SetUICulture("en-US")]
    public void Resolve_ResourceKeysPreserveClassAndMethodCase()
    {
        var registry = new GatewayCommandRegistry();
        var resolver = new CommandResolver(registry);
        var upper = resolver.Resolve(new ProjectCommands());
        var lower = resolver.Resolve(new projectCommands()).Single();
        Assert.That(upper.Single(c => c.Id == "upper").Title, Is.EqualTo("Project issues"));
        Assert.That(upper.Single(c => c.Id == "upper").Description, Is.EqualTo("Uppercase class description"));
        Assert.That(upper.Single(c => c.Id == "lowerMethod").Title, Is.EqualTo("Lowercase method"));
        Assert.That(upper.Single(c => c.Id == "lowerMethod").Description, Is.EqualTo("Lowercase method description"));
        Assert.That(lower.Title, Is.EqualTo("Other project issues"));
        Assert.That(lower.Description, Is.EqualTo("Lowercase class description"));
        Assert.That(registry.GetCommands(), Has.Count.EqualTo(3));
    }

    [Test]
    [SetUICulture("ru-RU")]
    public void Resolve_UsesCurrentUiCultureAndResourceFallback()
    {
        var commands = new CommandResolver(new()).Resolve(new LocalizedCommands());
        Assert.That(commands.Single(c => c.Id == "upper").Title, Is.EqualTo("Задачи проекта"));
        Assert.That(commands.Single(c => c.Id == "upper").Description, Is.EqualTo("Открыть задачи проекта"));
        Assert.That(commands.Single(c => c.Id == "fallback").Title, Is.EqualTo("Fallback title"));
    }

    [Test]
    public void Resolve_DescriptionIsOptionalAndCanBeOverridden()
    {
        var resolver = new CommandResolver(new());
        Assert.That(resolver.Resolve(new OptionalDescription()).Single().Description, Is.Null);
        Assert.That(resolver.Resolve(new ExplicitDescription()).Single().Description, Is.EqualTo("Explicit description"));
    }

    [Test]
    public void Resolve_WrongCaseTitleDoesNotMatchOrPartiallyRegister()
    {
        var registry = new GatewayCommandRegistry();
        var error = Assert.Throws<ArgumentException>(() => new CommandResolver(registry).Resolve(new MissingTitle()));
        Assert.That(error!.Message, Does.Contain("MissingTitle_Run_name"));
        Assert.That(registry.GetCommands(), Is.Empty);
    }

    [Test]
    public void Resolve_MissingResourceReportsResourceTypeAndKey()
    {
        var error = Assert.Throws<ArgumentException>(() => new CommandResolver(new()).Resolve(new MissingResource()));
        Assert.That(error!.Message, Does.Contain(typeof(MissingResource).FullName));
        Assert.That(error.Message, Does.Contain("MissingResource_Run_name"));
    }

    private sealed class ProjectCommands
    {
        [NodeCommand("upper", typeof(CaseSensitiveStrings))] public void OpenIssues() { }
        [NodeCommand("lowerMethod", typeof(CaseSensitiveStrings))] public void openIssues() { }
    }
    private sealed class projectCommands
    {
        [NodeCommand("lower", typeof(CaseSensitiveStrings))] public void OpenIssues() { }
    }
    private sealed class LocalizedCommands
    {
        [NodeCommand("upper", typeof(CommandStrings))] public void OpenIssues() { }
        [NodeCommand("fallback", typeof(CommandStrings))] public void Fallback() { }
    }
    private static class CaseSensitiveStrings
    {
        public static string ProjectCommands_OpenIssues_name => "Project issues";
        public static string ProjectCommands_OpenIssues_description => "Uppercase class description";
        public static string projectCommands_OpenIssues_name => "Other project issues";
        public static string projectCommands_OpenIssues_description => "Lowercase class description";
        public static string ProjectCommands_openIssues_name => "Lowercase method";
        public static string ProjectCommands_openIssues_description => "Lowercase method description";
    }
    private sealed class OptionalDescription
    {
        [NodeCommand("optional", typeof(CommandStrings))] public void Run() { }
    }
    private sealed class ExplicitDescription
    {
        [NodeCommand("explicit", typeof(CommandStrings), Description = "Explicit description")] public void Run() { }
    }
    private sealed class MissingTitle
    {
        [NodeCommand("valid", "Valid")] public void Valid() { }
        [NodeCommand("missing", typeof(CommandStrings))] public void Run() { }
    }
    private sealed class MissingResource
    {
        [NodeCommand("missing", typeof(MissingResource))] public void Run() { }
    }
}
