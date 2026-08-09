using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Diagnostics;
using Kanal.Core.Providers.Testing;
using Kanal.Core.Relay;
using Kanal.Host.Services;

namespace Kanal.UI.UnitTests;

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
        Assert.Contains("demo", opened.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("zh", opened.Message);
        Assert.Contains(room, l => l.Message.Contains("Room closed."));
    }

    [AvaloniaFact]
    public async Task AStartTheModeCannotServeIsLoggedWithTheReason()
    {
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.CloudCloud);

        await vm.StartCommand.ExecuteAsync(null);

        var refused = Assert.Single(sink.Lines, l => l.Message.Contains("Start refused"));
        Assert.Equal(LogLevel.Warning, refused.Level);
        Assert.Equal("room", refused.Category);
        Assert.Contains(vm.SelectedMode.Unavailable!, refused.Message);
    }

    [AvaloniaFact]
    public async Task ASessionThatEndsOnItsOwnSaysSo()
    {
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();
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

    [AvaloniaFact]
    public async Task AnExportThatCannotBeWrittenIsLoggedWithItsCause()
    {
        using var _ = Listening(out var sink);
        var vm = TestViewModels.Demo();
        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(100);

        vm.ChooseExportPath = (_, _) => Task.FromResult<string?>(
            Path.Combine(Path.GetTempPath(), "kanal-not-a-dir.txt", "nested", "room.md"));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "kanal-not-a-dir.txt"), "not a directory");

        await vm.ExportMarkdownCommand.ExecuteAsync(null);

        var failed = Assert.Single(sink.Lines, l => l.Message.Contains("transcript could not be written"));
        Assert.Equal(LogLevel.Error, failed.Level);
        Assert.NotNull(failed.Error);

        await vm.StopCommand.ExecuteAsync(null);
    }

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

    [AvaloniaFact]
    public async Task NothingSaidInTheRoomIsWrittenToTheLog()
    {
        using var _ = Listening(out var sink);
        var settings = new AppSettings { ApiKeys = { new ApiKeyEntry("prod", "gladia", "sk-secret-key") } };
        var vm = TestViewModels.Demo(settings);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(400); // long enough for the scripted demo to produce utterances
        await vm.StopCommand.ExecuteAsync(null);

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
