using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class MeetingTitleRowTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-title-" + Guid.NewGuid().ToString("N"));

    private WorkspaceStore Store()
    {
        Directory.CreateDirectory(Path.Combine(_root, "kappa"));
        return new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private MainViewModel WithMeeting(string title)
    {
        var store = Store();
        var workspace = store.CreateWorkspace("Kappa", Path.Combine(_root, "kappa")).Workspace!;
        store.CreateMeeting(workspace.Id, title);
        return TestViewModels.Hermetic(workspaces: () => store);
    }

    [Fact]
    public void WithNothingChosenTheRowStillNamesTheMeetingRatherThanShowingNothing()
    {
        var vm = TestViewModels.Hermetic();

        Assert.Equal(Localizer.Instance["meeting.untitled"], vm.MeetingTitle);
    }

    [Fact]
    public void ChoosingAMeetingRetitlesTheCentreColumn()
    {
        var vm = WithMeeting("Werkzeugübergabe");

        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single();

        Assert.Equal("Werkzeugübergabe", vm.MeetingTitle);

        vm.Sidebar.SelectedMeeting = null;
        Assert.Equal(Localizer.Instance["meeting.untitled"], vm.MeetingTitle);
    }

    /// <summary>
    /// The row used to fall back to the room id, which is a bearer capability — and which now
    /// also becomes the heading of an exported transcript and the name a save dialog offers.
    /// </summary>
    [Fact]
    public void ALoadedRoomIdNeverNamesTheRow()
    {
        var vm = WithMeeting("Werkzeugübergabe");

        vm.LoadedRoomId = "kanal-2026-09-07-a1";
        Assert.Equal(Localizer.Instance["meeting.untitled"], vm.MeetingTitle);

        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single();
        Assert.Equal("Werkzeugübergabe", vm.MeetingTitle);
    }

    [AvaloniaFact]
    public void TheTitleSharesItsRowWithTheFlagsAndNoProjectHeaderSitsAboveIt()
    {
        var vm = WithMeeting("Werkzeugübergabe");
        var window = new MainWindow { DataContext = vm, Width = 1320, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var room = window.GetLogicalDescendants().OfType<MeetingRoomView>().Single();
        var title = Named(room, "MeetingTitle");
        var flags = Named(room, "RoomLanguages");

        // The title sits in its own group beside the note now; what has to stay true is that the
        // group and the flags are on one row, and that row is the top of the transcript.
        var row = (Control)flags.Parent!;
        Assert.Contains(row, title.GetLogicalAncestors());
        Assert.Same(room.Content, row.Parent);

        window.Close();
    }

    private const string LongPolishTitle =
        "Przegląd wsporników KX-4402 — tolerancje, żądania zmian i terminy dostaw dla źródłowego łożyska ślizgowego";

    [AvaloniaTheory]
    [InlineData("供应商质量评审 KX-4402")]
    [InlineData("Supplier review KX-4402")]
    [InlineData("Żądanie wsporników")]
    public void EditingTheTitleKeepsItsHeightAndItsPlaceInTheRow(string title)
    {
        var (vm, window, room) = ShowRoom(title);
        var field = Named(room, "MeetingTitleField");
        var readText = Named(room, "MeetingTitle");
        var fieldBox = BoundsIn(field, room);
        var readTextBox = BoundsIn(readText, room);

        vm.BeginRenameTitleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var editor = (TextBox)Named(room, "MeetingTitleEditor");
        var editorBox = BoundsIn(editor, room);
        var editTextBox = BoundsIn(editor.GetVisualDescendants().OfType<TextPresenter>().Single(), room);

        Assert.InRange(editorBox.Height, fieldBox.Height - 1, fieldBox.Height + 1);
        Assert.InRange(editorBox.Top, fieldBox.Top - 1, fieldBox.Top + 1);
        Assert.InRange(editTextBox.Center.Y, readTextBox.Center.Y - 1, readTextBox.Center.Y + 1);
        Assert.InRange(editTextBox.Left, readTextBox.Left - 1, readTextBox.Left + 1);

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("供应商质量评审 KX-4402")]
    [InlineData("Supplier review KX-4402")]
    [InlineData("Żądanie wsporników")]
    public void TheEditorHugsAShortTitleWithoutClippingIt(string title)
    {
        var (vm, window, room) = ShowRoom(title, width: 1900);
        vm.BeginRenameTitleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var editor = (TextBox)Named(room, "MeetingTitleEditor");
        var text = editor.GetVisualDescendants().OfType<TextPresenter>().Single();
        var column = TitleColumnWidth(room);

        Assert.True(editor.Bounds.Width < column * 0.6,
            $"Editor is {editor.Bounds.Width}px in a {column}px column; it stretches instead of hugging the title.");
        Assert.True(text.Bounds.Width <= editor.Bounds.Width + 0.5,
            $"Title needs {text.Bounds.Width}px but the editor is {editor.Bounds.Width}px.");

        window.Close();
    }

    [AvaloniaFact]
    public void ALongTitleGrowsTheEditorUpToTheColumnButNotPastIt()
    {
        var (vm, window, room) = ShowRoom("Supplier review KX-4402", width: 1900);
        vm.BeginRenameTitleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var editor = (TextBox)Named(room, "MeetingTitleEditor");
        var shortWidth = editor.Bounds.Width;

        vm.TitleDraft = LongPolishTitle;
        Dispatcher.UIThread.RunJobs();

        Assert.True(editor.Bounds.Width > shortWidth);
        Assert.True(editor.Bounds.Right <= TitleColumnWidth(room) + 0.5,
            $"Editor ends at {editor.Bounds.Right}px, past the {TitleColumnWidth(room)}px title column.");

        window.Close();
    }

    [AvaloniaFact]
    public void ReopeningTheEditorSelectsTheWholeTitleInInkWithPaperTextFromItsStart()
    {
        var (vm, window, room) = ShowRoom(LongPolishTitle);
        var field = Named(room, "MeetingTitleField");
        var editor = (TextBox)Named(room, "MeetingTitleEditor");

        field.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();
        editor.CaretIndex = LongPolishTitle.Length;
        vm.CancelRenameTitleCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        field.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        Dispatcher.UIThread.RunJobs();

        var resources = Application.Current!.Resources;
        Assert.True(editor.IsFocused);
        Assert.Equal(LongPolishTitle, editor.SelectedText);
        Assert.Same(resources["Ink"], editor.SelectionBrush);
        Assert.Same(resources["Paper"], editor.SelectionForegroundBrush);
        Assert.Equal(0, editor.CaretIndex);

        window.Close();
    }

    private (MainViewModel, MainWindow, MeetingRoomView) ShowRoom(string title, double width = 1320)
    {
        var vm = WithMeeting(title);
        vm.Sidebar.SelectedMeeting = vm.Sidebar.Meetings.Single();
        var window = new MainWindow { DataContext = vm, Width = width, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, window, window.GetLogicalDescendants().OfType<MeetingRoomView>().Single());
    }

    private static double TitleColumnWidth(MeetingRoomView room) =>
        ((Grid)Named(room, "MeetingTitleEditor").Parent!).ColumnDefinitions[0].ActualWidth;

    private static Rect BoundsIn(Visual visual, Visual root) =>
        new(visual.TranslatePoint(default, root)!.Value, visual.Bounds.Size);

    private static Control Named(Control root, string name) =>
        root.GetLogicalDescendants().OfType<Control>()
            .Where(control => control.Name == name).Distinct().Single();
}
