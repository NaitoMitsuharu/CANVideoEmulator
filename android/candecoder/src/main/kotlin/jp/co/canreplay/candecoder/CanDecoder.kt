package jp.co.canreplay.candecoder

/** One classical CAN frame as received from the Spresense. */
data class RawCanFrame(
    val canId: Int,
    val extended: Boolean,
    val data: ByteArray,
    /** Arrival time on a monotonic clock, milliseconds. */
    val receivedAtMs: Long,
) {
    val dlc: Int get() = data.size

    fun idText(): String =
        if (extended) "0x%08X".format(canId) else "0x%03X".format(canId)

    fun dataHex(): String = data.joinToString("") { "%02X".format(it) }

    // Generated equals/hashCode would compare the array by identity.
    override fun equals(other: Any?): Boolean =
        other is RawCanFrame && canId == other.canId && extended == other.extended &&
            receivedAtMs == other.receivedAtMs && data.contentEquals(other.data)

    override fun hashCode(): Int =
        (((canId * 31 + extended.hashCode()) * 31) + data.contentHashCode()) * 31 +
            receivedAtMs.hashCode()
}

/** A decoded signal value at one instant. */
data class DecodedSignal(
    val messageName: String,
    val signalName: String,
    val raw: Long,
    val value: Double,
    val unit: String,
    /** Label from the DBC's value table, when the raw value has one. */
    val label: String?,
    val updatedAtMs: Long,
    /** Milliseconds after [updatedAtMs] at which this value becomes stale. */
    val staleAfterMs: Long,
) {
    fun isStale(nowMs: Long): Boolean = nowMs - updatedAtMs > staleAfterMs

    /** Text for the UI: the enum label if there is one, otherwise the number. */
    fun display(decimals: Int = 2): String = label ?: formatNumber(value, decimals)

    companion object {
        fun formatNumber(value: Double, decimals: Int): String =
            if (decimals <= 0) value.toLong().toString() else "%.${decimals}f".format(value)
    }
}

/**
 * Extracts signals from raw CAN frames using the generated definitions.
 *
 * The bit numbering matches the DBC convention exactly, and is verified against
 * the same reference vectors as the Python and C# implementations: bit *n* is
 * bit `n % 8` of byte `n / 8`, so bit 7 is the most significant bit of byte 0.
 * For little-endian signals `start_bit` is the least significant bit; for
 * big-endian signals it is the most significant bit and the signal walks
 * forwards through the bytes.
 */
object CanDecoder {

    /**
     * Extract the raw integer value of one signal.
     *
     * @throws IllegalArgumentException if [data] is too short to contain it.
     */
    fun extract(
        data: ByteArray,
        startBit: Int,
        length: Int,
        byteOrder: ByteOrder,
        signed: Boolean,
    ): Long {
        require(length in 1..64) { "signal length must be 1..64, got $length" }

        var value = 0L
        when (byteOrder) {
            ByteOrder.LITTLE_ENDIAN ->
                for (weight in 0 until length) {
                    if (bitAt(data, startBit + weight)) value = value or (1L shl weight)
                }

            ByteOrder.BIG_ENDIAN -> {
                // Collect most-significant bit first, walking forwards through
                // the bytes and downwards through the bits within each byte.
                var byte = startBit / 8
                var bit = startBit % 8
                val positions = IntArray(length)
                for (i in 0 until length) {
                    positions[i] = byte * 8 + bit
                    if (bit == 0) {
                        byte += 1
                        bit = 7
                    } else {
                        bit -= 1
                    }
                }
                for (i in 0 until length) {
                    // positions[0] is the MSB, so its weight is length-1.
                    if (bitAt(data, positions[i])) value = value or (1L shl (length - 1 - i))
                }
            }
        }

        if (signed && length < 64 && (value shr (length - 1)) and 1L == 1L) {
            value -= 1L shl length
        }
        return value
    }

    private fun bitAt(data: ByteArray, position: Int): Boolean {
        val byteIndex = position / 8
        require(byteIndex < data.size) {
            "signal needs byte $byteIndex but the frame has ${data.size} bytes"
        }
        return (data[byteIndex].toInt() shr (position % 8)) and 1 == 1
    }

    /** Decode every signal of [message] that [frame] is long enough to carry. */
    fun decode(
        message: MessageDefinition,
        frame: RawCanFrame,
        staleFactor: Double = DEFAULT_STALE_FACTOR,
    ): List<DecodedSignal> {
        val staleAfter = staleTimeout(message.cycleTimeMs, staleFactor)
        return message.signals.mapNotNull { signal ->
            if (frame.data.size < signal.requiredBytes) {
                // Requirement 55: a short frame is not an error, it just cannot
                // carry this signal. The frame is still shown on the Raw CAN tab.
                return@mapNotNull null
            }

            val raw = extract(frame.data, signal.startBit, signal.length, signal.byteOrder, signal.signed)
            DecodedSignal(
                messageName = message.name,
                signalName = signal.name,
                raw = raw,
                value = raw * signal.factor + signal.offset,
                unit = signal.unit,
                label = signal.label(raw),
                updatedAtMs = frame.receivedAtMs,
                staleAfterMs = staleAfter,
            )
        }
    }

    /**
     * How long a value stays trustworthy after its last update (requirement 53).
     *
     * Three missed cycles is the default: one missed frame is normal jitter on a
     * busy bus, three in a row means the stream really has stopped. The floor
     * keeps very fast messages (10 ms) from flickering stale on a brief hiccup.
     */
    fun staleTimeout(cycleTimeMs: Int, factor: Double = DEFAULT_STALE_FACTOR): Long =
        maxOf(MINIMUM_STALE_TIMEOUT_MS, (cycleTimeMs * factor).toLong())

    const val DEFAULT_STALE_FACTOR = 3.0
    const val MINIMUM_STALE_TIMEOUT_MS = 150L
}
