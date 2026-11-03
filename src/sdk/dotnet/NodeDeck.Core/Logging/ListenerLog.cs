using NLog;

namespace NodeDeck.Core.Logging;

/// <summary>Applies one listener's minimum level before forwarding events to the host's NLog targets.</summary>
public sealed class ListenerLog(Logger logger, LogLevel minimumLevel)
{
    public bool IsEnabled(LogLevel level) => level >= minimumLevel && logger.IsEnabled(level);

    public void Write(LogLevel level, string message, Exception? error = null)
    {
        if (IsEnabled(level))
            logger.Log(level, error, message);
    }
}
