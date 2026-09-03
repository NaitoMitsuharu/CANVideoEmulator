package jp.co.canreplay.viewer.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import jp.co.canreplay.candecoder.GaugeReading
import jp.co.canreplay.candecoder.StreamState
import jp.co.canreplay.candecoder.read
import jp.co.canreplay.viewer.ViewerUiState

/** Colours chosen so a stale reading is obvious from across an exhibition stand. */
object ViewerColors {
    val Background = Color(0xFF0E1116)
    val Surface = Color(0xFF161B22)
    val SurfaceRaised = Color(0xFF1D242E)
    val Border = Color(0xFF2A323D)
    val Accent = Color(0xFF4C9AFF)
    val TextPrimary = Color(0xFFE6EDF3)
    val TextSecondary = Color(0xFF8B97A6)
    val TextMuted = Color(0xFF5D6874)
    val Good = Color(0xFF3FB950)
    val Warn = Color(0xFFD29922)
    val Bad = Color(0xFFF85149)
}

enum class ViewerTab(val label: String) {
    OVERVIEW("Overview"),
    SIGNALS("Signals"),
    RAW_CAN("Raw CAN"),
    DEBUG("Debug"),
}

@Composable
fun ViewerScreen(
    state: ViewerUiState,
    onStart: (Boolean) -> Unit,
    onStop: () -> Unit,
    onSignalFilter: (String) -> Unit,
    onRawFilter: (String) -> Unit,
) {
    var tab by rememberSaveable { mutableIntStateOf(0) }
    val tabs = remember { ViewerTab.entries }

    Column(
        Modifier
            .fillMaxSize()
            .background(ViewerColors.Background)
    ) {
        StatusHeader(state, onStart, onStop)

        TabRow(
            selectedTabIndex = tab,
            containerColor = ViewerColors.Surface,
            contentColor = ViewerColors.Accent,
        ) {
            tabs.forEachIndexed { index, entry ->
                Tab(
                    selected = tab == index,
                    onClick = { tab = index },
                    text = {
                        Text(
                            entry.label,
                            color = if (tab == index) ViewerColors.TextPrimary
                            else ViewerColors.TextSecondary,
                            fontSize = 13.sp,
                        )
                    },
                )
            }
        }

        when (tabs[tab]) {
            ViewerTab.OVERVIEW -> OverviewTab(state)
            ViewerTab.SIGNALS -> SignalsTab(state, onSignalFilter)
            ViewerTab.RAW_CAN -> RawCanTab(state, onRawFilter)
            ViewerTab.DEBUG -> DebugTab(state)
        }
    }
}

@Composable
private fun StatusHeader(
    state: ViewerUiState,
    onStart: (Boolean) -> Unit,
    onStop: () -> Unit,
) {
    val pill = when (state.streamState) {
        StreamState.LIVE -> ViewerColors.Good
        StreamState.STALE -> ViewerColors.Warn
        StreamState.WAITING -> ViewerColors.TextMuted
    }

    Column(
        Modifier
            .fillMaxWidth()
            .background(ViewerColors.Surface)
            .padding(16.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier
                    .size(10.dp)
                    .clip(CircleShape)
                    .background(pill)
            )
            Spacer(Modifier.width(8.dp))
            Text(
                state.statusText,
                color = pill,
                fontWeight = FontWeight.Bold,
                fontSize = 14.sp,
            )
            Spacer(Modifier.width(12.dp))
            Text(
                state.profile.displayName,
                color = ViewerColors.TextPrimary,
                fontWeight = FontWeight.SemiBold,
                fontSize = 15.sp,
                modifier = Modifier.weight(1f),
            )

            if (state.running) {
                TextButton(onClick = onStop) { Text("Stop", color = ViewerColors.Accent) }
            } else {
                TextButton(onClick = { onStart(true) }) {
                    Text("Connect", color = ViewerColors.Accent)
                }
                if (state.replayAvailable) {
                    TextButton(onClick = { onStart(false) }) {
                        Text("Replay", color = ViewerColors.TextSecondary)
                    }
                }
            }
        }

        Text(
            "${state.sourceName} — ${state.sourceStatus.detail}",
            color = ViewerColors.TextMuted,
            fontSize = 11.sp,
            modifier = Modifier.padding(top = 4.dp),
        )

        if (state.streamState == StreamState.STALE) {
            // Requirement 53: say why the values are frozen. With no control
            // channel, a pause, a seek and a scenario change all look identical.
            Text(
                "No CAN traffic. The player is paused, seeking, or between scenarios — " +
                    "values below are the last received, not current.",
                color = ViewerColors.Warn,
                fontSize = 11.sp,
                modifier = Modifier.padding(top = 6.dp),
            )
        }

        state.loadError?.let {
            Text(it, color = ViewerColors.Bad, fontSize = 11.sp,
                modifier = Modifier.padding(top = 6.dp))
        }
    }
}

