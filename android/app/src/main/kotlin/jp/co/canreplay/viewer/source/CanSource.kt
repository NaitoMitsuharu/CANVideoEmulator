package jp.co.canreplay.viewer.source

import jp.co.canreplay.candecoder.RawCanFrame

/**
 * Where raw CAN frames come from.
 *
 * The viewer decodes frames; it does not care how they arrived. Everything above
 * this interface — the decoder, the gauges, the staleness handling — works the
 * same whether the frames came off a real bus or out of a file.
 *
 * ## Adding a hardware source
 *
 * The reference bench puts a small microcontroller between the CAN bus and the
 * phone: it reads frames off a CAN controller and forwards them over USB. Any
 * such bridge fits here. Implement this interface, hand each received frame to
 * the `onFrame` callback with a monotonic timestamp
 * ([android.os.SystemClock.elapsedRealtime]), and register it in
 * [CanSourceFactory].
 *
 * Two things matter for a source implementation:
 *
 *  * **`onFrame` is called on your thread, not the UI thread.** It is cheap and
 *    lock-scoped by design; do not marshal to the main thread first. At the
 *    ~1,100 frames per second these recordings replay at, a dispatch per frame
 *    would put the recomposer permanently behind.
 *  * **Timestamps must be monotonic.** Staleness detection compares them against
 *    a later reading of the same clock, so a wall clock that can step backwards
 *    would make live signals read as stale.
 *
 * No vendor SDK is bundled here. The bridge firmware and its host-side library
 * are whatever the integrator already uses.
 */
interface CanSource {
    /** Short name shown in the status header, e.g. "CAN bridge (USB)". */
    val name: String

    val status: SourceStatus

    /** Begin delivering frames to [onFrame]. Safe to call when already started. */
    fun start(onFrame: (RawCanFrame) -> Unit)

    /** Stop delivering frames. Safe to call when already stopped. */
    fun stop()
}

enum class SourceState {
    DISCONNECTED,
    CONNECTING,
    CONNECTED,
    ERROR,
}

data class SourceStatus(
    val state: SourceState,
    val detail: String,
)

/**
 * The sources this build can offer.
 *
 * Kept as one list so the UI does not have to know which are compiled in: it
 * shows a button per entry and reports honestly when there is nothing to
 * connect to.
 */
object CanSourceFactory {

    /**
     * A hardware source, when one has been added to this build.
     *
     * The published build has none — see the "Adding a hardware source" note on
     * [CanSource]. Returning null here is what makes the viewer say so plainly
     * rather than appear to connect and then sit at WAITING forever.
     */
    fun createHardwareSource(context: android.content.Context): CanSource? = null

    /** True when [createHardwareSource] can return something. */
    fun hasHardwareSource(context: android.content.Context): Boolean =
        createHardwareSource(context) != null

    const val NO_HARDWARE_SOURCE_MESSAGE: String =
        "This build has no CAN hardware source compiled in. Add one by " +
            "implementing CanSource and returning it from " +
            "CanSourceFactory.createHardwareSource(); see the notes on CanSource."
}
