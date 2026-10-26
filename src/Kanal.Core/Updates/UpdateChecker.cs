namespace Kanal.Core.Updates;

public sealed class UpdateChecker(HttpClient http, Uri endpoint, TimeSpan? timeout = null)
{
    public const string ProductionUrl = "https://kanal-relay.toniiv.workers.dev/?action=update";

    public Task<UpdateResult> CheckAsync(
        string currentVersion,
        string platform,
        CancellationToken ct = default) =>
        throw new NotImplementedException();
}
