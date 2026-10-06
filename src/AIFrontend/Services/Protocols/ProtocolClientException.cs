using System;

namespace AIFrontend.Services.Protocols;

/// <summary>Safe client error. Messages/codes are local constants, never provider response text.</summary>
public sealed class ProtocolClientException : Exception
{
    public ProtocolClientException(string message, string code, int? statusCode = null) : base(message)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public int? StatusCode { get; }
}
