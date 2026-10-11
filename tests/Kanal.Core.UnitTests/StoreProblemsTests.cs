using Kanal.Core.Diagnostics;
using Kanal.Core.Workspaces;

namespace Kanal.Core.UnitTests;

[Collection(LoggingCollection.Name)]
public class StoreProblemsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kanal-problems-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingSink _sink = new();
    private readonly ILogSink? _previous = Log.Sink;

    public StoreProblemsTests()
    {
        Directory.CreateDirectory(_root);
        Log.Install(_sink);
    }

    public void Dispose()
    {
        Log.Install(_previous);
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_failed_set_aside_logs_its_exception()
    {
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        var workspace = store.CreateWorkspace("W", Directory.CreateDirectory(Path.Combine(_root, "w")).FullName)
            .Workspace!;
        var folder = Directory.CreateDirectory(Path.Combine(workspace.RootPath, "meetings", "lost1")).FullName;
        File.WriteAllText(Path.Combine(workspace.RootPath, "kanal-set-aside"), "in the way");

        var result = store.RecoverMeeting(workspace.Id, folder, "Recovered meeting");

        Assert.Equal(StoreProblemKind.Unwritable, result.Problem!.Kind);
        var line = Assert.Single(_sink.Lines, l => l.Message.Contains("could not be moved"));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.NotNull(line.Error);
    }

    [Fact]
    public void An_unwritable_problem_joins_the_detail_and_the_cause()
    {
        var problem = StoreProblems.Unwritable("p", "It failed.", new IOException("disk full"));

        Assert.Equal(new StoreProblem(StoreProblemKind.Unwritable, "p", "It failed. disk full"), problem);
    }

    [Fact]
    public void Building_a_problem_writes_no_log_line()
    {
        StoreProblems.Unwritable(_root, "It failed.", new IOException("disk full"));

        Assert.DoesNotContain(_sink.Lines, line => line.Message.Contains(_root));
    }

    [Fact]
    public void The_store_logs_a_write_failure_once_with_its_path_and_cause()
    {
        var folder = Path.Combine(_root, "project");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "meetings"), "a file where the meetings folder belongs");
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));

        var problem = store.CreateWorkspace("Project", folder).Problem;

        Assert.Equal(StoreProblemKind.Unwritable, problem?.Kind);
        var line = Assert.Single(_sink.Lines, line => line.Message.Contains(_root));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal("workspaces", line.Category);
        Assert.Equal($"The workspace folder could not be written. ({problem!.Subject})", line.Message);
        Assert.NotNull(line.Error);
    }

    private sealed record Line(LogLevel Level, string Category, string Message, Exception? Error);

    private sealed class RecordingSink : ILogSink
    {
        private readonly object _gate = new();

        public List<Line> Lines
        {
            get
            {
                lock (_gate)
                    return [.. _lines];
            }
        }

        private readonly List<Line> _lines = [];

        public void Write(LogLevel level, string category, string message, Exception? error)
        {
            lock (_gate)
                _lines.Add(new Line(level, category, message, error));
        }
    }
}
