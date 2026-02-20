using System.Net;
using System.Threading.RateLimiting;
using Azure.Messaging.ServiceBus;
using Jonot.Functions.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.Extensions.Http;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Rate limiter — singleton, shared across all function invocations in the process
builder.Services.AddSingleton(new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
{
    PermitLimit = 5,
    Window = TimeSpan.FromSeconds(1),
    QueueLimit = 50,
    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    AutoReplenishment = true
}));

// Service Bus client for sending messages to output queues
builder.Services.AddSingleton(_ =>
{
    var connectionString = Environment.GetEnvironmentVariable("ServiceBusConnection")
        ?? throw new InvalidOperationException("ServiceBusConnection is not configured.");
    return new ServiceBusClient(connectionString);
});

// Named HttpClient with Polly retry policy for 429 responses
builder.Services.AddHttpClient("ExternalApi", client =>
{
    var baseUrl = Environment.GetEnvironmentVariable("ExternalApiBaseUrl")
        ?? throw new InvalidOperationException("ExternalApiBaseUrl is not configured.");
    client.BaseAddress = new Uri(baseUrl);
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddPolicyHandler(GetRetryPolicy());

// RestApiSender service
builder.Services.AddSingleton<IRestApiSender, RestApiSender>();

builder.Build().Run();

static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests)
        .WaitAndRetryAsync(
            retryCount: 3,
            sleepDurationProvider: (retryAttempt, result, _) =>
            {
                // Respect Retry-After header if present
                if (result.Result?.Headers.RetryAfter?.Delta is { } delta)
                {
                    return delta;
                }

                return TimeSpan.FromSeconds(Math.Pow(2, retryAttempt));
            },
            onRetryAsync: (_, _, _, _) => Task.CompletedTask);
}
