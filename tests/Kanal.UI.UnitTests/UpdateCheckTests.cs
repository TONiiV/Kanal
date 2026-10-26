using System.Net;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Core.Relay;
using Kanal.Core.Updates;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;

namespace Kanal.UI.UnitTests;

public class UpdateCheckTests
{
    private const string Download = "https://github.com/TONiiV/Kanal/releases/latest";

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class ScriptedServer : HttpMessageHandler
    {
        private int _requests;

        public Func<CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return await Respond(cancellationToken);
        }

        public void Answer(string latest, string minimum, string? downloadUrl = Download)
        {
            var link = downloadUrl is null ? "null" : $"\"{downloadUrl}\"";
            var body = $$"""
                {"latest":"{{latest}}","minimumSupported":"{{minimum}}","notes":"Fixes.","downloadUrl":{{link}} }
                """;
            Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }

    private static UpdateViewModel Updates(
        ScriptedServer server, string version = "1.0.0", TimeProvider? clock = null) =>
        new(
            new UpdateChecker(
                new HttpClient(server), new Uri("https://update.example.test/?action=update")),
            version,
            clock);

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void NothingIsShownBeforeTheFirstCheck()
    {
        var vm = TestViewModels.Demo();

        Assert.False(vm.Updates.ShowNotice);
        Assert.Null(vm.StartBlockedReason);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TheSidebarNamesTheNewVersionWhenOneExists()
    {
        var server = new ScriptedServer();
        server.Answer("1.0.1", "1.0.0");
        var vm = TestViewModels.Demo(updates: Updates(server));

        await vm.Updates.CheckIfDueAsync();

        Assert.Equal(UpdateStatus.UpdateAvailable, vm.Updates.Status);
        Assert.True(vm.Updates.ShowNotice);
        Assert.Equal("New version 1.0.1", vm.Updates.NoticeText);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Null(vm.StartBlockedReason);
    }

    [AvaloniaFact]
    public async Task TheSidebarSaysNothingWhenTheVersionIsCurrent()
    {
        var server = new ScriptedServer();
        server.Answer("1.0.0", "1.0.0");
        var vm = TestViewModels.Demo(updates: Updates(server));

        await vm.Updates.CheckIfDueAsync();

        Assert.Equal(UpdateStatus.UpToDate, vm.Updates.Status);
        Assert.False(vm.Updates.ShowNotice);
    }

    [AvaloniaFact]
    public async Task AFailedCheckShowsNothingAndBlocksNothing()
    {
        var server = new ScriptedServer();
        var vm = TestViewModels.Demo(updates: Updates(server));

        await vm.Updates.CheckIfDueAsync();

        Assert.Equal(1, server.Requests);
        Assert.Equal(UpdateStatus.Unknown, vm.Updates.Status);
        Assert.False(vm.Updates.ShowNotice);
        Assert.False(vm.Updates.IsUnsupported);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Null(vm.StartBlockedReason);
    }

    [AvaloniaFact]
    public async Task StartIsDisabledWithTheReasonWhenTheVersionIsTooOld()
    {
        var server = new ScriptedServer();
        server.Answer("1.1.0", "1.1.0");
        var vm = TestViewModels.Demo(updates: Updates(server));
        Assert.True(vm.StartCommand.CanExecute(null));

        await vm.Updates.CheckIfDueAsync();

        Assert.Equal(UpdateStatus.Unsupported, vm.Updates.Status);
        Assert.True(vm.Updates.IsUnsupported);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal("This version is too old. Install version 1.1.0.", vm.StartBlockedReason);
        Assert.Equal(vm.StartBlockedReason, vm.Updates.UnsupportedReason);
        Assert.True(vm.Updates.ShowNotice);
    }

    [AvaloniaFact]
    public async Task StartComesBackWhenALaterCheckFindsTheVersionSupported()
    {
        var server = new ScriptedServer();
        server.Answer("1.1.0", "1.1.0");
        var vm = TestViewModels.Demo(updates: Updates(server));
        await vm.Updates.CheckNowCommand.ExecuteAsync(null);
        Assert.False(vm.StartCommand.CanExecute(null));

        server.Answer("1.1.0", "1.0.0");
        await vm.Updates.CheckNowCommand.ExecuteAsync(null);

        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Null(vm.StartBlockedReason);
    }

    [AvaloniaFact]
    public async Task NoCheckRunsWhileAMeetingRunsAndTheOverdueOneRunsAfterStop()
    {
        var clock = new ManualClock();
        var server = new ScriptedServer();
        server.Answer("1.0.0", "1.0.0");
        var vm = TestViewModels.Demo(updates: Updates(server, clock: clock));
        await vm.Updates.CheckIfDueAsync();
        Assert.Equal(1, server.Requests);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);
        clock.Advance(TimeSpan.FromHours(25));
        await vm.Updates.CheckIfDueAsync();

        Assert.Equal(1, server.Requests);

        await vm.StopCommand.ExecuteAsync(null);
        await WaitForAsync(() => server.Requests == 2);
    }

