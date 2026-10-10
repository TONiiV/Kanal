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
        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception ex)
        {
            Log.Warning(StoreProblems.LogCategory, $"{path} could not be read.", ex);
            return (null, new StoreProblem(StoreProblemKind.Unreadable, path, ex.Message));
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

    public static void Write<T>(string path, T record) where T : IStoredRecord =>
        File.WriteAllText(path, JsonSerializer.Serialize(record, Options));
}
