using System.Security.Cryptography;

namespace Kanal.Core.Models;

/// <summary>
/// Streams model files into a models directory with progress, cancellation and
/// SHA256 verification. Interrupted or failed downloads leave nothing behind —
/// a file is either fully verified on disk or absent.
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

    public string GetPath(IDownloadableFile file) => Path.Combine(_directory, file.FileName);

    public bool IsDownloaded(IDownloadableFile file) => File.Exists(GetPath(file));

    public void Delete(IDownloadableFile file)
    {
        var path = GetPath(file);
        if (File.Exists(path))
            File.Delete(path);
        foreach (var part in LeftoverParts(file))
            TryDelete(part);
    }

    /// <summary>
    /// Part files of interrupted downloads — one per call, so there may be several. The bare
    /// <c>&lt;file&gt;.part</c> an older build left behind is matched too.
    /// </summary>
    private IEnumerable<string> LeftoverParts(IDownloadableFile file) => Directory.Exists(_directory)
        ? Directory.EnumerateFiles(_directory, file.FileName + "*.part")
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

    /// <summary>A model of several files is downloaded when every one of them is.</summary>
    public bool IsDownloaded(IEnumerable<IDownloadableFile> parts) => parts.All(IsDownloaded);

    public IReadOnlyList<IDownloadableFile> MissingParts(IEnumerable<IDownloadableFile> parts) =>
        parts.Where(p => !IsDownloaded(p)).ToList();

    public void Delete(IEnumerable<IDownloadableFile> parts)
    {
        foreach (var part in parts)
            Delete(part);
    }

    // Progress is weighted by declared size, not by file count: the encoder of a transcription
    // model is 96% of its bytes.
    public async Task DownloadAsync(
        IReadOnlyList<IDownloadableFile> parts, IProgress<double>? progress, CancellationToken ct)
    {
        var grandTotal = Math.Max(1, parts.Sum(p => p.SizeBytes));
        long doneBytes = 0;

        foreach (var part in parts)
        {
            var before = doneBytes;
            var share = part.SizeBytes;
            var relay = progress is null
                ? null
                : new RelayProgress(fraction =>
                    progress.Report(Math.Min(1.0, (before + fraction * share) / grandTotal)));

            await DownloadAsync(part, relay, ct);
            doneBytes += share;
        }

        progress?.Report(1.0);
    }

    // Progress<T> posts through the synchronization context, which would let a part's last
    // report land after the next part has already moved the baseline.
    private sealed class RelayProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Progress is 0..1 of the expected byte count.</summary>
    public async Task DownloadAsync(IDownloadableFile file, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(_directory);
        var finalPath = GetPath(file);

        // Each call streams into its own part file. Sharing one path keyed on the model id
        // meant a second download of the same model truncated — and then, in the finally,
        // deleted — the file a still-running first download owned, killing it at the final
        // rename after however many gigabytes had already been transferred.
        var partPath = $"{finalPath}.{Guid.NewGuid():N}.part";
        var created = false;

        try
        {
            using var response = await _http.GetAsync(
                file.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? file.SizeBytes;
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
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"SHA256 mismatch for {file.FileName}: expected {file.Sha256}, got {actual}.");

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
