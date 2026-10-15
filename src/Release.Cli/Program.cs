using System.CommandLine;
using Release.Cli;

// releasectl: the release pipeline's command line (ADR-0002, ADR-0005, ADR-0006).
// Exit codes: 0 ok, 1 error, 2 usage, 3 blocked by the guard or a gate, 4 verification failed or timed out.
var root = new RootCommand("release-guard release tooling: evidence gate, contract check, promote, wait, rollback, override, status.");
foreach (var command in Commands.All())
{
    root.Subcommands.Add(command);
}

return await root.Parse(args).InvokeAsync();
