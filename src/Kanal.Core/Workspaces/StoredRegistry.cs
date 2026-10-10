using System.Text.Json.Serialization;

namespace Kanal.Core.Workspaces;

internal sealed record StoredRegistry(int SchemaVersion, List<StoredRegistryEntry>? Workspaces)
    : IStoredRecord
{
    [JsonIgnore]
    public bool Complete => Workspaces is not null;
}
