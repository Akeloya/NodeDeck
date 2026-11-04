using CommandLine;

namespace NodeDeck.Console;

internal sealed class ConsoleOptions
{
    [Option("transport", HelpText = "Select tcp or bluetooth.")]
    public string? Transport { get; set; }

    [Option("debug-profile", HelpText = "Select tcp or bluetooth when running under the debugger.")]
    public string? DebugProfile { get; set; }

    [Option("bind", HelpText = "TCP listener IP address.")]
    public string? Bind { get; set; }

    [Option("port", HelpText = "TCP listener port.")]
    public int? Port { get; set; }

    [Option("secret", Default = "nodedeck-dev", HelpText = "Shared secret for client authentication.")]
    public string Secret { get; set; } = "nodedeck-dev";

    [Option("settings", Min = 1, MetaValue = "FILE...", HelpText = "Load one or more listener JSON files and start their transports together.")]
    public IEnumerable<string> Settings { get; set; } = [];

    [Option("save-settings", MetaValue = "FILE", HelpText = "Save a single listener configuration as JSON and exit.")]
    public string? SaveSettings { get; set; }
}
