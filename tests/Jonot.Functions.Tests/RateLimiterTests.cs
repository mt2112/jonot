namespace Jonot.Functions.Tests;

using System.Diagnostics;
using System.Threading.RateLimiting;

public class RateLimiterTests
{
    [Fact]
    public async Task WhenFiveRequestsPerSecondThenAllArePermitted()
    {
        // Arrange
        var rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

        // Act — request exactly 5 permits
        var results = new List<bool>();
        for (var i = 0; i < 5; i++)
        {
            using var lease = await rateLimiter.AcquireAsync(1);
            results.Add(lease.IsAcquired);
        }

        // Assert — all 5 should be acquired
        Assert.All(results, acquired => Assert.True(acquired));
    }

    [Fact]
    public async Task WhenSixthRequestInSameWindowThenItIsRejected()
    {
        // Arrange
        var rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 0, // No queuing — reject immediately
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

        // Act — acquire 5, then try a 6th
        for (var i = 0; i < 5; i++)
        {
            var lease = await rateLimiter.AcquireAsync(1);
            Assert.True(lease.IsAcquired);
        }

        using var sixthLease = await rateLimiter.AcquireAsync(1);

        // Assert — 6th should be rejected
        Assert.False(sixthLease.IsAcquired);
    }

    [Fact]
    public async Task WhenQueuedRequestsThenTheyAreProcessedInNextWindow()
    {
        // Arrange
        var rateLimiter = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 5, // Allow queuing of 5 more
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });

        // Act — use up the first 5
        for (var i = 0; i < 5; i++)
        {
            using var lease = await rateLimiter.AcquireAsync(1);
            Assert.True(lease.IsAcquired);
        }

        // The 6th should queue and wait for the next window
        var sw = Stopwatch.StartNew();
        using var queuedLease = await rateLimiter.AcquireAsync(1);
        sw.Stop();

        // Assert — should have waited roughly 1 second for the next window
        Assert.True(queuedLease.IsAcquired);
        Assert.True(sw.ElapsedMilliseconds >= 800, $"Expected wait >= 800ms, actual: {sw.ElapsedMilliseconds}ms");
    }
}
