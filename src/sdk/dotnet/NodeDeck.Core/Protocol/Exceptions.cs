namespace NodeDeck.Core.Protocol;

public abstract class NodeDeckProtocolException(string code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}

public abstract class ProtocolValidationException(string code, string message, Exception? innerException = null)
    : NodeDeckProtocolException(code, message, innerException);

public sealed class InvalidRequestException(string message, Exception? innerException = null)
    : ProtocolValidationException(ProtocolErrorCode.InvalidRequest, message, innerException);

public sealed class InvalidResponseException(string message, Exception? innerException = null)
    : ProtocolValidationException(ProtocolErrorCode.InvalidResponse, message, innerException);

public sealed class UnsupportedVersionException(string message, Exception? innerException = null)
    : ProtocolValidationException(ProtocolErrorCode.UnsupportedVersion, message, innerException);

public sealed class UnauthorizedException(string message, Exception? innerException = null)
    : ProtocolValidationException(ProtocolErrorCode.Unauthorized, message, innerException);

public sealed class MessageTooLargeException(string message, Exception? innerException = null)
    : ProtocolValidationException(ProtocolErrorCode.MessageTooLarge, message, innerException);

public sealed class OperationNotFoundException(string message, Exception? innerException = null)
    : ProtocolValidationException(ProtocolErrorCode.OperationNotFound, message, innerException);

public sealed class RequestConflictException(string message, Exception? innerException = null)
    : ProtocolValidationException(ProtocolErrorCode.RequestConflict, message, innerException);
