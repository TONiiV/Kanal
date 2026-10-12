using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class IconBarView : UserControl
{
    private const double StatusFloor = 460;

    public IconBarView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.ConfirmConsent = AskAsync;
        };
        SizeChanged += (_, _) => TransportStatus.IsVisible = Bounds.Width > StatusFloor;
        WatchReach(LeftCluster);
        WatchReach(RightCluster);
    }

    // Not LayoutUpdated: it fires on every layout pass in the window, and live transcripts make many.
    private static void WatchReach(Panel cluster)
    {
        foreach (var watched in cluster.Children.Prepend(cluster))
            watched.PropertyChanged += (_, e) =>
            {
                if (e.Property == BoundsProperty)
                    KeepOnlyReachableInTabOrder(cluster);
            };
    }

    // A control clipped away by the narrowing bar must not take keyboard focus unseen.
    private static void KeepOnlyReachableInTabOrder(Panel cluster)
    {
        var area = new Rect(cluster.Bounds.Size).Inflate(0.5);
        foreach (var control in cluster.GetLogicalDescendants().OfType<InputElement>()
                     .Where(element => element is Button or ComboBox))
        {
            if (control.TranslatePoint(default, cluster) is not { } origin)
                continue;

            control.IsTabStop = area.Contains(new Rect(origin, control.Bounds.Size));
        }
    }

    private async Task<bool?> AskAsync(bool saveAudio, bool emphasiseRemote)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return null;

        return await new ConsentWindow(saveAudio, emphasiseRemote).ShowDialog<bool?>(owner);
    }
}
