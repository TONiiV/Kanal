using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Kanal.Core.Diagnostics;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class SettingsWindowBindingTests
{
    [AvaloniaFact]
    public void TheLogPanelBindsToTheChosenLevelAndSize()
    {
        var window = new SettingsWindow(new SettingsViewModel(
            new AppSettings { LogLevel = LogLevel.Error, LogMaxFileSizeMb = 33 },
            () => null,
            isMacOs: false,
            deviceWatcherFactory: null,
            openFolder: _ => { }));
        window.Show();

        var levels = window.GetLogicalDescendants().OfType<ComboBox>()
            .Single(c => c.SelectedItem is LogLevelOption);
        Assert.Equal(LogLevel.Error, ((LogLevelOption)levels.SelectedItem!).Level);

        var size = Assert.Single(window.GetLogicalDescendants().OfType<NumericUpDown>());
        Assert.Equal(33m, size.Value);
        Assert.Equal(SettingsStore.MaxLogMaxFileSizeMb, size.Maximum);
        Assert.True(size.ClipValueToMinMax);

        window.Close();
    }
}
