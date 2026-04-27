using System;
using FluentAssertions;
using Xunit;

namespace Tracelit.Tests;

/// <summary>
/// Tests for <see cref="TracelitClient"/> static façade.
///
/// IMPORTANT: Because <see cref="TracelitClient"/> is a static class with
/// singleton state, each test calls <see cref="TracelitClient.Shutdown"/> in
/// the constructor and in Dispose to ensure complete test isolation.
/// </summary>
public sealed class TracelitClientTests : IDisposable
{
    public TracelitClientTests()
    {
        TracelitClient.Shutdown();
    }

    // ─── Configure ───────────────────────────────────────────────────────────

    [Fact]
    public void Configure_SetsApiKey()
    {
        TracelitClient.Configure(c => c.ApiKey = "tl_test_key");
        TracelitClient.Configuration.ApiKey.Should().Be("tl_test_key");
    }

    [Fact]
    public void Configure_NullDelegate_ThrowsArgumentNullException()
    {
        var act = () => TracelitClient.Configure(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Configure_AfterStart_ThrowsInvalidOperationException()
    {
        TracelitClient.Configure(ValidConfig());
        TracelitClient.Start();

        var act = () => TracelitClient.Configure(c => c.ApiKey = "too-late");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Configure*before*Start*");
    }

    // ─── Start ────────────────────────────────────────────────────────────────

    [Fact]
    public void Start_WithValidConfig_DoesNotThrow()
    {
        TracelitClient.Configure(ValidConfig());
        var act = () => TracelitClient.Start();
        act.Should().NotThrow();
    }

    [Fact]
    public void Start_IsIdempotent_CalledTwice_DoesNotThrow()
    {
        TracelitClient.Configure(ValidConfig());
        TracelitClient.Start();
        var act = () => TracelitClient.Start();
        act.Should().NotThrow(because: "Start is idempotent");
    }

    [Fact]
    public void Start_WhenDisabled_SkipsSetup()
    {
        TracelitClient.Configure(c =>
        {
            c.Enabled     = false;
            c.ApiKey      = "key";
            c.ServiceName = "svc";
        });

        // Should not throw even though there is no real OTel setup.
        var act = () => TracelitClient.Start();
        act.Should().NotThrow();
    }

    [Fact]
    public void Start_MissingApiKey_ThrowsArgumentException()
    {
        TracelitClient.Configure(c =>
        {
            c.ApiKey      = null;
            c.ServiceName = "svc";
        });

        var act = () => TracelitClient.Start();
        act.Should().Throw<ArgumentException>()
            .WithMessage("*ApiKey*");
    }

    [Fact]
    public void Start_MissingServiceName_ThrowsArgumentException()
    {
        TracelitClient.Configure(c =>
        {
            c.ApiKey      = "tl_key";
            c.ServiceName = null;
        });

        var act = () => TracelitClient.Start();
        act.Should().Throw<ArgumentException>()
            .WithMessage("*ServiceName*");
    }

    // ─── Tracer ───────────────────────────────────────────────────────────────

    [Fact]
    public void Tracer_BeforeStart_ThrowsInvalidOperationException()
    {
        var act = () => _ = TracelitClient.Tracer;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Start()*");
    }

    [Fact]
    public void Tracer_AfterStart_ReturnsActivitySource()
    {
        TracelitClient.Configure(ValidConfig());
        TracelitClient.Start();

        TracelitClient.Tracer.Should().NotBeNull();
        TracelitClient.Tracer.Name.Should().Be("test-service");
    }

    // ─── Metrics ─────────────────────────────────────────────────────────────

    [Fact]
    public void Metrics_BeforeStart_ThrowsInvalidOperationException()
    {
        var act = () => _ = TracelitClient.Metrics;
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Start()*");
    }

    [Fact]
    public void Metrics_AfterStart_ReturnsTracelitMetrics()
    {
        TracelitClient.Configure(ValidConfig());
        TracelitClient.Start();

        TracelitClient.Metrics.Should().NotBeNull();
    }

    [Fact]
    public void Metrics_AfterStart_CanCreateCounter()
    {
        TracelitClient.Configure(ValidConfig());
        TracelitClient.Start();

        var counter = TracelitClient.Metrics.Counter("test.counter");
        counter.Should().NotBeNull();
    }

    // ─── Shutdown ─────────────────────────────────────────────────────────────

    [Fact]
    public void Shutdown_AfterStart_AllowsRestart()
    {
        TracelitClient.Configure(ValidConfig());
        TracelitClient.Start();
        TracelitClient.Shutdown();

        // After shutdown we should be able to configure and start again.
        TracelitClient.Configure(ValidConfig());
        var act = () => TracelitClient.Start();
        act.Should().NotThrow();
    }

    [Fact]
    public void Shutdown_CalledTwice_DoesNotThrow()
    {
        TracelitClient.Configure(ValidConfig());
        TracelitClient.Start();
        TracelitClient.Shutdown();
        var act = () => TracelitClient.Shutdown();
        act.Should().NotThrow();
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static Action<TracelitConfiguration> ValidConfig() => c =>
    {
        c.ApiKey      = "tl_live_test";
        c.ServiceName = "test-service";
        c.Environment = "test";
    };

    public void Dispose() => TracelitClient.Shutdown();
}
