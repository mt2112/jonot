namespace Jonot.Functions.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.RateLimiting;
using Azure.Messaging.ServiceBus;
using Jonot.Functions.Functions;
using Jonot.Functions.Models;
using Jonot.Functions.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit.Abstractions;

/// <summary>
/// Demonstration test that visually proves the rate limiting architecture works.
/// Sends 20 concurrent messages through ApiSendFunction and logs a timeline
/// showing that no more than 5 API calls occur per second window.
///
/// Run with: dotnet test --filter "FullyQualifiedName~RateLimitingDemonstration" --logger "console;verbosity=detailed"
/// </summary>
public class RateLimitingDemonstrationTests
{
    private readonly ITestOutputHelper _output;

    public RateLimitingDemonstrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task WhenTwentyMessagesSentConcurrentlyThenMaxFiveApiCallsPerSecond()
    {
        // ── Configuration ──────────────────────────────────────────────
        const int totalMessages = 20;
        const int permitLimit = 5;
        const int apiLatencyMs = 50; // Simulated external API response time

        _output.WriteLine("========================================================");
        _output.WriteLine("  RATE LIMITING POC - DEMONSTRATION TEST");
        _output.WriteLine("========================================================");
        _output.WriteLine("");
        _output.WriteLine("  Architecture:");
        _output.WriteLine("  JonoA/BTrigger -> api-send-queue -> ApiSendFunction");
        _output.WriteLine("                     (sessions)       -> RestApiSender");
        _output.WriteLine("                                      -> jono-a/b-out");
        _output.WriteLine("");
        _output.WriteLine($"  Rate limit: {permitLimit} calls/second (FixedWindowRateLimiter)");
        _output.WriteLine($"  Messages to process: {totalMessages}");
        _output.WriteLine($"  Simulated API latency: {apiLatencyMs}ms");
        _output.WriteLine("========================================================");
        _output.WriteLine("");

        // ── Arrange ────────────────────────────────────────────────────

        // Track every API call with a timestamp
        var apiCallLog = new ConcurrentBag<ApiCallRecord>();
        var outputQueueLog = new ConcurrentBag<OutputQueueRecord>();
        var stopwatch = Stopwatch.StartNew();

        // Mock RestApiSender — records each call's timing
        var restApiSender = Substitute.For<IRestApiSender>();
        restApiSender
            .SendAsync(Arg.Any<ApiSendMessage>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                var msg = callInfo.Arg<ApiSendMessage>();
                var callTime = stopwatch.Elapsed;

                // Simulate external API processing time
                await Task.Delay(apiLatencyMs);

                var record = new ApiCallRecord(
                    msg.CorrelationId,
                    msg.SourceType,
                    callTime,
                    stopwatch.Elapsed);
                apiCallLog.Add(record);

                return new ApiResultMessage
                {
                    CorrelationId = msg.CorrelationId,
                    Success = true,
                    StatusCode = 200
                };
            });

        // Mock ServiceBusClient — records output queue routing
        var mockSenderA = Substitute.For<ServiceBusSender>();
        var mockSenderB = Substitute.For<ServiceBusSender>();

