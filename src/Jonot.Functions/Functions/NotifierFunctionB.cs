namespace Jonot.Functions.Functions;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

/// <summary>
/// Timer-triggered function that fetches data from a database
/// and sends batched messages to jono-b-in.
/// </summary>
public sealed class NotifierFunctionB
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ILogger<NotifierFunctionB> _logger;

    public NotifierFunctionB(ServiceBusClient serviceBusClient, ILogger<NotifierFunctionB> logger)
    {
        _serviceBusClient = serviceBusClient;
        _logger = logger;
    }

    [Function(nameof(NotifierFunctionB))]
    public async Task RunAsync(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("NotifierFunctionB triggered at {Time}", DateTime.UtcNow);

        // TODO: Replace with actual database fetch
        var items = new[] { new { Id = "1", Data = "sample-b-1" }, new { Id = "2", Data = "sample-b-2" } };

        await using var sender = _serviceBusClient.CreateSender("jono-b-in");

        foreach (var item in items)
        {
            var message = new ServiceBusMessage(JsonSerializer.Serialize(item))
            {
                ContentType = "application/json",
                MessageId = $"b-{item.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}"
            };

            await sender.SendMessageAsync(message, cancellationToken);
        }

        _logger.LogInformation("NotifierFunctionB sent {Count} messages", items.Length);
    }
}
