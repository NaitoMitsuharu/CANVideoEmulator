package jp.co.canreplay.viewer

import android.app.Application
import android.os.SystemClock
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import jp.co.canreplay.candecoder.RawCanFrame
import jp.co.canreplay.candecoder.SignalDatabase
import jp.co.canreplay.candecoder.StreamState
import jp.co.canreplay.candecoder.VehicleProfile
import jp.co.canreplay.candecoder.VehicleSnapshot
import jp.co.canreplay.candecoder.VehicleStateTracker
import jp.co.canreplay.viewer.source.CanSource
import jp.co.canreplay.viewer.source.ReplayCanSource
import jp.co.canreplay.viewer.source.SourceState
import jp.co.canreplay.viewer.source.SourceStatus
import jp.co.canreplay.viewer.source.CanSourceFactory
import jp.co.canreplay.viewer.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Application state for the viewer.
 *
 * Requirement 52: the CAN stream must never block the UI. Frames go straight
 * into [VehicleStateTracker] on the receiving thread, and the UI reads a
 * snapshot on a fixed [REFRESH_INTERVAL_MS] tick. At the ~1,100 frames per
 * second the RAV4 scenarios replay at, publishing per frame would put the
 * recomposer permanently behind.
 */
class ViewerViewModel(application: Application) : AndroidViewModel(application) {

    private val _state = MutableStateFlow(ViewerUiState())
    val state: StateFlow<ViewerUiState> = _state.asStateFlow()

    private var tracker: VehicleStateTracker? = null
    private var source: CanSource? = null

    init {
        viewModelScope.launch { loadProfile(VehicleProfile.TOYOTA_RAV4.id) }
        viewModelScope.launch { refreshLoop() }
    }

    /**
     * Load a vehicle's signals.json from assets.
     *
     * Requirement 48: the Windows side cannot tell the phone which vehicle is
     * playing, so the profile is chosen here and defaults to the RAV4.
     */
    private suspend fun loadProfile(profileId: String) {
        val profile = VehicleProfile.byId(profileId)
        val result = withContext(Dispatchers.IO) {
            runCatching {
                getApplication<Application>().assets
                    .open("profiles/$profileId/signals.json")
                    .bufferedReader()
                    .use { SignalDatabase.parse(it.readText()) }
            }
        }

        result.onSuccess { database ->
            tracker = VehicleStateTracker(database)
            _state.value = _state.value.copy(
                profile = profile,
                database = database,
                loadError = null,
            )
        }.onFailure { error ->
            _state.value = _state.value.copy(
                profile = profile,
                database = null,
                loadError = "signals.json for '$profileId' could not be loaded: " +
                    "${error.message}. Copy it from a Scenario Package's dbc/ folder " +
                    "into app/src/main/assets/profiles/$profileId/.",
            )
        }
    }

    fun selectProfile(profileId: String) {
        stop()
        viewModelScope.launch { loadProfile(profileId) }
    }

    /** Connect to the CAN bridge, or replay the bundled timeline in debug builds. */
    fun start(useHardware: Boolean = CanSourceFactory.hasHardwareSource(getApplication())) {
        stop()
        tracker?.reset()

        // Release builds carry no CAN fixture, so raw frames can only ever come
        // from hardware. Asking for replay there is refused rather than silently
        // falling back -- an exhibition app must not be able to show values that
        // did not come off the bus.
        if (!useHardware && !BuildConfig.HAS_REPLAY_FIXTURE) {
            reportUnavailable(
                "This is a release build: it contains no CAN fixture. " +
                    "Connect the CAN bridge to receive raw CAN.")
            return
        }

        val selected: CanSource = if (useHardware) {
            CanSourceFactory.createHardwareSource(getApplication()) ?: run {
                // Say so plainly rather than appearing to connect and then
                // sitting at WAITING forever.
                reportUnavailable(CanSourceFactory.NO_HARDWARE_SOURCE_MESSAGE)
                return
            }
        } else {
            ReplayCanSource(
                open = { getApplication<Application>().assets.open(REPLAY_ASSET) },
                label = "Replay ($REPLAY_ASSET)",
            )
        }

        source = selected
        selected.start { frame -> onFrame(frame) }
        _state.value = _state.value.copy(sourceName = selected.name, running = true)
    }

    private fun reportUnavailable(detail: String) {
        _state.value = _state.value.copy(
            running = false,
            sourceName = "none",
            sourceStatus = SourceStatus(SourceState.ERROR, detail),
        )
    }

    fun stop() {
        source?.stop()
        source = null
        _state.value = _state.value.copy(running = false)
    }

    private fun onFrame(frame: RawCanFrame) {
        // Called on the source's thread. Cheap and lock-scoped; no UI work here.
        tracker?.onFrame(frame)
    }

    private suspend fun refreshLoop() {
        while (true) {
            delay(REFRESH_INTERVAL_MS)
            val current = tracker ?: continue
            val now = SystemClock.elapsedRealtime()
            _state.value = _state.value.copy(
                snapshot = current.snapshot(now),
                sourceStatus = source?.status
                    ?: SourceStatus(SourceState.DISCONNECTED, "not started"),
            )
        }
    }

    fun setSignalFilter(text: String) {
        _state.value = _state.value.copy(signalFilter = text)
    }

    fun setRawFilter(text: String) {
        _state.value = _state.value.copy(rawFilter = text)
    }

    override fun onCleared() {
        stop()
        super.onCleared()
    }

    companion object {
        /**
         * 10 Hz. Fast enough that the gauges track the video, slow enough that a
         * 1 kHz stream cannot saturate recomposition (requirement 52).
         */
        const val REFRESH_INTERVAL_MS = 100L

        /** Bundled timeline used when no CAN bridge is attached. */
        const val REPLAY_ASSET = "demo/bus_0.canbin"
    }
}

data class ViewerUiState(
    val profile: VehicleProfile = VehicleProfile.TOYOTA_RAV4,
    val database: SignalDatabase? = null,
    val snapshot: VehicleSnapshot? = null,
    val sourceName: String = "not started",
    val sourceStatus: SourceStatus = SourceStatus(SourceState.DISCONNECTED, "not started"),
    val running: Boolean = false,
    /** False in release builds, which carry no bundled CAN (requirement 9). */
    val replayAvailable: Boolean = BuildConfig.HAS_REPLAY_FIXTURE,
    val loadError: String? = null,
    val signalFilter: String = "",
    val rawFilter: String = "",
) {
    val streamState: StreamState get() = snapshot?.streamState ?: StreamState.WAITING

    /**
     * Requirement 53: while traffic is stopped the last values may still be shown,
     * but only dimmed and labelled, never as if they were current.
     */
    val isStale: Boolean get() = streamState != StreamState.LIVE

    val statusText: String
        get() = when (streamState) {
            StreamState.LIVE -> "LIVE"
            StreamState.STALE -> "STALE"
            StreamState.WAITING -> "WAITING"
        }
}
