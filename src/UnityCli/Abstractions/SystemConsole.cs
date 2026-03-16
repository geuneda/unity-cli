namespace UnityCli.Abstractions;

public sealed class SystemConsole : IConsole
{
    public void WriteLine(string text) => Console.Out.WriteLine(text);

    public void ErrorLine(string text) => Console.Error.WriteLine(text);
}