        mockSenderA
            .SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var sbMsg = callInfo.Arg<ServiceBusMessage>();
                outputQueueLog.Add(new OutputQueueRecord(sbMsg.CorrelationId, "jono-a-out", stopwatch.Elapsed));
                return Task.CompletedTask;
            });

        mockSenderB
            .SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var sbMsg = callInfo.Arg<ServiceBusMessage>();
                outputQueueLog.Add(new OutputQueueRecord(sbMsg.CorrelationId, "jono-b-out", stopwatch.Elapsed));
                return Task.CompletedTask;
            });

        var serviceBusClient = Substitute.For<ServiceBusClient>();
        serviceBusClient.CreateSender("jono-a-out").Returns(mockSenderA);
        serviceBusClient.CreateSender("jono-b-out").Returns(mockSenderB);

        // Real rate limiter — same configuration as production
        var rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = totalMessages,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

        var logger = Substitute.For<ILogger<ApiSendFunction>>();
        var function = new ApiSendFunction(restApiSender, serviceBusClient, rateLimiter, logger);

        // Create test messages — mix of A and B source types
        var messages = Enumerable.Range(1, totalMessages)
            .Select(i => new ApiSendMessage
            {
                CorrelationId = $"msg-{i:D3}",
                SourceType = i % 2 == 0 ? SourceType.B : SourceType.A,
                Payload = JsonSerializer.Serialize(new { Id = i, Data = $"test-payload-{i}" })
            })
            .ToList();

        // ── Act ────────────────────────────────────────────────────────

        _output.WriteLine("[>] Sending all messages concurrently...");
        _output.WriteLine("");

        // Fire all messages at once — simulates Service Bus delivering them concurrently
        var tasks = messages.Select(msg =>
        {
            var sbMessage = ServiceBusModelFactory.ServiceBusReceivedMessage(
                body: BinaryData.FromString(JsonSerializer.Serialize(msg)),
                messageId: msg.CorrelationId);

            return function.RunAsync(sbMessage, CancellationToken.None);
        }).ToList();

        await Task.WhenAll(tasks);
        stopwatch.Stop();

        // ── Report ─────────────────────────────────────────────────────

        var sortedCalls = apiCallLog.OrderBy(c => c.StartTime).ToList();
        var sortedOutputs = outputQueueLog.OrderBy(o => o.Time).ToList();

        // Timeline of API calls
        _output.WriteLine("--- API CALL TIMELINE ---");
        _output.WriteLine($"{"#",4} | {"Start",9} | {"End",9} | {"Source",6} | {"CorrelationId",-16}");
        _output.WriteLine(new string('-', 60));

        for (var i = 0; i < sortedCalls.Count; i++)
        {
            var call = sortedCalls[i];
            _output.WriteLine(
                $"{i + 1,4} | {call.StartTime.TotalMilliseconds,7:F0}ms | {call.EndTime.TotalMilliseconds,7:F0}ms | {call.SourceType,6} | {call.CorrelationId,-16}");
        }

        _output.WriteLine("");

        // Group calls by 1-second windows and show distribution
        var windowGroups = sortedCalls
            .GroupBy(c => (int)c.StartTime.TotalSeconds)
            .OrderBy(g => g.Key)
            .ToList();

        _output.WriteLine("--- CALLS PER SECOND WINDOW ---");
        _output.WriteLine($"{"Window",-14} | {"Count",5} | Visual");
        _output.WriteLine(new string('-', 55));

        foreach (var group in windowGroups)
        {
            var bar = new string('#', group.Count());
            var status = group.Count() <= permitLimit ? " OK" : " OVER LIMIT!";
            _output.WriteLine(
                $"{group.Key,2}s - {group.Key + 1,2}s     |   {group.Count(),2}  | {bar}{status}");
        }

        _output.WriteLine("");

        // Output queue routing summary
        var aCount = sortedOutputs.Count(o => o.QueueName == "jono-a-out");
        var bCount = sortedOutputs.Count(o => o.QueueName == "jono-b-out");

        _output.WriteLine("--- OUTPUT QUEUE ROUTING ---");
        _output.WriteLine($"  jono-a-out : {aCount,3} messages (SourceType.A)");
        _output.WriteLine($"  jono-b-out : {bCount,3} messages (SourceType.B)");
        _output.WriteLine($"  Total      : {aCount + bCount,3} messages");
        _output.WriteLine("");

        // Summary
        _output.WriteLine($"  Total processing time:    {stopwatch.Elapsed.TotalSeconds:F2}s");
        _output.WriteLine($"  Theoretical min at {permitLimit}/s:  {(double)totalMessages / permitLimit:F1}s");
        _output.WriteLine($"  All {totalMessages} messages processed successfully");
        _output.WriteLine("");

        // ── Assert ─────────────────────────────────────────────────────

        // Core assertion: no 1-second window has more than 5 calls
        foreach (var group in windowGroups)
        {
            Assert.True(
                group.Count() <= permitLimit,
                $"Window {group.Key}s-{group.Key + 1}s had {group.Count()} calls, limit is {permitLimit}");
        }

        // All messages were processed
        Assert.Equal(totalMessages, sortedCalls.Count);

        // All results were routed to output queues
        Assert.Equal(totalMessages, sortedOutputs.Count);

        // Correct routing: odd messages → A, even → B
        Assert.Equal(totalMessages / 2, aCount);
        Assert.Equal(totalMessages / 2, bCount);

        _output.WriteLine("[OK] All assertions passed - rate limit respected!");
    }

    private sealed record ApiCallRecord(
        string CorrelationId,
        SourceType SourceType,
        TimeSpan StartTime,
        TimeSpan EndTime);

    private sealed record OutputQueueRecord(
        string CorrelationId,
        string QueueName,
        TimeSpan Time);
}
