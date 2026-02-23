namespace Jonot.Functions.Tests;

using System.Net;
using System.Text.Json;
using Jonot.Functions.Models;
using Jonot.Functions.Services;
using Microsoft.Extensions.Logging;
using Moq;

public class RestApiSenderTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILogger<RestApiSender>> _loggerMock;

    public RestApiSenderTests()
    {
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _loggerMock = new Mock<ILogger<RestApiSender>>();
    }

    [Fact]
    public async Task WhenApiReturnsSuccessThenResultIsSuccess()
    {
        // Arrange
        var handler = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://httpbin.io/get") };
        _httpClientFactoryMock.Setup(x => x.CreateClient("ExternalApi")).Returns(httpClient);

        var sender = new RestApiSender(_httpClientFactoryMock.Object, _loggerMock.Object);
        var message = new ApiSendMessage
        {
            CorrelationId = "test-123",
            SourceType = SourceType.A,
            Payload = """{"key":"value"}"""
        };

        // Act
        var result = await sender.SendAsync(message);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("test-123", result.CorrelationId);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task WhenApiReturns500ThenResultIsFailure()
    {
        // Arrange
        var response = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Internal Server Error")
        };
        var handler = new FakeHttpHandler(response);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://httpbin.io/get") };
        _httpClientFactoryMock.Setup(x => x.CreateClient("ExternalApi")).Returns(httpClient);

        var sender = new RestApiSender(_httpClientFactoryMock.Object, _loggerMock.Object);
        var message = new ApiSendMessage
        {
            CorrelationId = "test-456",
            SourceType = SourceType.B,
            Payload = """{"key":"value"}"""
        };

        // Act
        var result = await sender.SendAsync(message);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal("test-456", result.CorrelationId);
        Assert.Equal("Internal Server Error", result.ErrorMessage);
    }

    [Fact]
    public async Task WhenHttpExceptionThenResultIsFailureWithStatusCodeZero()
    {
        // Arrange
        var handler = new FakeHttpHandler(new HttpRequestException("Connection refused"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://httpbin.io/get") };
        _httpClientFactoryMock.Setup(x => x.CreateClient("ExternalApi")).Returns(httpClient);

        var sender = new RestApiSender(_httpClientFactoryMock.Object, _loggerMock.Object);
        var message = new ApiSendMessage
        {
            CorrelationId = "test-789",
            SourceType = SourceType.A,
            Payload = """{"key":"value"}"""
        };

        // Act
        var result = await sender.SendAsync(message);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(0, result.StatusCode);
        Assert.Equal("test-789", result.CorrelationId);
        Assert.Contains("Connection refused", result.ErrorMessage);
    }

    [Fact]
    public async Task WhenCancelledThenThrowsTaskCanceledException()
    {
        // Arrange
        var handler = new FakeHttpHandler(new HttpResponseMessage(HttpStatusCode.OK), delay: TimeSpan.FromSeconds(10));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://httpbin.io/get") };
        _httpClientFactoryMock.Setup(x => x.CreateClient("ExternalApi")).Returns(httpClient);

        var sender = new RestApiSender(_httpClientFactoryMock.Object, _loggerMock.Object);
        var message = new ApiSendMessage
        {
            CorrelationId = "test-cancel",
            SourceType = SourceType.A,
            Payload = """{"key":"value"}"""
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sender.SendAsync(message, cts.Token));
    }

    [Fact]
    public void WhenNullMessageThenThrowsArgumentNullException()
    {
        // Arrange
        var httpClient = new HttpClient { BaseAddress = new Uri("https://httpbin.io/get") };
        _httpClientFactoryMock.Setup(x => x.CreateClient("ExternalApi")).Returns(httpClient);
        var sender = new RestApiSender(_httpClientFactoryMock.Object, _loggerMock.Object);

        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(() => sender.SendAsync(null!));
    }

    /// <summary>
    /// Simple HttpMessageHandler for testing that returns a preconfigured response.
    /// </summary>
    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage? _response;
        private readonly HttpRequestException? _exception;
        private readonly TimeSpan _delay;

        public FakeHttpHandler(HttpResponseMessage response, TimeSpan delay = default)
        {
            _response = response;
            _delay = delay;
        }

        public FakeHttpHandler(HttpRequestException exception)
        {
            _exception = exception;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_delay > TimeSpan.Zero)
            {
                await Task.Delay(_delay, cancellationToken);
            }

            if (_exception is not null)
            {
                throw _exception;
            }

            return _response!;
        }
    }
}
