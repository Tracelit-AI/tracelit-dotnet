using System;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace Tracelit.Tests;

public sealed class ConfigurationTests
{
    // ─── Validate() happy path ────────────────────────────────────────────────

    [Fact]
    public void Validate_WithValidConfig_DoesNotThrow()
    {
        var config = ValidConfig();
        var act = () => config.Validate();
        act.Should().NotThrow();
    }

    // ─── ApiKey validation ────────────────────────────────────────────────────

    [Fact]
    public void Validate_NullApiKey_ThrowsArgumentException()
    {
        var config = ValidConfig();
        config.ApiKey = null;
        config.Invoking(c => c.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*ApiKey*");
    }

    [Fact]
    public void Validate_EmptyApiKey_ThrowsArgumentException()
    {
        var config = ValidConfig();
        config.ApiKey = "";
        config.Invoking(c => c.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*ApiKey*");
    }

    [Fact]
    public void Validate_WhitespaceApiKey_ThrowsArgumentException()
    {
        var config = ValidConfig();
        config.ApiKey = "   ";
        config.Invoking(c => c.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*ApiKey*");
    }

    // ─── ServiceName validation ───────────────────────────────────────────────

    [Fact]
    public void Validate_NullServiceName_ThrowsArgumentException()
    {
        var config = ValidConfig();
        config.ServiceName = null;
        config.Invoking(c => c.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*ServiceName*");
    }

    [Fact]
    public void Validate_EmptyServiceName_ThrowsArgumentException()
    {
        var config = ValidConfig();
        config.ServiceName = "";
        config.Invoking(c => c.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*ServiceName*");
    }

    // ─── SampleRate validation ────────────────────────────────────────────────

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(-100.0)]
    [InlineData(2.0)]
    public void Validate_OutOfRangeSampleRate_ThrowsArgumentException(double rate)
    {
        var config = ValidConfig();
        config.SampleRate = rate;
        config.Invoking(c => c.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*SampleRate*");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Validate_ValidSampleRate_DoesNotThrow(double rate)
    {
        var config = ValidConfig();
        config.SampleRate = rate;
        config.Invoking(c => c.Validate()).Should().NotThrow();
    }

    // ─── Defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public void DefaultEnvironment_IsProduction()
    {
        var config = new TracelitConfiguration();
        config.Environment.Should().Be("production");
    }

    [Fact]
    public void DefaultEndpoint_IsTracilitIngest()
    {
        var config = new TracelitConfiguration();
        config.Endpoint.Should().Be("https://ingest.tracelit.app");
    }

    [Fact]
    public void DefaultSampleRate_IsOne()
    {
        var config = new TracelitConfiguration();
        config.SampleRate.Should().Be(1.0);
    }

    [Fact]
    public void DefaultEnabled_IsTrue()
    {
        var config = new TracelitConfiguration();
        config.Enabled.Should().BeTrue();
    }

    [Fact]
    public void DefaultResourceAttributes_IsEmpty()
    {
        var config = new TracelitConfiguration();
        config.ResourceAttributes.Should().BeEmpty();
    }

    // ─── ResolvedServiceName ──────────────────────────────────────────────────

    [Fact]
    public void ResolvedServiceName_WithServiceName_ReturnsServiceName()
    {
        var config = ValidConfig();
        config.ServiceName = "my-service";
        config.ResolvedServiceName.Should().Be("my-service");
    }

    [Fact]
    public void ResolvedServiceName_NullServiceName_ReturnsUnknownService()
    {
        var config = new TracelitConfiguration();
        config.ServiceName = null;
        config.ResolvedServiceName.Should().Be("unknown-service");
    }

    [Fact]
    public void ResolvedServiceName_EmptyServiceName_ReturnsUnknownService()
    {
        var config = new TracelitConfiguration();
        config.ServiceName = "";
        config.ResolvedServiceName.Should().Be("unknown-service");
    }

    [Fact]
    public void ResolvedServiceName_WhitespaceServiceName_ReturnsUnknownService()
    {
        var config = new TracelitConfiguration();
        config.ServiceName = "  ";
        config.ResolvedServiceName.Should().Be("unknown-service");
    }

    // ─── ResourceAttributes ───────────────────────────────────────────────────

    [Fact]
    public void ResourceAttributes_CanBePopulated()
    {
        var config = ValidConfig();
        config.ResourceAttributes = new Dictionary<string, string>
        {
            ["deployment.region"] = "us-east-1",
            ["team"]              = "platform",
        };

        config.ResourceAttributes.Should().HaveCount(2)
            .And.ContainKey("deployment.region")
            .And.ContainKey("team");
    }

    // ─── Endpoint validation ──────────────────────────────────────────────────

    [Fact]
    public void Validate_EmptyEndpoint_ThrowsArgumentException()
    {
        var config = ValidConfig();
        config.Endpoint = "";
        config.Invoking(c => c.Validate())
            .Should().Throw<ArgumentException>()
            .WithMessage("*Endpoint*");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static TracelitConfiguration ValidConfig() => new()
    {
        ApiKey      = "tl_live_test",
        ServiceName = "test-service",
        Environment = "test",
    };
}
