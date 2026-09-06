using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Providers.Testing;
using Kanal.Core.Providers;
using Kanal.Host.Services;

namespace Kanal.UI.UnitTests;

public class CaptureProfileTests
{
    [AvaloniaFact]
    public void CaptureProfileIsIndependentFromTheSpeechPipeline()
    {
        var vm = TestViewModels.Hermetic();

        Assert.Equal(2, vm.CaptureProfiles.Count);
        Assert.Equal(CaptureProfileId.InRoom, vm.SelectedCaptureProfile.Id);

        var pipeline = vm.SelectedMode;
        vm.SelectedCaptureProfile = vm.CaptureProfiles.Single(p => p.Id == CaptureProfileId.OnlineMeeting);

        Assert.Same(pipeline, vm.SelectedMode);
        Assert.True(vm.NeedsComputerAudio);
        Assert.Contains("headphone", vm.CaptureProfileGuidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("chat", vm.ConsentReminder, StringComparison.OrdinalIgnoreCase);
    }

    [AvaloniaFact]
    public void ARealMeetingCannotStartUntilTheOperatorAttestsConsent()
    {
        var settings = new AppSettings { RecordAudio = false };
        settings.ApiKeys.Add(new ApiKeyEntry("meeting-room", "gladia", "k"));
        settings.ActiveGladiaKeyName = "meeting-room";
        var vm = TestViewModels.Hermetic(settings);
        vm.SelectedMode = vm.Modes.Single(o => o.Mode.Id == PipelineModeId.CloudCloud);

        Assert.False(vm.StartCommand.CanExecute(null));

        vm.ConsentConfirmed = true;

        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ConsentIsPerMeetingAndItsTimestampIsExported()
    {
        var settings = new AppSettings { RecordAudio = false };
        settings.ApiKeys.Add(new ApiKeyEntry("meeting-room", "gladia", "k"));
        settings.ActiveGladiaKeyName = "meeting-room";
        var now = new DateTimeOffset(2026, 9, 4, 12, 30, 0, TimeSpan.Zero);
        var vm = TestViewModels.Hermetic(settings, utcNow: () => now);
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
        vm.ConsentConfirmed = true;
        now = now.AddMinutes(7); // model startup must not rewrite when consent was actually given

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsRunning);
        Assert.True(vm.IsLiveTranscription);
        Assert.Contains("capture-profile: in-room", vm.BuildMarkdownExport(), StringComparison.Ordinal);
        Assert.Contains("consent-confirmed-at: 2026-09-04T12:30:00.0000000+00:00", vm.BuildMarkdownExport(), StringComparison.Ordinal);
        Assert.Contains("\"captureProfile\": \"inRoom\"", vm.BuildJsonExport(), StringComparison.Ordinal);
        Assert.Contains("\"consentConfirmedAt\": \"2026-09-04T12:30:00+00:00\"", vm.BuildJsonExport(), StringComparison.Ordinal);

        await vm.StopCommand.ExecuteAsync(null);

        Assert.False(vm.ConsentConfirmed);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ScriptedDemoIgnoresAnUnavailableAudioProfile()
    {
        var vm = TestViewModels.Demo();
        vm.SelectedCaptureProfile = vm.CaptureProfiles.Single(p => p.Id == CaptureProfileId.OnlineMeeting);

        Assert.True(vm.StartCommand.CanExecute(null));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);
        Assert.False(vm.IsLiveTranscription);
        await vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task HostKeepsTheStrongerRecordingNoticeWhenTheTranscriberEnds()
    {
        var audioFolder = Path.Combine(
            Path.GetTempPath(), "kanal-capture-notice-" + Guid.NewGuid().ToString("N"));
        var settings = new AppSettings { AudioFolder = audioFolder };
        settings.ApiKeys.Add(new ApiKeyEntry("meeting-room", "gladia", "k"));
        settings.ActiveGladiaKeyName = "meeting-room";
        var vm = TestViewModels.Hermetic(settings);
        vm.SelectedMode = vm.Modes.Single(o => o.Mode.Id == PipelineModeId.CloudCloud);
        vm.PlanFilter = plan => plan with
        {
            Asr = new FakeAsrProvider(
                script: [new FakeAsrProvider.Line("S01", "zh", "好")],
                partialInterval: TimeSpan.FromMilliseconds(5),
                loop: false,
                caps: new AsrCapabilities(
                    Streaming: true, Diarization: true, Translation: true,
                    AutoLanguageDetect: true, new HashSet<string> { "zh" }, LatencyClass.Realtime)),
            Mt = null,
            CloudTranslation = true,
        };
        vm.ConsentConfirmed = true;

        await vm.StartCommand.ExecuteAsync(null);
        var deadline = Environment.TickCount64 + 2_000;
        while (vm.IsTranscribing && Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsLiveTranscription);
        Assert.True(vm.IsRecording);
        Assert.True(vm.ShowProcessingNotice);
        Assert.Equal(Kanal.Host.Localization.Localizer.Instance["recording.only.notice"], vm.LiveNoticeText);
        Assert.True(vm.IsRunning); // Stop remains available to close and export the ended room
        await vm.StopCommand.ExecuteAsync(null);
        Directory.Delete(audioFolder, recursive: true);
    }

    [AvaloniaFact]
    public void OnlineMeetingIsVisibleButCannotStartBeforeNativeCaptureLands()
    {
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.Single(o => o.Mode.Id == PipelineModeId.CloudCloud);
        vm.SelectedCaptureProfile = vm.CaptureProfiles.Single(p => p.Id == CaptureProfileId.OnlineMeeting);
        vm.ConsentConfirmed = true;

        Assert.False(vm.SelectedCaptureProfile.IsAvailable);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.False(string.IsNullOrWhiteSpace(vm.SelectedCaptureProfile.Unavailable));
    }
}
