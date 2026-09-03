package jp.co.canreplay.candecoder

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.assertFalse

/**
 * Bit-extraction tests (requirement 67).
 *
 * The reference vectors are the real Toyota RAV4 definitions the scenarios ship,
 * so a regression here means the phone would show wrong vehicle values:
 *   WHEEL_SPEEDS.WHEEL_SPEED_FR : 6|15@0+ (0.01,-67.67) "km/h"   big endian
 *   STEER_ANGLE_SENSOR.STEER_ANGLE : 3|12@0- (1.5,0) "deg"       big endian, signed
 */
class CanDecoderTest {

    // --- bit numbering ---------------------------------------------------

    @Test
    fun `bit zero is the least significant bit of byte zero`() {
        val data = byteArrayOf(0b0000_0001, 0, 0, 0, 0, 0, 0, 0)
        assertEquals(1L, CanDecoder.extract(data, 0, 1, ByteOrder.LITTLE_ENDIAN, false))
        assertEquals(0L, CanDecoder.extract(data, 1, 1, ByteOrder.LITTLE_ENDIAN, false))
    }

    @Test
    fun `bit seven is the most significant bit of byte zero`() {
        val data = byteArrayOf(0b1000_0000.toByte(), 0, 0, 0, 0, 0, 0, 0)
        assertEquals(1L, CanDecoder.extract(data, 7, 1, ByteOrder.LITTLE_ENDIAN, false))
    }

    // --- little endian ---------------------------------------------------

    @Test
    fun `little endian unsigned spans bytes low to high`() {
        val data = byteArrayOf(0x34, 0x12, 0, 0, 0, 0, 0, 0)
        assertEquals(0x1234L, CanDecoder.extract(data, 0, 16, ByteOrder.LITTLE_ENDIAN, false))
    }

    @Test
    fun `little endian signed sign-extends`() {
        val data = byteArrayOf(0xFF.toByte(), 0xFF.toByte(), 0, 0, 0, 0, 0, 0)
        assertEquals(-1L, CanDecoder.extract(data, 0, 16, ByteOrder.LITTLE_ENDIAN, true))
        assertEquals(65535L, CanDecoder.extract(data, 0, 16, ByteOrder.LITTLE_ENDIAN, false))
    }

    @Test
    fun `little endian at an unaligned start bit`() {
        // Bits 4..11 of 0x5A 0x03 = 0x35.
        val data = byteArrayOf(0x5A, 0x03, 0, 0, 0, 0, 0, 0)
        assertEquals(0x35L, CanDecoder.extract(data, 4, 8, ByteOrder.LITTLE_ENDIAN, false))
    }

    // --- big endian ------------------------------------------------------

    @Test
    fun `big endian reads forwards through the bytes`() {
        val data = byteArrayOf(0xAB.toByte(), 0xCD.toByte(), 0, 0, 0, 0, 0, 0)
        assertEquals(0xABCDL, CanDecoder.extract(data, 7, 16, ByteOrder.BIG_ENDIAN, false))
    }

    @Test
    fun `big endian signed sign-extends`() {
        val data = byteArrayOf(0x80.toByte(), 0, 0, 0, 0, 0, 0, 0)
        assertEquals(-1L, CanDecoder.extract(data, 7, 1, ByteOrder.BIG_ENDIAN, true))
        assertEquals(1L, CanDecoder.extract(data, 7, 1, ByteOrder.BIG_ENDIAN, false))
    }

    @Test
    fun `real wheel speed signal decodes to the documented value`() {
        // WHEEL_SPEED_FR : 6|15@0+ (0.01,-67.67). Raw 6666 -> -1.01 km/h.
        val data = byteArrayOf(0x1A, 0x0A, 0, 0, 0, 0, 0, 0)
        val raw = CanDecoder.extract(data, 6, 15, ByteOrder.BIG_ENDIAN, false)
        assertEquals(6666L, raw)
        assertEquals(-1.01, raw * 0.01 - 67.67, 1e-9)
    }

    @Test
    fun `real steering angle signal decodes negative values`() {
        // STEER_ANGLE : 3|12@0- (1.5,0). Raw -2 -> -3.0 deg.
        val raw12 = (-2) and 0xFFF
        val data = byteArrayOf(((raw12 shr 8) and 0x0F).toByte(), (raw12 and 0xFF).toByte(),
            0, 0, 0, 0, 0, 0)
        val raw = CanDecoder.extract(data, 3, 12, ByteOrder.BIG_ENDIAN, true)
        assertEquals(-2L, raw)
        assertEquals(-3.0, raw * 1.5, 1e-9)
    }