// ---------------------------------------------------------------- Overview

@Composable
private fun OverviewTab(state: ViewerUiState) {
    val snapshot = state.snapshot
    val profile = state.profile

    LazyColumn(
        Modifier
            .fillMaxSize()
            .padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                BigReading(
                    "SPEED", snapshot?.read(profile.speed), "km/h",
                    Modifier.weight(1f), state.isStale,
                )
                BigReading(
                    "ENGINE", snapshot?.read(profile.rpm), "rpm",
                    Modifier.weight(1f), state.isStale,
                )
            }
        }

        item {
            SteeringCard(snapshot?.read(profile.steering), state.isStale)
        }

        item {
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                BarCard("ACCELERATOR", snapshot?.read(profile.accelerator), "%",
                    ViewerColors.Good, Modifier.weight(1f), state.isStale)
                BarCard("BRAKE PRESSURE", snapshot?.read(profile.brakePressure), "",
                    ViewerColors.Bad, Modifier.weight(1f), state.isStale)
            }
        }

        item {
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                LabelCard("GEAR", snapshot?.read(profile.gear), Modifier.weight(1f),
                    state.isStale)
                LabelCard("TURN SIGNAL", snapshot?.read(profile.turnSignal),
                    Modifier.weight(1f), state.isStale)
                LabelCard("BRAKE", snapshot?.read(profile.brake), Modifier.weight(1f),
                    state.isStale)
            }
        }

        item {
            SurfaceCard {
                Text("WHEEL SPEEDS", color = ViewerColors.TextMuted, fontSize = 11.sp,
                    fontWeight = FontWeight.SemiBold)
                Spacer(Modifier.height(8.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    profile.wheelSpeeds.forEach { mapping ->
                        val reading = snapshot?.read(mapping)
                        Column(Modifier.weight(1f), horizontalAlignment = Alignment.CenterHorizontally) {
                            Text(mapping.label, color = ViewerColors.TextMuted, fontSize = 10.sp)
                            Text(
                                reading?.display() ?: "---",
                                color = valueColor(reading, state.isStale),
                                fontFamily = FontFamily.Monospace,
                                fontSize = 16.sp,
                            )
                        }
                    }
                }
            }
        }

        item {
            SurfaceCard {
                Text("STEERING TORQUE", color = ViewerColors.TextMuted, fontSize = 11.sp,
                    fontWeight = FontWeight.SemiBold)
                val reading = snapshot?.read(profile.steeringTorque)
                Text(
                    "${reading?.display() ?: "---"} ${profile.steeringTorque?.unit.orEmpty()}",
                    color = valueColor(reading, state.isStale),
                    fontFamily = FontFamily.Monospace,
                    fontSize = 22.sp,
                    modifier = Modifier.padding(top = 4.dp),
                )
            }
        }
    }
}

private fun valueColor(reading: GaugeReading?, streamStale: Boolean): Color = when {
    reading == null || reading.signal == null -> ViewerColors.TextMuted
    reading.isStale || streamStale -> ViewerColors.TextMuted
    else -> ViewerColors.TextPrimary
}

@Composable
private fun SurfaceCard(content: @Composable ColumnScope.() -> Unit) {
    Card(
        colors = CardDefaults.cardColors(containerColor = ViewerColors.Surface),
        shape = RoundedCornerShape(10.dp),
        modifier = Modifier
            .fillMaxWidth()
            .border(1.dp, ViewerColors.Border, RoundedCornerShape(10.dp)),
    ) {
        Column(Modifier.padding(14.dp), content = content)
    }
}

@Composable
private fun BigReading(
    title: String,
    reading: GaugeReading?,
    unit: String,
    modifier: Modifier = Modifier,
    streamStale: Boolean,
) {
    Card(
        colors = CardDefaults.cardColors(containerColor = ViewerColors.Surface),
        shape = RoundedCornerShape(10.dp),
        modifier = modifier.border(1.dp, ViewerColors.Border, RoundedCornerShape(10.dp)),
    ) {
        Column(Modifier.padding(14.dp)) {
            Text(title, color = ViewerColors.TextMuted, fontSize = 11.sp,
                fontWeight = FontWeight.SemiBold)
            Row(verticalAlignment = Alignment.Bottom, modifier = Modifier.padding(top = 4.dp)) {
                Text(
                    reading?.display() ?: "---",
                    color = valueColor(reading, streamStale),
                    fontSize = 40.sp,
                    fontWeight = FontWeight.Bold,
                    fontFamily = FontFamily.Monospace,
                )
                Text(
                    " $unit",
                    color = ViewerColors.TextMuted,
                    fontSize = 14.sp,
                    modifier = Modifier.padding(bottom = 8.dp),
                )
            }
        }
    }
}

