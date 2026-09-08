namespace Kanal.Core.Models;

// A translation model is one GGUF; a transcription model is an encoder, a decoder, a joiner and a
// token table. What the downloader is handed is therefore a file, not a model.
public interface IDownloadableFile
{
    string FileName { get; }

    string DownloadUrl { get; }

    long SizeBytes { get; }

    string Sha256 { get; }
}