    // --- bounds ----------------------------------------------------------

    @Test
    fun `a frame too short to hold the signal is rejected`() {
        assertFailsWith<IllegalArgumentException> {
            CanDecoder.extract(byteArrayOf(0, 0), 0, 32, ByteOrder.LITTLE_ENDIAN, false)
        }
    }

    @Test
    fun `zero length is rejected`() {
        assertFailsWith<IllegalArgumentException> {
            CanDecoder.extract(ByteArray(8), 0, 0, ByteOrder.LITTLE_ENDIAN, false)
        }
    }

    @Test
    fun `full width unsigned value does not overflow into the sign`() {
        val data = ByteArray(8) { 0xFF.toByte() }
        assertEquals(-1L, CanDecoder.extract(data, 0, 64, ByteOrder.LITTLE_ENDIAN, true))
        assertEquals(0xFFFFFFFFL,
            CanDecoder.extract(data, 0, 32, ByteOrder.LITTLE_ENDIAN, false))
    }

    // --- factor / offset / enum -------------------------------------------

    @Test
    fun `factor and offset are applied to the raw value`() {
        val database = SignalDatabase.parse(TestSignals.JSON)
        val message = database.message(0xAA)!!
        val frame = RawCanFrame(0xAA, false, byteArrayOf(0x1A, 0x0A, 0, 0, 0, 0, 0, 0), 1000)

        val decoded = CanDecoder.decode(message, frame).first { it.signalName == "WHEEL_SPEED_FR" }
        assertEquals(6666L, decoded.raw)
        assertEquals(-1.01, decoded.value, 1e-9)
        assertEquals("km/h", decoded.unit)
    }

    @Test
    fun `value tables map raw values to labels`() {
        val database = SignalDatabase.parse(TestSignals.JSON)
        val message = database.message(0x3BC)!!
        val decoded = CanDecoder.decode(message,
            RawCanFrame(0x3BC, false, byteArrayOf(0, 0x20, 0, 0, 0, 0, 0, 0), 1000))

        val gear = decoded.first { it.signalName == "GEAR" }
        assertEquals(32L, gear.raw)
        assertEquals("P", gear.label)
        assertEquals("P", gear.display())
    }

    @Test
    fun `a raw value with no table entry has no label`() {
        val database = SignalDatabase.parse(TestSignals.JSON)
        val message = database.message(0x3BC)!!
        val decoded = CanDecoder.decode(message,
            RawCanFrame(0x3BC, false, byteArrayOf(0, 0x14, 0, 0, 0, 0, 0, 0), 1000))
        assertNull(decoded.first { it.signalName == "GEAR" }.label)
    }

    @Test
    fun `signals that do not fit a short frame are skipped not thrown`() {
        val database = SignalDatabase.parse(TestSignals.JSON)
        val message = database.message(0xAA)!!
        val decoded = CanDecoder.decode(message,
            RawCanFrame(0xAA, false, byteArrayOf(0x1A, 0x0A, 0x00), 1000))

        assertTrue(decoded.any { it.signalName == "WHEEL_SPEED_FR" })
        assertFalse(decoded.any { it.signalName == "WHEEL_SPEED_RR" })
    }

    // --- staleness ---------------------------------------------------------

    @Test
    fun `stale timeout is three cycles with a floor`() {
        assertEquals(300L, CanDecoder.staleTimeout(100))
        assertEquals(150L, CanDecoder.staleTimeout(10))     // floor applies
        assertEquals(150L, CanDecoder.staleTimeout(50))
        assertEquals(180L, CanDecoder.staleTimeout(60))
    }

    @Test
    fun `a signal goes stale after its timeout`() {
        val signal = DecodedSignal("M", "S", 1, 1.0, "", null,
            updatedAtMs = 1_000, staleAfterMs = 300)

        assertFalse(signal.isStale(1_200))
        assertFalse(signal.isStale(1_300))
        assertTrue(signal.isStale(1_301))
    }

    @Test
    fun `raw frame formatting`() {
        val frame = RawCanFrame(0xAA, false, byteArrayOf(0x01, 0x0F, 0xFF.toByte()), 0)
        assertEquals("0x0AA", frame.idText())
        assertEquals("010FFF", frame.dataHex())
        assertEquals(3, frame.dlc)

        val extended = RawCanFrame(0x18DAF110, true, ByteArray(0), 0)
        assertEquals("0x18DAF110", extended.idText())
        assertEquals(0, extended.dlc)
    }
}
