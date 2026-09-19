using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Core.Meetings;
using Kanal.Core.Models;
using Kanal.Core.Room;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;
using Kanal.Providers.LocalMt;

namespace Kanal.UI.UnitTests;

public class SidebarRenameAndTitleTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-rename-" + Guid.NewGuid().ToString("N"));

    private sealed class Titler(string? title) : IMeetingTitler
    {
        public int Calls { get; private set; }

        public IReadOnlyList<string> Seen { get; private set; } = [];

        public Task<string?> SuggestAsync(IReadOnlyList<string> lines, CancellationToken ct)
        {
            Calls++;
            Seen = lines;
            return Task.FromResult(title);
        }
    }

    private sealed class Owned(string? title, bool fails = false) : IMeetingTitler, IAsyncDisposable
    {
        public TaskCompletionSource<string?>? Held { get; init; }

        public int Disposed { get; private set; }

        public Task<string?> SuggestAsync(IReadOnlyList<string> lines, CancellationToken ct) =>
            fails ? throw new InvalidOperationException("decode failed")
            : Held?.Task ?? Task.FromResult(title);

        public ValueTask DisposeAsync()
        {
            Disposed++;
            return ValueTask.CompletedTask;
        }
    }

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

    private WorkspaceStore Store() => new(Path.Combine(_root, "workspaces.json"));

    private Workspace Opened(WorkspaceStore store)
    {
        var folder = Path.Combine(_root, "acme");
        Directory.CreateDirectory(folder);
        return store.CreateWorkspace("ACME", folder).Workspace!;
    }

    private static Utterance Said(string id, string text) =>
        new(id, "S1", 0, 1000, "de", text, 1, UtteranceState.Final, false, 1.0,
            new Dictionary<string, string> { ["zh"] = $"[zh] {text}" });

    private MeetingRecord Stored(WorkspaceStore store, Workspace workspace, string title)
    {
        var record = store.CreateMeeting(workspace.Id, title).Meeting!;
        var path = Path.Combine(store.MeetingFolder(workspace.Id, record.Id)!, TranscriptLog.FileName);
        using (var log = new TranscriptLogWriter(path, _ => { }))
        {
            log.Append(Said("u1", "Toleranz bei KX-4402"));
            log.Append(Said("u2", "Liefertermin bestätigt"));
        }

        var held = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
        return store.SaveMeeting(record with
        {
            StartedAt = held,
            EndedAt = held.AddMinutes(40),
            TranscriptPath = path,
            Languages = ["de", "zh"],
        }).Meeting!;
    }

    private static MeetingItemViewModel Row(MainViewModel vm, string id) =>
        vm.Sidebar.Meetings.Single(m => m.Id == id);

    private string DownloadedModel(AppSettings settings)
    {
        var model = LocalModelCatalog.Models[0];
        var dir = Path.Combine(_root, "models");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, model.FileName), [0x47, 0x47, 0x55, 0x46]);
        settings.ActiveTranslationModelId = model.Id;
        return dir;
    }

    private async Task<(MainViewModel Vm, MeetingRecord Ended, string Recording)> RecordingBesideAnEndedMeeting(
        IMeetingTitler titler)
    {
        var store = Store();
        var workspace = Opened(store);
        var ended = Stored(store, workspace, "Vorbesprechung");
        var settings = new AppSettings();
        var dir = Path.Combine(_root, "models");
        Directory.CreateDirectory(dir);
        var vm = TestViewModels.Hermetic(settings, dir, workspaces: () => store, titlerFactory: _ => titler);
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1200);
        var recording = vm.Sidebar.RecordingMeetingId!;

        // Downloaded only once the room is open: Demo would otherwise try to load the fake weights.
        DownloadedModel(settings);
        vm.RefreshPipelineStatus();
        return (vm, ended, recording);
    }

    private (MainViewModel Vm, MeetingRecord Ended) WithModel(IMeetingTitler titler)
    {
        var store = Store();
        var ended = Stored(store, Opened(store), "Vorbesprechung");
        var settings = new AppSettings();
        var vm = TestViewModels.Hermetic(
            settings, DownloadedModel(settings), workspaces: () => store, titlerFactory: _ => titler);
        return (vm, ended);
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
    public void RenamingFromTheSidebarRowChangesTheRecordAndTheRow()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = TestViewModels.Hermetic(workspaces: () => store);

        var row = Row(vm, older.Id);
        row.BeginRenameCommand.Execute(null);
        Assert.True(row.IsRenaming);
        Assert.Equal("Vorbesprechung", row.Draft);

        row.Draft = "  Werkzeugübergabe  ";
        row.CommitRenameCommand.Execute(null);

        Assert.False(row.IsRenaming);
        Assert.Equal("Werkzeugübergabe", row.Title);
        Assert.Equal("Werkzeugübergabe", store.ListMeetings(workspace.Id).Meetings.Single().Title);
    }

    [AvaloniaFact]
    public void AbandoningASidebarRenameLeavesTheTitleAlone()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = TestViewModels.Hermetic(workspaces: () => store);

        var row = Row(vm, older.Id);
        row.BeginRenameCommand.Execute(null);
        row.Draft = "half typed";
        row.CancelRenameCommand.Execute(null);

        Assert.False(row.IsRenaming);
        Assert.Equal("Vorbesprechung", row.Title);
        Assert.Equal("Vorbesprechung", store.ListMeetings(workspace.Id).Meetings.Single().Title);
    }

    [AvaloniaFact]
    public void RenamingASidebarRowToATakenTitleIsRefusedAndSaysSo()
    {
        var store = Store();
        var workspace = Opened(store);
        Stored(store, workspace, "Werkzeugübergabe");
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = TestViewModels.Hermetic(workspaces: () => store);

        var row = Row(vm, older.Id);
        row.BeginRenameCommand.Execute(null);
        row.Draft = "Werkzeugübergabe";
        row.CommitRenameCommand.Execute(null);

        Assert.Equal("Vorbesprechung", row.Title);
        Assert.Contains("Werkzeugübergabe", vm.Sidebar.ProblemNote);
        Assert.Equal(
            new[] { "Vorbesprechung", "Werkzeugübergabe" },
            store.ListMeetings(workspace.Id).Meetings.Select(m => m.Title).Order());
    }

    [AvaloniaFact]
    public async Task RenamingTheMeetingBeingRecordedMovesTheHeadingWithIt()
    {
        var store = Store();
        Opened(store);
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1200);
        var active = vm.Sidebar.SelectedMeeting!;

        active.BeginRenameCommand.Execute(null);
        active.Draft = "Toleranzprüfung";
        active.CommitRenameCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Toleranzprüfung", vm.MeetingTitle);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal("Toleranzprüfung", vm.MeetingTitle);
    }

    [AvaloniaFact]
    public async Task WithNothingBeingRecordedTheHeadingOfAnEndedMeetingIsStillEditable()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);

        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(1200);
        await vm.StopCommand.ExecuteAsync(null);

        vm.Sidebar.SelectedMeeting = Row(vm, older.Id);
        Assert.False(vm.IsTitleReadOnly);

        vm.BeginRenameTitleCommand.Execute(null);
        Assert.True(vm.IsRenamingTitle);
        vm.TitleDraft = "Werkzeugübergabe";
        vm.CommitRenameTitleCommand.Execute(null);

        Assert.Equal("Werkzeugübergabe", vm.MeetingTitle);
        Assert.Equal(
            "Werkzeugübergabe",
            store.ListMeetings(workspace.Id).Meetings.Single(m => m.Id == older.Id).Title);
    }

    [AvaloniaFact]
    public void TheNamingItemIsOffWithoutADownloadedModelAndOnWithOne()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");

        var without = TestViewModels.Hermetic(workspaces: () => store);
        Assert.False(Row(without, older.Id).CanGenerateTitle);
        Assert.False(Row(without, older.Id).GenerateTitleCommand.CanExecute(null));
        Assert.Equal(
            Localizer.Instance["meeting.generatetitle.nomodel"],
            Row(without, older.Id).GenerateTitleHeader);

        var model = LocalModelCatalog.Models[0];
        var dir = TestViewModels.EmptyModelsDir();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, model.FileName), [0x47, 0x47, 0x55, 0x46]);
        var with = TestViewModels.Hermetic(
            new AppSettings { ActiveTranslationModelId = model.Id },
            dir,
            workspaces: () => store);

        Assert.True(Row(with, older.Id).CanGenerateTitle);
        Assert.Equal(
            Localizer.Instance["meeting.generatetitle"], Row(with, older.Id).GenerateTitleHeader);
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task NamingAnEndedRecordRenamesItFromWhatWasSaidInIt()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var titler = new Titler("Toleranzprüfung");

        var model = LocalModelCatalog.Models[0];
        var dir = TestViewModels.EmptyModelsDir();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, model.FileName), [0x47, 0x47, 0x55, 0x46]);
        var vm = TestViewModels.Hermetic(
            new AppSettings { ActiveTranslationModelId = model.Id },
            dir,
            workspaces: () => store,
            titlerFactory: _ => titler);

        var row = Row(vm, older.Id);
        await row.GenerateTitleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, titler.Calls);
        Assert.Equal(["Toleranz bei KX-4402", "Liefertermin bestätigt"], titler.Seen);
        Assert.Equal("Toleranzprüfung", Row(vm, older.Id).Title);
        Assert.Equal(
            "Toleranzprüfung",
            store.ListMeetings(workspace.Id).Meetings.Single(m => m.Id == older.Id).Title);
        Assert.False(Row(vm, older.Id).IsNaming);
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task AGeneratedNameThatCollidesTakesASuffixRatherThanBeingRefused()
    {
        var store = Store();
        var workspace = Opened(store);
        Stored(store, workspace, "Toleranzprüfung");
        var older = Stored(store, workspace, "Vorbesprechung");

        var model = LocalModelCatalog.Models[0];
        var dir = TestViewModels.EmptyModelsDir();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, model.FileName), [0x47, 0x47, 0x55, 0x46]);
        var vm = TestViewModels.Hermetic(
            new AppSettings { ActiveTranslationModelId = model.Id },
            dir,
            workspaces: () => store,
            titlerFactory: _ => new Titler("Toleranzprüfung"));

        await Row(vm, older.Id).GenerateTitleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Toleranzprüfung 2", Row(vm, older.Id).Title);
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public async Task ANamingThatComesBackWithNothingSaysSoRatherThanRenamingToNothing()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");

        var model = LocalModelCatalog.Models[0];
        var dir = TestViewModels.EmptyModelsDir();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, model.FileName), [0x47, 0x47, 0x55, 0x46]);
        var vm = TestViewModels.Hermetic(
            new AppSettings { ActiveTranslationModelId = model.Id },
            dir,
            workspaces: () => store,
            titlerFactory: _ => new Titler(null));

        await Row(vm, older.Id).GenerateTitleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Vorbesprechung", Row(vm, older.Id).Title);
        Assert.Equal(Localizer.Instance["title.failed"], vm.Status);
        Directory.Delete(dir, recursive: true);
    }

    [AvaloniaFact]
    public void TheSidebarRowSwapsItsTitleForAnEditorWhileBeingRenamed()
    {
        var store = Store();
        var workspace = Opened(store);
        var older = Stored(store, workspace, "Vorbesprechung");
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var sidebar = window.GetLogicalDescendants().OfType<WorkspaceSidebarView>().Single();
        var title = sidebar.GetLogicalDescendants().OfType<TextBlock>()
            .Single(t => t.Name == "MeetingRowTitle");
        var editor = sidebar.GetLogicalDescendants().OfType<TextBox>()
            .Single(t => t.Name == "MeetingRowEditor");

        Assert.True(title.IsVisible);
        Assert.False(editor.IsVisible);

        Row(vm, older.Id).BeginRenameCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(title.IsVisible);
        Assert.True(editor.IsVisible);

        window.Close();
    }

    [AvaloniaFact]
    public async Task RenamingTheRecordedMeetingFromItsRowOverridesANameTheHeadingGaveIt()
    {
        var store = Store();
        Opened(store);
        var vm = TestViewModels.Hermetic(workspaces: () => store);
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);
        await vm.StartCommand.ExecuteAsync(null);
        await PumpAsync(600);

        vm.BeginRenameTitleCommand.Execute(null);
        vm.TitleDraft = "Toleranzprüfung";
        vm.CommitRenameTitleCommand.Execute(null);

        var active = vm.Sidebar.SelectedMeeting!;
        active.BeginRenameCommand.Execute(null);
        active.Draft = "Werkzeugübergabe";
        active.CommitRenameCommand.Execute(null);

        Assert.Equal("Werkzeugübergabe", vm.MeetingTitle);
        Assert.Equal("Werkzeugübergabe", vm.Titling.Title);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.Equal("Werkzeugübergabe", vm.MeetingTitle);
    }

    [AvaloniaFact]
    public async Task NamingIsOffWhileARoomIsRunningAndSaysWhy()
    {
        var (vm, ended, _) = await RecordingBesideAnEndedMeeting(new Titler("Toleranzprüfung"));

        Assert.False(Row(vm, ended.Id).GenerateTitleCommand.CanExecute(null));
        Assert.Equal(
            Localizer.Instance["meeting.generatetitle.running"], Row(vm, ended.Id).GenerateTitleHeader);

        await vm.StopCommand.ExecuteAsync(null);

        Assert.True(Row(vm, ended.Id).GenerateTitleCommand.CanExecute(null));
        Assert.Equal(Localizer.Instance["meeting.generatetitle"], Row(vm, ended.Id).GenerateTitleHeader);
    }

    [AvaloniaFact]
    public async Task NamingAnEndedMeetingNeverRenamesTheOneJustRecorded()
    {
        var (vm, ended, recording) = await RecordingBesideAnEndedMeeting(new Titler("Toleranzprüfung"));
        await vm.StopCommand.ExecuteAsync(null);
        var recordedTitle = Row(vm, recording).Title;
        vm.Sidebar.SelectedMeeting = Row(vm, ended.Id);

        await Row(vm, ended.Id).GenerateTitleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Toleranzprüfung", Row(vm, ended.Id).Title);
        Assert.Equal("Toleranzprüfung", vm.MeetingTitle);
        Assert.Equal(recordedTitle, Row(vm, recording).Title);

        vm.ReturnToActiveMeetingCommand.Execute(null);
        Assert.Equal(recordedTitle, vm.MeetingTitle);
    }

    [AvaloniaFact]
    public async Task NamingTheMeetingJustRecordedNeverRenamesTheEndedOneOnScreen()
    {
        var (vm, ended, recording) = await RecordingBesideAnEndedMeeting(new Titler("Toleranzprüfung"));
        vm.BeginRenameTitleCommand.Execute(null);
        vm.TitleDraft = "Werkzeugübergabe";
        vm.CommitRenameTitleCommand.Execute(null);
        var transcript = Row(vm, recording).Record.TranscriptPath!;
        for (var waited = 0; waited < 100 && !TranscriptLog.Read(transcript).Any(u => u.State == UtteranceState.Final); waited++)
            await PumpAsync(100);
        await vm.StopCommand.ExecuteAsync(null);
        vm.Sidebar.SelectedMeeting = Row(vm, ended.Id);

        await Row(vm, recording).GenerateTitleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Toleranzprüfung", Row(vm, recording).Title);
        Assert.Equal("Vorbesprechung", Row(vm, ended.Id).Title);
        Assert.Equal("Vorbesprechung", vm.MeetingTitle);

        vm.ReturnToActiveMeetingCommand.Execute(null);
        Assert.Equal("Toleranzprüfung", vm.MeetingTitle);
    }

    [AvaloniaFact]
    public async Task StartWaitsForANamingInFlight()
    {
        var held = new TaskCompletionSource<string?>();
        var titler = new Owned(null) { Held = held };
        var (vm, ended) = WithModel(titler);
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);
        Assert.True(vm.StartCommand.CanExecute(null));

        var naming = Row(vm, ended.Id).GenerateTitleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(Row(vm, ended.Id).IsNaming);
        Assert.False(vm.StartCommand.CanExecute(null));

        held.SetResult("Toleranzprüfung");
        await naming;
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TheNamingModelIsDisposedAfterASuccess()
    {
        var titler = new Owned("Toleranzprüfung");
        var (vm, ended) = WithModel(titler);

        await Row(vm, ended.Id).GenerateTitleCommand.ExecuteAsync(null);

        Assert.Equal("Toleranzprüfung", Row(vm, ended.Id).Title);
        Assert.Equal(1, titler.Disposed);
    }

    [AvaloniaFact]
    public async Task TheNamingModelIsDisposedAfterAFailure()
    {
        var titler = new Owned(null, fails: true);
        var (vm, ended) = WithModel(titler);

        await Row(vm, ended.Id).GenerateTitleCommand.ExecuteAsync(null);

        Assert.Equal("Vorbesprechung", Row(vm, ended.Id).Title);
        Assert.Equal(Localizer.Instance["title.failed"], vm.Status);
        Assert.Equal(1, titler.Disposed);
    }

    [AvaloniaFact]
    public async Task AMeetingWithNothingSaidIsNotNamedAndNoModelIsLoaded()
    {
        var store = Store();
        var workspace = Opened(store);
        var silent = store.CreateMeeting(workspace.Id, "Vorbesprechung").Meeting!;
        var loads = 0;
        var settings = new AppSettings();
        var vm = TestViewModels.Hermetic(
            settings, DownloadedModel(settings), workspaces: () => store,
            titlerFactory: _ => { loads++; return new Titler("Erfunden"); });

        await Row(vm, silent.Id).GenerateTitleCommand.ExecuteAsync(null);

        Assert.Equal(0, loads);
        Assert.Equal("Vorbesprechung", Row(vm, silent.Id).Title);
        Assert.Equal(Localizer.Instance["meeting.generatetitle.empty"], vm.Status);
    }

    [AvaloniaFact]
    public async Task AMeetingWhoseTranscriptIsGoneIsNotNamedAndNoModelIsLoaded()
    {
        var store = Store();
        var workspace = Opened(store);
        var gone = Stored(store, workspace, "Vorbesprechung");
        File.Delete(gone.TranscriptPath!);
        var loads = 0;
        var settings = new AppSettings();
        var vm = TestViewModels.Hermetic(
            settings, DownloadedModel(settings), workspaces: () => store,
            titlerFactory: _ => { loads++; return new Titler("Erfunden"); });

        await Row(vm, gone.Id).GenerateTitleCommand.ExecuteAsync(null);

        Assert.Equal(0, loads);
        Assert.Equal("Vorbesprechung", Row(vm, gone.Id).Title);
        Assert.Equal(Localizer.Instance["meeting.generatetitle.empty"], vm.Status);
    }

    [AvaloniaFact]
    public void TheMenuShowsTheNamingItemDisabledWithoutAModelAndEnabledWithOne()
    {
        var store = Store();
        var workspace = Opened(store);
        Stored(store, workspace, "Vorbesprechung");

        MenuItem Naming(MainViewModel vm)
        {
            var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var menu = window.GetLogicalDescendants().OfType<Button>()
                .Where(b => b.Name == "MeetingMenu").Distinct().Single();
            var flyout = Assert.IsType<MenuFlyout>(menu.Flyout);
            flyout.ShowAt(menu);
            Dispatcher.UIThread.RunJobs();
            return flyout.Items.OfType<MenuItem>().Single(i => i.Name == "GenerateMeetingTitle");
        }

        Assert.False(Naming(TestViewModels.Hermetic(workspaces: () => store)).IsEffectivelyEnabled);

        var settings = new AppSettings();
        var dir = DownloadedModel(settings);
        Assert.True(Naming(TestViewModels.Hermetic(settings, dir, workspaces: () => store)).IsEffectivelyEnabled);
    }
}
