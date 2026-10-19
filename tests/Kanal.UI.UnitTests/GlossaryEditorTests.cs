using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Kanal.Core.Workspaces;
using Kanal.Host.Localization;
using Kanal.Host.ViewModels;
using Kanal.Host.Views;

namespace Kanal.UI.UnitTests;

public class GlossaryEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-glossary-ui-" + Guid.NewGuid().ToString("N"));

    private readonly WorkspaceStore _store;
    private readonly Workspace _kappa;

    public GlossaryEditorTests()
    {
        _store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        var folder = Path.Combine(_root, "kappa");
        Directory.CreateDirectory(folder);
        _kappa = _store.CreateWorkspace("Kappa", folder).Workspace!;
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

    private static readonly GlossaryEntry Bracket = new("轴承座", "Lagerbock", "wspornik łożyska", "bearing bracket");
    private static readonly GlossaryEntry PartNumber = new("", "", "", "KX-4402", DoNotTranslate: true);

    private string GlossaryFile => Path.Combine(_kappa.RootPath, WorkspaceStore.GlossaryFileName);

    private ProjectSettingsViewModel Settings() => new(_store, _kappa, recording: false);

    private IReadOnlyList<GlossaryEntry> Stored() => _store.ReadGlossary(_kappa.Id).Entries!;

    [Fact]
    public void TheSettingsShowTheSavedGlossary()
    {
        _store.SaveGlossary(_kappa.Id, [Bracket, PartNumber]);

        var settings = Settings();

        Assert.Equal(2, settings.Glossary.Count);
        var row = settings.Glossary[0];
        Assert.Equal(("轴承座", "Lagerbock", "wspornik łożyska", "bearing bracket", false),
            (row.Zh, row.De, row.Pl, row.En, row.DoNotTranslate));
        Assert.True(settings.Glossary[1].DoNotTranslate);
        Assert.True(settings.CanEditGlossary);
    }

    [Fact]
    public void AnAddedRowIsStoredOnSave()
    {
        var settings = Settings();

        settings.AddTermCommand.Execute(null);
        var row = Assert.Single(settings.Glossary);
        Assert.Equal("", row.Zh + row.De + row.Pl + row.En);
        row.En = "KX-4402";
        row.DoNotTranslate = true;
        settings.SaveCommand.Execute(null);

        Assert.Equal([PartNumber], Stored());
    }

    [Fact]
    public void AnEditedCellIsStoredOnSave()
    {
        _store.SaveGlossary(_kappa.Id, [Bracket]);
        var settings = Settings();

        settings.Glossary[0].Pl = "wspornik";
        settings.SaveCommand.Execute(null);

        Assert.Equal([Bracket with { Pl = "wspornik" }], Stored());
    }

    [Fact]
    public void ARemovedRowIsGoneAfterSave()
    {
        _store.SaveGlossary(_kappa.Id, [Bracket, PartNumber]);
        var settings = Settings();

        settings.RemoveTermCommand.Execute(settings.Glossary[0]);
        settings.SaveCommand.Execute(null);

        Assert.Equal([PartNumber], Stored());
    }

    [Fact]
    public void ClosingWithoutSaveLeavesTheGlossaryUnchanged()
    {
        _store.SaveGlossary(_kappa.Id, [Bracket]);
        var settings = Settings();

        settings.Glossary[0].De = "Halter";
        settings.AddTermCommand.Execute(null);

        Assert.Equal([Bracket], Stored());
    }

    [Fact]
    public void SaveWithAnUntouchedGlossaryWritesNoFile()
    {
        var settings = Settings();

        settings.Name = "Kappa tooling";
        settings.SaveCommand.Execute(null);

        Assert.False(File.Exists(GlossaryFile));
    }

    [Fact]
    public void ADamagedGlossaryIsReportedAndLockedAndTheRestStillSaves()
    {
        File.WriteAllText(GlossaryFile, "{ not json");
        var settings = Settings();
        var finished = false;
        settings.Finished += () => finished = true;

        Assert.Equal(Localizer.Instance.Format("workspace.refused.unreadable", GlossaryFile), settings.GlossaryProblem);
        Assert.False(settings.CanEditGlossary);
        Assert.False(settings.AddTermCommand.CanExecute(null));
        Assert.Empty(settings.Glossary);

        settings.Name = "Kappa tooling";
        settings.SaveCommand.Execute(null);

        Assert.True(finished);
        Assert.Equal("Kappa tooling", _store.ListWorkspaces().Workspaces.Single().Name);
        Assert.Equal("{ not json", File.ReadAllText(GlossaryFile));
    }

    [Fact]
    public void ARefusedGlossarySaveKeepsTheWindowOpenAndExplainsWhy()
    {
        var settings = Settings();
        var finished = false;
        settings.Finished += () => finished = true;
        settings.AddTermCommand.Execute(null);
        settings.Glossary[0].De = "Lagerbock";
        Directory.Delete(_kappa.RootPath, recursive: true);

        settings.SaveCommand.Execute(null);

        Assert.False(finished);
        Assert.Equal(Localizer.Instance.Format("workspace.refused.foldermissing", _kappa.RootPath), settings.Problem);
    }

    [AvaloniaFact]
    public void TheWindowShowsOneCellPerLanguageAndTheFlagForEachRow()
    {
        _store.SaveGlossary(_kappa.Id, [Bracket]);
        var settings = Settings();
        var window = new ProjectSettingsWindow(settings);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var add = window.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "AddTerm");
        add.Command!.Execute(add.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        var cells = window.GetLogicalDescendants().OfType<TextBox>().Where(b => b.Name?.StartsWith("Term") == true).ToList();
        Assert.Equal(8, cells.Count);
        Assert.Equal("Lagerbock", cells.Single(c => c.Name == "TermDe" && c.Text != "").Text);
        Assert.All(cells, c => Assert.False(string.IsNullOrWhiteSpace(
            Avalonia.Automation.AutomationProperties.GetName(c))));
        Assert.Equal(2, window.GetLogicalDescendants().OfType<CheckBox>().Count(c => c.Name == "DoNotTranslate"));
        Assert.Equal(2, window.GetLogicalDescendants().OfType<Button>().Count(b => b.Name == "RemoveTerm"));

        window.Close();
    }
}
