using Kanal.Core.Diagnostics;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class WorkspaceBootstrapTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-bootstrap-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private sealed class CapturingSink : ILogSink
    {
        private readonly List<(LogLevel Level, string Category, string Message)> _lines = [];

        public IReadOnlyList<(LogLevel Level, string Category, string Message)> Lines
        {
            get
            {
                lock (_lines)
                    return _lines.ToArray();
            }
        }

        public void Write(LogLevel level, string category, string message, Exception? error)
        {
            lock (_lines) // other test classes log in parallel
                _lines.Add((level, category, message));
        }
    }

    [Fact]
    public void ADefaultProjectThatCannotBeCreatedIsLogged()
    {
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(blocker, "");
        var sink = new CapturingSink();
        var previous = Log.Sink;
        Log.Install(sink);
        try
        {
            var store = MainViewModel.OpenStore(Path.Combine(_root, "workspaces.json"), blocker);

            Assert.Empty(store.ListWorkspaces().Workspaces);
        }
        finally
        {
            Log.Install(previous);
        }

        var line = Assert.Single(sink.Lines, l => l.Message.Contains(blocker));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("workspaces", line.Category);
        Assert.StartsWith("Invalid on ", line.Message);
    }

    [Fact]
    public void ADefaultProjectThatIsCreatedLogsNothing()
    {
        var folder = Path.Combine(_root, "Kanal");
        var sink = new CapturingSink();
        var previous = Log.Sink;
        Log.Install(sink);
        try
        {
            var store = MainViewModel.OpenStore(Path.Combine(_root, "workspaces.json"), folder);

            Assert.Single(store.ListWorkspaces().Workspaces);
        }
        finally
        {
            Log.Install(previous);
        }

        Assert.DoesNotContain(sink.Lines, l => l.Message.Contains(folder));
    }
}
