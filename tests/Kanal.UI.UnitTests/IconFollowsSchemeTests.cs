using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Kanal.Host;
using Kanal.Host.Services;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class IconFollowsSchemeTests
{
    private static void Restore()
    {
        Appearance.SystemChanged(PlatformThemeVariant.Light);
        Appearance.Apply(ColourScheme.Light);
    }

    private static object Resource(string key, ThemeVariant variant)
    {
        Assert.True(Application.Current!.TryGetResource(key, variant, out var value), $"{key} is missing for {variant}.");
        return value!;
    }

    private static void AssertBrand(Control root, ThemeVariant variant)
    {
        var mark = Resource("BrandMark", variant);
        var images = root.GetLogicalDescendants().OfType<Image>().ToList();
        Assert.NotEmpty(images);
        Assert.All(images, image => Assert.Same(mark, image.Source));
        if (root is SplashWindow window)
            Assert.Same(Resource("BrandIcon", variant), window.Icon);
    }

    [AvaloniaFact]
    public void TheLightAndDarkMarksAreDifferentFiles()
    {
        Assert.NotSame(Resource("BrandMark", ThemeVariant.Light), Resource("BrandMark", ThemeVariant.Dark));
        Assert.NotSame(Resource("BrandIcon", ThemeVariant.Light), Resource("BrandIcon", ThemeVariant.Dark));
    }

    [AvaloniaTheory]
    [InlineData(ColourScheme.Light)]
    [InlineData(ColourScheme.Dark)]
    public void EveryBrandMarkAndWindowIconFollowsTheChosenScheme(ColourScheme scheme)
    {
        try
        {
            Appearance.Apply(scheme);
            var variant = scheme == ColourScheme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
            var splash = new SplashWindow();
            var main = new MainWindow { DataContext = TestViewModels.Hermetic(), Width = 1320, Height = 820 };
            var settings = new SettingsWindow(new SettingsViewModel(new AppSettings(), () => null, saveColourScheme: _ => { }));
            Window[] windows = [splash, main, settings];
            foreach (var window in windows)
                window.Show();
            Dispatcher.UIThread.RunJobs();

            AssertBrand(splash, variant);
            AssertBrand(settings, variant);
            Assert.Same(Resource("BrandIcon", variant), main.Icon);
            Assert.Single(main.GetLogicalDescendants().OfType<WorkspaceSidebarView>().Single()
                .GetLogicalDescendants().OfType<Image>().Where(i => ReferenceEquals(i.Source, Resource("BrandMark", variant))));
            Assert.Single(main.GetLogicalDescendants().OfType<MeetingRoomView>().Single()
                .GetLogicalDescendants().OfType<Image>().Where(i => ReferenceEquals(i.Source, Resource("BrandMark", variant))));

            foreach (var window in windows)
                window.Close();
        }
        finally
        {
            Restore();
        }
    }

    [AvaloniaFact]
    public void ALiveChangeSwitchesTheOpenWindowsAtOnce()
    {
        try
        {
            Appearance.Apply(ColourScheme.System);
            var splash = new SplashWindow();
            splash.Show();
            AssertBrand(splash, ThemeVariant.Light);

            Appearance.SystemChanged(PlatformThemeVariant.Dark);
            AssertBrand(splash, ThemeVariant.Dark);

            Appearance.Apply(ColourScheme.Light);
            AssertBrand(splash, ThemeVariant.Light);

            splash.Close();
        }
        finally
        {
            Restore();
        }
    }
}
