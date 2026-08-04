using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

/// <summary>
/// Export used to write <c>%USERPROFILE%\Documents\kanal-093005-x7kq.md</c> and print the path in
/// a status line the operator had already stopped looking at. The transcript is the deliverable
/// of the meeting — the thing that gets mailed to the supplier — so where it lands is the
/// operator's decision, not a constant.
/// </summary>
public class ExportTests
{
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

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "kanal-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Runs a short demo meeting so there is something worth exporting.</summary>
    private static async Task<MainViewModel> MeetingWithContentAsync(AppSettings? settings = null)
    {
        var vm = TestViewModels.Demo(settings);
        await vm.StartCommand.ExecuteAsync(null);
        // long enough for the scripted provider to finalise at least one utterance: only
        // finals are exported, and partials would leave the file empty
        await PumpAsync(2000);
        await vm.StopCommand.ExecuteAsync(null);
        return vm;
    }

    [AvaloniaFact]
    public async Task ExportWritesTheTranscriptWhereThePickerSays()
    {
        var dir = TempDir();
        var chosen = Path.Combine(dir, "supplier-meeting.md");
        var vm = await MeetingWithContentAsync();
        vm.ChooseExportPath = (_, _) => Task.FromResult<string?>(chosen);

        await vm.ExportMarkdownCommand.ExecuteAsync(null);

        Assert.True(File.Exists(chosen), "the transcript was not written where the picker said.");
        var text = await File.ReadAllTextAsync(chosen);
        Assert.Contains("KX-4402", text); // the part number the demo script speaks
        Assert.Contains(chosen, vm.Status);
    }

    /// <summary>A cancelled dialog is not a failure and must not write anything anywhere.</summary>
    [AvaloniaFact]
    public async Task CancellingThePickerWritesNothing()
    {
        var dir = TempDir();
        var vm = await MeetingWithContentAsync();
        vm.ChooseExportPath = (_, _) => Task.FromResult<string?>(null);

        await vm.ExportMarkdownCommand.ExecuteAsync(null);

        Assert.Empty(Directory.GetFiles(dir));
        Assert.Contains("cancel", vm.Status, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The dialog opens where the operator said transcripts go, on a name they can recognise a
    /// week later. Both are only suggestions — the picker can be pointed anywhere.
    /// </summary>
    [AvaloniaFact]
    public async Task ThePickerOpensOnTheConfiguredFolderAndTheRoomId()
    {
        var dir = TempDir();
        var settings = new AppSettings { TranscriptFolder = dir };
        var vm = await MeetingWithContentAsync(settings);

        string? offeredFolder = null;
        string? offeredName = null;
        vm.ChooseExportPath = (folder, name) =>
        {
            offeredFolder = folder;
            offeredName = name;
            return Task.FromResult<string?>(null);
        };

        await vm.ExportMarkdownCommand.ExecuteAsync(null);

        Assert.Equal(dir, offeredFolder);
        Assert.EndsWith(".md", offeredName);
        Assert.StartsWith("kanal-", offeredName);
    }

    [AvaloniaFact]
    public async Task ExportBeforeAnyMeetingSaysThereIsNothingToExport()
    {
        var vm = TestViewModels.Demo();
        var asked = false;
        vm.ChooseExportPath = (_, _) => { asked = true; return Task.FromResult<string?>(null); };

        await vm.ExportMarkdownCommand.ExecuteAsync(null);

        Assert.False(asked, "a file dialog was opened for a meeting that never happened.");
        Assert.Contains("Nothing to export", vm.Status);
    }

    /// <summary>
    /// A read-only folder, a full disk, a path the operator no longer has rights to. Losing the
    /// transcript at the last step is the worst possible moment, so the failure is reported
    /// rather than thrown out of a command nothing is awaiting.
    /// </summary>
    [AvaloniaFact]
    public async Task AFailedWriteIsReportedRatherThanThrown()
    {
        var dir = TempDir();
        // a file where a directory would have to be: export creates missing folders, so the
        // failure has to be one that creating them cannot fix
        var blocker = Path.Combine(dir, "blocked");
        await File.WriteAllTextAsync(blocker, "not a directory");

        var vm = await MeetingWithContentAsync();
        vm.ChooseExportPath = (_, _) => Task.FromResult<string?>(Path.Combine(blocker, "t.md"));

        await vm.ExportMarkdownCommand.ExecuteAsync(null);

        Assert.Contains("Export failed", vm.Status);
    }
}

/// <summary>The settings view model round-trips the folders chosen by the operator.</summary>
public class SettingsViewModelFolderTests
{
    [AvaloniaFact]
    public void SettingsStateRoundTripsBothFolders()
    {
        var settings = new AppSettings
        {
            TranscriptFolder = @"D:\a",
            AudioFolder = @"D:\b",
            RecordAudio = false,
        };
        var vm = new SettingsViewModel(settings);

        Assert.Equal(@"D:\a", vm.TranscriptFolder);
        Assert.Equal(@"D:\b", vm.AudioFolder);
        Assert.False(vm.RecordAudio);

        vm.TranscriptFolder = @"D:\c";
        vm.AudioFolder = @"D:\d";
        vm.RecordAudio = true;
        var written = new AppSettings();
        vm.ApplyTo(written);

        Assert.Equal(@"D:\c", written.TranscriptFolder);
        Assert.Equal(@"D:\d", written.AudioFolder);
        Assert.True(written.RecordAudio);
    }
}

/// <summary>
/// Recording the room is on by default — the recording is the only artefact that can settle a
/// disagreement about what was said, which is the situation this tool exists for — but it is a
/// file about a private negotiation, so where it goes and whether it happens at all are decided
/// in exactly one place.
/// </summary>
public class RecordingTests
{
    private static readonly PipelineMode Demo = PipelineMode.Of(PipelineModeId.Demo);
    private static readonly PipelineMode Live = PipelineMode.Of(PipelineModeId.CloudCloud);

    [Fact]
    public void ALiveMeetingIsRecordedIntoTheAudioFolderUnderTheRoomId()
    {
        // Built with Path.Combine rather than written out: CI runs on Linux, where the separator
        // is "/", and a hardcoded backslash passes on the developer's machine and nowhere else.
        var folder = Path.Combine("meetings", "audio");
        var settings = new AppSettings { AudioFolder = folder };

        var path = MainViewModel.RecordingPathFor(Live, settings, "kanal-093005-x7kq");

        Assert.Equal(Path.Combine(folder, "kanal-093005-x7kq.wav"), path);
    }

    /// <summary>A scripted run has no room audio to record — there is no microphone open.</summary>
    [Fact]
    public void ScriptedModesRecordNothing()
    {
        Assert.Null(MainViewModel.RecordingPathFor(Demo, new AppSettings(), "kanal-1"));
    }

    [Fact]
    public void TurningItOffInSettingsRecordsNothing()
    {
        var settings = new AppSettings { RecordAudio = false };

        Assert.Null(MainViewModel.RecordingPathFor(Live, settings, "kanal-1"));
    }

    [Fact]
    public void UnsetAudioFolderFallsBackRatherThanWritingToNowhere()
    {
        var path = MainViewModel.RecordingPathFor(Live, new AppSettings(), "kanal-1");

        Assert.NotNull(path);
        Assert.StartsWith(SettingsStore.DefaultOutputFolder, path);
        Assert.EndsWith("kanal-1.wav", path);
    }
}
