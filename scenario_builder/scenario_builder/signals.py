"""Generate signals.json -- the Android app's signal definitions (requirement 46).

The Android side deliberately contains no DBC parser.  Everything it needs to
turn a raw frame into a named, scaled, unit-carrying value is precomputed here:
bit geometry, sign, factor/offset, limits, unit and value tables.

Cycle times get two separate fields, because conflating them would be a guess:

``cycle_time_ms_dbc``
    Straight from the DBC's ``BA_ "GenMsgCycleTime"``, or ``null``.  Most opendbc
    files carry no cycle-time attributes at all.

``observed_cycle_time_ms``
    The median inter-arrival time actually measured in the scenario's recorded
    CAN, with the sample count that backs it.  This is a measurement of this
    recording, not a claim about the vehicle, and it is what the Android app uses
    to size per-signal staleness timeouts when the DBC is silent.
"""

from __future__ import annotations

import json
import statistics
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable, Sequence

from .canbin import CanFrame
from .dbc import Database, Message

SIGNALS_FORMAT_VERSION = 1

# Used only when neither the DBC nor the recording says anything about a
# message's period.  Deliberately generous: a wrong-but-short timeout would grey
# out live signals, which is worse than a slightly late STALE.
DEFAULT_CYCLE_TIME_MS = 100


@dataclass(slots=True)
class MessageStatistics:
    frame_count: int
    median_interval_ms: float | None
    min_interval_ms: float | None
    max_interval_ms: float | None
    first_us: int
    last_us: int


def measure_messages(frames: Iterable[CanFrame],
                     *, include_tx_echo: bool = False) -> dict[int, MessageStatistics]:
    """Measure per-CAN-ID arrival statistics from a recorded timeline."""
    times: dict[int, list[int]] = {}
    for frame in frames:
        if frame.tx_echo and not include_tx_echo:
            continue
        times.setdefault(frame.can_id, []).append(frame.timestamp_us)

    out: dict[int, MessageStatistics] = {}
    for can_id, stamps in times.items():
        gaps = [(b - a) / 1000.0 for a, b in zip(stamps, stamps[1:])]
        out[can_id] = MessageStatistics(
            frame_count=len(stamps),
            median_interval_ms=round(statistics.median(gaps), 3) if gaps else None,
            min_interval_ms=round(min(gaps), 3) if gaps else None,
            max_interval_ms=round(max(gaps), 3) if gaps else None,
            first_us=stamps[0],
            last_us=stamps[-1],
        )
    return out


def _signal_dict(signal) -> dict:
    return {
        "name": signal.name,
        "start_bit": signal.start_bit,
        "length": signal.length,
        "byte_order": signal.byte_order,     # "little_endian" | "big_endian"
        "signed": signal.signed,
        "factor": signal.factor,
        "offset": signal.offset,
        "minimum": signal.minimum,
        "maximum": signal.maximum,
        "unit": signal.unit,
        "multiplexer": signal.multiplexer,
        "comment": signal.comment,
        "values": {str(k): v for k, v in sorted(signal.values.items())} or None,
    }


def _message_dict(message: Message, stats: MessageStatistics | None) -> dict:
    if message.cycle_time_ms is not None:
        effective = message.cycle_time_ms
        source = "dbc"
    elif stats is not None and stats.median_interval_ms:
        effective = int(round(stats.median_interval_ms))
        source = "observed"
    else:
        effective = DEFAULT_CYCLE_TIME_MS
        source = "default"

    return {
        "can_id": message.frame_id,
        "can_id_hex": f"0x{message.frame_id:X}",
        "extended": message.extended,
        "name": message.name,
        "dlc": message.dlc,
        "transmitter": message.transmitter,
        "comment": message.comment,
        "cycle_time_ms_dbc": message.cycle_time_ms,
        "observed_cycle_time_ms": stats.median_interval_ms if stats else None,
        "observed_frame_count": stats.frame_count if stats else 0,
        "effective_cycle_time_ms": effective,
        "effective_cycle_time_source": source,
        "signals": [_signal_dict(s) for s in message.signals],
    }


def build(database: Database, frames: Sequence[CanFrame], *,
          profile: str, vehicle: str, bus_index: int,
          present_only: bool = True) -> dict:
    """Build the signals.json document for one bus of one scenario.

    ``present_only`` keeps only the messages this recording actually contains, so
    the Android app is not populated with hundreds of definitions it will never
    see.  Frames present in the recording but absent from the DBC are listed
    under ``undecoded_can_ids`` -- requirement 55 says they must still be visible
    on the Raw CAN tab, never silently dropped.

    TX-echo frames are counted here even though validation ignores them: they
    were physically on the recorded bus and so are replayed onto the wire, which
    means the Android app will receive them and needs their definitions.
    """
    stats = measure_messages(frames, include_tx_echo=True)
    known = database.by_frame_id()

    if present_only:
        selected = [known[can_id] for can_id in sorted(stats) if can_id in known]
    else:
        selected = sorted(database.messages, key=lambda m: m.frame_id)

    undecoded = sorted(can_id for can_id in stats if can_id not in known)

    return {
        "format_version": SIGNALS_FORMAT_VERSION,
        "profile": profile,
        "vehicle": vehicle,
        "bus": bus_index,
        "dbc_files": list(database.source_files),
        "default_cycle_time_ms": DEFAULT_CYCLE_TIME_MS,
        "messages": [_message_dict(m, stats.get(m.frame_id)) for m in selected],
        "undecoded_can_ids": [
            {
                "can_id": can_id,
                "can_id_hex": f"0x{can_id:X}",
                "observed_frame_count": stats[can_id].frame_count,
                "observed_cycle_time_ms": stats[can_id].median_interval_ms,
            }
            for can_id in undecoded
        ],
        "value_tables": {name: {str(k): v for k, v in table.items()}
                         for name, table in sorted(database.value_tables.items())},
    }


def write(path: Path | str, document: dict) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n",
                    encoding="utf-8")
