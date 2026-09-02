using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Kanal.Audio;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Providers.LocalMt;

namespace Kanal.UI.UnitTests;

/// <summary>
/// The operator plugs the USB microphone in after the app is already open — because the mic
/// lives in the meeting-room drawer, not next to the laptop. The device dropdowns must notice
/// without a restart. The native listeners themselves (CoreAudio property listener, WASAPI
/// notification client) are thin registration wrappers exercised by hand: plug and unplug a
/// USB microphone while the main window and Settings are open.
/// </summary>
public class DeviceHotplugTests
{
    /// <summary>Enumeration-only capture: a device list the test can rewrite mid-run.</summary>
    private sealed class FakeDeviceSource : IAudioCaptureService
    {
        public List<AudioDeviceInfo> Current = [new("mic-1", "Table microphone")];

        /// <summary>Fresh instances every call, the way a real enumeration builds them —
        /// survival of a selection must come from the stable id, not the reference.</summary>
        public IReadOnlyList<AudioDeviceInfo> GetDevices() =>
            Current.Select(d => new AudioDeviceInfo(d.Id, d.Name)).ToList();

        public IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(string? deviceId, CancellationToken ct) =>
            throw new NotSupportedException("these tests never open a device");
    }

    /// <summary>A watcher the test fires by hand, in place of the platform listener.</summary>
    private sealed class FakeDeviceWatcher : IAudioDeviceWatcher
    {
        public bool Disposed;

        public event Action? DevicesChanged;

        public bool HasSubscribers => DevicesChanged is not null;

        public void Raise() => DevicesChanged?.Invoke();

        public void Dispose() => Disposed = true;
    }

    private static MainViewModel MainVm(FakeDeviceSource source, FakeDeviceWatcher watcher)
    {
        var settings = new AppSettings();
        var dir = Path.Combine(Path.GetTempPath(), "kanal-no-models-" + Guid.NewGuid().ToString("N"));
        return new MainViewModel(
            () => settings,
            () => new ModelDownloadManager(dir),
            SettingsStore.ResolveStoredGladiaKey,
            () => source,
            () => watcher)
        {
            RelayEnabled = false,
        };
    }

    private static SettingsViewModel SettingsVm(FakeDeviceSource source, FakeDeviceWatcher watcher) =>
        new(new AppSettings(), () => source, isMacOs: false, deviceWatcherFactory: () => watcher);

    [AvaloniaFact]
    public void APluggedInMicrophoneAppearsWithoutReopeningTheWindow()
    {
        var source = new FakeDeviceSource();
        var watcher = new FakeDeviceWatcher();
        var vm = MainVm(source, watcher);
        Assert.Single(vm.Devices);

        source.Current.Add(new AudioDeviceInfo("usb-1", "USB conference mic"));
        watcher.Raise();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["mic-1", "usb-1"], vm.Devices.Select(d => d.Id));
    }

    [AvaloniaFact]
    public void TheSelectedDeviceSurvivesARefreshByItsStableId()
    {
        var source = new FakeDeviceSource
        {
            Current =
            [
                new("mic-1", "Table microphone"),
                new("usb-1", "USB conference mic"),
            ],
        };
        var watcher = new FakeDeviceWatcher();
        var vm = MainVm(source, watcher);
        vm.SelectedDevice = vm.Devices[1];

        // The OS renames the device between enumerations; the id is what persists.
        source.Current = [new("mic-1", "Table microphone"), new("usb-1", "USB mic (2)")];
        watcher.Raise();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.SelectedDevice);
        Assert.Equal("usb-1", vm.SelectedDevice!.Id);
        Assert.Contains(vm.SelectedDevice, vm.Devices);
    }

    [AvaloniaFact]
    public void UnpluggingTheSelectedDeviceFallsBackToTheDefault()
    {
        var source = new FakeDeviceSource
        {
            Current =
            [
                new("mic-1", "Table microphone"),
                new("usb-1", "USB conference mic"),
            ],
        };
        var watcher = new FakeDeviceWatcher();
        var vm = MainVm(source, watcher);
        vm.SelectedDevice = vm.Devices[1];

        source.Current = [new("mic-1", "Table microphone")];
        watcher.Raise();
        Dispatcher.UIThread.RunJobs();

        // The backend treats the list head as the default; the selection must not dangle.
        Assert.Equal("mic-1", vm.SelectedDevice!.Id);
        Assert.Single(vm.Devices);
    }

    [AvaloniaFact]
    public void EveryDeviceGoneLeavesAnEmptyListAndNoSelection()
    {
        var source = new FakeDeviceSource();
        var watcher = new FakeDeviceWatcher();
        var vm = MainVm(source, watcher);

        source.Current = [];
        watcher.Raise();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(vm.Devices);
        Assert.Null(vm.SelectedDevice);
    }

    [AvaloniaFact]
    public void AFailingEnumerationKeepsTheLastKnownList()
    {
        var source = new FakeDeviceSource();
        var watcher = new FakeDeviceWatcher();
        var vm = MainVm(source, watcher);
        vm.SelectedDevice = vm.Devices[0];

        // Mid-unplug the platform enumeration can fail transiently; a stale list beats none.
        source.Current = null!; // GetDevices will throw
        watcher.Raise();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(vm.Devices);
        Assert.Equal("mic-1", vm.SelectedDevice!.Id);
    }

    [AvaloniaFact]
    public void DisposingTheMainViewModelReleasesTheWatcher()
    {
        var source = new FakeDeviceSource();
        var watcher = new FakeDeviceWatcher();
        var vm = MainVm(source, watcher);
        Assert.True(watcher.HasSubscribers);

        vm.Dispose();

        Assert.True(watcher.Disposed);
        Assert.False(watcher.HasSubscribers);
    }

    [AvaloniaFact]
    public void TheSettingsTestDeviceListRefreshesToo()
    {
        var source = new FakeDeviceSource();
        var watcher = new FakeDeviceWatcher();
        var vm = SettingsVm(source, watcher);
        Assert.Single(vm.Devices);

        source.Current.Add(new AudioDeviceInfo("usb-1", "USB conference mic"));
        watcher.Raise();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["mic-1", "usb-1"], vm.Devices.Select(d => d.Id));
        Assert.Equal("mic-1", vm.TestDevice!.Id); // untouched selection stays where it was
    }

    [AvaloniaFact]
    public void UnpluggingTheSettingsTestDeviceFallsBackToTheDefault()
    {
        var source = new FakeDeviceSource
        {
            Current =
            [
                new("mic-1", "Table microphone"),
                new("usb-1", "USB conference mic"),
            ],
        };
        var watcher = new FakeDeviceWatcher();
        var vm = SettingsVm(source, watcher);
        vm.TestDevice = vm.Devices[1];

        source.Current = [new("mic-1", "Table microphone")];
        watcher.Raise();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("mic-1", vm.TestDevice!.Id);
    }

    /// <summary>Same close-time cleanup as the downloads and the mic test: the window owns it.</summary>
    [AvaloniaFact]
    public void ClosingSettingsReleasesTheWatcher()
    {
        var source = new FakeDeviceSource();
        var watcher = new FakeDeviceWatcher();
        var vm = SettingsVm(source, watcher);
        Assert.True(watcher.HasSubscribers);

        vm.CancelDownloads(); // the settings-state close path

        Assert.True(watcher.Disposed);
        Assert.False(watcher.HasSubscribers);
    }
}
