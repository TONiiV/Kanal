using System.Text.Json.Serialization;

namespace Kanal.Core.Workspaces;

internal interface IStoredRecord
{
    int SchemaVersion { get; }

    [JsonIgnore]
    bool Complete { get; }
}
