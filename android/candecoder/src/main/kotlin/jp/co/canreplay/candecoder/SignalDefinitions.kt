package jp.co.canreplay.candecoder

import org.json.JSONObject

/**
 * The signal definitions the Scenario Builder generates from opendbc.
 *
 * Requirement 46: the phone contains no DBC parser. Everything needed to turn a
 * raw frame into a named, scaled value is precomputed on the Windows side and
 * shipped as `signals.json`, so this module only has to read a fixed schema.
 */
data class SignalDefinition(
    val name: String,
    val startBit: Int,
    val length: Int,
    val byteOrder: ByteOrder,
    val signed: Boolean,
    val factor: Double,
    val offset: Double,
    val minimum: Double?,
    val maximum: Double?,
    val unit: String,
    val comment: String?,
    /** Raw value to label, from the DBC's VAL_ table. */
    val values: Map<Long, String>,
) {
    /** Highest frame byte this signal needs; a shorter frame cannot carry it. */
    val requiredBytes: Int = run {
        val highestBit = when (byteOrder) {
            ByteOrder.LITTLE_ENDIAN -> startBit + length - 1
            ByteOrder.BIG_ENDIAN -> {
                var byte = startBit / 8
                var consumed = (startBit % 8) + 1
                while (consumed < length) {
                    byte += 1
                    consumed += 8
                }
                byte * 8 + 7
            }
        }
        highestBit / 8 + 1
    }

    fun label(raw: Long): String? = values[raw]
}

enum class ByteOrder {
    LITTLE_ENDIAN,
    BIG_ENDIAN;

    companion object {
        fun parse(text: String): ByteOrder = when (text) {
            "little_endian" -> LITTLE_ENDIAN
            "big_endian" -> BIG_ENDIAN
            else -> throw IllegalArgumentException("unknown byte order '$text'")
        }
    }
}

data class MessageDefinition(
    val canId: Int,
    val extended: Boolean,
    val name: String,
    val dlc: Int,
    val comment: String?,
    /**
     * Period used for staleness, in milliseconds.
     *
     * The builder resolves this from the DBC's GenMsgCycleTime when present,
     * otherwise from the period actually measured in the recording, otherwise a
     * safe default. [cycleTimeSource] says which, so the Debug tab can show it.
     */
    val cycleTimeMs: Int,
    val cycleTimeSource: String,
    val signals: List<SignalDefinition>,
)

data class UndecodedMessage(
    val canId: Int,
    val observedFrameCount: Int,
    val observedCycleTimeMs: Double?,
)

/** A parsed `signals.json`. */
class SignalDatabase(
    val formatVersion: Int,
    val profile: String,
    val vehicle: String,
    val bus: Int,
    val dbcFiles: List<String>,
    val defaultCycleTimeMs: Int,
    val messages: List<MessageDefinition>,
    val undecodedCanIds: List<UndecodedMessage>,
) {
    private val byId: Map<Int, MessageDefinition> = messages.associateBy { it.canId }

    fun message(canId: Int): MessageDefinition? = byId[canId]

    val signalCount: Int get() = messages.sumOf { it.signals.size }

    fun findSignal(messageName: String, signalName: String): Pair<MessageDefinition, SignalDefinition>? {
        val message = messages.firstOrNull { it.name == messageName } ?: return null
        val signal = message.signals.firstOrNull { it.name == signalName } ?: return null
        return message to signal
    }

    companion object {
        const val SUPPORTED_FORMAT_VERSION = 1

        /**
         * Parse a `signals.json` document.
         *
         * A newer format version is rejected outright rather than parsed on a
         * best-effort basis: silently mis-reading bit positions would show
         * plausible but wrong vehicle values, which is worse than showing none.
         */
        fun parse(json: String): SignalDatabase {
            val root = JSONObject(json)

            val version = root.optInt("format_version", -1)
            require(version == SUPPORTED_FORMAT_VERSION) {
                "signals.json format_version $version is not supported by this build " +
                    "(which reads version $SUPPORTED_FORMAT_VERSION)"
            }

            val messages = root.getJSONArray("messages").let { array ->
                (0 until array.length()).map { parseMessage(array.getJSONObject(it)) }
            }

            val undecoded = root.optJSONArray("undecoded_can_ids")?.let { array ->
                (0 until array.length()).map {
                    val item = array.getJSONObject(it)
                    UndecodedMessage(
                        canId = item.getInt("can_id"),
                        observedFrameCount = item.optInt("observed_frame_count", 0),
                        observedCycleTimeMs = item.optDoubleOrNull("observed_cycle_time_ms"),
                    )
                }
            } ?: emptyList()

            val dbcFiles = root.optJSONArray("dbc_files")?.let { array ->
                (0 until array.length()).map { array.getString(it) }
            } ?: emptyList()

            return SignalDatabase(
                formatVersion = version,
                profile = root.optString("profile", "unknown"),
                vehicle = root.optString("vehicle", "unknown"),
                bus = root.optInt("bus", 0),
                dbcFiles = dbcFiles,
                defaultCycleTimeMs = root.optInt("default_cycle_time_ms", 100),
                messages = messages,
                undecodedCanIds = undecoded,
            )
        }

        private fun parseMessage(item: JSONObject): MessageDefinition {
            val signalsArray = item.getJSONArray("signals")
            val signals = (0 until signalsArray.length()).map { parseSignal(signalsArray.getJSONObject(it)) }
            return MessageDefinition(
                canId = item.getInt("can_id"),
                extended = item.optBoolean("extended", false),
                name = item.getString("name"),
                dlc = item.optInt("dlc", 8),
                comment = item.optStringOrNull("comment"),
                cycleTimeMs = item.optInt("effective_cycle_time_ms", 100),
                cycleTimeSource = item.optString("effective_cycle_time_source", "default"),
                signals = signals,
            )
        }

        private fun parseSignal(item: JSONObject): SignalDefinition {
            val values = item.optJSONObject("values")?.let { table ->
                table.keys().asSequence().associate { key -> key.toLong() to table.getString(key) }
            } ?: emptyMap()

            return SignalDefinition(
                name = item.getString("name"),
                startBit = item.getInt("start_bit"),
                length = item.getInt("length"),
                byteOrder = ByteOrder.parse(item.getString("byte_order")),
                signed = item.getBoolean("signed"),
                factor = item.getDouble("factor"),
                offset = item.getDouble("offset"),
                minimum = item.optDoubleOrNull("minimum"),
                maximum = item.optDoubleOrNull("maximum"),
                unit = item.optString("unit", ""),
                comment = item.optStringOrNull("comment"),
                values = values,
            )
        }
    }
}

private fun JSONObject.optStringOrNull(key: String): String? =
    if (isNull(key)) null else optString(key).takeIf { it.isNotEmpty() }

private fun JSONObject.optDoubleOrNull(key: String): Double? =
    if (isNull(key) || !has(key)) null else optDouble(key).takeIf { !it.isNaN() }
