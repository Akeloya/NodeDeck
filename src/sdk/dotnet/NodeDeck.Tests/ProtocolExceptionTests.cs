using System.Text.Json;
using NodeDeck.Protocol;
using NUnit.Framework;

namespace NodeDeck.Tests;

public class ProtocolExceptionTests
{
    [Test]
    public void MalformedJsonRetainsItsProtocolCodeAndParsingError()
    {
        var exception = Assert.Throws<InvalidRequestException>(
            () => ProtocolJson.Deserialize<ProtocolRequest>("{"u8));

        Assert.That(exception, Is.InstanceOf<ProtocolValidationException>());
        Assert.That(exception!.Code, Is.EqualTo(ProtocolErrorCode.InvalidRequest));
        Assert.That(exception.InnerException, Is.InstanceOf<JsonException>());
    }

    [Test]
    public void OversizedMessageHasADistinctValidationException()
    {
        var exception = Assert.Throws<MessageTooLargeException>(
            () => ProtocolJson.Deserialize<ProtocolRequest>("{}"u8, maxBytes: 1));

        Assert.That(exception, Is.InstanceOf<NodeDeckProtocolException>());
        Assert.That(exception!.Code, Is.EqualTo(ProtocolErrorCode.MessageTooLarge));
    }
}
