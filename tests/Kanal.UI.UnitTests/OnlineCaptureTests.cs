using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Audio;
using Kanal.Core.Diagnostics;
using Kanal.Core.Models;
using Kanal.Core.Providers;
using Kanal.Core.Relay;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Providers.LocalMt;

namespace Kanal.UI.UnitTests;

public class OnlineCaptureTests
{
    private const string ApiKey = "sk-online-secret";

    private static Localizer L => Localizer.Instance;

    [AvaloniaFact]
    public async Task OnlineStartOpensBothChosenSourcesAndTheirMixReachesTheTranscriber()
    {
        using var rig = new Rig();
        rig.Online();
        rig.Choose("mic-b");

        await rig.StartAsync();
        await Until(() => rig.Asr.Heard((short)Math.Round((1000 + 3000) * Math.Sqrt(0.5))));

        Assert.Equal(["mic-b"], rig.Microphone.Opened);
        Assert.Single(rig.Computer.Opened);
        Assert.True(rig.Vm.ShowMicLevel);
        Assert.True(rig.Vm.ShowComputerLevel);
        Assert.Contains(rig.Lines, l => l.Category == "room" && l.Message.Contains("capture online"));

        await rig.Vm.StopCommand.ExecuteAsync(null);

        Assert.Equal(1, rig.Microphone.Closes);
        Assert.Equal(1, rig.Computer.Closes);
    }

    [AvaloniaFact]
    public async Task PauseReleasesBothSourcesAndResumeReopensThem()
    {
        using var rig = new Rig();
        rig.Online();
        rig.Choose("mic-b");
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        await rig.Vm.PauseCommand.ExecuteAsync(null);

        Assert.True(rig.Vm.IsPaused);
        Assert.Equal(1, rig.Microphone.Closes);
        Assert.Equal(1, rig.Computer.Closes);
        var whilePaused = rig.Asr.Pushes;
        await Pump(200);
        Assert.Equal(whilePaused, rig.Asr.Pushes);

        await rig.Vm.PauseCommand.ExecuteAsync(null);
        await Until(() => rig.Asr.Pushes > whilePaused);

        Assert.False(rig.Vm.IsPaused);
        Assert.Equal(["mic-b", "mic-b"], rig.Microphone.Opened);
        Assert.Equal(2, rig.Computer.Opened.Count);
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task PausingAnInRoomMeetingReleasesTheMicrophoneToo()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        await rig.Vm.PauseCommand.ExecuteAsync(null);
        Assert.Equal(1, rig.Microphone.Closes);

        await rig.Vm.PauseCommand.ExecuteAsync(null);
        await Until(() => rig.Microphone.Opened.Count == 2);
        Assert.Empty(rig.Computer.Opened);
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AResumeThatLandsAfterStopReopensNothing()
    {
        using var rig = new Rig();
        rig.Online();
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);
        await rig.Vm.PauseCommand.ExecuteAsync(null);
        rig.Relay.Hold();

        var resume = rig.Vm.PauseCommand.ExecuteAsync(null);
        await Until(() => rig.Relay.ResumesWaiting == 1);
        await rig.Vm.StopCommand.ExecuteAsync(null);
        var stopped = rig.Vm.Status;
        rig.Relay.Release();
        await resume;
        await Pump(100);

        Assert.False(rig.Vm.IsRunning);
        Assert.False(rig.Vm.IsPaused);
        Assert.Equal(stopped, rig.Vm.Status);
        Assert.Single(rig.Microphone.Opened);
        Assert.Single(rig.Computer.Opened);
    }

    [AvaloniaFact]
    public async Task AResumeThatLandsInTheNextMeetingLeavesItAlone()
    {
        using var rig = new Rig();
        rig.Online();
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);
        await rig.Vm.PauseCommand.ExecuteAsync(null);
        rig.Relay.Hold();
        var resume = rig.Vm.PauseCommand.ExecuteAsync(null);
        await Until(() => rig.Relay.ResumesWaiting == 1);
        await rig.Vm.StopCommand.ExecuteAsync(null);

        rig.Vm.SelectedMode = rig.Vm.Modes.Single(o => o.Mode.Id == PipelineModeId.Demo);
        await rig.StartAsync();
        await Pump(100);
        var live = rig.Vm.Status;
        rig.Relay.Release();
        await resume;
        await Pump(100);

