namespace AIFrontend.Services.OpenCode;

public sealed record ClearEncryptedResult(long ChangedMessages, long ChangedEvents, long RemovedFields);

/// <summary>Contains only bounded, recognized error metadata; never arbitrary backend text/data.</summary>
public sealed class OpenCodePluginMutationRpcException(int statusCode, string? failureCode,
    string safeReason, bool isDefinitiveRejection) : OpenCodeException(safeReason)
{
    public int StatusCode { get; } = statusCode;
    public string? FailureCode { get; } = failureCode;
    public bool IsDefinitiveRejection { get; } = isDefinitiveRejection;
}
