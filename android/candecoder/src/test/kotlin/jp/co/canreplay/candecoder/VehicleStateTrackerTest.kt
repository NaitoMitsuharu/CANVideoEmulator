package jp.co.canreplay.candecoder

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SignalDatabaseTest {

    @Test
    fun `parses the document the builder emits`() {
        val database = SignalDatabase.parse(TestSignals.JSON)

        assertEquals(1, database.formatVersion)
        assertEquals("toyota_rav4_2017", database.profile)
        assertEquals(0, database.bus)
        assertEquals(3, database.messages.size)
        assertEquals(6, database.signalCount)
        assertEquals(listOf("toyota_new_mc_pt_generated.dbc"), database.dbcFiles)
    }

    @Test
    fun `messages are indexed by CAN id`() {
        val database = SignalDatabase.parse(TestSignals.JSON)
        assertEquals("WHEEL_SPEEDS", database.message(0xAA)?.name)
        assertEquals("GEAR_PACKET", database.message(0x3BC)?.name)
        assertNull(database.message(0x999))
    }

    @Test
    fun `cycle time and its source are carried through`() {
        val message = SignalDatabase.parse(TestSignals.JSON).message(0xAA)!!
        assertEquals(12, message.cycleTimeMs)
        assertEquals("observed", message.cycleTimeSource)
    }

    @Test
    fun `undecoded ids are preserved for the Raw CAN tab`() {
        val database = SignalDatabase.parse(TestSignals.JSON)
        assertEquals(1, database.undecodedCanIds.size)
        assertEquals(0x3F9, database.undecodedCanIds[0].canId)
    }

    @Test
    fun `an unsupported format version is refused rather than misread`() {
        val json = TestSignals.JSON.replace("\"format_version\": 1", "\"format_version\": 99")
        val error = assertFailsWith<IllegalArgumentException> { SignalDatabase.parse(json) }
        assertTrue(error.message!!.contains("format_version 99"))
    }

    @Test
    fun `signal geometry survives the round trip`() {
        val (_, signal) = SignalDatabase.parse(TestSignals.JSON)
            .findSignal("WHEEL_SPEEDS", "WHEEL_SPEED_FR")!!

        assertEquals(6, signal.startBit)
        assertEquals(15, signal.length)
        assertEquals(ByteOrder.BIG_ENDIAN, signal.byteOrder)
        assertFalse(signal.signed)
        assertEquals(0.01, signal.factor, 1e-12)
        assertEquals(-67.67, signal.offset, 1e-12)
        assertEquals("km/h", signal.unit)
    }

    @Test
    fun `required byte count is computed for both byte orders`() {
        val database = SignalDatabase.parse(TestSignals.JSON)
        // 6|15 big endian: 7 bits from byte 0 (bits 6..0) then 8 from byte 1,
        // so it ends inside byte 1 and needs 2 bytes.
        assertEquals(2, database.findSignal("WHEEL_SPEEDS", "WHEEL_SPEED_FR")!!.second.requiredBytes)
        // 38|15 big endian: 7 bits from byte 4 then 8 from byte 5 -> 6 bytes.
        assertEquals(6, database.findSignal("WHEEL_SPEEDS", "WHEEL_SPEED_RR")!!.second.requiredBytes)
    }
}

class VehicleStateTrackerTest {

    private fun tracker() = VehicleStateTracker(SignalDatabase.parse(TestSignals.JSON))

    /**
     * WHEEL_SPEED_FR is 6|15 big endian: its top 7 bits sit in byte 0 (bits 6..0)
     * and its low 8 bits in byte 1.
     */
    private fun wheelSpeeds(atMs: Long, rawFr: Int = 6666) = RawCanFrame(
        canId = 0xAA, extended = false,
        data = byteArrayOf(((rawFr shr 8) and 0x7F).toByte(), (rawFr and 0xFF).toByte(),
            0, 0, 0, 0, 0, 0),
        receivedAtMs = atMs,
    )

    @Test
    fun `starts in WAITING before any frame arrives`() {
        assertEquals(StreamState.WAITING, tracker().streamState(1_000))
    }

    @Test
    fun `becomes LIVE while frames arrive`() {
        val tracker = tracker()
        tracker.onFrame(wheelSpeeds(1_000))
        assertEquals(StreamState.LIVE, tracker.streamState(1_100))
    }

