using UnityCli.Abstractions;
using UnityCli.Cli;

var exitCode = await new CliApplication(new SystemConsole()).RunAsync(args, CancellationToken.None);
return exitCode;
