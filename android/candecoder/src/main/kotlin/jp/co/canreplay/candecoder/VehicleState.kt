package jp.co.canreplay.candecoder

/**
 * Playback state inferred purely from CAN traffic (requirement 53).
 *
 * There is no control channel from Windows: the only thing crossing the bus is
 * recorded vehicle traffic (requirement 2). So the app infers what the player is
 * doing from whether frames are arriving at all. A scenario change, a pause and a
 * seek all look the same from here -- traffic stops -- and all three need the
 * same response: stop presenting the last values as if they were live.
 */
enum class StreamState {
    /** Nothing has arrived since start-up. */
    WAITING,

    /** Frames are arriving. */
    LIVE,

    /** Traffic has stopped: paused, seeking, between scenarios, or disconnected. */
    STALE,
}

/** One CAN ID's arrival statistics, for the Raw CAN tab. */
data class MessageActivity(
    val canId: Int,
    val extended: Boolean,
    val name: String?,
    val lastFrame: RawCanFrame,
    val count: Long,
    val ratePerSecond: Double,
    val isDecodable: Boolean,
)

/**
 * Accumulates decoded values and arrival statistics from the CAN stream.
 *
 * Deliberately free of Android and coroutine types so it can be unit tested on
 * the JVM, and deliberately cheap per frame: at the ~1,100 frames per second the
 * RAV4 recordings replay at, anything allocating heavily here would fall behind.
 * The UI reads snapshots on its own timer (requirement 52).
 */