    @Test
    fun `goes STALE when traffic stops`() {
        val tracker = tracker()
        tracker.onFrame(wheelSpeeds(1_000))

        assertEquals(StreamState.LIVE, tracker.streamState(1_250))
        assertEquals(StreamState.STALE, tracker.streamState(1_251))
    }

    @Test
    fun `the transition gap between scenarios is long enough to detect`() {
        // The player's default gap is 350 ms; the detector must fire inside it.
        val tracker = tracker()
        tracker.onFrame(wheelSpeeds(1_000))
        assertEquals(StreamState.STALE, tracker.streamState(1_000 + 350))
    }

    @Test
    fun `returns to LIVE when the next scenario starts`() {
        val tracker = tracker()
        tracker.onFrame(wheelSpeeds(1_000))
        assertEquals(StreamState.STALE, tracker.streamState(1_400))

        tracker.onFrame(wheelSpeeds(1_400))
        assertEquals(StreamState.LIVE, tracker.streamState(1_450))
    }

    @Test
    fun `decoded signals are exposed and updated`() {
        val tracker = tracker()
        tracker.onFrame(RawCanFrame(0xAA, false,
            byteArrayOf(0x1A, 0x0A, 0, 0, 0, 0, 0, 0), 1_000))

        val signal = tracker.signal("WHEEL_SPEEDS", "WHEEL_SPEED_FR")
        assertNotNull(signal)
        assertEquals(-1.01, signal.value, 1e-9)
    }

    @Test
    fun `a stale value is not offered as live`() {
        val tracker = tracker()
        tracker.onFrame(RawCanFrame(0xAA, false,
            byteArrayOf(0x1A, 0x0A, 0, 0, 0, 0, 0, 0), 1_000))

        assertNotNull(tracker.snapshot(1_050).liveValue("WHEEL_SPEEDS", "WHEEL_SPEED_FR"))
        // Past the whole-stream timeout: nothing is live any more.
        assertNull(tracker.snapshot(1_500).liveValue("WHEEL_SPEEDS", "WHEEL_SPEED_FR"))
        // ...but the last value is still available for a dimmed display.
        assertNotNull(tracker.snapshot(1_500).signal("WHEEL_SPEEDS", "WHEEL_SPEED_FR"))
    }

    @Test
    fun `undecodable frames are counted and kept, not dropped`() {
        val tracker = tracker()
        tracker.onFrame(wheelSpeeds(1_000))
        tracker.onFrame(RawCanFrame(0x3F9, false, byteArrayOf(1, 2, 3), 1_010))

        val snapshot = tracker.snapshot(1_020)
        assertEquals(2, snapshot.totalFrames)
        assertEquals(1, snapshot.undecodedFrames)
        assertEquals(1, snapshot.decodedFrames)

        val unknown = snapshot.activity.first { it.canId == 0x3F9 }
        assertFalse(unknown.isDecodable)
        assertEquals("010203", unknown.lastFrame.dataHex())
    }

    @Test
    fun `per-id counts and rates are tracked`() {
        val tracker = tracker()
        repeat(101) { tracker.onFrame(wheelSpeeds(1_000L + it * 10)) }

        val activity = tracker.snapshot(3_000).activity.first { it.canId == 0xAA }
        assertEquals(101, activity.count)
        assertEquals("WHEEL_SPEEDS", activity.name)
        assertTrue(activity.isDecodable)
        assertEquals(100.0, activity.ratePerSecond, 1.0)
    }

    @Test
    fun `recent frames are newest first and bounded`() {
        val tracker = tracker()
        repeat(VehicleStateTracker.RECENT_CAPACITY + 50) {
            tracker.onFrame(wheelSpeeds(1_000L + it))
        }

        val recent = tracker.snapshot(2_000).recentFrames
        assertEquals(VehicleStateTracker.RECENT_CAPACITY, recent.size)
        assertTrue(recent[0].receivedAtMs > recent[1].receivedAtMs)
    }