        Assert.True(rig.Vm.IsRunning);
        Assert.False(rig.Vm.IsPaused);
        Assert.Equal(live, rig.Vm.Status);
        Assert.Single(rig.Microphone.Opened);
        Assert.Single(rig.Computer.Opened);
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task PauseAndStopTogetherShareOneTeardown()
    {
        using var rig = new Rig();
        rig.Online();
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Microphone.CloseGate = release.Task;

        var pause = rig.Vm.PauseCommand.ExecuteAsync(null);
        await Until(() => rig.Vm.IsPaused);
        var stop = rig.Vm.StopCommand.ExecuteAsync(null);
        await Pump(100);

        Assert.False(pause.IsCompleted);
        Assert.False(stop.IsCompleted);
        Assert.Equal(0, rig.Microphone.Closes);

        release.SetResult();
        await pause;
        await stop;

        Assert.False(rig.Vm.IsRunning);
        Assert.False(rig.Vm.IsPaused);
        Assert.Single(rig.Microphone.Opened);
        Assert.Equal(1, rig.Microphone.Closes);
        Assert.Equal(1, rig.Computer.Closes);
    }

    [AvaloniaFact]
    public async Task LosingTheActiveMicrophoneStopsTheOnlineMeetingVisibly()
    {
        using var rig = new Rig();
        rig.Online();
        rig.Choose("mic-b");
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        rig.Microphone.DeviceIds.Remove("mic-b");
        await Task.Run(rig.Watcher.Raise, TestContext.Current.CancellationToken);
        await Until(() => !rig.Vm.IsRunning && !rig.Vm.IsStopping);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(L.Format("status.audiofailed", L["capture.device.lost"]), rig.Vm.Status);
        Assert.Contains(rig.Lines, l => l.Category == "audio" &&
            l.Message.Contains("capture_fault code=device_unavailable source=microphone"));
        Assert.Equal(1, rig.Microphone.Closes);
        Assert.Equal(1, rig.Computer.Closes);
    }

