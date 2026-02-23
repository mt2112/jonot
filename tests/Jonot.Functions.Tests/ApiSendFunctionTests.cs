namespace Jonot.Functions.Tests;

using System.Text.Json;
using System.Threading.RateLimiting;
using Azure.Messaging.ServiceBus;
using Jonot.Functions.Functions;
using Jonot.Functions.Models;
using Jonot.Functions.Services;
using Microsoft.Extensions.Logging;
using Moq;

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

        var restApiSenderMock = new Mock<IRestApiSender>();
        restApiSenderMock
            .Setup(x => x.SendAsync(It.IsAny<ApiSendMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var mockSender = new Mock<ServiceBusSender>();
        var serviceBusClientMock = new Mock<ServiceBusClient>();
        serviceBusClientMock
            .Setup(x => x.CreateSender("jono-a-out"))
            .Returns(mockSender.Object);

        var rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 10,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

        var loggerMock = new Mock<ILogger<ApiSendFunction>>();
        var function = new ApiSendFunction(restApiSenderMock.Object, serviceBusClientMock.Object, rateLimiter, loggerMock.Object);

        var sbMessage = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(JsonSerializer.Serialize(sendMessage)),
            messageId: "msg-1");

        // Act
        await function.RunAsync(sbMessage, CancellationToken.None);

        // Assert
        restApiSenderMock.Verify(
            x => x.SendAsync(
                It.Is<ApiSendMessage>(m => m.CorrelationId == "corr-1" && m.SourceType == SourceType.A),
                It.IsAny<CancellationToken>()),
            Times.Once());

        serviceBusClientMock.Verify(x => x.CreateSender("jono-a-out"), Times.Once());

        mockSender.Verify(
            x => x.SendMessageAsync(
                It.Is<ServiceBusMessage>(m => m.CorrelationId == "corr-1"),
                It.IsAny<CancellationToken>()),
            Times.Once());
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

        var restApiSenderMock = new Mock<IRestApiSender>();
        restApiSenderMock
            .Setup(x => x.SendAsync(It.IsAny<ApiSendMessage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResult);

        var mockSender = new Mock<ServiceBusSender>();
        var serviceBusClientMock = new Mock<ServiceBusClient>();
        serviceBusClientMock
            .Setup(x => x.CreateSender("jono-b-out"))
            .Returns(mockSender.Object);

        var rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 10,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

        var loggerMock = new Mock<ILogger<ApiSendFunction>>();
        var function = new ApiSendFunction(restApiSenderMock.Object, serviceBusClientMock.Object, rateLimiter, loggerMock.Object);

        var sbMessage = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(JsonSerializer.Serialize(sendMessage)),
            messageId: "msg-2");

        // Act
        await function.RunAsync(sbMessage, CancellationToken.None);

        // Assert
        serviceBusClientMock.Verify(x => x.CreateSender("jono-b-out"), Times.Once());
    }
}
