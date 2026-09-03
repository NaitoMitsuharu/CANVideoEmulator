package jp.co.canreplay.viewer.source

import android.os.SystemClock
import jp.co.canreplay.candecoder.RawCanFrame
import jp.co.canreplay.candecoder.CanBinReader
import java.io.InputStream
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.concurrent.thread

/**
 * Replays a `.canbin` timeline from app assets, for development and for showing
 * the screens without a CAN bridge attached.
 *
 * It reads the same binary format the Scenario Builder writes and the Windows
 * player transmits, so what the UI sees here is byte-for-byte what it will see
 * over the real bus -- including the gaps, which is what makes the STALE
 * handling testable off-hardware.
 */
class ReplayCanSource(
    private val open: () -> InputStream,
    private val label: String = "Replay (asset)",
    /** Silence inserted at the end of each pass, mimicking the player's gap. */
    private val loopGapMs: Long = 350,
    private val loop: Boolean = true,
) : CanSource {

    private val running = AtomicBoolean(false)
    private var worker: Thread? = null

    @Volatile
    private var currentStatus = SourceStatus(SourceState.DISCONNECTED, "not started")

    override val name: String get() = label

    override val status: SourceStatus get() = currentStatus

    override fun start(onFrame: (RawCanFrame) -> Unit) {
        if (!running.compareAndSet(false, true)) return

        currentStatus = SourceStatus(SourceState.CONNECTING, "loading timeline")
        worker = thread(name = "ReplayCanSource", isDaemon = true) {
            try {
                val frames = open().use { CanBinReader.read(it) }
                currentStatus = SourceStatus(SourceState.CONNECTED,
                    "${frames.size} frames from $label")

                do {
                    val origin = SystemClock.elapsedRealtime()
                    for (frame in frames) {
                        if (!running.get()) return@thread

                        // Absolute deadlines, like the Windows scheduler: sleeping
                        // for each gap in turn would accumulate every overshoot.
                        val deadline = origin + frame.offsetMs
                        val wait = deadline - SystemClock.elapsedRealtime()
                        if (wait > 0) Thread.sleep(wait)

                        onFrame(RawCanFrame(frame.canId, frame.extended, frame.data,
                            SystemClock.elapsedRealtime()))
                    }
                    if (loop && running.get()) Thread.sleep(loopGapMs)
                } while (loop && running.get())

                currentStatus = SourceStatus(SourceState.DISCONNECTED, "timeline finished")
            } catch (interrupted: InterruptedException) {
                Thread.currentThread().interrupt()
            } catch (error: Exception) {
                currentStatus = SourceStatus(SourceState.ERROR,
                    "replay failed: ${error.message}")
            } finally {
                running.set(false)
            }
        }
    }

    override fun stop() {
        running.set(false)
        worker?.interrupt()
        worker = null
        currentStatus = SourceStatus(SourceState.DISCONNECTED, "stopped")
    }
}
