namespace Jonot.Functions.Tests;

using System.Text.Json;
using System.Threading.RateLimiting;
using Azure.Messaging.ServiceBus;
using Jonot.Functions.Functions;
using Jonot.Functions.Models;
using Jonot.Functions.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

public class ApiSendFunctionTests
{
    [Fact]
    public async Task WhenMessageProcessedThenResultIsSentToCorrectOutputQueue()
    {
        // Arrange
        var sendMessage = new ApiSendMessage
        {
            CorrelationId = "corr-1",
            SourceType = SourceType.A,
            Payload = """{"data":"test"}"""
        };

        var expectedResult = new ApiResultMessage
        {
            CorrelationId = "corr-1",
            Success = true,
            StatusCode = 200
        };

        var restApiSender = Substitute.For<IRestApiSender>();
        restApiSender.SendAsync(Arg.Any<ApiSendMessage>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);

        var mockSender = Substitute.For<ServiceBusSender>();
        var serviceBusClient = Substitute.For<ServiceBusClient>();
        serviceBusClient.CreateSender("jono-a-out").Returns(mockSender);

        var rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 10,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

        var logger = Substitute.For<ILogger<ApiSendFunction>>();
        var function = new ApiSendFunction(restApiSender, serviceBusClient, rateLimiter, logger);

        var sbMessage = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(JsonSerializer.Serialize(sendMessage)),
            messageId: "msg-1");

        // Act
        await function.RunAsync(sbMessage, CancellationToken.None);

        // Assert
        await restApiSender.Received(1).SendAsync(
            Arg.Is<ApiSendMessage>(m => m.CorrelationId == "corr-1" && m.SourceType == SourceType.A),
            Arg.Any<CancellationToken>());

        serviceBusClient.Received(1).CreateSender("jono-a-out");

        await mockSender.Received(1).SendMessageAsync(
            Arg.Is<ServiceBusMessage>(m => m.CorrelationId == "corr-1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenSourceTypeBThenResultIsSentToJonoBOut()
    {
        // Arrange
        var sendMessage = new ApiSendMessage
        {
            CorrelationId = "corr-2",
            SourceType = SourceType.B,
            Payload = """{"data":"test-b"}"""
        };

        var expectedResult = new ApiResultMessage
        {
            CorrelationId = "corr-2",
            Success = false,
            StatusCode = 500,
            ErrorMessage = "Server error"
        };

        var restApiSender = Substitute.For<IRestApiSender>();
        restApiSender.SendAsync(Arg.Any<ApiSendMessage>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);

        var mockSender = Substitute.For<ServiceBusSender>();
        var serviceBusClient = Substitute.For<ServiceBusClient>();
        serviceBusClient.CreateSender("jono-b-out").Returns(mockSender);

        var rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 10,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

        var logger = Substitute.For<ILogger<ApiSendFunction>>();
        var function = new ApiSendFunction(restApiSender, serviceBusClient, rateLimiter, logger);

        var sbMessage = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(JsonSerializer.Serialize(sendMessage)),
            messageId: "msg-2");

        // Act
        await function.RunAsync(sbMessage, CancellationToken.None);

        // Assert
        serviceBusClient.Received(1).CreateSender("jono-b-out");
    }
}
