using System.Text.Json;
using System.Text.Json.Serialization;
using Kanal.Core.Diagnostics;

namespace Kanal.Core.Workspaces;

internal static class RecordFile
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static (T? Value, StoreProblem? Problem) Read<T>(string path) where T : class, IStoredRecord
    {
        var (text, unreachable) = ReadText(path);
        return unreachable is not null ? (null, unreachable) : Parse<T>(text!, path);
    }

    public static (string? Text, StoreProblem? Problem) ReadText(string path)
    {
        try
        {
            return (File.ReadAllText(path), null);
        }
        catch (Exception ex)
        {
            Log.Warning(StoreProblems.LogCategory, $"{path} could not be read.", ex);
            return (null, StoreProblems.Unreadable(path, ex.Message));
        }
    }

    public static (T? Value, StoreProblem? Problem) Parse<T>(string text, string path) where T : class, IStoredRecord
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (JsonException ex)
        {
            Log.Warning(StoreProblems.LogCategory, $"{path} could not be parsed.", ex);
            return (null, StoreProblems.Unreadable(path, ex.Message));
        }

        if (value is null)
            return (null, new StoreProblem(StoreProblemKind.Unreadable, path, "The file is empty."));

        // Refused, not half-read: the next save would write the dropped fields back as loss.
        if (value.SchemaVersion != SchemaVersion)
            return (null, new StoreProblem(
                StoreProblemKind.UnsupportedVersion, path,
                $"Unsupported workspace schema {value.SchemaVersion}."));

        // Well-formed JSON is not a well-formed record: a missing field deserializes to null.
        return value.Complete
            ? (value, null)
            : (null, new StoreProblem(StoreProblemKind.Unreadable, path, "The record is missing fields."));
    }

    public static string TemporaryPathFor(string path) => path + ".tmp";

    public static void Write<T>(string path, T record) where T : IStoredRecord
    {
        var temporary = TemporaryPathFor(path);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record, Options);
            stream.Flush(flushToDisk: true);
        }

        Replace(temporary, path);
    }

    private const int ReplaceAttempts = 10;
    private static readonly TimeSpan ReplaceDelay = TimeSpan.FromMilliseconds(50);

    // Windows refuses to replace a file that a reader holds open; the background scan holds it only briefly.
    private static void Replace(string temporary, string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < ReplaceAttempts && ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning(StoreProblems.LogCategory, $"{path} is in use. The replace is tried again.", ex);
                Thread.Sleep(ReplaceDelay);
            }
        }
    }
}
