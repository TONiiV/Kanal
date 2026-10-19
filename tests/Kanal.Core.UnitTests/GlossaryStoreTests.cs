using System.Text.Json;
using Kanal.Core.Workspaces;

namespace Kanal.Core.UnitTests;

public class GlossaryStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-glossary-" + Guid.NewGuid().ToString("N"));

    private WorkspaceStore Store() => new(Path.Combine(_root, "workspaces.json"));

    private Workspace Project(WorkspaceStore store, string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        var result = store.CreateWorkspace(name, folder);
        Assert.Null(result.Problem);
        return result.Workspace!;
    }

    private static string FileOf(Workspace project) =>
        Path.Combine(project.RootPath, WorkspaceStore.GlossaryFileName);

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

    [Fact]
    public void ANewProjectHasAnEmptyGlossaryAndNoFile()
    {
        var store = Store();
        var project = Project(store, "acme");

        var read = store.ReadGlossary(project.Id);

        Assert.Null(read.Problem);
        Assert.Empty(read.Entries!);
        Assert.False(File.Exists(FileOf(project)));
    }

    [Fact]
    public void ASavedGlossarySurvivesARestartWithEveryCellAndFlag()
    {
        var store = Store();
        var project = Project(store, "acme");

        Assert.Null(store.SaveGlossary(project.Id, [Bracket, PartNumber]));
        var read = Store().ReadGlossary(project.Id);

        Assert.Null(read.Problem);
        Assert.Equal([Bracket, PartNumber], read.Entries!);
    }

    [Fact]
    public void TheFileIsPlainJsonInTheProjectFolder()
    {
        var store = Store();
        var project = Project(store, "acme");

        store.SaveGlossary(project.Id, [Bracket, PartNumber]);

        using var document = JsonDocument.Parse(File.ReadAllText(FileOf(project)));
        var root = document.RootElement;
        Assert.Equal(WorkspaceStore.SchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        var entries = root.GetProperty("entries");
        Assert.Equal("wspornik łożyska", entries[0].GetProperty("pl").GetString());
        Assert.Equal("轴承座", entries[0].GetProperty("zh").GetString());
        Assert.False(entries[0].GetProperty("doNotTranslate").GetBoolean());
        Assert.True(entries[1].GetProperty("doNotTranslate").GetBoolean());
        Assert.False(File.Exists(FileOf(project) + ".tmp"));
    }

    [Fact]
    public void SaveTrimsCellsAndDropsRowsWithNoTerm()
    {
        var store = Store();
        var project = Project(store, "acme");

        store.SaveGlossary(project.Id,
        [
            new(" 轴承座 ", "Lagerbock  ", "", "", false),
            new("", " ", "", "", true),
        ]);

        var entry = Assert.Single(store.ReadGlossary(project.Id).Entries!);
        Assert.Equal(new GlossaryEntry("轴承座", "Lagerbock", "", "", false), entry);
    }

    [Fact]
    public void AnEmptyGlossaryCanBeSavedOverAFullOne()
    {
        var store = Store();
        var project = Project(store, "acme");
        store.SaveGlossary(project.Id, [Bracket]);

        Assert.Null(store.SaveGlossary(project.Id, []));

        Assert.Empty(store.ReadGlossary(project.Id).Entries!);
    }

    [Fact]
    public void AHandEditedFileWithAMissingCellReadsTheCellAsEmpty()
    {
        var store = Store();
        var project = Project(store, "acme");
        File.WriteAllText(FileOf(project),
            """{ "schemaVersion": 1, "entries": [ { "de": "Lagerbock" } ] }""");

        var read = store.ReadGlossary(project.Id);

        Assert.Null(read.Problem);
        Assert.Equal(new GlossaryEntry("", "Lagerbock", "", "", false), Assert.Single(read.Entries!));
    }

    [Fact]
    public void ADamagedFileIsReportedNotEmptied()
    {
        var store = Store();
        var project = Project(store, "acme");
        File.WriteAllText(FileOf(project), "{ not json");

        var read = store.ReadGlossary(project.Id);

        Assert.Null(read.Entries);
        Assert.Equal(StoreProblemKind.Unreadable, read.Problem!.Kind);
        Assert.Equal("{ not json", File.ReadAllText(FileOf(project)));
    }

    [Fact]
    public void AFileWithoutEntriesIsReportedNotEmptied()
    {
        var store = Store();
        var project = Project(store, "acme");
        File.WriteAllText(FileOf(project), """{ "schemaVersion": 1 }""");

        Assert.Equal(StoreProblemKind.Unreadable, store.ReadGlossary(project.Id).Problem!.Kind);
    }

    [Fact]
    public void AFileFromALaterSchemaIsRefused()
    {
        var store = Store();
        var project = Project(store, "acme");
        File.WriteAllText(FileOf(project),
            $$"""{ "schemaVersion": {{WorkspaceStore.SchemaVersion + 1}}, "entries": [] }""");

        Assert.Equal(StoreProblemKind.UnsupportedVersion, store.ReadGlossary(project.Id).Problem!.Kind);
    }

    [Fact]
    public void AnUnknownProjectIsRefused()
    {
        var store = Store();
        Project(store, "acme");

        Assert.Equal(StoreProblemKind.NotFound, store.ReadGlossary("nope").Problem!.Kind);
        Assert.Equal(StoreProblemKind.NotFound, store.SaveGlossary("nope", [Bracket])!.Kind);
    }

    [Fact]
    public void AProjectWhoseFolderHasVanishedIsRefused()
    {
        var store = Store();
        var project = Project(store, "acme");
        Directory.Delete(project.RootPath, recursive: true);

        Assert.Equal(StoreProblemKind.FolderMissing, store.ReadGlossary(project.Id).Problem!.Kind);
        Assert.Equal(StoreProblemKind.FolderMissing, store.SaveGlossary(project.Id, [Bracket])!.Kind);
        Assert.False(Directory.Exists(project.RootPath));
    }

    [Fact]
    public void TwoProjectsKeepSeparateGlossaries()
    {
        var store = Store();
        var acme = Project(store, "acme");
        var beta = Project(store, "beta");

        store.SaveGlossary(acme.Id, [Bracket]);
        store.SaveGlossary(beta.Id, [PartNumber]);

        Assert.Equal([Bracket], store.ReadGlossary(acme.Id).Entries!);
        Assert.Equal([PartNumber], store.ReadGlossary(beta.Id).Entries!);
    }

    [Fact]
    public void TheGlossaryMovesWithTheProjectFolder()
    {
        var store = Store();
        var project = Project(store, "acme");
        store.SaveGlossary(project.Id, [Bracket]);
        var moved = Path.Combine(_root, "acme-moved");
        Directory.Move(project.RootPath, moved);

        Assert.Null(store.RelocateWorkspace(project.Id, moved).Problem);

        Assert.Equal([Bracket], store.ReadGlossary(project.Id).Entries!);
    }

    [Fact]
    public void DeletingTheProjectFilesDeletesTheGlossaryAndTheEmptyFolder()
    {
        var store = Store();
        var project = Project(store, "acme");
        store.SaveGlossary(project.Id, [Bracket]);

        Assert.Null(store.DeleteWorkspace(project.Id));

        Assert.False(Directory.Exists(project.RootPath));
    }
}
