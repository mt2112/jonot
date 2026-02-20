namespace Jonot.Functions.Functions;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Jonot.Functions.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

/// <summary>
/// Reads API result messages from jono-a-out and performs bookkeeping.
/// </summary>
public sealed class JonoAOutTriggerFunction
{
    private readonly ILogger<JonoAOutTriggerFunction> _logger;

    public JonoAOutTriggerFunction(ILogger<JonoAOutTriggerFunction> logger)
    {
        _logger = logger;
    }

    [Function(nameof(JonoAOutTriggerFunction))]
    public Task RunAsync(
        [ServiceBusTrigger("jono-a-out", Connection = "ServiceBusConnection")]
        ServiceBusReceivedMessage message,
        CancellationToken cancellationToken)
    {
        var result = JsonSerializer.Deserialize<ApiResultMessage>(message.Body.ToString());

        if (result is null)
        {
            _logger.LogWarning("Failed to deserialize result message {MessageId}", message.MessageId);
            return Task.CompletedTask;
        }

        if (result.Success)
        {
            _logger.LogInformation(
                "A-pipeline: Message {CorrelationId} delivered successfully (HTTP {StatusCode})",
                result.CorrelationId,
                result.StatusCode);
        }
        else
        {
            _logger.LogWarning(
                "A-pipeline: Message {CorrelationId} failed (HTTP {StatusCode}): {Error}",
                result.CorrelationId,
                result.StatusCode,
                result.ErrorMessage);
        }

        // TODO: Implement actual bookkeeping (database update, etc.)

        return Task.CompletedTask;
    }
}