    [AvaloniaFact]
    public async Task AnUnrelatedDeviceChangeLeavesTheMeetingRunning()
    {
        using var rig = new Rig();
        rig.Online();
        rig.Choose("mic-b");
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        rig.Microphone.DeviceIds.Remove("mic-a");
        rig.Microphone.DeviceIds.Add("usb-1");
        await Task.Run(rig.Watcher.Raise, TestContext.Current.CancellationToken);
        await Pump(150);

        Assert.True(rig.Vm.IsRunning);
        Assert.Equal("mic-b", rig.Vm.SelectedDevice?.Id);
        Assert.Equal(0, rig.Microphone.Closes);
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task PickingAnotherMicrophoneWhileRecordingMovesCaptureToIt()
    {
        using var rig = new Rig();
        rig.Choose("mic-a");
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        Assert.True(rig.Vm.CanChooseDevice);
        Assert.False(rig.Vm.CanChooseAudio);
        rig.Choose("mic-b");
        await Until(() => rig.Microphone.Opened.Count == 2);
        var afterSwitch = rig.Asr.Pushes;
        await Until(() => rig.Asr.Pushes > afterSwitch);

        Assert.True(rig.Vm.IsRunning);
        Assert.Equal(["mic-a", "mic-b"], rig.Microphone.Opened);
        Assert.Equal(1, rig.Microphone.Closes);
        Assert.Empty(rig.Computer.Opened);
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task PickingAnotherMicrophoneInAnOnlineMeetingReopensTheComputerAudioToo()
    {
        using var rig = new Rig();
        rig.Online();
        rig.Choose("mic-a");
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        rig.Choose("mic-b");
        await Until(() => rig.Microphone.Opened.Count == 2 && rig.Computer.Opened.Count == 2);

        Assert.True(rig.Vm.IsRunning);
        Assert.Equal(["mic-a", "mic-b"], rig.Microphone.Opened);
        Assert.Equal(1, rig.Computer.Closes);
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AfterTheInRoomMicrophoneIsUnpluggedTheMenuShowsNoneAndAnyRemainingOneCanBePicked()
    {
        using var rig = new Rig();
        rig.Choose("mic-b");
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        var live = rig.Vm.Status;
        rig.Microphone.DeviceIds.Remove("mic-b");
        rig.Microphone.Fail(new AudioCaptureException(AudioCaptureFault.DeviceUnavailable, "gone"));
        await Task.Run(rig.Watcher.Raise, TestContext.Current.CancellationToken);
        await Until(() => rig.Vm.Status != live);

        Assert.True(rig.Vm.IsRunning);
        Assert.Null(rig.Vm.SelectedDevice);
        rig.Choose("mic-a");
        await Until(() => rig.Microphone.Opened.Count == 2);

        Assert.Equal(["mic-b", "mic-a"], rig.Microphone.Opened);
        await Until(() => rig.Vm.Status == live);
        await rig.Vm.StopCommand.ExecuteAsync(null);
        Assert.Equal("mic-a", rig.Vm.SelectedDevice?.Id);
    }

    [AvaloniaFact]
    public async Task AMicrophoneThatAppearsDuringAMeetingOnTheDefaultDeviceCanBePicked()
    {
        using var rig = new Rig(microphones: []);
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        rig.Microphone.DeviceIds.Add("usb-1");
        await Task.Run(rig.Watcher.Raise, TestContext.Current.CancellationToken);
        await Pump(150);

        Assert.Null(rig.Vm.SelectedDevice);
        rig.Choose("usb-1");
        await Until(() => rig.Microphone.Opened.Count == 2);
        Assert.Equal(new string?[] { null, "usb-1" }, rig.Microphone.Opened);
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task PickingAWorkingMicrophoneAfterAnInRoomFaultClearsTheFailureFromTheStatus()
    {
        using var rig = new Rig();
        rig.Choose("mic-a");
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);
        var live = rig.Vm.Status;

        rig.Microphone.Fail(new IOException("usb gone"));
        await Until(() => rig.Vm.Status != live);

        rig.Choose("mic-b");
        await Until(() => rig.Microphone.Opened.Count == 2);
        await Until(() => rig.Vm.Status == live);
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task ADevicePickedWhilePausedIsOpenedOnResume()
    {
        using var rig = new Rig();
        rig.Choose("mic-a");
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);
        await rig.Vm.PauseCommand.ExecuteAsync(null);

        rig.Choose("mic-b");
        await Pump(150);
        Assert.Equal(["mic-a"], rig.Microphone.Opened);

        await rig.Vm.PauseCommand.ExecuteAsync(null);
        await Until(() => rig.Microphone.Opened.Count == 2);
        Assert.Equal(["mic-a", "mic-b"], rig.Microphone.Opened);
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AMeetingOnTheDefaultDevicesIsNotStoppedByAListChange()
    {
        using var rig = new Rig(microphones: []);
        rig.Online();
        Assert.Null(rig.Vm.SelectedDevice);
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        rig.Microphone.DeviceIds.Add("usb-1");
        await Task.Run(rig.Watcher.Raise, TestContext.Current.CancellationToken);
        await Pump(150);

        Assert.True(rig.Vm.IsRunning);
        Assert.Equal(new string?[] { null }, rig.Microphone.Opened);
        Assert.Single(rig.Computer.Opened);
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AnOnlineSourceFaultStopsTheMeetingWithALocalizedReasonAndACodedLogLine()
    {
        using var rig = new Rig();
        rig.Online();
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        rig.Computer.Fail(new UnauthorizedAccessException("denied by the platform"));
        await Until(() => !rig.Vm.IsRunning && !rig.Vm.IsStopping);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(
            L.Format("status.audiofailed",
                L.Format("capture.fault.permission_denied", L["capture.source.system"])),
            rig.Vm.Status);
        var fault = Assert.Single(rig.Lines, l => l.Message.StartsWith("capture_fault"));
        Assert.Equal(LogLevel.Error, fault.Level);
        Assert.Equal("audio", fault.Category);
        Assert.Contains("code=permission_denied source=system mode=online", fault.Message);
        var written = string.Join("\n", rig.Lines.Select(l => $"{l.Message} {l.Error}"));
        Assert.DoesNotContain(ApiKey, written);
        Assert.Equal(1, rig.Microphone.Closes);
    }

    [AvaloniaFact]
    public async Task AnInRoomCaptureFailureKeepsTheRoomLiveAndSaysWhy()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        rig.Microphone.Fail(new IOException("usb gone"));
        var expected = L.Format("status.audiofailed",
            L.Format("capture.fault.source_failed", L["capture.source.microphone"], "usb gone"));
        await Until(() => rig.Vm.Status == expected);

        Assert.True(rig.Vm.IsRunning);
        var fault = Assert.Single(rig.Lines, l => l.Message.StartsWith("capture_fault"));
        Assert.Contains("code=source_failed source=microphone mode=in-room", fault.Message);
        Assert.Contains("the room is live with no audio arriving", fault.Message);
        Assert.NotNull(fault.Error);
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AnInRoomStreamThatEndsSaysSoAndKeepsTheRoomLive()
    {
        using var rig = new Rig();
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        rig.Microphone.End();
        var expected = L.Format("status.audiofailed",
            L.Format("capture.fault.source_ended", L["capture.source.microphone"]));
        await Until(() => rig.Vm.Status == expected);

        Assert.True(rig.Vm.IsRunning);
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task InRoomCaptureLogsItsFirstFrameAndItsProgressAtDebug()
    {
        using var rig = new Rig();
        rig.Microphone.Burst = 50;
        rig.Choose("mic-b");
        await rig.StartAsync();
        await Until(() => rig.Lines.Any(l => l.Message == "500 frames captured."));

        var first = Assert.Single(rig.Lines, l => l.Message == $"Capture running on {AudioDeviceIds.Hash("mic-b")}.");
        Assert.Equal(LogLevel.Debug, first.Level);
        Assert.Equal("audio", first.Category);
        await rig.Vm.StopCommand.ExecuteAsync(null);
        Assert.DoesNotContain(rig.Lines, l => l.Message.Contains("mic-b"));
    }

    [AvaloniaFact]
    public async Task AnInRoomMicrophoneThatDeliversOnlyZerosRaisesTheSilenceHintUntilSoundArrives()
    {
        using var rig = new Rig();
        rig.Microphone.Level = 0;
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        rig.Clock.Advance(SignalWatch.StartupGrace + TimeSpan.FromSeconds(1));
        await Until(() => rig.Vm.AudioSourceStatus == L["capture.microphone.silent"]);
        Assert.True(rig.Vm.ShowAudioHintInStatusBar);
        Assert.Contains(rig.Lines, l => l.Message == "signal source=microphone state=silent_signal");

        rig.Microphone.Level = 5000;
        await Until(() => Tick(rig) && rig.Vm.AudioSourceStatus == "");
        Assert.True(rig.Vm.MicLevel > 0);

        await rig.Vm.StopCommand.ExecuteAsync(null);
        Assert.False(rig.Vm.ShowAudioHintInStatusBar);
    }

    [AvaloniaFact]
    public async Task AnInRoomMicrophoneThatOpensButNeverDeliversIsNamedAsNoSignal()
    {
        using var rig = new Rig();
        rig.Microphone.Hang = true;
        await rig.StartAsync();
        await Until(() => rig.Microphone.Opened.Count == 1);

        rig.Clock.Advance(SignalWatch.StartupGrace + TimeSpan.FromSeconds(1));
        await Until(() => rig.Vm.AudioSourceStatus == L["capture.microphone.silent"]);

        Assert.Contains(rig.Lines, l => l.Message == "signal source=microphone state=no_signal");
        Assert.Equal(0, rig.Asr.Pushes);
        Assert.True(rig.Vm.IsRunning);
        await rig.Vm.StopCommand.ExecuteAsync(null);
        Assert.Equal(1, rig.Microphone.Closes);
    }

    [AvaloniaFact]
    public async Task OnlineMetersAreSeparateAndOnlyTheSilentComputerAudioIsFlagged()
    {
        using var rig = new Rig();
        rig.Online();
        rig.Microphone.Level = 16384;
        rig.Computer.Level = 50;
        await rig.StartAsync();
        await Until(() => rig.Vm.MicLevel > 40 && rig.Vm.ComputerLevel > 0);

        Assert.InRange(rig.Vm.MicLevel, 49, 51);
        Assert.InRange(rig.Vm.ComputerLevel, 0.01, SignalWatch.SoundPeak * 100);

        rig.Clock.Advance(SignalWatch.StartupGrace + TimeSpan.FromSeconds(1));
        await Pump(400);
        Assert.Equal("", rig.Vm.AudioSourceStatus);

        rig.Clock.Advance(SignalWatch.RemoteStartupGrace);
        await Until(() => rig.Vm.AudioSourceStatus == L["capture.system.silent"]);

        rig.Computer.Level = 8000;
        await Until(() => rig.Vm.AudioSourceStatus == "");
        Assert.Single(rig.Lines, l => l.Message == "signal source=system state=silent_signal");
        Assert.Single(rig.Lines, l => l.Message == "signal source=system state=sound");
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task TheComputerAudioIsNotFlaggedWhileNobodyHasSpokenYet()
    {
        using var rig = new Rig();
        rig.Online();
        rig.Microphone.Level = 0;
        rig.Computer.Level = 0;
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        rig.Clock.Advance(SignalWatch.RemoteStartupGrace + TimeSpan.FromSeconds(1));
        await Until(() => rig.Vm.AudioSourceStatus == L["capture.microphone.silent"]);
        await Pump(400);

        Assert.Equal(L["capture.microphone.silent"], rig.Vm.AudioSourceStatus);
        Assert.DoesNotContain(rig.Lines, l => l.Message.StartsWith("signal source=system state=") &&
            !l.Message.EndsWith("=pending"));
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task ALongQuietSpellOnTheComputerAudioIsDescribedNotCommanded()
    {
        using var rig = new Rig();
        rig.Online();
        await rig.StartAsync();
        await Until(() => rig.Vm.ComputerLevel > 1);

        rig.Computer.Level = 0;
        await Until(() => rig.Vm.ComputerLevel == 0);
        rig.Clock.Advance(SignalWatch.QuietLimit + TimeSpan.FromSeconds(1));
        await Until(() => rig.Vm.AudioSourceStatus == L["capture.system.quiet"]);

        Assert.NotEqual(L["capture.system.silent"], L["capture.system.quiet"]);
        Assert.Contains(rig.Lines, l => l.Message == "signal source=system state=went_quiet");
        rig.Computer.Level = 3000;
        await Until(() => rig.Vm.AudioSourceStatus == "");
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task ConsentCoversTheProfileAndDevicesChosenWhenRecordWasPressed()
    {
        using var rig = new Rig();
        rig.Choose("mic-a");
        bool? emphasised = null;
        bool? choosable = null;
        rig.Vm.ConfirmConsent = (save, remote) =>
        {
            emphasised = remote;
            choosable = rig.Vm.CanChooseAudio;
            rig.Online();
            rig.Choose("mic-b");
            return Task.FromResult<bool?>(save);
        };

        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        Assert.False(emphasised);
        Assert.False(choosable);
        Assert.Equal(["mic-a"], rig.Microphone.Opened);
        Assert.Empty(rig.Computer.Opened);
        Assert.Contains(rig.Lines, l => l.Category == "room" && l.Message.Contains("capture in-room"));
        Assert.Contains(
            $"capture-profile: {rig.Profile(CaptureProfileId.InRoom).Profile.MarkdownValue}",
            rig.Vm.BuildMarkdownExport());
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task OnlineConsentEmphasisesRemoteVoicesAndCapturesWhatWasConsentedTo()
    {
        using var rig = new Rig();
        rig.Online();
        rig.Choose("mic-b");
        bool? emphasised = null;
        rig.Vm.ConfirmConsent = (save, remote) =>
        {
            emphasised = remote;
            rig.Vm.SelectedCaptureProfile = rig.Profile(CaptureProfileId.InRoom);
            rig.Choose("mic-a");
            return Task.FromResult<bool?>(save);
        };

        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        Assert.True(emphasised);
        Assert.Equal(["mic-b"], rig.Microphone.Opened);
        Assert.Single(rig.Computer.Opened);
        Assert.Contains(
            $"capture-profile: {rig.Profile(CaptureProfileId.OnlineMeeting).Profile.MarkdownValue}",
            rig.Vm.BuildMarkdownExport());
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task AudioChoicesAreLockedUntilTheRoomIsLive()
    {
        using var rig = new Rig();
        rig.Choose("mic-a");
        rig.Asr.Hold();

        var start = rig.Vm.StartCommand.ExecuteAsync(null);
        await Until(() => rig.Asr.Starts == 1);

        Assert.False(rig.Vm.CanChooseAudio);
        Assert.False(rig.Vm.CanChooseDevice);
        Assert.False(rig.Vm.StartCommand.CanExecute(null));
        rig.Online();
        rig.Choose("mic-b");
        Assert.Empty(rig.Microphone.Opened);

        rig.Asr.Release();
        await start;
        await Until(() => rig.Asr.Pushes > 0);

        Assert.True(rig.Vm.IsRunning);
        Assert.Equal(["mic-a"], rig.Microphone.Opened);
        Assert.Empty(rig.Computer.Opened);
        await rig.Vm.StopCommand.ExecuteAsync(null);
        Assert.True(rig.Vm.CanChooseAudio);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedTranscriptionPushIsReportedAsTranscriptionNotAudioAndAudioKeepsFlowing(bool online)
    {
        using var rig = new Rig();
        if (online)
            rig.Online();
        rig.Choose("mic-b");
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);
        var live = rig.Vm.Status;

        rig.Asr.FailNextPushes(3);
        var expected = L.Format("status.transcriptionpushfailed", "The remote party closed the WebSocket connection.");
        await Until(() => rig.Vm.Status == expected);
        var before = rig.Asr.Pushes;
        await Until(() => rig.Asr.Pushes > before + 5);
        await Until(() => rig.Vm.Status == live);

        Assert.True(rig.Vm.IsRunning);
        Assert.DoesNotContain(rig.Lines, l => l.Message.StartsWith("capture_fault"));
        var failed = Assert.Single(rig.Lines, l => l.Message.StartsWith("transcription_push_failed"));
        Assert.Equal(("asr", LogLevel.Error), (failed.Category, failed.Level));
        Assert.Contains($"mode={(online ? "online" : "in-room")}", failed.Message);
        Assert.NotNull(failed.Error);
        Assert.Contains(rig.Lines, l => l.Category == "asr" && l.Message == "transcription_push_recovered dropped_frames=3");
        Assert.DoesNotContain(rig.Lines, l => l.Message.Contains("mic-b"));
        await rig.Vm.StopCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task ClearingTheBoundSelectionMidMeetingNeitherMovesCaptureNorHidesTheLossOfTheActiveDevice()
    {
        using var rig = new Rig();
        rig.Online();
        rig.Choose("mic-b");
        await rig.StartAsync();
        await Until(() => rig.Asr.Pushes > 0);

        rig.Vm.SelectedDevice = null;
        rig.Microphone.DeviceIds.Remove("mic-a");
        await Task.Run(rig.Watcher.Raise, TestContext.Current.CancellationToken);
        await Pump(150);
        Assert.True(rig.Vm.IsRunning);
        Assert.Equal(["mic-b"], rig.Microphone.Opened);
        Assert.Single(rig.Computer.Opened);

        rig.Microphone.DeviceIds.Remove("mic-b");
        await Task.Run(rig.Watcher.Raise, TestContext.Current.CancellationToken);
        await Until(() => !rig.Vm.IsRunning && !rig.Vm.IsStopping);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(L.Format("status.audiofailed", L["capture.device.lost"]), rig.Vm.Status);
        Assert.Contains(rig.Lines, l => l.Message.Contains("capture_fault code=device_unavailable source=microphone"));
    }

    [Fact]
    public void EveryNewCaptureStringExistsInEveryLanguage()
    {
        string[] keys =
        [
            "capture.microphone.silent", "capture.system.silent",
            "capture.microphone.quiet", "capture.system.quiet", "status.transcriptionpushfailed",
            "capture.source.microphone", "capture.source.system", "capture.source.mixer",
            "capture.fault.clock_stalled", "capture.fault.consumer_stalled", "capture.fault.source_ended",
            "capture.fault.permission_denied", "capture.fault.device_unavailable", "capture.fault.source_failed",
            "capture.device.lost",
        ];
        foreach (var language in Localizer.Available)
        foreach (var key in keys)
            Assert.False(
                string.IsNullOrWhiteSpace(Strings.Tables[language.Code].GetValueOrDefault(key)),
                $"{language.Code}/{key} is missing.");
    }

    [Fact]
    public void EveryFaultCodeHasAString()
    {
        foreach (var fault in Enum.GetValues<AudioCaptureFault>())
            Assert.True(
                Strings.Tables["en"].ContainsKey($"capture.fault.{AudioCaptureException.CodeOf(fault)}"),
                AudioCaptureException.CodeOf(fault));
    }

    private static async Task Pump(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static bool Tick(Rig rig)
    {
        rig.Clock.Advance(OnlineMeetingCapture.LevelInterval);
        return true;
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        Dispatcher.UIThread.RunJobs();
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static byte[] Frame(short level)
    {
        var frame = new byte[OnlineMeetingCapture.FrameSamples * sizeof(short)];
        for (var i = 0; i < OnlineMeetingCapture.FrameSamples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(i * sizeof(short)), level);
        return frame;
    }

    private sealed class Rig : IDisposable
    {
        private readonly ILogSink? _previousSink = Log.Sink;
        private readonly RecordingSink _sink = new();

        public Rig(string[]? microphones = null)
        {
            Microphone = new FakeSource(microphones ?? ["mic-a", "mic-b"]) { Level = 1000 };
            Computer = new FakeSource([]) { Level = 3000 };
            Log.Install(_sink);
            var settings = new AppSettings { RecordAudio = false };
            settings.ApiKeys.Add(new ApiKeyEntry("meeting-room", "gladia", ApiKey));
            settings.ActiveGladiaKeyName = "meeting-room";
            var dir = TestViewModels.EmptyModelsDir();
            Vm = new MainViewModel(
                () => settings,
                () => new ModelDownloadManager(dir),
                SettingsStore.ResolveStoredGladiaKey,
                captureFactory: () => Microphone,
                deviceWatcherFactory: () => Watcher,
                workspaces: () => new WorkspaceStore(Path.Combine(dir, "workspaces.json")),
                systemCaptureFactory: () => Computer,
                signalClock: Clock)
            {
                RelayEnabled = true,
                RelayPublisherFactory = _ => Relay,
            };
            Vm.SelectedMode = Vm.Modes.Single(o => o.Mode.Id == PipelineModeId.CloudCloud);
            Vm.PlanFilter = plan => plan with { Asr = Asr, Mt = null, CloudTranslation = true };
            Vm.ConfirmConsent = (save, _) => Task.FromResult<bool?>(save);
        }

        public CaptureProfileOption Profile(CaptureProfileId id) => Vm.CaptureProfiles.Single(p => p.Id == id);

        public FakeSource Microphone { get; }
        public FakeSource Computer { get; }
        public RecordingAsr Asr { get; } = new();
        public FakeWatcher Watcher { get; } = new();
        public ManualClock Clock { get; } = new();
        public ResumeGate Relay { get; } = new();
        public MainViewModel Vm { get; }
        public IReadOnlyList<LogLine> Lines => _sink.Lines;

        public void Online() =>
            Vm.SelectedCaptureProfile = Vm.CaptureProfiles.Single(p => p.Id == CaptureProfileId.OnlineMeeting);

        public void Choose(string microphone) => Vm.SelectedDevice = Vm.Devices.Single(d => d.Id == microphone);

        public async Task StartAsync()
        {
            await Vm.StartCommand.ExecuteAsync(null);
            Assert.True(Vm.IsRunning, Vm.Status);
        }

        public void Dispose()
        {
            Relay.Release();
            Asr.Release();
            if (Vm.IsRunning && !Vm.IsStopping)
                _ = Vm.StopCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Vm.Dispose();
            Log.Install(_previousSink);
        }
    }

    private sealed record LogLine(LogLevel Level, string Category, string Message, Exception? Error);

    private sealed class RecordingSink : ILogSink
    {
        private readonly List<LogLine> _lines = [];

        public IReadOnlyList<LogLine> Lines
        {
            get
            {
                lock (_lines)
                    return _lines.ToArray();
            }
        }

        public void Write(LogLevel level, string category, string message, Exception? error)
        {
            lock (_lines)
                _lines.Add(new LogLine(level, category, message, error));
        }
    }

    private sealed class FakeSource(string[] devices) : IAudioCaptureService, ISystemAudioCaptureService
    {
        private readonly object _gate = new();
        private readonly ConcurrentQueue<string?> _opened = new();
        private Exception? _fault;
        private bool _ended;
        private int _closes;

        public List<string> DeviceIds { get; } = [.. devices];
        public volatile short Level;
        public volatile bool Hang;
        public int Burst = 1;
        public Task? CloseGate;

        public IReadOnlyList<string?> Opened => _opened.ToArray();
        public int Closes => Volatile.Read(ref _closes);

        public SystemAudioBackend Backend => SystemAudioBackend.CoreAudioProcessTap;

        public IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(CancellationToken ct) => CaptureAsync(null, ct);

        public IReadOnlyList<AudioDeviceInfo> GetDevices()
        {
            lock (_gate)
                return DeviceIds.Select(id => new AudioDeviceInfo(id, $"Device {id}")).ToList();
        }

        public void Fail(Exception error)
        {
            lock (_gate)
                _fault = error;
        }

        public void End()
        {
            lock (_gate)
                _ended = true;
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(
            string? deviceId, [EnumeratorCancellation] CancellationToken ct)
        {
            _opened.Enqueue(deviceId);
            try
            {
                if (Hang)
                    await Task.Delay(Timeout.Infinite, ct);
                while (true)
                {
                    await Task.Delay(20, ct);
                    Exception? fault;
                    bool ended;
                    lock (_gate)
                    {
                        (fault, _fault) = (_fault, null);
                        (ended, _ended) = (_ended, false);
                    }
                    if (fault is not null)
                        throw fault;
                    if (ended)
                        yield break;
                    for (var i = 0; i < Burst; i++)
                        yield return Frame(Level);
                }
            }
            finally
            {
                if (CloseGate is { } gate)
                    await gate;
                Interlocked.Increment(ref _closes);
            }
        }
    }

    private sealed class FakeWatcher : IAudioDeviceWatcher
    {
        public event Action? DevicesChanged;

        public void Raise() => DevicesChanged?.Invoke();

        public void Dispose()
        {
        }
    }

    private sealed class RecordingAsr : IAsrProvider
    {
        private readonly ConcurrentQueue<byte[]> _pushed = new();
        private int _sessions;
        private int _starts;
        private int _failPushes;
        private volatile TaskCompletionSource? _held;

        public int Starts => Volatile.Read(ref _starts);

        public void Hold() => _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _held?.TrySetResult();

        public void FailNextPushes(int count) => Volatile.Write(ref _failPushes, count);

        private bool ShouldFail()
        {
            while (true)
            {
                var left = Volatile.Read(ref _failPushes);
                if (left == 0)
                    return false;
                if (Interlocked.CompareExchange(ref _failPushes, left - 1, left) == left)
                    return true;
            }
        }

        public string Id => "recording";

        public AsrCapabilities Caps { get; } = new(
            Streaming: true,
            Diarization: true,
            Translation: true,
            AutoLanguageDetect: true,
            Languages: new HashSet<string> { "zh", "de", "pl" },
            Latency: LatencyClass.Realtime);

        public int Pushes => _pushed.Count;
        public int Sessions => Volatile.Read(ref _sessions);

        public bool Heard(int sample) => _pushed.ToArray().Any(frame =>
            Enumerable.Range(0, frame.Length / sizeof(short))
                .Any(i => BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(i * sizeof(short))) == sample));

        public async Task<IAsrSession> StartAsync(AsrSessionOptions options, CancellationToken ct)
        {
            Interlocked.Increment(ref _starts);
            if (_held is { } held)
                await held.Task;
            Interlocked.Increment(ref _sessions);
            return new Session(this);
        }

        private sealed class Session(RecordingAsr owner) : IAsrSession
        {
            private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public IAsyncEnumerable<AsrEvent> Events => ReadAsync();

            public ValueTask PushAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct = default)
            {
                if (owner.ShouldFail())
                    throw new System.Net.WebSockets.WebSocketException("The remote party closed the WebSocket connection.");
                owner._pushed.Enqueue(pcm16.ToArray());
                return ValueTask.CompletedTask;
            }

            private async IAsyncEnumerable<AsrEvent> ReadAsync([EnumeratorCancellation] CancellationToken ct = default)
            {
                await Task.WhenAny(_closed.Task, Task.Delay(Timeout.Infinite, ct));
                yield break;
            }

            public ValueTask DisposeAsync()
            {
                _closed.TrySetResult();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class ResumeGate : IRelayPublisher
    {
        private volatile TaskCompletionSource? _held;
        private int _waiting;

        public int ResumesWaiting => Volatile.Read(ref _waiting);

        public void Hold() => _held = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _held?.TrySetResult();

        public async Task PublishAsync(RelayMessage message, CancellationToken ct = default)
        {
            if (_held is { } held && Unwrap(message) is RoomPausedMessage { Paused: false })
            {
                Interlocked.Increment(ref _waiting);
                await held.Task;
            }
        }

        private static RelayMessage Unwrap(RelayMessage message)
        {
            if (message is not SignedRelayMessage signed)
                return message;

            var encoded = signed.Data.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            return RelayJson.Deserialize(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)))
                ?? throw new InvalidOperationException("Signed test message had no payload.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_gate)
                return _ticks;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _ticks += by.Ticks;
                due = _timers.Where(t => t.DueAt <= _ticks).ToList();
                foreach (var timer in due)
                    timer.DueAt = null;
            }
            foreach (var timer in due)
                timer.Fire();
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public long? DueAt;

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate)
                {
                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._ticks + dueTime.Ticks;
                    if (!clock._timers.Contains(this))
                        clock._timers.Add(this);
                }
                return true;
            }

            public void Dispose()
            {
                lock (clock._gate)
                    clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
