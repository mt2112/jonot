namespace Jonot.Functions.Functions;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Jonot.Functions.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

/// <summary>
/// Reads API result messages from jono-b-out and performs bookkeeping.
/// </summary>
public sealed class JonoBOutTriggerFunction
{
    private readonly ILogger<JonoBOutTriggerFunction> _logger;

    public JonoBOutTriggerFunction(ILogger<JonoBOutTriggerFunction> logger)
    {
        _logger = logger;
    }

    [Function(nameof(JonoBOutTriggerFunction))]
    public Task RunAsync(
        [ServiceBusTrigger("jono-b-out", Connection = "ServiceBusConnection")]
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
                "B-pipeline: Message {CorrelationId} delivered successfully (HTTP {StatusCode})",
                result.CorrelationId,
                result.StatusCode);
        }
        else
        {
            _logger.LogWarning(
                "B-pipeline: Message {CorrelationId} failed (HTTP {StatusCode}): {Error}",
                result.CorrelationId,
                result.StatusCode,
                result.ErrorMessage);
        }

        // TODO: Implement actual bookkeeping (database update, etc.)

        return Task.CompletedTask;
    }
}
