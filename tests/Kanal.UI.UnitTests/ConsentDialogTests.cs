using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Core.Providers;
using Kanal.Core.Providers.Testing;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

/// <summary>
/// Pressing record asks before it captures anyone, and nothing at all exists until the operator
/// says they have informed the room (ADR 0054, decisions 2 and 24–27).
/// </summary>
public class ConsentDialogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-consent-" + Guid.NewGuid().ToString("N"));

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

    private (MainViewModel Vm, WorkspaceStore Store, Workspace Workspace) Live(
        AppSettings? settings = null, Func<DateTimeOffset>? utcNow = null)
    {
        var folder = Path.Combine(_root, "acme");
        Directory.CreateDirectory(folder);
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        var workspace = store.CreateWorkspace("ACME", folder).Workspace!;

        var resolved = settings ?? new AppSettings();
        resolved.ApiKeys.Add(new ApiKeyEntry("meeting-room", "gladia", "k"));
        resolved.ActiveGladiaKeyName = "meeting-room";
        var vm = TestViewModels.Hermetic(resolved, workspaces: () => store, utcNow: utcNow);
        vm.SelectedMode = vm.Modes.Single(o => o.Mode.Id == PipelineModeId.CloudCloud);
        vm.PlanFilter = plan => plan with
        {
            Asr = new FakeAsrProvider(loop: true, caps: new AsrCapabilities(
                Streaming: true,
                Diarization: true,
                Translation: true,
                AutoLanguageDetect: true,
                Languages: new HashSet<string> { "zh", "de", "pl" },
                Latency: LatencyClass.Realtime)),
            Mt = null,
            CloudTranslation = true,
        };
        return (vm, store, workspace);
    }

    [AvaloniaFact]
    public void RecordIsOfferedWithoutAnyPriorAttestation()
    {
        var (vm, _, _) = Live();

        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task CancellingLeavesTheWorkspaceExactlyAsItWas()
    {
        var (vm, store, workspace) = Live();
        vm.ConfirmConsent = (_, _) => Task.FromResult<bool?>(null);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.Empty(store.ListMeetings(workspace.Id).Meetings);
        Assert.Equal(Localizer.Instance["status.idle"], vm.Status);
    }

    [AvaloniaFact]
    public async Task AnUnwiredDialogReadsAsARefusal()
    {
        var (vm, store, workspace) = Live();

        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.Empty(store.ListMeetings(workspace.Id).Meetings);
    }

    [AvaloniaFact]
    public async Task ConfirmingStartsTheMeetingAndWritesTheRecordWithItsDefaultTitle()
    {
        var now = new DateTimeOffset(2026, 9, 8, 12, 30, 0, TimeSpan.Zero);
        var (vm, store, workspace) = Live(utcNow: () => now);
        vm.ConfirmConsent = (save, _) => Task.FromResult<bool?>(save);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsRunning);
        var record = Assert.Single(store.ListMeetings(workspace.Id).Meetings);
        Assert.Equal(now, record.StartedAt);
        Assert.Equal($"ACME {now.ToLocalTime():yyyy-MM-dd HH:mm}", record.Title);
        Assert.Equal(record.Title, vm.MeetingTitle);
        Assert.False(vm.Titling.NamedByHand);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task TheAudioTickIsAOneOffAndIsNeverWrittenBackToTheSettings()
    {
        var settings = new AppSettings { RecordAudio = true };
        var (vm, _, _) = Live(settings);
        bool? offered = null;
        vm.ConfirmConsent = (save, _) =>
        {
            offered = save;
            return Task.FromResult<bool?>(false);
        };

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(offered);
        Assert.True(vm.IsRunning);
        Assert.False(vm.IsRecording);
        Assert.True(settings.RecordAudio);

        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task RecordingAgainIsAskedAsContinuingTheMeetingByName()
    {
        var (vm, store, workspace) = Live();
        var offered = new List<string?>();
        vm.ConfirmConsent = (_, continuing) =>
        {
            offered.Add(continuing);
            return Task.FromResult<bool?>(false);
        };

        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);
        var record = Assert.Single(store.ListMeetings(workspace.Id).Meetings);
        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);

        Assert.Equal([null, record.Title], offered);
        Assert.Equal(2, Assert.Single(store.ListMeetings(workspace.Id).Meetings).Segments.Count);
    }

    [AvaloniaFact]
    public async Task CancellingAContinuationLeavesTheMeetingExactlyAsItWas()
    {
        var (vm, store, workspace) = Live();
        vm.ConfirmConsent = (_, _) => Task.FromResult<bool?>(true);
        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);
        var folder = store.MeetingFolder(workspace.Id, vm.Sidebar.SelectedMeeting!.Id)!;
        var meta = File.ReadAllBytes(Path.Combine(folder, WorkspaceStore.MeetingFileName));
        var files = Directory.GetFiles(folder);

        vm.ConfirmConsent = (_, _) => Task.FromResult<bool?>(null);
        await vm.StartCommand.ExecuteAsync(null);

        Assert.False(vm.IsRunning);
        Assert.Equal(meta, File.ReadAllBytes(Path.Combine(folder, WorkspaceStore.MeetingFileName)));
        Assert.Equal(files, Directory.GetFiles(folder));
    }

    [AvaloniaFact]
    public async Task EachRunKeepsARecordingOfItsOwnAndTheFirstIsNotTouched()
    {
        var (vm, store, workspace) = Live();
        vm.ConfirmConsent = (_, _) => Task.FromResult<bool?>(true);
        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);
        var first = Assert.Single(store.ListMeetings(workspace.Id).Meetings).Segments[0];
        var audio = File.ReadAllBytes(first.AudioPath!);
        var transcript = File.ReadAllBytes(first.TranscriptPath);

        await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null);

        var runs = Assert.Single(store.ListMeetings(workspace.Id).Meetings).Segments;
        Assert.Equal(
            [WorkspaceStore.AudioFileName, "audio-2.wav"], runs.Select(r => Path.GetFileName(r.AudioPath!)));
        Assert.True(File.Exists(runs[1].AudioPath));
        Assert.Equal(audio, File.ReadAllBytes(runs[0].AudioPath!));
        Assert.Equal(transcript, File.ReadAllBytes(runs[0].TranscriptPath));
    }

    [AvaloniaFact]
    public void TheDialogSaysWhichMeetingItContinues()
    {
        var fresh = new ConsentWindow(saveAudio: false, emphasiseRemote: false);
        fresh.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.False(Named<TextBlock>(fresh, "Continuing").IsVisible);
        fresh.Close();

        var again = new ConsentWindow(saveAudio: false, emphasiseRemote: false, continuing: "Werkzeugübergabe");
        again.Show();
        Dispatcher.UIThread.RunJobs();
        var note = Named<TextBlock>(again, "Continuing");

        Assert.True(note.IsVisible);
        Assert.Equal(Localizer.Instance.Format("consent.continue", "Werkzeugübergabe"), note.Text);
        Assert.Contains("Werkzeugübergabe", note.Text);
        again.Close();
    }

    [AvaloniaFact]
    public void ConfirmAndStartWaitsForTheSecondTick()
    {
        var window = new ConsentWindow(saveAudio: true, emphasiseRemote: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var save = Named<CheckBox>(window, "SaveAudio");
        var informed = Named<CheckBox>(window, "Informed");
        var start = Named<Button>(window, "Start");

        Assert.True(save.IsChecked);
        Assert.False(informed.IsChecked);
        Assert.False(start.IsEnabled);

        informed.IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(start.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void TheRemoteReminderIsAlwaysThereAndIsEmphasisedForAnOnlineMeeting()
    {
        var quiet = new ConsentWindow(saveAudio: false, emphasiseRemote: false);
        quiet.Show();
        Dispatcher.UIThread.RunJobs();
        var reminder = Named<TextBlock>(quiet, "RemoteReminder");

        Assert.True(reminder.IsVisible);
        Assert.Equal(Localizer.Instance["consent.remote.reminder"], reminder.Text);
        var plain = reminder.FontWeight;
        quiet.Close();

        var online = new ConsentWindow(saveAudio: false, emphasiseRemote: true);
        online.Show();
        Dispatcher.UIThread.RunJobs();
        var loud = Named<TextBlock>(online, "RemoteReminder");

        Assert.True(loud.IsVisible);
        Assert.NotEqual(plain, loud.FontWeight);
        online.Close();
    }

    [AvaloniaFact]
    public void TheToolbarNoLongerCarriesAConsentCheckbox()
    {
        var (vm, _, _) = Live();
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var bar = window.GetLogicalDescendants().OfType<IconBarView>().Single();

        Assert.Empty(bar.GetLogicalDescendants().OfType<CheckBox>());
        window.Close();
    }

    private static T Named<T>(Window root, string name) where T : Control =>
        root.GetLogicalDescendants().OfType<T>()
            .Where(control => control.Name == name).Distinct().Single();
}
