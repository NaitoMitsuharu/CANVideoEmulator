package jp.co.canreplay.candecoder

import java.io.File
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNotNull
import kotlin.test.assertTrue

/**
 * Decodes a real built Scenario Package with the Android decoder and checks the
 * result against the figures the Windows-side builder recorded in validation.json.
 *
 * This is the join between the two implementations: the builder proved its own
 * Python decode matches comma2k19's processed_log, and this proves the Kotlin
 * decode matches the builder. Without it, the two could drift and nothing would
 * notice until the exhibition showed wrong numbers.
 *
 * Skips itself when no scenario has been built, so a fresh checkout still passes.
 */
class ScenarioEndToEndTest {

    private val scenario: File? = sequenceOf(
        System.getenv("CANREPLAY_SCENARIO_DIR"),
        "../../Scenarios/rav4_001",
        "../Scenarios/rav4_001",
    ).filterNotNull()
        .map { File(it) }
        .firstOrNull { it.isDirectory && File(it, "scenario.json").isFile }

    private fun load(): Triple<SignalDatabase, List<CanBinReader.Entry>, File>? {
        val directory = scenario ?: return null
        val database = File(directory, "dbc/signals.json")
            .readText().let { SignalDatabase.parse(it) }
        val frames = File(directory, "can/bus_0.canbin")
            .inputStream().use { CanBinReader.read(it) }
        return Triple(database, frames, directory)
    }

    @Test
    fun `the built package's signals json parses`() {
        val loaded = load() ?: return
        val (database, _, _) = loaded

        assertEquals("toyota_rav4_2017", database.profile)
        assertEquals("Toyota RAV4", database.vehicle)
        assertEquals(0, database.bus)
        assertTrue(database.messages.isNotEmpty())
        assertTrue(database.undecodedCanIds.isNotEmpty(),
            "requirement 55: IDs with no DBC entry must still be listed")
    }

    @Test
    fun `the built package's CAN timeline reads back intact`() {
        val loaded = load() ?: return
        val (_, frames, _) = loaded

        assertTrue(frames.size > 10_000, "expected a full minute of CAN, got ${frames.size}")
        assertTrue(frames.all { it.data.size <= 8 }, "classical CAN only")
        assertTrue(frames.zipWithNext().all { (a, b) -> a.offsetMs <= b.offsetMs },
            "frames must be time-ordered")
        assertTrue(frames.any { it.txEcho },
            "the RAV4 recordings contain openpilot's own transmissions")
    }

    @Test
    fun `every profile mapping resolves against the real definitions`() {
        val loaded = load() ?: return
        val (database, _, _) = loaded

        val missing = VehicleProfile.TOYOTA_RAV4.allMappings.filter {
            database.findSignal(it.messageName, it.signalName) == null
        }
        assertTrue(missing.isEmpty(),
            "profile names signals the shipped definitions do not contain: " +
                missing.joinToString { "${it.messageName}.${it.signalName}" })
    }

    @Test
    fun `decoding the real timeline reproduces the recorded vehicle state`() {
        val loaded = load() ?: return
        val (database, frames, _) = loaded

        val tracker = VehicleStateTracker(database)
        val speeds = mutableListOf<Double>()
        val angles = mutableListOf<Double>()

        for (entry in frames) {
            tracker.onFrame(RawCanFrame(entry.canId, entry.extended, entry.data,
                receivedAtMs = entry.offsetMs))
            tracker.signal("WHEEL_SPEEDS", "WHEEL_SPEED_FL")
                ?.takeIf { it.updatedAtMs == entry.offsetMs }
                ?.let { speeds.add(it.value) }
            tracker.signal("STEER_ANGLE_SENSOR", "STEER_ANGLE")
                ?.takeIf { it.updatedAtMs == entry.offsetMs }
                ?.let { angles.add(it.value) }
        }

        assertTrue(speeds.size > 1_000, "expected many speed samples, got ${speeds.size}")
        assertTrue(angles.size > 1_000, "expected many steering samples, got ${angles.size}")

        // The comma2k19 example segment is ~60 s of highway driving; the builder
        // measured a median wheel speed of 62.93 km/h against processed_log.
        val median = speeds.sorted()[speeds.size / 2]
        assertTrue(median in 55.0..70.0,
            "median wheel speed $median km/h is outside the range this segment records")

        // Physically plausible bounds: a decode that mis-reads bit positions
        // produces values far outside these long before it produces a wrong median.
        assertTrue(speeds.all { it in -5.0..200.0 }, "wheel speed out of physical range")
        assertTrue(angles.all { it in -500.0..500.0 }, "steering angle out of DBC range")
    }

    @Test
    fun `decoded values agree with the builder's validation report`() {
        val loaded = load() ?: return
        val (database, frames, directory) = loaded

        val report = File(directory, "validation.json").takeIf { it.isFile }?.readText() ?: return
        // Read the selected profile's wheel-speed RMSE without pulling in a JSON
        // dependency the library does not otherwise need.
        val rmse = Regex("\"reference\": \"wheel_speed\"[\\s\\S]*?\"rmse\": ([0-9.eE-]+)")
            .find(report)?.groupValues?.get(1)?.toDouble()
        assertNotNull(rmse, "validation.json has no wheel_speed comparison")
        assertTrue(rmse < 0.05,
            "the builder's own wheel-speed RMSE was $rmse m/s, which is too high " +
                "for this test's premise to hold")

        // The builder validated in m/s; this decoder yields km/h. If the two
        // agree, converting the Kotlin median must land near the builder's mean.
        val tracker = VehicleStateTracker(database)
        val speedsMs = mutableListOf<Double>()
        for (entry in frames) {
            tracker.onFrame(RawCanFrame(entry.canId, entry.extended, entry.data, entry.offsetMs))
            tracker.signal("WHEEL_SPEEDS", "WHEEL_SPEED_FL")
                ?.takeIf { it.updatedAtMs == entry.offsetMs }
                ?.let { speedsMs.add(it.value / 3.6) }
        }

        val meanDecoded = speedsMs.average()
        val meanReference = Regex("\"reference\": \"wheel_speed\"[\\s\\S]*?\"mean_reference\": ([0-9.eE-]+)")
            .find(report)?.groupValues?.get(1)?.toDouble()
        assertNotNull(meanReference)
        assertTrue(kotlin.math.abs(meanDecoded - meanReference) < 0.5,
            "Kotlin decode averaged $meanDecoded m/s but comma2k19's processed_log " +
                "averaged $meanReference m/s")
    }

    @Test
    fun `the transition gap in a real timeline produces STALE`() {
        val loaded = load() ?: return
        val (database, frames, _) = loaded

        val tracker = VehicleStateTracker(database)
        frames.take(5_000).forEach {
            tracker.onFrame(RawCanFrame(it.canId, it.extended, it.data, it.offsetMs))
        }

        val last = frames[4_999].offsetMs
        assertEquals(StreamState.LIVE, tracker.streamState(last + 50))
        // The player's default inter-scenario gap is 350 ms.
        assertEquals(StreamState.STALE, tracker.streamState(last + 350))
    }
}
