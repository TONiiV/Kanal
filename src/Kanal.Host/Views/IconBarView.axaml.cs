using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;
using Kanal.Host.Services;
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
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                vm.ConfirmConsent = saveAudio => AskAsync(vm, saveAudio);
        };
    }

    private async Task<bool?> AskAsync(MainViewModel vm, bool saveAudio)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner)
            return null;

        return await new ConsentWindow(
            saveAudio,
            emphasiseRemote: vm.SelectedCaptureProfile.Id == CaptureProfileId.OnlineMeeting)
            .ShowDialog<bool?>(owner);
    }
}
