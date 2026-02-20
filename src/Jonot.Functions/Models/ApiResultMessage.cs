namespace Jonot.Functions.Models;

/// <summary>
/// Result message returned from the external API call, routed back to the appropriate output queue.
/// </summary>
public sealed record ApiResultMessage
{
    public required string CorrelationId { get; init; }
    public required bool Success { get; init; }
    public required int StatusCode { get; init; }
    public string? ErrorMessage { get; init; }
}