    @Test
    fun `a high frame rate does not lose the stream state`() {
        // The RAV4 recordings replay at about 1,100 frames per second.
        val tracker = tracker()
        var now = 1_000L
        repeat(11_000) {
            tracker.onFrame(wheelSpeeds(now))
            now += 1
        }

        val snapshot = tracker.snapshot(now)
        assertEquals(StreamState.LIVE, snapshot.streamState)
        assertEquals(11_000, snapshot.totalFrames)
        assertEquals(VehicleStateTracker.RECENT_CAPACITY, snapshot.recentFrames.size)
    }

    @Test
    fun `reset clears everything`() {
        val tracker = tracker()
        tracker.onFrame(wheelSpeeds(1_000))
        tracker.reset()

        val snapshot = tracker.snapshot(1_100)
        assertEquals(StreamState.WAITING, snapshot.streamState)
        assertEquals(0, snapshot.totalFrames)
        assertTrue(snapshot.signals.isEmpty())
    }
}

class VehicleProfileTest {

    @Test
    fun `the RAV4 profile only names signals the shipped DBC defines`() {
        val database = SignalDatabase.parse(TestSignals.JSON)
        val profile = VehicleProfile.TOYOTA_RAV4

        // Of the mappings this cut-down fixture covers, each must resolve.
        for (mapping in listOfNotNull(profile.speed, profile.steering, profile.gear)) {
            assertNotNull(database.findSignal(mapping.messageName, mapping.signalName),
                "${mapping.messageName}.${mapping.signalName} is not in the database")
        }
    }

    @Test
    fun `a gauge with no data reads as unknown, never as zero`() {
        val tracker = VehicleStateTracker(SignalDatabase.parse(TestSignals.JSON))
        val reading = tracker.snapshot(1_000).read(VehicleProfile.TOYOTA_RAV4.speed)!!

        assertFalse(reading.hasValue)
        assertEquals("---", reading.display())
        assertNull(reading.fraction)
    }

    @Test
    fun `a live gauge reports its value and position in range`() {
        val tracker = VehicleStateTracker(SignalDatabase.parse(TestSignals.JSON))
        // WHEEL_SPEED_FL is 22|15 big endian: top 7 bits in byte 2, low 8 in
        // byte 3. Raw 15767 -> 15767 * 0.01 - 67.67 = 90.00 km/h.
        val raw = Math.round((90.0 + 67.67) / 0.01).toInt()
        val data = ByteArray(8)
        data[2] = ((raw shr 8) and 0x7F).toByte()
        data[3] = (raw and 0xFF).toByte()
        tracker.onFrame(RawCanFrame(0xAA, false, data, 1_000))

        val reading = tracker.snapshot(1_010).read(VehicleProfile.TOYOTA_RAV4.speed)!!
        assertTrue(reading.hasValue)
        assertEquals(90.0, reading.value!!, 0.01)
        assertEquals(0.5, reading.fraction!!, 0.01)   // 90 of 0..180
        assertEquals("90", reading.display())
    }

    @Test
    fun `a stale gauge shows dashes but keeps the last value for a dimmed display`() {
        val tracker = VehicleStateTracker(SignalDatabase.parse(TestSignals.JSON))
        tracker.onFrame(RawCanFrame(0xAA, false,
            byteArrayOf(0, 0, 0x1A, 0x0A, 0, 0, 0, 0), 1_000))

        val reading = tracker.snapshot(2_000).read(VehicleProfile.TOYOTA_RAV4.speed)!!
        assertFalse(reading.hasValue)
        assertEquals("---", reading.display())
        assertNotNull(reading.lastKnownDisplay())
    }

    @Test
    fun `gear renders its enum label`() {
        val tracker = VehicleStateTracker(SignalDatabase.parse(TestSignals.JSON))
        tracker.onFrame(RawCanFrame(0x3BC, false,
            byteArrayOf(0, 0x20, 0, 0, 0, 0, 0, 0), 1_000))

        val reading = tracker.snapshot(1_010).read(VehicleProfile.TOYOTA_RAV4.gear)!!
        assertEquals("P", reading.display())
    }

    @Test
    fun `profiles are looked up by id with a safe fallback`() {
        assertEquals("toyota_rav4_2017", VehicleProfile.byId("toyota_rav4_2017").id)
        assertEquals("toyota_rav4_2017", VehicleProfile.byId("nonexistent").id)
    }
}
