using UnityCli.Abstractions;

namespace UnityCli.Tests;

public sealed class RecordingConsole : IConsole
{
    private readonly List<string> _stdout = [];
    private readonly List<string> _stderr = [];

    public IReadOnlyList<string> StandardOutput => _stdout;

    public IReadOnlyList<string> StandardError => _stderr;

    public string StdoutText => string.Join(Environment.NewLine, _stdout);

    public string StderrText => string.Join(Environment.NewLine, _stderr);

    public void WriteLine(string text) => _stdout.Add(text);

    public void ErrorLine(string text) => _stderr.Add(text);
}
