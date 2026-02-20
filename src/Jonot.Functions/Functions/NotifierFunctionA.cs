namespace Jonot.Functions.Functions;

using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

/// <summary>
/// Timer-triggered function that fetches data from a database
/// and sends batched messages to jono-a-in.
/// </summary>
public sealed class NotifierFunctionA
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly ILogger<NotifierFunctionA> _logger;

    public NotifierFunctionA(ServiceBusClient serviceBusClient, ILogger<NotifierFunctionA> logger)
    {
        _serviceBusClient = serviceBusClient;
        _logger = logger;
    }

    [Function(nameof(NotifierFunctionA))]
    public async Task RunAsync(
        [TimerTrigger("0 */5 * * * *")] TimerInfo timerInfo,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("NotifierFunctionA triggered at {Time}", DateTime.UtcNow);

        // TODO: Replace with actual database fetch
        var items = new[] { new { Id = "1", Data = "sample-a-1" }, new { Id = "2", Data = "sample-a-2" } };

        await using var sender = _serviceBusClient.CreateSender("jono-a-in");

        foreach (var item in items)
        {
            var message = new ServiceBusMessage(JsonSerializer.Serialize(item))
            {
                ContentType = "application/json",
                MessageId = $"a-{item.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}"
            };

            await sender.SendMessageAsync(message, cancellationToken);
        }

        _logger.LogInformation("NotifierFunctionA sent {Count} messages", items.Length);
    }
}
