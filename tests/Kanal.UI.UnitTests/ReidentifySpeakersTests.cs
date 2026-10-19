using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Meetings;
using Kanal.Core.Models;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class ReidentifySpeakersTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-reidentify-" + Guid.NewGuid().ToString("N"));

    private sealed class FakeDiarizer : ISpeakerDiarizer
    {
        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(
            string wavPath, int? speakerCount, IProgress<double>? progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SpeakerTurn>>([new(0, 1.0, 4), new(1.0, 2.0, 9)]);
    }

    private sealed class CountingDiarizer : ISpeakerDiarizer
    {
        public List<int?> Counts { get; } = [];

        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(
            string wavPath, int? speakerCount, IProgress<double>? progress, CancellationToken ct)
        {
            Counts.Add(speakerCount);
            return Task.FromResult<IReadOnlyList<SpeakerTurn>>([new(0, 2.0, 0)]);
        }
    }

    private sealed class OneSpeaker : ISpeakerDiarizer
    {
        public Task<IReadOnlyList<SpeakerTurn>> DiarizeAsync(
            string wavPath, int? speakerCount, IProgress<double>? progress, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SpeakerTurn>>([new(0, 1_000_000, 0)]);
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

    private (WorkspaceStore Store, MeetingRecord Record) Stored(bool recorded)
    {
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        var folder = Path.Combine(_root, "acme");
        Directory.CreateDirectory(folder);
        var workspace = store.CreateWorkspace("ACME", folder).Workspace!;
        var record = store.CreateMeeting(workspace.Id, "Abnahme").Meeting!;
        var meetingFolder = store.MeetingFolder(workspace.Id, record.Id)!;
        var transcript = Path.Combine(meetingFolder, TranscriptLog.FileName);
        using (var log = new TranscriptLogWriter(transcript, _ => { }))
        {
            log.Append(Said("u1", 0, 1_000));
            log.Append(Said("u2", 1_000, 2_000));
        }

        string? audio = null;
        if (recorded)
        {
            audio = Path.Combine(meetingFolder, WorkspaceStore.AudioFileName);
            File.WriteAllBytes(audio, [0]);
        }

        var held = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        return (store, store.SaveMeeting(record with
        {
            StartedAt = held,
            EndedAt = held.AddMinutes(30),
            TranscriptPath = transcript,
            AudioPath = audio,
            Languages = ["de", "zh"],
        }).Meeting!);
    }

    private static Utterance Said(string id, long startMs, long endMs) =>
        new(id, "S01", startMs, endMs, "de", $"Satz {id}", 1, UtteranceState.Final, false, 1,
            new Dictionary<string, string>());

    private string ModelsWithDefaultPair()
    {
        var dir = Path.Combine(_root, "models");
        Directory.CreateDirectory(dir);
        foreach (var model in new[] { DiarizationModelCatalog.DefaultSegmentation, DiarizationModelCatalog.DefaultEmbedding })
            File.WriteAllBytes(Path.Combine(dir, model.FileName), [0]);
        return dir;
    }

    [AvaloniaFact]
    public void AMeetingWithoutARecordingSaysWhyItCannotBeReidentified()
    {
        var (store, record) = Stored(recorded: false);
        var vm = TestViewModels.Hermetic(workspaces: () => store);

        var row = vm.Sidebar.Meetings.Single(m => m.Id == record.Id);

        Assert.False(row.ReidentifySpeakersCommand.CanExecute(null));
        Assert.Equal(Localizer.Instance["meeting.reidentify.noaudio"], row.ReidentifySpeakersHeader);
    }

    [AvaloniaFact]
    public async Task ReidentifyingABrowsedMeetingShowsTheNewSpeakersAndKeepsTheTranscript()
    {
        var (store, record) = Stored(recorded: true);
        var made = new List<(string, string)>();
        var vm = TestViewModels.Hermetic(
            modelsDir: ModelsWithDefaultPair(),
            workspaces: () => store,
            diarizerFactory: (segmentation, embedding) =>
            {
                made.Add((Path.GetFileName(segmentation), Path.GetFileName(embedding)));
                return new FakeDiarizer();
            });
        var row = vm.Sidebar.Meetings.Single(m => m.Id == record.Id);
        vm.Sidebar.SelectedMeeting = row;
        Dispatcher.UIThread.RunJobs();
        var before = File.ReadAllBytes(record.TranscriptPath!);

        Assert.True(row.ReidentifySpeakersCommand.CanExecute(null));
        await row.ReidentifySpeakersCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            new[] { (DiarizationModelCatalog.DefaultSegmentation.FileName, DiarizationModelCatalog.DefaultEmbedding.FileName) },
            made);
        Assert.Equal(["D01", "D02"],
            vm.ShownColumns.First().Bubbles.Select(b => b.SpeakerTag));
        Assert.Equal(before, File.ReadAllBytes(record.TranscriptPath!));
        Assert.Equal(Localizer.Instance.Format("meeting.reidentify.done", 2), vm.Status);
        Assert.False(row.IsReidentifying);
        Assert.Null(vm.Sidebar.ReidentifyingMeetingId);
    }

    [AvaloniaFact]
    public async Task ReidentifyingTheJustStoppedMeetingRetagsItsBubblesAndItsExport()
    {
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        Directory.CreateDirectory(Path.Combine(_root, "acme"));
        store.CreateWorkspace("ACME", Path.Combine(_root, "acme"));
        var vm = TestViewModels.Hermetic(
            modelsDir: ModelsWithDefaultPair(),
            workspaces: () => store,
            diarizerFactory: (_, _) => new OneSpeaker());
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);

        await vm.StartCommand.ExecuteAsync(null);
        var deadline = Environment.TickCount64 + 15_000;
        while (!vm.Columns.Any(c => c.Bubbles.Any(b => !b.IsPartial)) && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        await vm.StopCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var row = vm.Sidebar.SelectedMeeting!;
        Assert.False(vm.IsBrowsingRecord);
        var audio = Path.Combine(vm.Sidebar.FolderOf(row.Record)!, WorkspaceStore.AudioFileName);
        File.WriteAllBytes(audio, [0]);
        vm.Sidebar.SaveRecord(row.Record with { AudioPath = audio });

        await row.ReidentifySpeakersCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var finals = vm.Columns.SelectMany(c => c.Bubbles).Where(b => !b.IsPartial).ToList();
        Assert.NotEmpty(finals);
        Assert.All(finals, b => Assert.Equal("D01", b.SpeakerTag));
        Assert.Contains("**D01**", vm.BuildMarkdownExport());
        Assert.DoesNotContain("**S", vm.BuildMarkdownExport());
    }

    [AvaloniaTheory]
    [InlineData("3", 3)]
    [InlineData("8", 8)]
    [InlineData(null, null)]
    public async Task TheChosenNumberOfPeopleReachesTheDiarizer(string? chosen, int? expected)
    {
        var (store, record) = Stored(recorded: true);
        var diarizer = new CountingDiarizer();
        var vm = TestViewModels.Hermetic(
            modelsDir: ModelsWithDefaultPair(),
            workspaces: () => store,
            diarizerFactory: (_, _) => diarizer);
        var row = vm.Sidebar.Meetings.Single(m => m.Id == record.Id);

        await row.ReidentifySpeakersCommand.ExecuteAsync(chosen);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([expected], diarizer.Counts);
    }
}
