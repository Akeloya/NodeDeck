using NLog;

namespace NodeDeck.Core.Settings
{
    public interface IListenerSettings
    {
        string Type { get; }
        LogLevel LogLevel { get; }
    }
}
