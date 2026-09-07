using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;
using Kanal.Host.ViewModels;

namespace Kanal.Host.Views;

public partial class IconBarView : UserControl
{
    public IconBarView()
    {
        InitializeComponent();
        Bar.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver<Rect>(bounds =>
        {
            if (DataContext is MainViewModel vm)
                vm.Shell.HeaderHeight = bounds.Height;
        }));
    }
}
