namespace Jonot.Functions.Services;

using System.Text;
using System.Text.Json;
using Jonot.Functions.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Sends messages to the external REST API using a named HttpClient with Polly retry policies.
/// </summary>
public sealed class RestApiSender : IRestApiSender
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RestApiSender> _logger;

    public RestApiSender(IHttpClientFactory httpClientFactory, ILogger<RestApiSender> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClient = httpClientFactory.CreateClient("ExternalApi");
        _logger = logger;
    }

    public async Task<ApiResultMessage> SendAsync(ApiSendMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var content = new StringContent(message.Payload, Encoding.UTF8, "application/json");

        _logger.LogInformation(
            "Sending message {CorrelationId} to external API (source: {SourceType})",
            message.CorrelationId,
            message.SourceType);

        try
        {
            using var response = await _httpClient.PostAsync("api/messages", content, cancellationToken);

            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Message {CorrelationId} sent successfully, status {StatusCode}",
                    message.CorrelationId,
                    statusCode);

                return new ApiResultMessage
                {
                    CorrelationId = message.CorrelationId,
                    Success = true,
                    StatusCode = statusCode
                };
            }

            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogWarning(
                "Message {CorrelationId} failed with status {StatusCode}: {Error}",
                message.CorrelationId,
                statusCode,
                errorBody);

            return new ApiResultMessage
            {
                CorrelationId = message.CorrelationId,
                Success = false,
                StatusCode = statusCode,
                ErrorMessage = errorBody
            };
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex,
                "HTTP error sending message {CorrelationId}",
                message.CorrelationId);

            return new ApiResultMessage
            {
                CorrelationId = message.CorrelationId,
                Success = false,
                StatusCode = 0,
                ErrorMessage = ex.Message
            };
        }
    }
}