@Composable
private fun SteeringCard(reading: GaugeReading?, streamStale: Boolean) {
    Card(
        colors = CardDefaults.cardColors(containerColor = ViewerColors.Surface),
        shape = RoundedCornerShape(10.dp),
        modifier = Modifier
            .fillMaxWidth()
            .border(1.dp, ViewerColors.Border, RoundedCornerShape(10.dp)),
    ) {
        Column(Modifier.padding(14.dp)) {
            Text("STEERING", color = ViewerColors.TextMuted, fontSize = 11.sp,
                fontWeight = FontWeight.SemiBold)

            // A centre-zero bar: the marker sits mid-track when the wheel is
            // straight, so left and right are readable at a glance.
            val fraction = reading?.fraction
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(26.dp)
                    .padding(vertical = 9.dp)
                    .background(ViewerColors.SurfaceRaised, RoundedCornerShape(4.dp))
            ) {
                Box(
                    Modifier
                        .fillMaxWidth(0.5f)
                        .height(8.dp)
                        .background(Color.Transparent)
                )
                if (fraction != null && !streamStale) {
                    Box(
                        Modifier
                            .fillMaxWidth(fraction.toFloat().coerceIn(0.02f, 1f))
                            .height(8.dp)
                            .background(ViewerColors.Accent, RoundedCornerShape(4.dp))
                    )
                }
            }

            Row(verticalAlignment = Alignment.Bottom) {
                Text(
                    reading?.display() ?: "---",
                    color = valueColor(reading, streamStale),
                    fontSize = 30.sp,
                    fontWeight = FontWeight.Bold,
                    fontFamily = FontFamily.Monospace,
                )
                Text(" deg", color = ViewerColors.TextMuted, fontSize = 13.sp,
                    modifier = Modifier.padding(bottom = 6.dp))
            }
        }
    }
}

@Composable
private fun BarCard(
    title: String,
    reading: GaugeReading?,
    unit: String,
    color: Color,
    modifier: Modifier = Modifier,
    streamStale: Boolean,
) {
    Card(
        colors = CardDefaults.cardColors(containerColor = ViewerColors.Surface),
        shape = RoundedCornerShape(10.dp),
        modifier = modifier.border(1.dp, ViewerColors.Border, RoundedCornerShape(10.dp)),
    ) {
        Column(Modifier.padding(14.dp)) {
            Text(title, color = ViewerColors.TextMuted, fontSize = 11.sp,
                fontWeight = FontWeight.SemiBold)
            Text(
                "${reading?.display() ?: "---"} $unit",
                color = valueColor(reading, streamStale),
                fontSize = 22.sp,
                fontWeight = FontWeight.Bold,
                fontFamily = FontFamily.Monospace,
                modifier = Modifier.padding(vertical = 6.dp),
            )
            Box(
                Modifier
                    .fillMaxWidth()
                    .height(8.dp)
                    .background(ViewerColors.SurfaceRaised, RoundedCornerShape(4.dp))
            ) {
                val fraction = reading?.fraction
                if (fraction != null && !streamStale) {
                    Box(
                        Modifier
                            .fillMaxWidth(fraction.toFloat().coerceIn(0f, 1f))
                            .height(8.dp)
                            .background(color, RoundedCornerShape(4.dp))
                    )
                }
            }
        }
    }
}

@Composable
private fun LabelCard(
    title: String,
    reading: GaugeReading?,
    modifier: Modifier = Modifier,
    streamStale: Boolean,
) {
    Card(
        colors = CardDefaults.cardColors(containerColor = ViewerColors.Surface),
        shape = RoundedCornerShape(10.dp),
        modifier = modifier.border(1.dp, ViewerColors.Border, RoundedCornerShape(10.dp)),
    ) {
        Column(
            Modifier
                .padding(14.dp)
                .fillMaxWidth(),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(title, color = ViewerColors.TextMuted, fontSize = 10.sp,
                fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center)
            Text(
                reading?.display() ?: "---",
                color = valueColor(reading, streamStale),
                fontSize = 26.sp,
                fontWeight = FontWeight.Bold,
                fontFamily = FontFamily.Monospace,
                modifier = Modifier.padding(top = 4.dp),
            )
        }
    }
}

