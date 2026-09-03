package jp.co.canreplay.candecoder

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The counters the Debug tab shows so a frame loss can be localised to a link
 * (phase 2 requirement 3).
 *
 * The comparison the exhibition actually needs is:
 *   Windows "frames sent"  ->  Spresense recv_count  ->  Android "raw CAN received"
 * so these have to count raw frames received, not decode successes, and they have
 * to stay correct when a scenario contains IDs the DBC does not define.
 */
class EndToEndCountersTest {

    private fun tracker() = VehicleStateTracker(SignalDatabase.parse(TestSignals.JSON))

    private fun wheelSpeeds(atMs: Long, rawFr: Int = 6666) = RawCanFrame(
        canId = 0xAA, extended = false,
        data = byteArrayOf(((rawFr shr 8) and 0x7F).toByte(), (rawFr and 0xFF).toByte(),
            0, 0, 0, 0, 0, 0),
        receivedAtMs = atMs,
    )

    @Test
    fun `raw received counts every frame including ones with no definition`() {
        val tracker = tracker()
        repeat(10) { tracker.onFrame(wheelSpeeds(1_000L + it)) }
        repeat(5) { tracker.onFrame(RawCanFrame(0x3F9, false, byteArrayOf(1, 2, 3), 1_020L + it)) }

        val snapshot = tracker.snapshot(1_030)
        assertEquals(15, snapshot.totalFrames)
        assertEquals(10, snapshot.decodedFrames)
        assertEquals(5, snapshot.undecodedFrames)
    }

    @Test
    fun `decoded signal updates count values written, not distinct signals`() {
        val tracker = tracker()
        // WHEEL_SPEEDS has three signals in the fixture, and an 8 byte frame
        // carries all of them.
        repeat(10) { tracker.onFrame(wheelSpeeds(1_000L + it)) }

        val snapshot = tracker.snapshot(1_020)
        assertEquals(30, snapshot.decodedSignalUpdates)
        assertEquals(3, snapshot.signals.size)
    }

    @Test
    fun `frames with no definition contribute no signal updates`() {
        val tracker = tracker()
        repeat(20) { tracker.onFrame(RawCanFrame(0x3F9, false, byteArrayOf(1, 2), 1_000L + it)) }

        val snapshot = tracker.snapshot(1_030)
        assertEquals(20, snapshot.totalFrames)
        assertEquals(0, snapshot.decodedSignalUpdates)
    }

    @Test
    fun `frame rate reflects the recent arrival rate`() {
        val tracker = tracker()
        // 1 kHz for a second.
        repeat(1_000) { tracker.onFrame(wheelSpeeds(10_000L + it)) }

        val rate = tracker.snapshot(11_000).framesPerSecond
        assertTrue(rate in 900.0..1_100.0, "expected about 1000 frames/s, got $rate")
    }

    @Test
    fun `frame rate drops to zero once traffic stops`() {
        val tracker = tracker()
        repeat(500) { tracker.onFrame(wheelSpeeds(10_000L + it)) }

        assertTrue(tracker.snapshot(10_500).framesPerSecond > 100)
        // Past the stream timeout the rate must read 0, not the rate it had.
        assertEquals(0.0, tracker.snapshot(11_000).framesPerSecond)
    }

    @Test
    fun `frame rate is zero before anything arrives`() {
        assertEquals(0.0, tracker().snapshot(1_000).framesPerSecond)
    }

    @Test
    fun `elapsed spans first to last frame`() {
        val tracker = tracker()
        tracker.onFrame(wheelSpeeds(5_000))
        tracker.onFrame(wheelSpeeds(5_750))

        val snapshot = tracker.snapshot(5_800)
        assertEquals(750, snapshot.elapsedMs)
        assertEquals(5_000, snapshot.firstFrameAtMs)
        assertEquals(5_750, snapshot.lastFrameAtMs)
    }

    @Test
    fun `reset zeroes every counter so a timed test starts clean`() {
        val tracker = tracker()
        repeat(100) { tracker.onFrame(wheelSpeeds(1_000L + it)) }
        tracker.reset()

        val snapshot = tracker.snapshot(1_200)
        assertEquals(0, snapshot.totalFrames)
        assertEquals(0, snapshot.decodedSignalUpdates)
        assertEquals(0.0, snapshot.framesPerSecond)
        assertEquals(0, snapshot.elapsedMs)
        assertEquals(null, snapshot.firstFrameAtMs)
        assertEquals(StreamState.WAITING, snapshot.streamState)
    }

    @Test
    fun `counters survive a full minute at the rate the RAV4 scenarios replay at`() {
        // ~1,100 frames/s for 60 s is what bus 0 of the example segment produces.
        val tracker = tracker()
        var now = 1_000L
        repeat(66_000) {
            tracker.onFrame(wheelSpeeds(now))
            if (it % 1_100 == 0) now += 1 else now += if (it % 2 == 0) 1 else 0
        }

        val snapshot = tracker.snapshot(now)
        assertEquals(66_000, snapshot.totalFrames)
        assertEquals(66_000 * 3, snapshot.decodedSignalUpdates)
        assertEquals(StreamState.LIVE, snapshot.streamState)
    }
}
