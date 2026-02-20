namespace Jonot.Functions.Functions;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Jonot.Functions.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

/// <summary>
/// Reads messages from jono-a-in and forwards them to the centralized api-send-queue
/// with a fixed session ID to enforce single-instance processing.
/// </summary>
public sealed class JonoATriggerFunction
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ILogger<JonoATriggerFunction> _logger;

    public JonoATriggerFunction(ServiceBusClient serviceBusClient, ILogger<JonoATriggerFunction> logger)
    {
        _serviceBusClient = serviceBusClient;
        _logger = logger;
    }

    [Function(nameof(JonoATriggerFunction))]
    public async Task RunAsync(
        [ServiceBusTrigger("jono-a-in", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("JonoATrigger received message {MessageId}", message.MessageId);

        var sendMessage = new ApiSendMessage
        {
            CorrelationId = message.MessageId,
            SourceType = SourceType.A,
            Payload = message.Body.ToString(),
            RetryCount = 0
        };

        await using var sender = _serviceBusClient.CreateSender("api-send-queue");

        var outMessage = new ServiceBusMessage(JsonSerializer.Serialize(sendMessage))
        {
            SessionId = "global-rate-limit",
            ContentType = "application/json",
            CorrelationId = sendMessage.CorrelationId
        };

        await sender.SendMessageAsync(outMessage, cancellationToken);

        _logger.LogInformation(
            "Message {CorrelationId} forwarded to api-send-queue (source: A)",
            sendMessage.CorrelationId);
    }
}
