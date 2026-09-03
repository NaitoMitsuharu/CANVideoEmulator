namespace CANVideoEmulator.Core.Video;

/// <summary>
/// The video surface, behind an interface so the backend can be swapped
/// (requirement 16).
/// </summary>
/// <remarks>
/// The shipped implementation is WPF's <c>MediaElement</c>, which uses Media
/// Foundation and therefore needs no third-party runtime on the exhibition PC.
/// If its seek accuracy ever proves insufficient, only this interface has to be
/// re-implemented (LibVLCSharp being the obvious alternative) -- the clock, the
/// scheduler and the session logic are unaffected.
///
/// Implementations may be UI-thread affine; the session marshals calls.
/// </remarks>
public interface IVideoPlayer : IDisposable
{
    /// <summary>Path of the currently open file, or null.</summary>
    string? Source { get; }

    /// <summary>True once the backend has reported the media as ready to play.</summary>
    bool IsReady { get; }

    /// <summary>Media length once known, otherwise <see cref="TimeSpan.Zero"/>.</summary>
    TimeSpan NaturalDuration { get; }

    /// <summary>Current position within the media.</summary>
    TimeSpan Position { get; }

    /// <summary>Raised when the backend has finished loading a source.</summary>
    event EventHandler? Opened;

    /// <summary>Raised when the backend fails; the message is safe to show.</summary>
    event EventHandler<string>? Failed;

    /// <summary>Open a file. Must not begin playback.</summary>
    void Open(string path);

    /// <summary>Release the current media.</summary>
    void CloseMedia();

    void Play();

    void Pause();

    /// <summary>Pause and return to the start.</summary>
    void Stop();

    /// <summary>Jump to <paramref name="position"/>.</summary>
    void Seek(TimeSpan position);
}

/// <summary>
/// Keeps the video within tolerance of the playback clock (requirement 41).
/// </summary>
/// <remarks>
/// A decoder's own clock drifts slowly against ours. Correcting every tick would
/// make the picture stutter visibly, so a seek is issued only once the error
/// exceeds <see cref="Tolerance"/>, and then not again until
/// <see cref="Cooldown"/> has passed -- a seek itself takes time to settle, and
/// re-measuring during that settle would cause a correction storm.
/// </remarks>
public sealed class VideoDriftCorrector
{
    private TimeSpan _lastCorrectionAt = TimeSpan.MinValue;

    public VideoDriftCorrector(TimeSpan? tolerance = null, TimeSpan? cooldown = null)
    {
        Tolerance = tolerance ?? TimeSpan.FromMilliseconds(250);
        Cooldown = cooldown ?? TimeSpan.FromSeconds(2);
    }

    /// <summary>Error that must be exceeded before correcting.</summary>
    public TimeSpan Tolerance { get; set; }

    /// <summary>Minimum spacing between corrections.</summary>
    public TimeSpan Cooldown { get; set; }

    /// <summary>Signed error of the most recent evaluation (video minus clock).</summary>
    public TimeSpan LastDrift { get; private set; }

    public int CorrectionCount { get; private set; }

    public void Reset()
    {
        _lastCorrectionAt = TimeSpan.MinValue;
        LastDrift = TimeSpan.Zero;
    }

    /// <summary>
    /// Decide whether the video should be nudged back onto the clock.
    /// </summary>
    /// <param name="videoPosition">Where the decoder says it is.</param>
    /// <param name="targetPosition">Where the clock says it should be.</param>
    /// <param name="now">A monotonic reading, used only for the cooldown.</param>
    public bool ShouldCorrect(TimeSpan videoPosition, TimeSpan targetPosition, TimeSpan now)
    {
        LastDrift = videoPosition - targetPosition;

        if (LastDrift.Duration() <= Tolerance)
        {
            return false;
        }

        if (_lastCorrectionAt != TimeSpan.MinValue && now - _lastCorrectionAt < Cooldown)
        {
            return false;
        }

        _lastCorrectionAt = now;
        CorrectionCount++;
        return true;
    }
}
