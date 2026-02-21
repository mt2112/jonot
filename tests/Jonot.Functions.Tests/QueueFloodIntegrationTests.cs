namespace Jonot.Functions.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

/// <summary>
/// Black-box integration test that demonstrates the full message delivery pipeline
/// using the real Service Bus emulator and the Functions host running as a separate process.
///
/// The test:
///   1. Starts a mock HTTP server that records every incoming API call with timestamps
///   2. Floods jono-a-in / jono-b-in with 100 messages via real Service Bus
///   3. The separately-running Functions host picks them up end-to-end:
///      JonoA/BTrigger → api-send-queue → ApiSendFunction → RestApiSender → mock HTTP server
///   4. Analyzes mock server timestamps to prove rate limiting was enforced
///
/// Verification uses the mock HTTP server as sole evidence (not output queues,
/// because the Functions host's own JonoAOutTriggerFunction / JonoBOutTriggerFunction
/// consume from jono-a-out / jono-b-out and race with any external receiver).
///
/// Prerequisites:
///   1. Start the Service Bus emulator:
///        docker compose -f emulator/docker-compose.yml up -d
///   2. Set ExternalApiBaseUrl in local.settings.json to the mock server URL:
///        "ExternalApiBaseUrl": "http://localhost:5099"
///   3. Start the Functions host in a separate terminal:
///        cd src/Jonot.Functions &amp;&amp; func start
///
/// Run with:
///   dotnet test --filter "FullyQualifiedName~QueueFloodIntegrationTests" --logger "console;verbosity=detailed"
/// </summary>
public class QueueFloodIntegrationTests : IAsyncLifetime
{
    private const string ServiceBusConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;";

    private const int TotalMessages = 100;
    private const int PermitLimit = 5;
    private const int MockServerPort = 5099;

    /// <summary>
    /// Maximum time to wait for the mock server to record at least TotalMessages API calls.
    /// Should be well above the theoretical minimum (TotalMessages / PermitLimit seconds).
    /// </summary>
    private static readonly TimeSpan MockServerTimeout = TimeSpan.FromMinutes(2);

    private readonly ITestOutputHelper _output;
    private ServiceBusClient _serviceBusClient = null!;

    public QueueFloodIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Writes to both ITestOutputHelper (for IDE test explorers) and Console (for dotnet test CLI).
    /// </summary>
    private void Log(string message)
    {
        _output.WriteLine(message);
        Console.WriteLine(message);
    }

    public async Task InitializeAsync()
    {
        _serviceBusClient = new ServiceBusClient(ServiceBusConnectionString);
        await PurgeInputQueuesAsync();
    }

    public async Task DisposeAsync()
    {
        await _serviceBusClient.DisposeAsync();
    }

    [Fact]
    public async Task WhenHundredMessagesFloodQueuesThenMaxFiveApiCallsPerSecond()
    {
        // ── Banner ─────────────────────────────────────────────────────

        Log("================================================================");
        Log("  QUEUE FLOOD INTEGRATION TEST — BLACK-BOX");
        Log("================================================================");
        Log("");
        Log("  Architecture (separate processes):");
        Log("  Test process:        flood jono-a-in/b-in + mock HTTP server");
        Log("  Functions host:      JonoA/BTrigger → api-send-queue");
        Log("                       → ApiSendFunction (rate-limited)");
        Log("                       → RestApiSender → mock HTTP :5099");
        Log("  Verification:        mock server recorded timestamps");
        Log("");
        Log($"  Rate limit:          {PermitLimit} calls/second (FixedWindowRateLimiter)");
        Log($"  Messages to flood:   {TotalMessages} ({TotalMessages / 2} A + {TotalMessages / 2} B)");
        Log($"  Mock server port:    {MockServerPort}");
        Log("================================================================");
        Log("");

        // ── Arrange: Start mock HTTP server ────────────────────────────

        var apiCallTimestamps = new ConcurrentBag<ApiCallRecord>();
        var stopwatch = new Stopwatch();

        await using var mockServer = BuildMockApiServer(apiCallTimestamps, stopwatch);
        await mockServer.StartAsync();

        Log($"[0/3] Mock HTTP server started on http://localhost:{MockServerPort}");
        Log("");

        // ── Act: Phase 1 — Flood input queues ──────────────────────────

        Log("[1/3] Flooding jono-a-in and jono-b-in with messages...");

        stopwatch.Start();

        await using var senderA = _serviceBusClient.CreateSender("jono-a-in");
        await using var senderB = _serviceBusClient.CreateSender("jono-b-in");

        var sendTasks = new List<Task>(TotalMessages);

        for (var i = 1; i <= TotalMessages / 2; i++)
        {
            var payloadA = JsonSerializer.Serialize(new { Id = i, Source = "A", Data = $"payload-a-{i}" });
            var messageA = new ServiceBusMessage(payloadA)
            {
                MessageId = $"flood-a-{i:D3}",
                ContentType = "application/json"
            };

            var payloadB = JsonSerializer.Serialize(new { Id = i, Source = "B", Data = $"payload-b-{i}" });
            var messageB = new ServiceBusMessage(payloadB)
            {
                MessageId = $"flood-b-{i:D3}",
                ContentType = "application/json"
            };

            sendTasks.Add(senderA.SendMessageAsync(messageA));
            sendTasks.Add(senderB.SendMessageAsync(messageB));
        }

        await Task.WhenAll(sendTasks);

        Log($"    Sent {TotalMessages} messages to input queues in {stopwatch.Elapsed.TotalSeconds:F2}s");
        Log("");

        // ── Act: Phase 2 — Wait for mock server to record enough API calls ──

        Log("[2/3] Waiting for mock server to record API calls...");
        Log($"    (expecting ≥ {TotalMessages} calls, timeout: {MockServerTimeout.TotalMinutes:F0} min)");

        var deadline = Stopwatch.StartNew();

        while (apiCallTimestamps.Count < TotalMessages && deadline.Elapsed < MockServerTimeout)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            Log($"    ... {apiCallTimestamps.Count}/{TotalMessages} API calls recorded ({deadline.Elapsed.TotalSeconds:F0}s)");
        }

