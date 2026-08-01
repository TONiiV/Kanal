using System;
using Kanal.Audio;

namespace Kanal.Host.Services;

/// <summary>
/// The failure policy around <see cref="WavWriter"/> that a live meeting requires. The recorder
/// sits on the audio capture path, so <see cref="Write"/> must never throw: an exception
/// escaping it would take the capture loop — and with it the meeting — down along with the
/// recording. A full disk or a pulled USB stick costs the recording, once, reported once
/// through the callback; every frame after that is dropped silently, because retrying against a
/// dead disk would only overwrite the real reason with "cannot access a disposed object".
/// </summary>
public sealed class MeetingRecorder : IDisposable
{
    private readonly WavWriter _writer;
    private readonly Action<string> _onStopped;
    private bool _stopped;

    /// <param name="writer">Owned by the recorder from here on.</param>
    /// <param name="onStopped">
    /// The reason the recording ended early. Invoked at most once, on whatever thread the
    /// failing write ran on — the caller marshals to the UI thread, not this class.
    /// </param>
    public MeetingRecorder(WavWriter writer, Action<string> onStopped)
    {
        _writer = writer;
        _onStopped = onStopped;
    }

    public string Path => _writer.Path;

    /// <summary>Called on the capture thread for every accepted frame. Never throws.</summary>
    public void Write(ReadOnlySpan<byte> pcm16)
    {
        if (_stopped)
            return;

        try
        {
            _writer.Write(pcm16);
        }
        catch (Exception ex)
        {
            _stopped = true;
            try
            {
                _writer.Dispose(); // patches the lengths: what was written so far still plays
            }
            catch
            {
                // the same dead disk the write just met; the frames that reached it are kept
            }

            _onStopped(ex.Message);
        }
    }

    /// <summary>
    /// The normal end of a recording. A frame that arrives after this — a straggler during
    /// teardown — is dropped without a report: Stop is not a failure.
    /// </summary>
    public void Dispose()
    {
        _stopped = true;
        _writer.Dispose();
    }
}
