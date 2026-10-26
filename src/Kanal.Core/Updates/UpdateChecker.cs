namespace Kanal.Core.Updates;

public sealed class UpdateChecker(HttpClient http, Uri endpoint, TimeSpan? timeout = null)
{
    public const string ProductionUrl = "https://kanal-relay.toniiv.workers.dev/?action=update";

    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    public async Task<UpdateResult> CheckAsync(
        string currentVersion,
        string platform,
        CancellationToken ct = default)
    {
        if (!ReleaseVersion.TryParse(currentVersion, out var current))
            return UpdateResult.Unknown;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            using var response = await http.GetAsync(Request(currentVersion, platform), limit.Token);
            if (!response.IsSuccessStatusCode)
                return UpdateResult.Unknown;

            var body = await response.Content.ReadAsStringAsync(limit.Token);
            return UpdateManifest.TryParse(body, out var manifest)
                ? new UpdateResult(UpdatePolicy.Evaluate(current, manifest), manifest)
                : UpdateResult.Unknown;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return UpdateResult.Unknown;
        }
    }

    private Uri Request(string version, string platform)
    {
        var builder = new UriBuilder(endpoint);
        var existing = builder.Query.TrimStart('?');
        var added = $"version={Uri.EscapeDataString(version)}&platform={Uri.EscapeDataString(platform)}";
        builder.Query = string.IsNullOrEmpty(existing) ? added : $"{existing}&{added}";
        return builder.Uri;
    }
}
