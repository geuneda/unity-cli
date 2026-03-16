namespace UnityCli.Abstractions;

public interface IConsole
{
    void WriteLine(string text);

    void ErrorLine(string text);
}
