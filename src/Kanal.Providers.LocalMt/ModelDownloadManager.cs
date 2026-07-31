using System.Security.Cryptography;

namespace Kanal.Providers.LocalMt;

/// <summary>
/// Streams GGUF files into a models directory with progress, cancellation and
/// SHA256 verification. Interrupted or failed downloads leave nothing behind —
/// a model is either fully verified on disk or absent.
/// </summary>
public sealed class ModelDownloadManager
{
    private readonly string _directory;
    private readonly HttpClient _http;

    public ModelDownloadManager(string directory, HttpClient? http = null)
    {
        _directory = directory;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string GetPath(LocalModelInfo model) => Path.Combine(_directory, model.FileName);

    public bool IsDownloaded(LocalModelInfo model) => File.Exists(GetPath(model));

    public void Delete(LocalModelInfo model)
    {
        var path = GetPath(model);
        if (File.Exists(path))
            File.Delete(path);
        foreach (var part in LeftoverParts(model))
            TryDelete(part);
    }

    /// <summary>
    /// Part files of interrupted downloads — one per call, so there may be several. The bare
    /// <c>&lt;file&gt;.part</c> an older build left behind is matched too.
    /// </summary>
    private IEnumerable<string> LeftoverParts(LocalModelInfo model) => Directory.Exists(_directory)
        ? Directory.EnumerateFiles(_directory, model.FileName + "*.part")
        : [];

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // another download still owns it; its own finally will clean up
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Progress is 0..1 of the expected byte count.</summary>
    public async Task DownloadAsync(LocalModelInfo model, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var finalPath = GetPath(model);

        // Each call streams into its own part file. Sharing one path keyed on the model id
        // meant a second download of the same model truncated — and then, in the finally,
        // deleted — the file a still-running first download owned, killing it at the final
        // rename after however many gigabytes had already been transferred.
        var partPath = $"{finalPath}.{Guid.NewGuid():N}.part";
        var created = false;

        try
        {
            using var response = await _http.GetAsync(
                model.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? model.SizeBytes;
            using var sha = SHA256.Create();
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var destination = File.Create(partPath))
            {
                created = true;
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    if (total > 0)
                        progress?.Report(Math.Min(1.0, (double)done / total));
                }
            }

            sha.TransformFinalBlock([], 0, 0);
            var actual = Convert.ToHexStringLower(sha.Hash!);
            if (!actual.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"SHA256 mismatch for {model.FileName}: expected {model.Sha256}, got {actual}.");

            File.Move(partPath, finalPath, overwrite: true);
            progress?.Report(1.0);
        }
        finally
        {
            // only ever this call's own part file — never one another download is streaming into
            if (created && File.Exists(partPath))
                TryDelete(partPath);
        }
    }
}
