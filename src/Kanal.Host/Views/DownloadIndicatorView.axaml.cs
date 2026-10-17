using Avalonia;
using Avalonia.Controls;

namespace Kanal.Host.Views;

public partial class DownloadIndicatorView : UserControl
{
    public static readonly StyledProperty<bool> BareProperty =
        AvaloniaProperty.Register<DownloadIndicatorView, bool>(nameof(Bare));

    public DownloadIndicatorView() => InitializeComponent();

    public bool Bare
    {
        get => GetValue(BareProperty);
        set => SetValue(BareProperty, value);
    }
}