// ---------------------------------------------------------------- Signals

@Composable
private fun SignalsTab(state: ViewerUiState, onFilter: (String) -> Unit) {
    val signals = state.snapshot?.signals.orEmpty().filter { signal ->
        state.signalFilter.isBlank() ||
            signal.signalName.contains(state.signalFilter, ignoreCase = true) ||
            signal.messageName.contains(state.signalFilter, ignoreCase = true)
    }
    val now = state.snapshot?.nowMs ?: 0L

    Column(Modifier.fillMaxSize()) {
        OutlinedTextField(
            value = state.signalFilter,
            onValueChange = onFilter,
            label = { Text("Filter message or signal", color = ViewerColors.TextMuted) },
            singleLine = true,
            modifier = Modifier
                .fillMaxWidth()
                .padding(12.dp),
        )

        Row(
            Modifier
                .fillMaxWidth()
                .background(ViewerColors.SurfaceRaised)
                .padding(horizontal = 12.dp, vertical = 6.dp)
        ) {
            HeaderCell("MESSAGE", 0.28f)
            HeaderCell("SIGNAL", 0.30f)
            HeaderCell("VALUE", 0.20f)
            HeaderCell("UNIT", 0.10f)
            HeaderCell("AGE", 0.12f)
        }

        LazyColumn(Modifier.fillMaxSize()) {
            items(signals, key = { "${it.messageName}.${it.signalName}" }) { signal ->
                val stale = state.isStale || signal.isStale(now)
                val colour = if (stale) ViewerColors.TextMuted else ViewerColors.TextPrimary
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp, vertical = 5.dp)
                ) {
                    Cell(signal.messageName, 0.28f, ViewerColors.TextSecondary)
                    Cell(signal.signalName, 0.30f, colour)
                    // Requirement 53: a stale value is never printed as if live.
                    Cell(if (stale) "---" else signal.display(), 0.20f, colour)
                    Cell(signal.unit, 0.10f, ViewerColors.TextMuted)
                    Cell("${(now - signal.updatedAtMs).coerceAtLeast(0)} ms", 0.12f,
                        ViewerColors.TextMuted)
                }
            }
        }
    }
}

@Composable
private fun RowScope.HeaderCell(text: String, weight: Float) {
    Text(text, color = ViewerColors.TextMuted, fontSize = 10.sp,
        fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(weight))
}

@Composable
private fun RowScope.Cell(
    text: String,
    weight: Float,
    color: Color,
) {
    Text(text, color = color, fontSize = 12.sp, fontFamily = FontFamily.Monospace,
        modifier = Modifier.weight(weight))
}

// ---------------------------------------------------------------- Raw CAN

@Composable
private fun RawCanTab(state: ViewerUiState, onFilter: (String) -> Unit) {
    val activity = state.snapshot?.activity.orEmpty().filter { entry ->
        state.rawFilter.isBlank() ||
            entry.lastFrame.idText().contains(state.rawFilter, ignoreCase = true) ||
            entry.name.orEmpty().contains(state.rawFilter, ignoreCase = true)
    }

    Column(Modifier.fillMaxSize()) {
        OutlinedTextField(
            value = state.rawFilter,
            onValueChange = onFilter,
            label = { Text("Filter CAN ID or message", color = ViewerColors.TextMuted) },
            singleLine = true,
            modifier = Modifier
                .fillMaxWidth()
                .padding(12.dp),
        )

        Row(
            Modifier
                .fillMaxWidth()
                .background(ViewerColors.SurfaceRaised)
                .padding(horizontal = 12.dp, vertical = 6.dp)
        ) {
            HeaderCell("ID", 0.16f)
            HeaderCell("MESSAGE", 0.26f)
            HeaderCell("DLC", 0.08f)
            HeaderCell("DATA", 0.34f)
            HeaderCell("RATE", 0.16f)
        }

        LazyColumn(Modifier.fillMaxSize()) {
            items(activity, key = { it.canId }) { entry ->
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 12.dp, vertical = 5.dp)
                ) {
                    Cell(entry.lastFrame.idText(), 0.16f, ViewerColors.TextPrimary)
                    // Requirement 55: frames with no DBC entry are still listed,
                    // just without a name.
                    Cell(entry.name ?: "(not in DBC)", 0.26f,
                        if (entry.isDecodable) ViewerColors.TextSecondary else ViewerColors.TextMuted)
                    Cell(entry.lastFrame.dlc.toString(), 0.08f, ViewerColors.TextMuted)
                    Cell(entry.lastFrame.dataHex(), 0.34f, ViewerColors.TextPrimary)
                    Cell("%.0f/s".format(entry.ratePerSecond), 0.16f, ViewerColors.TextMuted)
                }
            }
        }
    }
}

