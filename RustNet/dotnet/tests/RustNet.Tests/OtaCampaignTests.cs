using RustNet.Deploy;
using Xunit;

namespace RustNet.Tests;

/// <summary>
/// Unit tests for the fleet OTA rollout orchestration: canary-first ordering,
/// abort-after-failures with the remainder skipped, and per-device tracking.
/// </summary>
public class OtaCampaignTests
{
    private static List<string> Fleet(int n) =>
        Enumerable.Range(0, n).Select(i => $"dev{i}").ToList();

    [Fact]
    public void AllSucceedWhenEveryPushConfirms()
    {
        var fleet = Fleet(5);
        var result = OtaCampaign.Run(fleet, new OtaCampaignPolicy { CanarySize = 1 },
            d => new DeviceOutcome(d, OtaStatus.Confirmed));
        Assert.False(result.Aborted);
        Assert.Equal(5, result.Succeeded);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void CanaryFailureAbortsAndSkipsTheRest()
    {
        var fleet = Fleet(6);
        var attempted = new List<string>();
        var result = OtaCampaign.Run(fleet,
            new OtaCampaignPolicy { CanarySize = 1, AbortAfterFailures = 1 },
            d =>
            {
                attempted.Add(d);
                return new DeviceOutcome(d, OtaStatus.Failed, "boom");
            });

        Assert.True(result.Aborted);
        // Only the canary was attempted; the other five are skipped.
        Assert.Single(attempted);
        Assert.Equal("dev0", attempted[0]);
        Assert.Equal(1, result.Failed);
        Assert.Equal(5, result.Skipped);
        Assert.Equal(6, result.Outcomes.Count);
        Assert.All(result.Outcomes.Skip(1), o => Assert.Equal(OtaStatus.Skipped, o.Status));
    }

    [Fact]
    public void ToleratesFailuresUpToTheThreshold()
    {
        var fleet = Fleet(5);
        // Fail the 2nd device; abort only after 2 failures, so the rollout
        // continues past the first failure.
        var result = OtaCampaign.Run(fleet,
            new OtaCampaignPolicy { CanarySize = 2, BatchSize = 2, AbortAfterFailures = 2 },
            d => d == "dev1"
                ? new DeviceOutcome(d, OtaStatus.Failed, "flaky")
                : new DeviceOutcome(d, OtaStatus.Confirmed));

        Assert.False(result.Aborted);
        Assert.Equal(4, result.Succeeded);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Skipped);
    }

    [Fact]
    public void PushExceptionsCountAsFailures()
    {
        var fleet = Fleet(3);
        var result = OtaCampaign.Run(fleet,
            new OtaCampaignPolicy { CanarySize = 1, AbortAfterFailures = 1 },
            d => throw new InvalidOperationException("unreachable"));
        Assert.True(result.Aborted);
        Assert.Equal(OtaStatus.Failed, result.Outcomes[0].Status);
        Assert.Contains("unreachable", result.Outcomes[0].Error);
        Assert.Equal(2, result.Skipped);
    }
}
