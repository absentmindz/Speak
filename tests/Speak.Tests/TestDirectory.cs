namespace Speak.Tests;

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; }

    public TestDirectory()
    {
        string parent = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "Speak.Tests");
        Directory.CreateDirectory(parent);

        Path = System.IO.Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Combine(params string[] parts)
    {
        string result = Path;
        foreach (string part in parts)
        {
            result = System.IO.Path.Combine(result, part);
        }

        return result;
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        string expectedParent = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Speak.Tests"))
            .TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar)
            + System.IO.Path.DirectorySeparatorChar;
        string resolved = System.IO.Path.GetFullPath(Path);

        if (!resolved.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to remove test directory outside '{expectedParent}'.");
        }

        Directory.Delete(resolved, recursive: true);
    }
}

internal sealed class SpeakDataRootScope : IDisposable
{
    private readonly string? _previous;

    public SpeakDataRootScope(string dataRoot)
    {
        _previous = Environment.GetEnvironmentVariable("SPEAK_DATA_ROOT");
        Environment.SetEnvironmentVariable("SPEAK_DATA_ROOT", dataRoot);
        MaxFlowWindows.Core.SpeakDataPaths.ResetCache();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SPEAK_DATA_ROOT", _previous);
        MaxFlowWindows.Core.SpeakDataPaths.ResetCache();
    }
}