class VehicleStateTracker(
    private val database: SignalDatabase,
    private val staleFactor: Double = CanDecoder.DEFAULT_STALE_FACTOR,
    /** Whole-stream timeout: no frame at all for this long means STALE. */
    private val streamTimeoutMs: Long = DEFAULT_STREAM_TIMEOUT_MS,
) {
    private val signals = LinkedHashMap<String, DecodedSignal>()
    private val activity = LinkedHashMap<Int, MutableActivity>()
    private val recent = ArrayDeque<RawCanFrame>()
    private val lock = Any()

    private var lastFrameAtMs: Long = Long.MIN_VALUE
    private var firstFrameAtMs: Long = Long.MIN_VALUE
    private var totalFrames: Long = 0
    private var undecodedFrames: Long = 0

    /**
     * Every signal value written, not the number of distinct signals.
     *
     * This is the figure that pairs with the Windows player's "frames sent" and
     * the Spresense's recv_count: if frames are arriving but this is flat, the
     * link is fine and the problem is the definitions.
     */
    private var decodedSignalUpdates: Long = 0

    // A short window so the displayed rate reflects now rather than the run
    // average -- a stall is much easier to see that way.
    private val rateWindow = ArrayDeque<Long>()

    /** Feed one frame. Safe to call from the receiving thread. */
    fun onFrame(frame: RawCanFrame) {
        synchronized(lock) {
            totalFrames++
            if (firstFrameAtMs == Long.MIN_VALUE) firstFrameAtMs = frame.receivedAtMs
            lastFrameAtMs = frame.receivedAtMs

            rateWindow.addLast(frame.receivedAtMs)
            while (rateWindow.size > RATE_WINDOW_FRAMES ||
                (rateWindow.isNotEmpty() &&
                    frame.receivedAtMs - rateWindow.first() > RATE_WINDOW_MS)
            ) {
                rateWindow.removeFirst()
            }

            val message = database.message(frame.canId)
            if (message == null) {
                undecodedFrames++
            } else {
                for (decoded in CanDecoder.decode(message, frame, staleFactor)) {
                    signals["${decoded.messageName}.${decoded.signalName}"] = decoded
                    decodedSignalUpdates++
                }
            }

            val entry = activity.getOrPut(frame.canId) {
                MutableActivity(frame.canId, frame.extended, message?.name, message != null,
                    frame.receivedAtMs)
            }
            entry.record(frame)

            recent.addLast(frame)
            while (recent.size > RECENT_CAPACITY) {
                recent.removeFirst()
            }
        }
    }

    fun reset() {
        synchronized(lock) {
            signals.clear()
            activity.clear()
            recent.clear()
            rateWindow.clear()
            lastFrameAtMs = Long.MIN_VALUE
            firstFrameAtMs = Long.MIN_VALUE
            totalFrames = 0
            undecodedFrames = 0
            decodedSignalUpdates = 0
        }
    }

    /** Frames per second over the recent window; 0 when nothing is arriving. */
    fun framesPerSecond(nowMs: Long): Double = synchronized(lock) { framesPerSecondLocked(nowMs) }

    private fun framesPerSecondLocked(nowMs: Long): Double {
        if (rateWindow.size < 2) return 0.0
        // A stalled stream must read 0, not the rate it had before it stopped.
        if (nowMs - rateWindow.last() > streamTimeoutMs) return 0.0
        val span = rateWindow.last() - rateWindow.first()
        if (span <= 0) return 0.0
        return (rateWindow.size - 1) * 1000.0 / span
    }

    fun streamState(nowMs: Long): StreamState = synchronized(lock) {
        when {
            lastFrameAtMs == Long.MIN_VALUE -> StreamState.WAITING
            nowMs - lastFrameAtMs > streamTimeoutMs -> StreamState.STALE
            else -> StreamState.LIVE
        }
    }

    /**
     * All decoded signals, newest values first by message then signal name.
     *
     * Stale entries are returned rather than dropped so the UI can grey them out
     * and show when they were last seen -- requirement 53 forbids continuing to
     * present a stale value as if it were current, not showing it at all.
     */
    fun snapshot(nowMs: Long): VehicleSnapshot = synchronized(lock) {
        VehicleSnapshot(
            streamState = streamState(nowMs),
            nowMs = nowMs,
            signals = signals.values.sortedWith(
                compareBy({ it.messageName }, { it.signalName })),
            activity = activity.values
                .map { it.toActivity(nowMs) }
                .sortedBy { it.canId },
            recentFrames = recent.toList().asReversed(),
            totalFrames = totalFrames,
            undecodedFrames = undecodedFrames,
            decodedSignalUpdates = decodedSignalUpdates,
            framesPerSecond = framesPerSecondLocked(nowMs),
            lastFrameAtMs = if (lastFrameAtMs == Long.MIN_VALUE) null else lastFrameAtMs,
            firstFrameAtMs = if (firstFrameAtMs == Long.MIN_VALUE) null else firstFrameAtMs,
        )
    }

    /** Look up one signal by "MESSAGE.SIGNAL", for the Overview gauges. */
    fun signal(messageName: String, signalName: String): DecodedSignal? =
        synchronized(lock) { signals["$messageName.$signalName"] }

    private class MutableActivity(
        val canId: Int,
        val extended: Boolean,
        val name: String?,
        val decodable: Boolean,
        val firstAtMs: Long,
    ) {
        var count: Long = 0
        lateinit var last: RawCanFrame

        fun record(frame: RawCanFrame) {
            count++
            last = frame
        }

        fun toActivity(nowMs: Long): MessageActivity {
            val span = (last.receivedAtMs - firstAtMs).coerceAtLeast(1L)
            return MessageActivity(
                canId = canId,
                extended = extended,
                name = name,
                lastFrame = last,
                count = count,
                ratePerSecond = if (count > 1) (count - 1) * 1000.0 / span else 0.0,
                isDecodable = decodable,
            )
        }
    }

    companion object {
        /**
         * 350 ms is the player's default gap between scenarios; this must be
         * comfortably below it so the gap is actually detected, and comfortably
         * above the slowest message period (~100 ms) so ordinary traffic never
         * trips it.
         */
        const val DEFAULT_STREAM_TIMEOUT_MS = 250L

        const val RECENT_CAPACITY = 300

        /** Frames kept for the rolling rate estimate. */
        const val RATE_WINDOW_FRAMES = 2000

        /** ...and the longest span they may cover. */
        const val RATE_WINDOW_MS = 2000L
    }
}

data class VehicleSnapshot(
    val streamState: StreamState,
    val nowMs: Long,
    val signals: List<DecodedSignal>,
    val activity: List<MessageActivity>,
    val recentFrames: List<RawCanFrame>,
    val totalFrames: Long,
    val undecodedFrames: Long,
    val decodedSignalUpdates: Long,
    val framesPerSecond: Double,
    val lastFrameAtMs: Long?,
    val firstFrameAtMs: Long?,
) {
    val decodedFrames: Long get() = totalFrames - undecodedFrames

    /** How long frames have been arriving, in milliseconds. */
    val elapsedMs: Long
        get() = if (firstFrameAtMs != null && lastFrameAtMs != null)
            lastFrameAtMs - firstFrameAtMs else 0L

    fun signal(messageName: String, signalName: String): DecodedSignal? =
        signals.firstOrNull { it.messageName == messageName && it.signalName == signalName }

    /**
     * A signal's value for display, or null when it must not be shown as live.
     *
     * Returns null both when the signal has never been seen and when it has gone
     * stale, which is what lets the UI print "---" instead of a number that
     * stopped being true (requirement 53).
     */
    fun liveValue(messageName: String, signalName: String): DecodedSignal? =
        signal(messageName, signalName)?.takeIf {
            streamState == StreamState.LIVE && !it.isStale(nowMs)
        }
}