        // Allow a brief settling period for any in-flight calls to complete
        await Task.Delay(TimeSpan.FromSeconds(3));

        stopwatch.Stop();

        Log($"    Final count: {apiCallTimestamps.Count} API calls in {stopwatch.Elapsed.TotalSeconds:F2}s");
        Log("");

        // ── Phase 3 — Analyze and report ────────────────────────────────

        Log("[3/3] Analyzing mock HTTP server call timestamps...");
        Log("");

        await mockServer.StopAsync();

        var sortedCalls = apiCallTimestamps.OrderBy(c => c.StartTime).ToList();

        // ── Report: Timeline ───────────────────────────────────────────

        Log("--- API CALL TIMELINE (from mock HTTP server) ---");
        Log($"{"#",4} | {"Start",9} | {"End",9} | {"CorrelationId",-16}");
        Log(new string('-', 50));

        for (var i = 0; i < sortedCalls.Count; i++)
        {
            var call = sortedCalls[i];
            Log(
                $"{i + 1,4} | {call.StartTime.TotalMilliseconds,7:F0}ms | {call.EndTime.TotalMilliseconds,7:F0}ms | {call.CorrelationId,-16}");
        }

        Log("");

        // ── Report: Rate limiter windows ───────────────────────────────
        // Align windows to the first API call (matches how FixedWindowRateLimiter works)

        var firstCallTime = sortedCalls.Count > 0 ? sortedCalls[0].StartTime : TimeSpan.Zero;

        var windowGroups = sortedCalls
            .GroupBy(c => (int)(c.StartTime - firstCallTime).TotalSeconds)
            .OrderBy(g => g.Key)
            .ToList();

        Log("--- CALLS PER RATE-LIMITER WINDOW (aligned to first call) ---");
        Log($"{"Window",-14} | {"Count",5} | Visual");
        Log(new string('-', 55));

        foreach (var group in windowGroups)
        {
            var bar = new string('#', group.Count());
            var status = group.Count() <= PermitLimit
                ? " OK"
                : group.Count() <= PermitLimit * 2
                    ? " OK (boundary)"
                    : " OVER LIMIT!";
            Log(
                $"{group.Key,2}s - {group.Key + 1,2}s     |   {group.Count(),2}  | {bar}{status}");
        }

        Log("");
        Log($"  Total elapsed time:          {stopwatch.Elapsed.TotalSeconds:F2}s");
        Log($"  Theoretical min at {PermitLimit}/s:     {(double)TotalMessages / PermitLimit:F1}s");
        Log($"  API calls recorded by mock:  {sortedCalls.Count}");
        Log("");

        // ── Assert ─────────────────────────────────────────────────────

        // The FixedWindowRateLimiter (AutoReplenishment=true) starts its internal
        // windows from creation time (singleton in DI), not from the first request.
        // Because our observation windows are not aligned with the rate limiter's,
        // a single observation window can straddle two rate limiter windows,
        // allowing up to PermitLimit * 2 calls in the worst case.
        //
        // We verify two properties:
        //   1. No observation window exceeds PermitLimit * 2 (boundary tolerance)
        //   2. The overall average rate respects the limit (long-term correctness)

        var maxAllowedPerWindow = PermitLimit * 2;

        foreach (var group in windowGroups)
        {
            Assert.True(
                group.Count() <= maxAllowedPerWindow,
                $"Window {group.Key}s-{group.Key + 1}s had {group.Count()} calls, " +
                $"which exceeds even the boundary-straddling maximum of {maxAllowedPerWindow}");
        }

        // At least TotalMessages were processed (timer triggers may add a few extra)
        Assert.True(
            sortedCalls.Count >= TotalMessages,
            $"Expected at least {TotalMessages} API calls, but only {sortedCalls.Count} were recorded");

        // Overall average rate must not exceed the limit (with 1/s tolerance for timing jitter)
        if (sortedCalls.Count >= 2)
        {
            var totalDuration = (sortedCalls[^1].StartTime - sortedCalls[0].StartTime).TotalSeconds;

            if (totalDuration > 0)
            {
                var averageRate = sortedCalls.Count / totalDuration;
                Assert.True(
                    averageRate <= PermitLimit + 1,
                    $"Average rate was {averageRate:F2} calls/s, expected ≤ {PermitLimit + 1}/s");
            }
        }

        // Wall-clock floor: processing must take at least (total / rate - 1) seconds
        Assert.True(
            stopwatch.Elapsed.TotalSeconds >= (double)TotalMessages / PermitLimit - 2,
            $"Processing completed too fast ({stopwatch.Elapsed.TotalSeconds:F2}s) — rate limiter may not be working");

        Log("[OK] All assertions passed — queue flooding did NOT exceed rate limits!");
    }

