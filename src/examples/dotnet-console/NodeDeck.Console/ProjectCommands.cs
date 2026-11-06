using System.Diagnostics;
using NodeDeck.Gateway;

namespace NodeDeck.Console;

internal sealed class ProjectCommands(CommandResolver resolver)
{
    public void Init() => resolver.Resolve(this);

    // CommandResolver discovers this property by the Can{MethodName} convention.
    public bool CanOpenProjectIssues => Environment.UserInteractive;

    [NodeCommand("project.openIssues", "Open NodeDeck issues",
        Description = "Open this project's GitHub issues in the gateway computer's default browser.",
        UnavailableReason = "An interactive desktop session is required.")]
    public void OpenProjectIssues()
    {
        using var browser = Process.Start(new ProcessStartInfo("https://github.com/Akeloya/NodeDeck/issues")
        {
            UseShellExecute = true
        });
    }
}