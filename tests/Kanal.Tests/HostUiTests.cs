using System.Buffers.Binary;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Host;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;
using Kanal.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

// A desktop application has exactly one UI language at a time, so Localizer is a singleton — and
// a test that switches it changes what every other test's window says. Running classes in
// parallel made a handful of unrelated assertions fail at random depending on which language a
// localisation test happened to be holding at that instant.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

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
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo); // never touch the network
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
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);
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
        var vm = TestViewModels.Hermetic();
        vm.SelectedMode = vm.Modes.First(o => o.Mode.Id == PipelineModeId.Demo);
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

    /// <summary>The operator picks a microphone from this list, so it must fill on every
    /// platform that has a capture backend — not Windows only.</summary>
    [AvaloniaFact]
    public void MicrophoneListFillsOnAnySupportedPlatform()
    {
        var vm = TestViewModels.Hermetic();

        if (!Kanal.Audio.AudioCaptureFactory.IsSupported)
            return;

        Assert.NotEmpty(vm.Devices);
        Assert.Equal(vm.Devices[0], vm.SelectedDevice);
    }

    // Only catches a missing or renamed resource: the headless platform has no
    // image codec, so Icon is a HeadlessBitmapStub and a truncated or garbage
    // kanal.ico of the right name would still pass. The bytes are checked
    // separately by WindowIconIsAWellFormedIcoContainer.
    [AvaloniaFact]
    public void WindowIconLoadsFromAssets()
    {
        var window = new MainWindow();
        Assert.NotNull(window.Icon);
        window.Close();
    }

    /// <summary>kanal.ico is generated by design/kanal-icon.py, which writes the ICO
    /// container by hand rather than through an imaging library. A malformed one would
    /// only ever surface as a missing taskbar and Alt-Tab icon at runtime, never as a
    /// build error — and headless Avalonia decodes nothing, so the structure is parsed
    /// here directly.</summary>
    [AvaloniaFact]
    public void WindowIconIsAWellFormedIcoContainer()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Kanal.Host/Assets/kanal.ico"));
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var ico = buffer.ToArray();

        // ICONDIR: reserved, type (1 = icon), image count.
        Assert.True(ico.Length >= 6, $"ICO is {ico.Length} B — too short for a header.");
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(0)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)));

        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        Assert.True(count > 0, "ICO declares no images.");
        Assert.True(
            ico.Length >= 6 + 16 * count,
            $"ICO declares {count} entries but is only {ico.Length} B — the directory is truncated.");

        // Every ICONDIRENTRY must point at a payload that is actually present:
        // this is what a hand-written offset table gets wrong, and what Windows
        // reads before it will draw anything.
        for (int i = 0; i < count; i++)
        {
            var entry = ico.AsSpan(6 + 16 * i, 16);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(12));

            Assert.True(size > 0, $"entry {i} declares an empty payload.");
            Assert.True(
                offset >= (uint)(6 + 16 * count),
                $"entry {i} points at offset {offset}, inside the directory itself.");
            Assert.True(
                offset + (long)size <= ico.Length,
                $"entry {i} spans {offset}..{offset + size} but the file is only {ico.Length} B.");
        }
    }
}
