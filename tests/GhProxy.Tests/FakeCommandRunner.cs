using System.Diagnostics;
using GhProxy.Api.Services;

namespace GhProxy.Tests;

internal sealed class FakeCommandRunner : ICommandRunner
{
    private readonly Queue<CommandResult> _results = new();

    public List<CommandSpec> Commands { get; } = [];

    public void Enqueue(CommandResult result) => _results.Enqueue(result);

    public Task<CommandResult> RunAsync(CommandSpec command, CancellationToken cancellationToken)
    {
        Commands.Add(command);
        return Task.FromResult(_results.Count == 0 ? new CommandResult(0, "", "") : _results.Dequeue());
    }

    public Process Start(CommandSpec command)
    {
        Commands.Add(command);
        throw new NotSupportedException("Fake runner does not start real processes.");
    }
}
