package jp.co.canreplay.candecoder

import java.io.InputStream

/**
 * Reader for the `.canbin` format (see docs/scenario_package.md).
 *
 * Kept minimal and independent of the Windows implementation so a mistake in one
 * cannot silently propagate to the other; the layout constants are the contract.
 */
object CanBinReader {
    private const val HEADER_SIZE = 64
    private const val RECORD_SIZE = 20
    private const val SUPPORTED_VERSION = 1
    private val MAGIC = byteArrayOf(0x43, 0x41, 0x4E, 0x42, 0x49, 0x4E, 0x00, 0x00)

    data class Entry(
        val offsetMs: Long,
        val canId: Int,
        val extended: Boolean,
        val data: ByteArray,
        val txEcho: Boolean,
    ) {
        override fun equals(other: Any?): Boolean =
            other is Entry && offsetMs == other.offsetMs && canId == other.canId &&
                extended == other.extended && txEcho == other.txEcho &&
                data.contentEquals(other.data)

        override fun hashCode(): Int =
            ((offsetMs.hashCode() * 31 + canId) * 31 + data.contentHashCode()) * 31 +
                txEcho.hashCode()
    }

    fun read(stream: InputStream): List<Entry> {
        val header = stream.readNBytesCompat(HEADER_SIZE)
        require(header.size == HEADER_SIZE) { "truncated .canbin header" }
        require(header.copyOfRange(0, 8).contentEquals(MAGIC)) { "not a .canbin file" }

        val version = readUInt16(header, 8)
        require(version == SUPPORTED_VERSION) {
            ".canbin format_version $version is not supported (this build reads " +
                "$SUPPORTED_VERSION)"
        }
        require(readUInt16(header, 10) == HEADER_SIZE) { "unexpected header size" }
        require(readUInt32(header, 12) == RECORD_SIZE.toLong()) { "unexpected record size" }

        val count = readUInt32(header, 16).toInt()
        val body = stream.readNBytesCompat(count * RECORD_SIZE)
        require(body.size == count * RECORD_SIZE) { "truncated .canbin body" }

        return (0 until count).map { index ->
            val at = index * RECORD_SIZE
            val rawId = readUInt32(body, at + 4)
            val dlc = body[at + 8].toInt() and 0xFF
            require(dlc <= 8) { "frame $index declares DLC $dlc; classical CAN only" }
            Entry(
                offsetMs = readUInt32(body, at) / 1000,
                canId = (rawId and 0x7FFF_FFFFL).toInt(),
                extended = (rawId and 0x8000_0000L) != 0L,
                data = body.copyOfRange(at + 10, at + 10 + dlc),
                txEcho = (body[at + 9].toInt() and 0x01) != 0,
            )
        }
    }

    private fun readUInt16(bytes: ByteArray, at: Int): Int =
        (bytes[at].toInt() and 0xFF) or ((bytes[at + 1].toInt() and 0xFF) shl 8)

    private fun readUInt32(bytes: ByteArray, at: Int): Long =
        (bytes[at].toLong() and 0xFF) or
            ((bytes[at + 1].toLong() and 0xFF) shl 8) or
            ((bytes[at + 2].toLong() and 0xFF) shl 16) or
            ((bytes[at + 3].toLong() and 0xFF) shl 24)

    /** minSdk 34 has InputStream.readNBytes, but this keeps the reader portable. */
    private fun InputStream.readNBytesCompat(count: Int): ByteArray {
        val buffer = ByteArray(count)
        var read = 0
        while (read < count) {
            val n = read(buffer, read, count - read)
            if (n <= 0) break
            read += n
        }
        return if (read == count) buffer else buffer.copyOf(read)
    }
}
