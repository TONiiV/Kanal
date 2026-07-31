using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;
using Kanal.Providers.LocalMt;

namespace Kanal.Tests;

public class SettingsWindowUiTests
{
    [AvaloniaFact]
    public void TranslationModelSectionListsCloudAndCatalog()
    {
        var window = new SettingsWindow
        {
            DataContext = new SettingsViewModel(new AppSettings()),
        };
        window.Show();

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(t => t is not null).ToList();
        Assert.Contains("TRANSLATION MODEL", texts);
        Assert.Contains("Gladia cloud", texts);
        foreach (var model in LocalModelCatalog.Models)
            Assert.Contains(model.DisplayName, texts);

        // one radio per choice, in the shared activeModel group
        var radios = window.GetVisualDescendants().OfType<RadioButton>()
            .Where(r => r.GroupName == "activeModel").ToList();
        Assert.Equal(LocalModelCatalog.Models.Count + 1, radios.Count);
        Assert.Equal(1, radios.Count(r => r.IsChecked == true));

        window.Close();
    }

    [AvaloniaFact]
    public void DownloadButtonsShowOnlyForLocalModelsNotYetDownloaded()
    {
        var window = new SettingsWindow
        {
            DataContext = new SettingsViewModel(new AppSettings()),
        };
        window.Show();

        var downloadButtons = window.GetVisualDescendants().OfType<Button>()
            .Where(b => Equals(b.Content, "Download") && b.IsVisible).ToList();
        // the cloud row never offers a download
        Assert.InRange(downloadButtons.Count, 0, LocalModelCatalog.Models.Count);
        Assert.All(downloadButtons, b =>
            Assert.True(((TranslationModelItemViewModel)b.DataContext!).IsLocal));

        window.Close();
    }
}
