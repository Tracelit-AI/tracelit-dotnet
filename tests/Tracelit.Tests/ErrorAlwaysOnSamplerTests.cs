using System.Diagnostics;
using FluentAssertions;
using OpenTelemetry.Trace;
using Tracelit.Tracing;
using Xunit;

namespace Tracelit.Tests;

/// <summary>
/// Tests for <see cref="ErrorAlwaysOnSampler"/>.
/// </summary>
public sealed class ErrorAlwaysOnSamplerTests
{
    // ─── Drop → RecordOnly upgrade ────────────────────────────────────────────

    [Fact]
    public void ShouldSample_WhenInnerDrops_ReturnsRecordOnly()
    {
        // A rate of 0.0 means the inner TraceIdRatioBasedSampler will always Drop.
        var sampler = new ErrorAlwaysOnSampler(0.0);

        var result = sampler.ShouldSample(BuildParameters());

        result.Decision.Should().Be(SamplingDecision.RecordOnly,
            because: "Drop decisions must be upgraded so the processor pipeline fires");
    }

    [Fact]
    public void ShouldSample_WithRate0_NeverDrops()
    {
        var sampler = new ErrorAlwaysOnSampler(0.0);

        // Run many samples — none should be Drop.
        for (var i = 0; i < 100; i++)
        {
            var result = sampler.ShouldSample(BuildParameters());
            result.Decision.Should().NotBe(SamplingDecision.Drop,
                because: "Drop is never a valid decision when ErrorAlwaysOnSampler is in use");
        }
    }

    // ─── RecordAndSample preserved ────────────────────────────────────────────

    [Fact]
    public void ShouldSample_WhenInnerSamples_ReturnsRecordAndSample()
    {
        // A rate of 1.0 means the inner sampler always samples everything.
        var sampler = new ErrorAlwaysOnSampler(1.0);

        var result = sampler.ShouldSample(BuildParameters());

        result.Decision.Should().Be(SamplingDecision.RecordAndSample,
            because: "Sampled spans must not be downgraded");
    }

    // ─── Partial rate — never returns Drop ───────────────────────────────────

    [Fact]
    public void ShouldSample_WithPartialRate_NeverReturnsDrop()
    {
        var sampler = new ErrorAlwaysOnSampler(0.5);

        for (var i = 0; i < 200; i++)
        {
            var result = sampler.ShouldSample(BuildParameters());
            result.Decision.Should().NotBe(SamplingDecision.Drop,
                because: "Partial rate should produce RecordOnly or RecordAndSample, never Drop");
        }
    }

    // ─── Description ─────────────────────────────────────────────────────────

    [Fact]
    public void Description_ContainsSamplerNameAndRate()
    {
        var sampler = new ErrorAlwaysOnSampler(0.25);
        sampler.Description.Should().Contain("ErrorAlwaysOnSampler",
            because: "description must identify the sampler type");
        sampler.Description.Should().Contain("0.25",
            because: "description must include the configured sample rate");
    }

    [Fact]
    public void Description_WithRate1_ContainsOne()
    {
        var sampler = new ErrorAlwaysOnSampler(1.0);
        sampler.Description.Should().Contain("1");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static SamplingParameters BuildParameters()
    {
        var traceId  = ActivityTraceId.CreateRandom();
        var parentContext = default(ActivityContext);
        return new SamplingParameters(parentContext, traceId, "test-span",
            ActivityKind.Internal, null, null);
    }
}
