using GhProxy.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace GhProxy.Tests;

public sealed class ProcessCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_WritesStartAndSuccessEvents()
    {
        var sink = new CapturingOperationalEventSink();
        var runner = new ProcessCommandRunner(NullLogger<ProcessCommandRunner>.Instance, sink);

        var result = await runner.RunAsync(new CommandSpec("sh", ["-c", "printf ok"], TimeSpan.FromSeconds(5), "test.success"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(sink.Events, x => x.EventType == "command.start" && x.CommandKind == "test.success");
        var success = Assert.Single(sink.Events, x => x.EventType == "command.success");
        Assert.Equal(0, success.ExitCode);
        Assert.NotNull(success.Duration);
        Assert.Equal("ok", success.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_WritesFailureEventWithExitCode()
    {
        var sink = new CapturingOperationalEventSink();
        var runner = new ProcessCommandRunner(NullLogger<ProcessCommandRunner>.Instance, sink);

        var result = await runner.RunAsync(new CommandSpec("sh", ["-c", "printf fail >&2; exit 7"], TimeSpan.FromSeconds(5), "test.failure"), CancellationToken.None);

        Assert.False(result.Succeeded);
        var failure = Assert.Single(sink.Events, x => x.EventType == "command.failure");
        Assert.Equal(7, failure.ExitCode);
        Assert.Equal("fail", failure.StandardError);
    }

    [Fact]
    public async Task RunAsync_WritesTimeoutEvent()
    {
        var sink = new CapturingOperationalEventSink();
        var runner = new ProcessCommandRunner(NullLogger<ProcessCommandRunner>.Instance, sink);

        var result = await runner.RunAsync(new CommandSpec("sh", ["-c", "sleep 2"], TimeSpan.FromMilliseconds(50), "test.timeout"), CancellationToken.None);

        Assert.True(result.TimedOut);
        var timeout = Assert.Single(sink.Events, x => x.EventType == "command.timeout");
        Assert.True(timeout.TimedOut);
        Assert.Equal(124, timeout.ExitCode);
        Assert.NotNull(timeout.Details);
    }

    [Fact]
    public async Task RunAsync_WritesStartFailedEventWhenExecutableIsMissing()
    {
        var sink = new CapturingOperationalEventSink();
        var runner = new ProcessCommandRunner(NullLogger<ProcessCommandRunner>.Instance, sink);

        var result = await runner.RunAsync(new CommandSpec($"missing-command-{Guid.NewGuid():N}", [], TimeSpan.FromSeconds(5), "test.missing"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(127, result.ExitCode);
        var failure = Assert.Single(sink.Events, x => x.EventType == "command.start.failed");
        Assert.Equal("test.missing", failure.CommandKind);
        Assert.Equal(127, failure.ExitCode);
    }

    private sealed class CapturingOperationalEventSink : IOperationalEventSink
    {
        public List<OperationalEventWrite> Events { get; } = [];

        public Task WriteAsync(OperationalEventWrite entry, CancellationToken cancellationToken = default)
        {
            Events.Add(entry);
            return Task.CompletedTask;
        }
    }
}
