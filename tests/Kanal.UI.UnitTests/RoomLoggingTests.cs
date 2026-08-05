using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Diagnostics;
using Kanal.Core.Providers.Testing;
using Kanal.Core.Relay;
using Kanal.Host.Services;

namespace Kanal.UI.UnitTests;

/// <summary>
/// The claim the log feature is sold on: a meeting that went wrong leaves something behind. These
/// drive the real view model through the real paths and read back what the sink was handed —
/// without them, "rooms opening and closing leave a line" was a sentence in a changelog.
/// </summary>
public class RoomLoggingTests
{
    private sealed record Line(LogLevel Level, string Category, string Message, Exception? Error);

    private sealed class RecordingSink : ILogSink
    {
        private readonly List<Line> _lines = [];

        public IReadOnlyList<Line> Lines
        {
            get
            {
                lock (_lines)
                    return _lines.ToArray();
            }
        }

        public void Write(LogLevel level, string category, string message, Exception? error)
        {
            lock (_lines) // capture and dispatcher threads both write here
                _lines.Add(new Line(level, category, message, error));
        }
    }

    /// <summary>Installs a sink for the duration and puts back whatever was there before.</summary>
    private static IDisposable Listening(out RecordingSink sink)
    {
        var previous = Log.Sink;
        var recording = new RecordingSink();
        sink = recording;
        Log.Install(recording);
        return new Restore(() => Log.Install(previous));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private static async Task PumpAsync(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ARoomOpeningAndClosingLeavesALineEach()
    {
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);
        await vm.StopCommand.ExecuteAsync(null);

        var room = sink.Lines.Where(l => l.Category == "room").ToList();
        var opened = Assert.Single(room, l => l.Message.Contains("open:"));
        Assert.Equal(LogLevel.Info, opened.Level);
        // the mode and the languages, which is what makes an old log readable at all
        Assert.Contains("demo", opened.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zh", opened.Message);
        Assert.Contains(room, l => l.Message.Contains("Room closed."));
    }

    /// <summary>
    /// A Start that never opens a room is the one the operator phones about, and the status line
    /// carrying the reason is gone by the time they do — so the line has to carry the reason too,
    /// not just the name of the mode that refused.
    /// </summary>
    [AvaloniaFact]
    public async Task AStartTheModeCannotServeIsLoggedWithTheReason()
    {
        using var _ = Listening(out var sink);
        // cloud transcription with no stored key: the planner refuses before anything opens
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.CloudCloud);

        await vm.StartCommand.ExecuteAsync(null);

        var refused = Assert.Single(sink.Lines, l => l.Message.Contains("Start refused"));
        Assert.Equal(LogLevel.Warning, refused.Level);
        Assert.Equal("room", refused.Category);
        Assert.Contains(vm.SelectedMode.Unavailable!, refused.Message);
    }

    /// <summary>
    /// The ending nobody chose. A transcription service that closes the socket ends the session
    /// without raising an error first, so a room that went deaf at minute 40 has to be
    /// distinguishable in the file from one the operator stopped at minute 90.
    /// </summary>
    [AvaloniaFact]
    public async Task ASessionThatEndsOnItsOwnSaysSo()
    {
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();
        // one short line, then the script runs out and the session ends by itself
        vm.PlanFilter = plan => plan with
        {
            Asr = new FakeAsrProvider(
                script: [new FakeAsrProvider.Line("S01", "zh", "好")],
                partialInterval: TimeSpan.FromMilliseconds(10),
                loop: false),
        };

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1000);

        var ended = Assert.Single(sink.Lines, l => l.Message.StartsWith("Session ended"));
        Assert.Equal(LogLevel.Info, ended.Level);
        Assert.Contains("script finished", ended.Message);

        await vm.StopCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Losing the transcript at the last step is the worst moment for a failure that only ever
    /// appeared in a status line.
    /// </summary>
    [AvaloniaFact]
    public async Task AnExportThatCannotBeWrittenIsLoggedWithItsCause()
    {
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();
        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);

        // a path that cannot be created on any platform this runs on
        vm.ChooseExportPath = (_, _) => Task.FromResult<string?>(
            Path.Combine(Path.GetTempPath(), "kanal-not-a-dir.txt", "nested", "room.md"));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "kanal-not-a-dir.txt"), "not a directory");

        await vm.ExportMarkdownCommand.ExecuteAsync(null);

        var failed = Assert.Single(sink.Lines, l => l.Message.Contains("transcript could not be written"));
        Assert.Equal(LogLevel.Error, failed.Level);
        Assert.NotNull(failed.Error);

        await vm.StopCommand.ExecuteAsync(null);
    }

    /// <summary>
    /// Debug is offered in Settings, so it has to mean something. Before this it was byte-identical
    /// to Info: nothing in the host wrote a single Debug line, and an operator told to "turn on
    /// Debug and reproduce it" sent back the same file.
    /// </summary>
    [AvaloniaFact]
    public async Task TheDebugLevelCarriesSomethingInfoDoesNot()
    {
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();
        vm.RelayEnabled = true;
        vm.RelayPublisherFactory = _ => new NullRelayPublisher();

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.NotEmpty(sink.Lines.Where(l => l.Level == LogLevel.Debug));
    }

    /// <summary>Nothing a participant said, and no credential, may end up in the file.</summary>
    [AvaloniaFact]
    public async Task NothingSaidInTheRoomIsWrittenToTheLog()
    {
        using var _ = Listening(out var sink);
        var settings = new AppSettings { ApiKeys = { new ApiKeyEntry("prod", "gladia", "sk-secret-key") } };
        var vm = TestViewModels.Demo(settings);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(400); // long enough for the scripted demo to produce utterances
        await vm.StopCommand.ExecuteAsync(null);

        // whatever the demo script said is on screen; none of it is in the log
        var spoken = vm.Columns.SelectMany(c => c.Bubbles).Select(b => b.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList();
        Assert.NotEmpty(spoken);

        var written = string.Join("\n", sink.Lines.Select(l => $"{l.Message} {l.Error}"));
        foreach (var line in spoken)
            Assert.DoesNotContain(line, written);
        Assert.DoesNotContain("sk-secret-key", written);
    }
}
