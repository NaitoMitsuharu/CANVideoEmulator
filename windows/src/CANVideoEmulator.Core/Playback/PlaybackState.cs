namespace CANVideoEmulator.Core.Playback;

/// <summary>States of the shared <see cref="PlaybackClock"/> (requirement 11).</summary>
public enum PlaybackState
{
    /// <summary>Position is 0 and nothing is being sent on the bus.</summary>
    Stopped,

    /// <summary>Time is advancing; the CAN scheduler is the only thing that may transmit.</summary>
    Playing,

    /// <summary>Time is frozen at the current position; transmission has stopped.</summary>
    Paused,

    /// <summary>
    /// Time is frozen while the video and CAN timelines are repositioned. Kept
    /// distinct from Paused so the transition gap can be observed and measured.
    /// </summary>
    Seeking,
}
