namespace Jonot.Functions.Models;

/// <summary>
/// Message sent to the centralized api-send-queue for rate-limited dispatch to the external API.
/// </summary>
public sealed record ApiSendMessage
{
    public required string CorrelationId { get; init; }
    public required SourceType SourceType { get; init; }
    public required string Payload { get; init; }
    public int RetryCount { get; init; }
}
