namespace Jonot.Functions.Services;

using Jonot.Functions.Models;

/// <summary>
/// Sends messages to the external REST API.
/// </summary>
public interface IRestApiSender
{
    /// <summary>
    /// Sends the payload to the external API and returns the result.
    /// </summary>
    Task<ApiResultMessage> SendAsync(ApiSendMessage message, CancellationToken cancellationToken = default);
}
