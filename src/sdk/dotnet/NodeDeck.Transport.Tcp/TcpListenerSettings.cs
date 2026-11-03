using NodeDeck.Core.Protocol;
using NodeDeck.Core.Settings;
using NLog;

namespace NodeDeck.Transport.Tcp
{
    public class TcpListenerSettings : IListenerSettings
    {
        public string Type => "tcp";
        public LogLevel LogLevel { get; set; } = LogLevel.Info;
        public string IPAddress { get; set; } = "0.0.0.0";
        public int Port { get; set; } = 5050;
        public int Backlog { get; set; } = 100;
        public int InitialMaxMessageBytes { get; set; } = ProtocolConstants.InitialMaxMessageBytes;
    }
}