// ---------------------------------------------------------------- Debug

@Composable
private fun DebugTab(state: ViewerUiState) {
    val snapshot = state.snapshot
    val database = state.database

    LazyColumn(
        Modifier
            .fillMaxSize()
            .padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        item {
            SurfaceCard {
                Text("STREAM", color = ViewerColors.TextMuted, fontSize = 11.sp,
                    fontWeight = FontWeight.SemiBold)
                DebugRow("State", state.statusText)
                DebugRow("Source", state.sourceName)
                DebugRow("Source status", state.sourceStatus.detail)
            }
        }

        // The three figures that pair with the Windows player's "frames sent"
        // and the bridge's own received count, so a loss can be localised to a link.
        item {
            SurfaceCard {
                Text("END-TO-END COUNTERS", color = ViewerColors.TextMuted, fontSize = 11.sp,
                    fontWeight = FontWeight.SemiBold)
                Text(
                    "Compare these with the Windows player's Frames sent and the " +
                        "CAN bridge's own received count for the same run.",
                    color = ViewerColors.TextMuted, fontSize = 10.sp,
                    modifier = Modifier.padding(top = 4.dp, bottom = 6.dp),
                )
                DebugRow("Raw CAN received", snapshot?.totalFrames?.toString() ?: "0")
                DebugRow("CAN rate",
                    snapshot?.let { "%.0f frames/s".format(it.framesPerSecond) } ?: "0 frames/s")
                DebugRow("Decoded signal updates",
                    snapshot?.decodedSignalUpdates?.toString() ?: "0")
                DebugRow("Frames decoded", snapshot?.decodedFrames?.toString() ?: "0")
                DebugRow("Frames not in DBC", snapshot?.undecodedFrames?.toString() ?: "0")
                DebugRow("Distinct CAN IDs", snapshot?.activity?.size?.toString() ?: "0")
                DebugRow("Receiving for",
                    snapshot?.let { "%.1f s".format(it.elapsedMs / 1000.0) } ?: "0.0 s")
                Text(
                    "Counters run from Stop/Connect; press Stop then Connect to zero them " +
                        "before a timed test.",
                    color = ViewerColors.TextMuted, fontSize = 10.sp,
                    modifier = Modifier.padding(top = 6.dp),
                )
            }
        }

        item {
            SurfaceCard {
                Text("SIGNAL DEFINITIONS", color = ViewerColors.TextMuted, fontSize = 11.sp,
                    fontWeight = FontWeight.SemiBold)
                DebugRow("Profile", state.profile.displayName)
                DebugRow("signals.json profile", database?.profile ?: "not loaded")
                DebugRow("Vehicle", database?.vehicle ?: "-")
                DebugRow("Recorded bus", database?.bus?.toString() ?: "-")
                DebugRow("DBC files", database?.dbcFiles?.joinToString() ?: "-")
                DebugRow("Messages", database?.messages?.size?.toString() ?: "0")
                DebugRow("Signals", database?.signalCount?.toString() ?: "0")
                DebugRow("IDs with no definition",
                    database?.undecodedCanIds?.size?.toString() ?: "0")
                DebugRow("Bundled CAN fixture",
                    if (state.replayAvailable) "yes (debug build)"
                    else "no -- raw CAN must come from the bridge")
            }
        }

        item {
            SurfaceCard {
                Text("CYCLE TIMES", color = ViewerColors.TextMuted, fontSize = 11.sp,
                    fontWeight = FontWeight.SemiBold)
                Text(
                    "Staleness uses 3x each message's cycle time, floored at 150 ms. " +
                        "The source column says whether the period came from the DBC, " +
                        "from the recording, or from the default.",
                    color = ViewerColors.TextMuted, fontSize = 10.sp,
                    modifier = Modifier.padding(vertical = 6.dp),
                )
                database?.messages?.take(30)?.forEach { message ->
                    DebugRow(
                        message.name,
                        "${message.cycleTimeMs} ms (${message.cycleTimeSource})",
                    )
                }
            }
        }
    }
}

@Composable
private fun DebugRow(name: String, value: String) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(vertical = 2.dp)
    ) {
        Text(name, color = ViewerColors.TextSecondary, fontSize = 11.sp,
            modifier = Modifier.weight(0.45f))
        Text(value, color = ViewerColors.TextPrimary, fontSize = 11.sp,
            fontFamily = FontFamily.Monospace, modifier = Modifier.weight(0.55f))
    }
}
