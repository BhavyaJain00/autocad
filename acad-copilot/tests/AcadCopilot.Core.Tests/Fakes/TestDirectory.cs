namespace AcadCopilot.Tests.Fakes;

/// <summary>A unique temp folder deleted on dispose.</summary>
public sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AcadCopilotTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A process may still hold a file; temp cleanup is best effort.
        }
        catch (UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
