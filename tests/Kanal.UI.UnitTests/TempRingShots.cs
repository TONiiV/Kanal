using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class TempRingShots(ITestOutputHelper output)
{
    private static void ForceScale(Window window, double scale, ITestOutputHelper output)
    {
        var impl = window.PlatformImpl!;
        var type = impl.GetType();
        output.WriteLine($"impl={type.FullName} before={window.RenderScaling}");
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var f in type.GetFields(all).Where(f => f.Name.Contains("caling", StringComparison.OrdinalIgnoreCase)))
            output.WriteLine($"field {f.Name} : {f.FieldType}");
        foreach (var p in type.GetProperties(all).Where(p => p.Name.Contains("caling", StringComparison.OrdinalIgnoreCase)))
            output.WriteLine($"prop {p.Name} : {p.PropertyType} canWrite={p.CanWrite}");
        foreach (var m in type.GetMethods(all).Where(m => m.Name.Contains("caling", StringComparison.OrdinalIgnoreCase)))
            output.WriteLine($"method {m.Name}");

        var backing = type.GetField("<RenderScaling>k__BackingField", all);
        backing?.SetValue(impl, scale);
        var prop = type.GetProperty("RenderScaling", all);
        if (backing is null && prop is { CanWrite: true })
            prop.SetValue(impl, scale);
        var raise = type.GetField("ScalingChanged", all)?.GetValue(impl) as Action<double>
            ?? impl.GetType().GetProperty("ScalingChanged", all)?.GetValue(impl) as Action<double>;
        raise?.Invoke(scale);
        output.WriteLine($"after={window.RenderScaling} raised={(raise is not null)}");
    }

    private void Dump(string name, Window window)
    {
        var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            output.WriteLine($"SHOT {name} NOFRAME");
            return;
        }

        using var stream = new MemoryStream();
        frame.Save(stream);
        output.WriteLine($"SHOT {name} {frame.PixelSize.Width}x{frame.PixelSize.Height} {Convert.ToBase64String(stream.ToArray())}");
    }

    [AvaloniaFact]
    public void Shots()
    {
        if (Environment.GetEnvironmentVariable("KANAL_SHOTS") != "1")
            return;

        var dir = Path.Combine(Path.GetTempPath(), "kanal-tests", Guid.NewGuid().ToString("N"));
        var rows = new ModelRows(new ModelDownloadManager(dir, new HttpClient(new SettingsViewModelDownloadTests.StalledHandler())));
        var items = new[]
        {
            ("transcript", rows.Transcription[0].Download),
            ("translate", rows.Translation.First(r => r.IsLocal).Download),
        };
        foreach (var (_, item) in items)
            item.Row.Progress = 0.35;

        var scale = double.Parse(Environment.GetEnvironmentVariable("KANAL_SCALE") ?? "1.5",
            System.Globalization.CultureInfo.InvariantCulture);

        foreach (var (name, item) in items)
        {
            var view = new DownloadIndicatorView { DataContext = item, Bare = true, Margin = new Thickness(8) };
            var window = new Window
            {
                Width = 48, Height = 48, SizeToContent = SizeToContent.Manual,
                RequestedThemeVariant = ThemeVariant.Dark, Content = view,
            };
            if (Application.Current!.TryGetResource("Paper", ThemeVariant.Dark, out var paper) && paper is Color c)
                window.Background = new SolidColorBrush(c);
            window.Show();
            ForceScale(window, scale, output);
            window.UpdateLayout();

            var grid = view.GetVisualDescendants().OfType<Grid>().First(g => g.Width == 26);
            grid.UseLayoutRounding = true;
            window.UpdateLayout();
            Dump($"{name}-before", window);

            grid.UseLayoutRounding = false;
            window.UpdateLayout();
            Dump($"{name}-after", window);
            window.Close();
        }
    }
}
