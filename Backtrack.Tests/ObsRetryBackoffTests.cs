using System;
using Xunit;

namespace Backtrack.Tests;

public class ObsRetryBackoffTests
{
    private static int CalculateDelay(int retryCount)
    {
        return Math.Min(5 * (1 << Math.Min(retryCount, 3)), 30);
    }

    [Theory]
    [InlineData(0, 5)]   // 5 * 1 = 5s
    [InlineData(1, 10)]  // 5 * 2 = 10s
    [InlineData(2, 20)]  // 5 * 4 = 20s
    [InlineData(3, 30)]  // 5 * 8 = 40s -> capped at 30s
    [InlineData(4, 30)]  // capped at 30s
    [InlineData(10, 30)] // capped at 30s
    public void CalculateDelay_BackoffProgression_MatchesExpectedCap(int retryCount, int expectedDelay)
    {
        int delay = CalculateDelay(retryCount);
        Assert.Equal(expectedDelay, delay);
    }
}