    [AvaloniaFact]
    public async Task AFailedLaterCheckKeepsTheNewVersionNotice()
    {
        var clock = new ManualClock();
        var server = new ScriptedServer();
        server.Answer("1.0.1", "1.0.0");
        var vm = TestViewModels.Demo(updates: Updates(server, clock: clock));
        await vm.Updates.CheckIfDueAsync();

        server.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        clock.Advance(TimeSpan.FromHours(25));
        await vm.Updates.CheckIfDueAsync();

        Assert.Equal(2, server.Requests);
        Assert.True(vm.Updates.ShowNotice);
        Assert.Equal("New version 1.0.1", vm.Updates.NoticeText);
        Assert.Equal("Version 1.0.1 is available.", vm.Updates.StatusText);

        await vm.Updates.CheckNowCommand.ExecuteAsync(null);

        Assert.True(vm.Updates.ShowNotice);
        Assert.Equal("Kanal could not check for updates.", vm.Updates.StatusText);
    }

    [AvaloniaFact]
    public async Task AFailedCheckReopensStartAndKeepsTheNoticeWhenTheVersionWasTooOld()
    {
        var server = new ScriptedServer();
        server.Answer("1.1.0", "1.1.0");
        var vm = TestViewModels.Demo(updates: Updates(server));
        await vm.Updates.CheckNowCommand.ExecuteAsync(null);
        Assert.False(vm.StartCommand.CanExecute(null));

        server.Respond = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        await vm.Updates.CheckNowCommand.ExecuteAsync(null);

        Assert.False(vm.Updates.IsUnsupported);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Null(vm.StartBlockedReason);
        Assert.True(vm.Updates.ShowNotice);
        Assert.Equal("New version 1.1.0", vm.Updates.NoticeText);
    }

    [AvaloniaFact]
    public async Task TheCheckButtonIsDisabledWhileAMeetingRuns()
    {
        var server = new ScriptedServer();
        server.Answer("1.0.0", "1.0.0");
        var vm = TestViewModels.Demo(updates: Updates(server));
        Assert.True(vm.Updates.CheckNowCommand.CanExecute(null));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsRunning);
        Assert.False(vm.Updates.CheckNowCommand.CanExecute(null));

