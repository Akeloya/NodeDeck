using System.Text.Json;
using NLog;
using NodeDeck.Core.Transport;
using NodeDeck.Transport.Tcp;
using NUnit.Framework;

namespace NodeDeck.Tests;

public sealed class ListenerSettingsTests
{
    private readonly ProtocolMessageListenerFactory _factory = new ProtocolMessageListenerFactory()
        .Register<TcpListenerSettings>(settings => new TcpJsonServer(settings));

    [Test]
    public async Task SavedSettingsCreateConfiguredTcpListener()
    {
        var path = Path.GetTempFileName();
        try
        {
            _factory.SaveSettings(path, new TcpListenerSettings
            {
                IPAddress = "127.0.0.1", Port = 0, Backlog = 5, InitialMaxMessageBytes = 128, LogLevel = LogLevel.Off
            });
            var settings = (TcpListenerSettings)_factory.LoadSettings(path);
            Assert.That(settings.LogLevel, Is.EqualTo(LogLevel.Off));
            Assert.That(settings.Backlog, Is.EqualTo(5));
            await using var listener = _factory.Create(settings);
            listener.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var client = await TcpJsonConnection.ConnectAsync(settings.IPAddress, ((TcpJsonServer)listener).Port,
                cancellationToken: timeout.Token);
            await using var accepted = await listener.AcceptAsync(timeout.Token);
            Assert.That(accepted.MaxMessageBytes, Is.EqualTo(128));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestCase("{}")]
    [TestCase("""{"Type":"unknown"}""")]
    [TestCase("""{"Type":"tcp","LogLevel":"invalid"}""")]
    public void InvalidSettingsAreRejected(string json)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            Assert.Throws<JsonException>(() => _factory.LoadSettings(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
