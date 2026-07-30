using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;
using Kanal.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Kanal.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class HostUiTests
{
    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 15_000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("Condition not met in time.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }

        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task RenameSpeakerUpdatesRoomAndHistoryBubbles()
    {
        var vm = new MainViewModel { RelayEnabled = false };
        vm.SelectedMode = "Demo (scripted)"; // never touch the network in tests
        var window = new MainWindow { DataContext = vm };
        window.Show();

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Speakers.Count > 0 &&
                                 vm.Columns.Any(c => c.Bubbles.Count > 0));

        var speaker = vm.Speakers[0];
        speaker.Name = "王工";

        // the ✓ button must resolve its command through the item view model
        var button = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.DataContext is SpeakerItemViewModel s && s.Tag == speaker.Tag);
        Assert.NotNull(button);
        Assert.NotNull(button!.Command);
        button.Command!.Execute(button.CommandParameter);

        await WaitForAsync(() => vm.Columns
            .SelectMany(c => c.Bubbles)
            .Where(b => b.SpeakerTag == speaker.Tag)
            .All(b => b.SpeakerName == "王工"));

        await vm.StopCommand.ExecuteAsync(null);
        window.Close();
    }

    [AvaloniaFact]
    public async Task RenameStillWorksAfterStop()
    {
        var vm = new MainViewModel { RelayEnabled = false };
        vm.SelectedMode = "Demo (scripted)";
        var window = new MainWindow { DataContext = vm };
        window.Show();

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Speakers.Count > 0);
        await vm.StopCommand.ExecuteAsync(null);

        var speaker = vm.Speakers[0];
        speaker.Name = "Marek";
        speaker.RenameCommand.Execute(null);

        await WaitForAsync(() => vm.Speakers[0].Name == "Marek" &&
                                 vm.Columns.SelectMany(c => c.Bubbles)
                                     .Where(b => b.SpeakerTag == speaker.Tag)
                                     .All(b => b.SpeakerName == "Marek"));

        window.Close();
    }

    [AvaloniaFact]
    public async Task StartStopStartYieldsFreshRoom()
    {
        var vm = new MainViewModel { RelayEnabled = false };
        vm.SelectedMode = "Demo (scripted)";
        var window = new MainWindow { DataContext = vm };
        window.Show();

        await vm.StartCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Columns.Any(c => c.Bubbles.Count > 0));
        await vm.StopCommand.ExecuteAsync(null);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);
        await vm.StopCommand.ExecuteAsync(null);
        window.Close();
    }
}
