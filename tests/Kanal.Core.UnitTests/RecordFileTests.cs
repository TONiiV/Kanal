using Kanal.Core.Workspaces;

namespace Kanal.Core.UnitTests;

public class RecordFileTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "kanal-record-" + Guid.NewGuid().ToString("N"));

    private string Target => Path.Combine(_root, "workspaces.json");

    public RecordFileTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private static StoredRegistry Registry(string name) =>
        new(RecordFile.SchemaVersion, [new StoredRegistryEntry(name, name, null, DateTimeOffset.UnixEpoch)]);

    [Fact]
    public async Task AWriteWaitsForAReaderThatHoldsTheTargetOpen()
    {
        RecordFile.Write(Target, Registry("before"));
        var reader = new FileStream(Target, FileMode.Open, FileAccess.Read, FileShare.Read);

        var write = Task.Run(() => RecordFile.Write(Target, Registry("after")), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await reader.DisposeAsync();
        await write.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var (read, problem) = RecordFile.Read<StoredRegistry>(Target);
        Assert.Null(problem);
        Assert.Equal("after", Assert.Single(read!.Workspaces!).Id);
        Assert.False(File.Exists(Target + ".tmp"));
    }
}
