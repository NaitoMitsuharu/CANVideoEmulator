package jp.co.canreplay.candecoder

/**
 * Which decoded signals feed each gauge on the Overview screen.
 *
 * Requirement 48: Phase 1 targets the RAV4 only, but the profile is data rather
 * than hard-coded lookups so another vehicle is a new [VehicleProfile] plus its
 * own signals.json -- no decoder changes. Requirement 2 rules out the Windows
 * side telling the phone which vehicle is playing, so the profile is chosen in
 * Settings and defaults to the RAV4.
 *
 * Every mapping below names a signal that exists in the Toyota DBC the Scenario
 * Builder selected, and each carries the conversion needed to reach the display
 * unit. Nothing is guessed: a gauge with no signal simply shows no value.
 */
data class GaugeMapping(
    val id: String,
    val label: String,
    val messageName: String,
    val signalName: String,
    val unit: String,
    val decimals: Int = 0,
    /** Applied after the DBC's own factor/offset, e.g. km/h to the shown unit. */
    val scale: Double = 1.0,
    val bias: Double = 0.0,
    /** Range for bar and dial gauges; null for plain numeric readouts. */
    val minimum: Double? = null,
    val maximum: Double? = null,
) {
    fun convert(value: Double): Double = value * scale + bias
}

data class VehicleProfile(
    val id: String,
    val displayName: String,
    val speed: GaugeMapping?,
    val steering: GaugeMapping?,
    val accelerator: GaugeMapping?,
    val brake: GaugeMapping?,
    val brakePressure: GaugeMapping?,
    val gear: GaugeMapping?,
    val rpm: GaugeMapping?,
    /**
     * A single enum signal, not a pair of lamps: the Toyota DBC encodes the
     * stalk as BLINKERS_STATE.TURN_SIGNALS with 1 = left, 2 = right, 3 = none.
     */
    val turnSignal: GaugeMapping?,
    val steeringTorque: GaugeMapping?,
    val wheelSpeeds: List<GaugeMapping>,
) {
    val allMappings: List<GaugeMapping> = listOfNotNull(
        speed, steering, accelerator, brake, brakePressure, gear, rpm,
        turnSignal, steeringTorque,
    ) + wheelSpeeds

    companion object {
        /**
         * Toyota RAV4 2016-2018, matching `toyota_new_mc_pt_generated.dbc` --
         * the DBC opendbc assigns to TOYOTA_RAV4 and the one the builder's
         * validation confirmed against comma2k19's processed_log.
         */
        val TOYOTA_RAV4: VehicleProfile = VehicleProfile(
            id = "toyota_rav4_2017",
            displayName = "Toyota RAV4 (2016-2018)",
            // WHEEL_SPEEDS is used for road speed rather than SPEED.SPEED: the
            // builder measured it at RMSE 0.009 m/s against processed_log versus
            // 0.36 m/s for SPEED.SPEED.
            speed = GaugeMapping("speed", "Speed", "WHEEL_SPEEDS", "WHEEL_SPEED_FL",
                "km/h", decimals = 0, minimum = 0.0, maximum = 180.0),
            steering = GaugeMapping("steering", "Steering", "STEER_ANGLE_SENSOR", "STEER_ANGLE",
                "deg", decimals = 1, minimum = -180.0, maximum = 180.0),
            // GAS_PEDAL.GAS_PEDAL is already a percentage in this DBC ("%",
            // factor 0.5), not a 0..1 fraction.
            accelerator = GaugeMapping("accelerator", "Accelerator", "GAS_PEDAL", "GAS_PEDAL",
                "%", decimals = 0, minimum = 0.0, maximum = 100.0),
            brake = GaugeMapping("brake", "Brake", "BRAKE_MODULE", "BRAKE_PRESSED",
                "", decimals = 0, minimum = 0.0, maximum = 1.0),
            brakePressure = GaugeMapping("brake_pressure", "Brake Pressure",
                "BRAKE_MODULE", "BRAKE_PRESSURE", "", decimals = 0,
                minimum = 0.0, maximum = 4047.0),
            gear = GaugeMapping("gear", "Gear", "GEAR_PACKET", "GEAR", ""),
            rpm = GaugeMapping("rpm", "Engine", "ENGINE_RPM", "RPM",
                "rpm", decimals = 0, minimum = 0.0, maximum = 7000.0),
            turnSignal = GaugeMapping("turn_signal", "Turn Signal",
                "BLINKERS_STATE", "TURN_SIGNALS", ""),
            steeringTorque = GaugeMapping("steer_torque", "Steering Torque",
                "STEER_TORQUE_SENSOR", "STEER_TORQUE_DRIVER", "Nm", decimals = 0,
                minimum = -400.0, maximum = 400.0),
            wheelSpeeds = listOf(
                GaugeMapping("ws_fl", "FL", "WHEEL_SPEEDS", "WHEEL_SPEED_FL", "km/h", 1),
                GaugeMapping("ws_fr", "FR", "WHEEL_SPEEDS", "WHEEL_SPEED_FR", "km/h", 1),
                GaugeMapping("ws_rl", "RL", "WHEEL_SPEEDS", "WHEEL_SPEED_RL", "km/h", 1),
                GaugeMapping("ws_rr", "RR", "WHEEL_SPEEDS", "WHEEL_SPEED_RR", "km/h", 1),
            ),
        )

        val ALL: List<VehicleProfile> = listOf(TOYOTA_RAV4)

        fun byId(id: String): VehicleProfile = ALL.firstOrNull { it.id == id } ?: TOYOTA_RAV4
    }
}

/** A gauge's current reading, or the fact that it has none. */
data class GaugeReading(
    val mapping: GaugeMapping,
    val signal: DecodedSignal?,
    val isStale: Boolean,
) {
    val hasValue: Boolean get() = signal != null && !isStale

    val value: Double? get() = signal?.let { mapping.convert(it.value) }

    /** Position within the gauge's range, 0..1, or null when unknown. */
    val fraction: Double?
        get() {
            val current = value ?: return null
            val low = mapping.minimum ?: return null
            val high = mapping.maximum ?: return null
            if (high <= low) return null
            return ((current - low) / (high - low)).coerceIn(0.0, 1.0)
        }

    /**
     * Requirement 53: a stale reading must never render as a plausible live
     * number, so it becomes "---" rather than the last value seen.
     */
    fun display(): String = when {
        signal == null -> "---"
        isStale -> "---"
        signal.label != null -> signal.label
        else -> DecodedSignal.formatNumber(mapping.convert(signal.value), mapping.decimals)
    }

    /** The last value, shown dimmed while paused so the screen is not blank. */
    fun lastKnownDisplay(): String? = signal?.let {
        it.label ?: DecodedSignal.formatNumber(mapping.convert(it.value), mapping.decimals)
    }
}

fun VehicleSnapshot.read(mapping: GaugeMapping?): GaugeReading? {
    if (mapping == null) return null
    val signal = signal(mapping.messageName, mapping.signalName)
    val stale = signal == null ||
        streamState != StreamState.LIVE ||
        signal.isStale(nowMs)
    return GaugeReading(mapping, signal, stale)
}