        await vm.StopCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Updates.CheckNowCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task StartSendsOneLaunchCheckAndASecondStartSendsNothing()
    {
        var clock = new ManualClock();
        var server = new ScriptedServer();
        server.Answer("1.0.0", "1.0.0");
        var updates = Updates(server, clock: clock);

        updates.Start();
        await WaitForAsync(() => server.Requests == 1 && !updates.IsChecking);

        clock.Advance(TimeSpan.FromHours(25));
        updates.Start();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, server.Requests);
        Assert.False(updates.IsChecking);
    }

    [AvaloniaFact]
    public async Task ACheckThatIsNotDueSendsNothing()
    {
        var clock = new ManualClock();
        var server = new ScriptedServer();
        server.Answer("1.0.0", "1.0.0");
        var vm = TestViewModels.Demo(updates: Updates(server, clock: clock));

        await vm.Updates.CheckIfDueAsync();
        await vm.Updates.CheckIfDueAsync();
        Assert.Equal(1, server.Requests);

        clock.Advance(TimeSpan.FromHours(24));
        await vm.Updates.CheckIfDueAsync();
        Assert.Equal(2, server.Requests);
    }

    [AvaloniaFact]
    public async Task ARunningMeetingIsNotStoppedWhenTheAnswerSaysTheVersionIsTooOld()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ScriptedServer();
        server.Respond = async ct =>
        {
            await gate.Task.WaitAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"latest":"1.1.0","minimumSupported":"1.1.0","notes":"n","downloadUrl":null}"""),
            };
        };
        var vm = TestViewModels.Demo(updates: Updates(server));
        var pending = vm.Updates.CheckIfDueAsync();
        await WaitForAsync(() => server.Requests == 1);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);
        gate.SetResult();
        await pending;

        Assert.True(vm.Updates.IsUnsupported);
        Assert.True(vm.IsRunning);
        Assert.False(vm.IsStopping);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal("This version is too old. Install version 1.1.0.", vm.StartBlockedReason);
    }

    [AvaloniaFact]
    public async Task SettingsShowTheStatusTheNotesAndTheDownloadLink()
    {
        var server = new ScriptedServer();
        server.Answer("1.0.1", "1.0.0");
        var vm = TestViewModels.Demo(updates: Updates(server));
        var settings = new SettingsViewModel(new AppSettings(), () => null, updates: vm.Updates);

        await settings.Updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Same(vm.Updates, settings.Updates);
        Assert.Equal("Version 1.0.1 is available.", settings.Updates.StatusText);
        Assert.Equal("Fixes.", settings.Updates.Notes);
        Assert.Equal(new Uri(Download), settings.Updates.DownloadUrl);
        Assert.True(settings.Updates.ShowDownloadLink);
        Assert.False(settings.Updates.ShowContactMaintainer);
        Assert.Equal("New version 1.0.1", vm.Updates.NoticeText);
    }

    [AvaloniaFact]
    public async Task WithoutADownloadLinkSettingsSayToContactTheMaintainer()
    {
        var server = new ScriptedServer();
        server.Answer("1.0.1", "1.0.0", downloadUrl: null);
        var vm = TestViewModels.Demo(updates: Updates(server));

        await vm.Updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Null(vm.Updates.DownloadUrl);
        Assert.False(vm.Updates.ShowDownloadLink);
        Assert.True(vm.Updates.ShowContactMaintainer);
    }

    [AvaloniaFact]
    public async Task SettingsSayTheVersionIsCurrentAfterAManualCheck()
    {
        var server = new ScriptedServer();
        server.Answer("1.0.0", "1.0.0");
        var vm = TestViewModels.Demo(updates: Updates(server));

        await vm.Updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal("This is the latest version.", vm.Updates.StatusText);
        Assert.False(vm.Updates.ShowDownloadLink);
        Assert.False(vm.Updates.ShowContactMaintainer);
    }

    [AvaloniaFact]
    public async Task SettingsSayWhenAManualCheckFailed()
    {
        var vm = TestViewModels.Demo(updates: Updates(new ScriptedServer()));

        await vm.Updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal("Kanal could not check for updates.", vm.Updates.StatusText);
        Assert.False(vm.Updates.ShowNotice);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task AManualCheckIgnoresTheSchedule()
    {
        var server = new ScriptedServer();
        server.Answer("1.0.0", "1.0.0");
        var vm = TestViewModels.Demo(updates: Updates(server, clock: new ManualClock()));

        await vm.Updates.CheckIfDueAsync();
        await vm.Updates.CheckNowCommand.ExecuteAsync(null);

        Assert.Equal(2, server.Requests);
    }

    [AvaloniaFact]
    public async Task AnUpgradeRequiredAnswerFromTheRelayIsExplainedToTheOperator()
    {
        var vm = TestViewModels.Demo();
        vm.RelayEnabled = true;
        vm.RelayPublisherFactory = (_, _) => throw new RelayVersionRejectedException(
            "Relay room creation failed (426): minimum_version");

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.IsRunning);
        Assert.True(vm.HasJoinError);
        Assert.Contains(
            "This version of Kanal is too old to open a room. Install the new version.",
            vm.JoinError);
        await vm.StopCommand.ExecuteAsync(null);
    }
}
