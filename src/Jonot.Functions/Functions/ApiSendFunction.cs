namespace Jonot.Functions.Functions;

using System.Text.Json;
using System.Threading.RateLimiting;
using Azure.Messaging.ServiceBus;
using Jonot.Functions.Models;
using Jonot.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

/// <summary>
/// Centralized function that reads from api-send-queue (session-enabled)
/// and dispatches messages to the external REST API with rate limiting.
/// Only one instance processes this queue at a time due to the fixed session ID.
/// </summary>
public sealed class ApiSendFunction
{
    private readonly IRestApiSender _restApiSender;
    private readonly ServiceBusClient _serviceBusClient;
    private readonly FixedWindowRateLimiter _rateLimiter;
    private readonly ILogger<ApiSendFunction> _logger;

    public ApiSendFunction(
        IRestApiSender restApiSender,
        ServiceBusClient serviceBusClient,
        FixedWindowRateLimiter rateLimiter,
        ILogger<ApiSendFunction> logger)
    {
        _restApiSender = restApiSender;
        _serviceBusClient = serviceBusClient;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    [Function(nameof(ApiSendFunction))]
    public async Task RunAsync(
        [ServiceBusTrigger("api-send-queue", IsSessionsEnabled = true, Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        var sendMessage = JsonSerializer.Deserialize<ApiSendMessage>(message.Body.ToString())
            ?? throw new InvalidOperationException(
                $"Failed to deserialize ApiSendMessage from message {message.MessageId}");

        _logger.LogInformation(
            "ApiSendFunction processing message {CorrelationId} (source: {SourceType})",
            sendMessage.CorrelationId,
            sendMessage.SourceType);

        // Acquire rate limit lease — waits if the limit is reached
        using var lease = await _rateLimiter.AcquireAsync(permitCount: 1, cancellationToken);

        if (!lease.IsAcquired)
        {
            _logger.LogWarning(
                "Rate limit exceeded for message {CorrelationId}, will be retried via Service Bus",
                sendMessage.CorrelationId);
            throw new InvalidOperationException("Rate limit exceeded — message will be retried.");
        }

        // Send to external API
        var result = await _restApiSender.SendAsync(sendMessage, cancellationToken);

        // Route result to the appropriate output queue based on source
        var outputQueue = sendMessage.SourceType == SourceType.A ? "jono-a-out" : "jono-b-out";

        await using var sender = _serviceBusClient.CreateSender(outputQueue);

        var resultMessage = new ServiceBusMessage(JsonSerializer.Serialize(result))
        {
            ContentType = "application/json",
            CorrelationId = result.CorrelationId
        };

        await sender.SendMessageAsync(resultMessage, cancellationToken);

        _logger.LogInformation(
            "Result for {CorrelationId} sent to {OutputQueue} (success: {Success})",
            result.CorrelationId,
            outputQueue,
            result.Success);
    }
}
