using Avalonia.Headless.XUnit;
using Kanal.Core.Diagnostics;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class RefusedStoreOperationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-refused-" + Guid.NewGuid().ToString("N"));

    private sealed record Line(LogLevel Level, string Category, string Message);

    private sealed class RecordingSink : ILogSink
    {
        public List<Line> Lines { get; } = [];

        public void Write(LogLevel level, string category, string message, Exception? error) =>
            Lines.Add(new Line(level, category, message));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private WorkspaceStore Store() => new(Path.Combine(_root, "workspaces.json"));

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
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

    private static IDisposable Listening(out RecordingSink sink)
    {
        var previous = Log.Sink;
        var recording = new RecordingSink();
        sink = recording;
        Log.Install(recording);
        return new Restore(() => Log.Install(previous));
    }

    private static IDisposable Speaking(string language)
    {
        var l = Localizer.Instance;
        var before = l.Current;
        l.Current = language;
        return new Restore(() => l.Current = before);
    }

    private (WorkspaceStore Store, WorkspaceSidebarViewModel Vm, Workspace Workspace, MeetingRecord Meeting) Opened()
    {
        var store = Store();
        var workspace = store.CreateWorkspace("Kappa", Folder("kappa")).Workspace!;
        var meeting = store.CreateMeeting(workspace.Id, "Delivery call").Meeting!;
        var vm = new WorkspaceSidebarViewModel(store);
        vm.SelectedWorkspace = vm.Workspaces.Single(w => w.Id == workspace.Id);
        return (store, vm, workspace, meeting);
    }

    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("de")]
    [InlineData("pl")]
    public void ABlankTitleIsRefusedInTheInterfaceLanguage(string language)
    {
        using var _ = Speaking(language);
        using var __ = Listening(out var sink);
        var (_, vm, _, meeting) = Opened();

        var renamed = vm.RenameMeeting(meeting.Id, "   ");

        var l = Localizer.Instance;
        Assert.False(renamed);
        Assert.Equal(l["workspace.refused.invalid"], vm.ProblemNote);
        Assert.DoesNotContain("needs a title", vm.ProblemNote);
        var line = Assert.Single(sink.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal($"Invalid on {meeting.Id}: A meeting needs a title.", line.Message);
    }

    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("de")]
    [InlineData("pl")]
    public void AMeetingThatCannotBeWrittenNamesItsFolderInTheInterfaceLanguage(string language)
    {
        using var _ = Speaking(language);
        using var __ = Listening(out var sink);
        var (store, vm, _, meeting) = Opened();
        var folder = store.MeetingFolder(meeting.WorkspaceId, meeting.Id)!;
        Directory.Delete(folder, recursive: true);
        File.WriteAllText(folder, "a file where the folder was");

        var saved = vm.SaveRecord(meeting with { Title = "Delivery call 2" });

        Assert.Null(saved);
        Assert.Equal(Localizer.Instance.Format("workspace.refused.unwritable", folder), vm.ProblemNote);
        Assert.Contains(folder, vm.ProblemNote);
        Assert.DoesNotContain("could not be written", vm.ProblemNote);
        var line = Assert.Single(sink.Lines, l => l.Message.StartsWith("Unwritable on "));
        Assert.Contains("The meeting could not be written.", line.Message);
    }

    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("de")]
    [InlineData("pl")]
    public void AMeetingThatIsNotThereIsRefusedInTheInterfaceLanguage(string language)
    {
        using var _ = Speaking(language);
        using var __ = Listening(out var sink);
        var (_, vm, _, _) = Opened();

        vm.RenameMeeting("nosuchmeeting", "Anything");

        Assert.Equal(Localizer.Instance["workspace.refused.notfound"], vm.ProblemNote);
        Assert.DoesNotContain("No such", vm.ProblemNote);
        Assert.Equal("NotFound on nosuchmeeting: No such meeting.", Assert.Single(sink.Lines).Message);
    }

    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("de")]
    [InlineData("pl")]
    public void AMissingProjectFolderIsNamedInTheInterfaceLanguage(string language)
    {
        using var _ = Speaking(language);
        using var __ = Listening(out var sink);
        var (_, vm, workspace, _) = Opened();
        Directory.Delete(workspace.RootPath, recursive: true);

        vm.NewMeetingCommand.Execute(null);

        Assert.Equal(
            Localizer.Instance.Format("workspace.refused.foldermissing", workspace.RootPath), vm.ProblemNote);
        Assert.DoesNotContain("is not there", vm.ProblemNote);
        Assert.Contains(sink.Lines, l => l.Message.StartsWith($"FolderMissing on {workspace.RootPath}: "));
    }

    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("de")]
    [InlineData("pl")]
    public void ADamagedMeetingRecordIsRefusedInTheInterfaceLanguage(string language)
    {
        using var _ = Speaking(language);
        using var __ = Listening(out var sink);
        var (store, vm, _, meeting) = Opened();
        var file = Path.Combine(store.MeetingFolder(meeting.WorkspaceId, meeting.Id)!, WorkspaceStore.MeetingFileName);
        File.WriteAllText(file, "{ not json");

        vm.RenameMeeting(meeting.Id, "Another title");

        Assert.Equal(Localizer.Instance.Format("workspace.refused.unreadable", file), vm.ProblemNote);
        Assert.Contains(sink.Lines, l => l.Message.StartsWith($"Unreadable on {file}: "));
    }

    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("de")]
    [InlineData("pl")]
    public void ARecordInAFormatThisKanalCannotReadIsRefusedInTheInterfaceLanguage(string language)
    {
        using var _ = Speaking(language);
        using var __ = Listening(out var sink);
        var (store, vm, _, meeting) = Opened();
        var file = Path.Combine(store.MeetingFolder(meeting.WorkspaceId, meeting.Id)!, WorkspaceStore.MeetingFileName);
        File.WriteAllText(file, "{\"schemaVersion\": 999}");

        vm.RenameMeeting(meeting.Id, "Another title");

        Assert.Equal(Localizer.Instance.Format("workspace.refused.unsupportedversion", file), vm.ProblemNote);
        Assert.DoesNotContain("Unsupported", vm.ProblemNote);
        Assert.Contains(sink.Lines, l => l.Message.StartsWith($"UnsupportedVersion on {file}: Unsupported"));
    }

    [AvaloniaTheory]
    [InlineData("zh")]
    [InlineData("de")]
    [InlineData("pl")]
    public async Task AnImportOfAFileThatIsNoBundleIsRefusedInTheInterfaceLanguage(string language)
    {
        using var _ = Speaking(language);
        using var __ = Listening(out var sink);
        var (_, vm, _, _) = Opened();
        var bundle = Path.Combine(Folder("incoming"), "broken" + MeetingBundle.Extension);
        File.WriteAllText(bundle, "this is not a zip file");

        await vm.ImportMeetingFromAsync(bundle);

        Assert.Equal(Localizer.Instance.Format("workspace.refused.unreadable", bundle), vm.ProblemNote);
        Assert.Contains(sink.Lines, l => l.Message.StartsWith($"Unreadable on {bundle}: "));
    }

    [AvaloniaFact]
    public void ATitleAnotherMeetingHoldsKeepsItsOwnSentence()
    {
        using var _ = Speaking("de");
        var (store, vm, workspace, meeting) = Opened();
        store.CreateMeeting(workspace.Id, "Second call");

        vm.RenameMeeting(meeting.Id, "Second call");

        Assert.Equal(Localizer.Instance.Format("workspace.titletaken", "Second call"), vm.ProblemNote);
    }

    [AvaloniaFact]
    public void EveryRefusalSentenceExistsInEveryLanguageAndDiffersFromEnglish()
    {
        string[] keys =
        [
            "workspace.refused.invalid",
            "workspace.refused.notfound",
            "workspace.refused.foldermissing",
            "workspace.refused.unreadable",
            "workspace.refused.unwritable",
            "workspace.refused.unsupportedversion",
            "workspace.refused.other",
        ];
        foreach (var code in new[] { "zh", "de", "pl" })
            foreach (var key in keys)
            {
                Assert.True(Strings.Tables[code].ContainsKey(key), $"{code} lacks {key}");
                Assert.NotEqual(Strings.Tables["en"][key], Strings.Tables[code][key]);
            }
    }

    [AvaloniaTheory]
    [InlineData(StoreProblemKind.TooLarge, "workspace.icon.toolarge")]
    [InlineData(StoreProblemKind.WrongType, "workspace.icon.badtype")]
    [InlineData(StoreProblemKind.NotThisWorkspace, "workspace.remove.notthisproject")]
    [InlineData(StoreProblemKind.TitleTaken, "workspace.titletaken")]
    [InlineData(StoreProblemKind.Invalid, "workspace.refused.invalid")]
    [InlineData(StoreProblemKind.NotFound, "workspace.refused.notfound")]
    [InlineData(StoreProblemKind.FolderMissing, "workspace.refused.foldermissing")]
    [InlineData(StoreProblemKind.Unreadable, "workspace.refused.unreadable")]
    [InlineData(StoreProblemKind.Unwritable, "workspace.refused.unwritable")]
    [InlineData(StoreProblemKind.UnsupportedVersion, "workspace.refused.unsupportedversion")]
    public void EveryKindOfRefusalHasASentenceAndOnlyTheLogGetsTheDetail(StoreProblemKind kind, string key)
    {
        using var _ = Speaking("pl");
        using var __ = Listening(out var sink);
        var problem = new StoreProblem(kind, "subject-x", "English detail for the log.");

        var text = StoreRefusal.Report(problem, "workspace");

        Assert.Equal(Localizer.Instance.Format(key, "subject-x"), text);
        Assert.DoesNotContain("English detail", text);
        Assert.Equal($"{kind} on subject-x: English detail for the log.", Assert.Single(sink.Lines).Message);
    }

    [AvaloniaFact]
    public void ACallerMayChooseTheSentenceAndTheLogStillGetsTheDetail()
    {
        using var _ = Speaking("de");
        using var __ = Listening(out var sink);
        var problem = new StoreProblem(StoreProblemKind.NotFound, "x.png", "That image is not there.");

        var text = StoreRefusal.Report(problem, "workspace", "workspace.icon.missing");

        Assert.Equal(Localizer.Instance["workspace.icon.missing"], text);
        Assert.Equal("NotFound on x.png: That image is not there.", Assert.Single(sink.Lines).Message);
    }
}