    /// <summary>
    /// Builds an ASP.NET Core minimal API that mimics the external REST API.
    /// Every POST to /api/messages is recorded with timestamps and returns 200 OK.
    /// </summary>
    private static WebApplication BuildMockApiServer(
        ConcurrentBag<ApiCallRecord> callLog,
        Stopwatch stopwatch)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{MockServerPort}");
        // Suppress ASP.NET Core console logging — we only care about test output
        builder.Logging.ClearProviders();

        var app = builder.Build();

        app.MapPost("/api/messages", async (HttpRequest request) =>
        {
            var startTime = stopwatch.Elapsed;

            // Read the incoming payload (body must be consumed)
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body);
            var correlationId = body.TryGetProperty("CorrelationId", out var cid)
                ? cid.GetString() ?? "unknown"
                : "unknown";

            var record = new ApiCallRecord(correlationId, startTime, stopwatch.Elapsed);
            callLog.Add(record);

            // Return a success response matching what RestApiSender expects
            return Results.Ok(new { status = "accepted" });
        });

        return app;
    }

    /// <summary>
    /// Purges input queues to ensure a clean state. Output queues are not purged
    /// because the Functions host's own out-trigger functions consume from them.
    /// </summary>
    private async Task PurgeInputQueuesAsync()
    {
        var inputQueues = new[] { "jono-a-in", "jono-b-in" };

        foreach (var queue in inputQueues)
        {
            await DrainQueueAsync(queue);
        }

        await PurgeSessionQueueAsync("api-send-queue");
    }

    /// <summary>
    /// Drains all messages from a non-session queue using ReceiveAndDelete mode.
    /// </summary>
    private async Task DrainQueueAsync(string queueName)
    {
        await using var receiver = _serviceBusClient.CreateReceiver(
            queueName,
            new ServiceBusReceiverOptions { ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete });

        while (true)
        {
            var batch = await receiver.ReceiveMessagesAsync(maxMessages: 100, maxWaitTime: TimeSpan.FromSeconds(2));

            if (batch.Count == 0)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Purges a session-enabled queue by accepting sessions until none remain.
    /// Uses a short timeout so the purge doesn't hang on an empty queue.
    /// </summary>
    private async Task PurgeSessionQueueAsync(string queueName)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await using var receiver = await _serviceBusClient.AcceptNextSessionAsync(
                    queueName,
                    new ServiceBusSessionReceiverOptions
                    {
                        ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete
                    },
                    cts.Token);

                while (true)
                {
                    var msg = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1), cts.Token);

                    if (msg is null)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ServiceBusException or OperationCanceledException)
        {
            // Queue is empty or timeout reached — either way, purge is done
        }
    }

    private sealed record ApiCallRecord(
        string CorrelationId,
        TimeSpan StartTime,
        TimeSpan EndTime);
}
