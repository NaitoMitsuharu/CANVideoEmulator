"""A DBC (Vector CAN database) reader, limited to what this project needs.

Only the statements that carry signal geometry are interpreted -- ``BO_``,
``SG_``, ``VAL_``, ``VAL_TABLE_``, ``CM_`` and ``BA_ "GenMsgCycleTime"``.  Node
lists, environment variables, multiplexing extensions and attribute definitions
beyond the cycle time are skipped, and anything unrecognised is ignored rather
than guessed at.

Bit numbering follows the DBC convention exactly:

    SG_ <name> : <start_bit>|<length>@<byte_order><sign> (<factor>,<offset>)
                 [<min>|<max>] "<unit>" <receivers>

``byte_order`` is ``1`` for little-endian (Intel) and ``0`` for big-endian
(Motorola) -- note that the digit is the inverse of what the words suggest.
``start_bit`` is the position of the signal's least-significant bit for
little-endian signals and of its most-significant bit for big-endian signals,
in the "bit 7 is the MSB of byte 0" numbering used by DBC files.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field
from pathlib import Path

BYTE_ORDER_LITTLE = "little_endian"
BYTE_ORDER_BIG = "big_endian"

_BO_RE = re.compile(r"^BO_\s+(?P<id>\d+)\s+(?P<name>[\w]+)\s*:\s*(?P<dlc>\d+)\s+(?P<node>\S+)")
_SG_RE = re.compile(
    r"^\s*SG_\s+(?P<name>[\w]+)\s*"
    r"(?:(?P<mux>m\d+|M)\s*)?"
    r":\s*(?P<start>\d+)\|(?P<length>\d+)@(?P<order>[01])(?P<sign>[+-])\s*"
    r"\(\s*(?P<factor>[-+0-9.eE]+)\s*,\s*(?P<offset>[-+0-9.eE]+)\s*\)\s*"
    r"\[\s*(?P<min>[-+0-9.eE]*)\s*\|\s*(?P<max>[-+0-9.eE]*)\s*\]\s*"
    r"\"(?P<unit>[^\"]*)\"")
_VAL_RE = re.compile(r"^VAL_\s+(?P<id>\d+)\s+(?P<signal>[\w]+)\s+(?P<pairs>.*?);\s*$")
_VAL_TABLE_RE = re.compile(r"^VAL_TABLE_\s+(?P<name>[\w]+)\s+(?P<pairs>.*?);\s*$")
_SGVAL_RE = re.compile(r"^SIG_VALTYPE_\s+(?P<id>\d+)\s+(?P<signal>[\w]+)\s*:?\s*(?P<type>\d+)\s*;")
_PAIR_RE = re.compile(r"(-?\d+)\s+\"([^\"]*)\"")
_CYCLE_RE = re.compile(r'^BA_\s+"GenMsgCycleTime"\s+BO_\s+(?P<id>\d+)\s+(?P<value>[0-9.]+)\s*;')
_CM_SG_RE = re.compile(r'^CM_\s+SG_\s+(?P<id>\d+)\s+(?P<signal>[\w]+)\s+"(?P<text>.*)"\s*;\s*$')
_CM_BO_RE = re.compile(r'^CM_\s+BO_\s+(?P<id>\d+)\s+"(?P<text>.*)"\s*;\s*$')

# DBC stores extended (29-bit) identifiers with bit 31 set.
_EXTENDED_FLAG = 0x8000_0000


@dataclass(slots=True)
class Signal:
    name: str
    start_bit: int
    length: int
    byte_order: str
    signed: bool
    factor: float
    offset: float
    minimum: float | None
    maximum: float | None
    unit: str
    multiplexer: str | None = None
    values: dict[int, str] = field(default_factory=dict)
    comment: str | None = None

    def decode(self, data: bytes) -> float:
        """Extract this signal from ``data`` and apply factor/offset."""
        return extract_signal(data, self.start_bit, self.length,
                              self.byte_order, self.signed) * self.factor + self.offset


@dataclass(slots=True)
class Message:
    frame_id: int
    name: str
    dlc: int
    transmitter: str
    extended: bool = False
    cycle_time_ms: int | None = None
    comment: str | None = None
    signals: list[Signal] = field(default_factory=list)

    def decode(self, data: bytes) -> dict[str, float]:
        out: dict[str, float] = {}
        for signal in self.signals:
            if _signal_fits(signal, len(data)):
                out[signal.name] = signal.decode(data)
        return out


@dataclass(slots=True)
class Database:
    messages: list[Message] = field(default_factory=list)
    value_tables: dict[str, dict[int, str]] = field(default_factory=dict)
    source_files: list[str] = field(default_factory=list)

    def by_frame_id(self) -> dict[int, Message]:
        return {m.frame_id: m for m in self.messages}


def _signal_fits(signal: Signal, data_len: int) -> bool:
    if signal.byte_order == BYTE_ORDER_LITTLE:
        highest = signal.start_bit + signal.length - 1
    else:
        # Big-endian signals walk forwards through the bytes from the MSB.
        byte = signal.start_bit // 8
        bit = signal.start_bit % 8
        consumed = bit + 1
        while consumed < signal.length:
            byte += 1
            consumed += 8
        highest = byte * 8 + 7
    return highest // 8 < data_len


def extract_signal(data: bytes, start_bit: int, length: int,
                   byte_order: str, signed: bool) -> int:
    """Extract a raw integer signal value from ``data``.

    Bit ``n`` of the frame is bit ``n % 8`` of byte ``n // 8``, i.e. the standard
    DBC numbering where bit 7 is the most significant bit of byte 0.
    """
    if length <= 0:
        raise ValueError("signal length must be positive")

    if byte_order == BYTE_ORDER_LITTLE:
        bits = [start_bit + i for i in range(length)]
    elif byte_order == BYTE_ORDER_BIG:
        bits = []
        byte, bit = divmod(start_bit, 8)
        for _ in range(length):
            bits.append(byte * 8 + bit)
            if bit == 0:
                byte += 1
                bit = 7
            else:
                bit -= 1
        bits.reverse()  # collected MSB-first, we accumulate LSB-first below
    else:
        raise ValueError(f"unknown byte order {byte_order!r}")

    value = 0
    for weight, position in enumerate(bits):
        byte_index, bit_index = divmod(position, 8)
        if byte_index >= len(data):
            raise ValueError(
                f"signal needs byte {byte_index} but frame has {len(data)} bytes")
        if data[byte_index] >> bit_index & 1:
            value |= 1 << weight

    if signed and value >> (length - 1) & 1:
        value -= 1 << length
    return value


def _parse_pairs(text: str) -> dict[int, str]:
    return {int(raw): label for raw, label in _PAIR_RE.findall(text)}


def _number_or_none(text: str) -> float | None:
    text = text.strip()
    if not text:
        return None
    try:
        return float(text)
    except ValueError:
        return None


def parse(path: Path | str) -> Database:
    """Parse a single .dbc file."""
    path = Path(path)
    database = Database(source_files=[path.name])
    by_id: dict[int, Message] = {}
    current: Message | None = None

    with path.open("r", encoding="utf-8", errors="replace") as handle:
        for raw_line in handle:
            line = raw_line.rstrip("\r\n")
            stripped = line.strip()

            match = _BO_RE.match(stripped)
            if match:
                raw_id = int(match["id"])
                message = Message(
                    frame_id=raw_id & ~_EXTENDED_FLAG,
                    name=match["name"],
                    dlc=int(match["dlc"]),
                    transmitter=match["node"],
                    extended=bool(raw_id & _EXTENDED_FLAG),
                )
                database.messages.append(message)
                by_id[message.frame_id] = message
                current = message
                continue

            if stripped.startswith("SG_ ") and current is not None:
                match = _SG_RE.match(line)
                if match is None:
                    continue
                current.signals.append(Signal(
                    name=match["name"],
                    start_bit=int(match["start"]),
                    length=int(match["length"]),
                    byte_order=BYTE_ORDER_LITTLE if match["order"] == "1" else BYTE_ORDER_BIG,
                    signed=match["sign"] == "-",
                    factor=float(match["factor"]),
                    offset=float(match["offset"]),
                    minimum=_number_or_none(match["min"]),
                    maximum=_number_or_none(match["max"]),
                    unit=match["unit"],
                    multiplexer=match["mux"],
                ))
                continue

            if not stripped:
                current = None
                continue

            match = _VAL_TABLE_RE.match(stripped)
            if match:
                database.value_tables[match["name"]] = _parse_pairs(match["pairs"])
                continue

            match = _VAL_RE.match(stripped)
            if match:
                message = by_id.get(int(match["id"]) & ~_EXTENDED_FLAG)
                if message:
                    for signal in message.signals:
                        if signal.name == match["signal"]:
                            signal.values = _parse_pairs(match["pairs"])
                continue

            match = _CYCLE_RE.match(stripped)
            if match:
                message = by_id.get(int(match["id"]) & ~_EXTENDED_FLAG)
                if message:
                    message.cycle_time_ms = int(float(match["value"]))
                continue

            match = _CM_SG_RE.match(stripped)
            if match:
                message = by_id.get(int(match["id"]) & ~_EXTENDED_FLAG)
                if message:
                    for signal in message.signals:
                        if signal.name == match["signal"]:
                            signal.comment = match["text"]
                continue

            match = _CM_BO_RE.match(stripped)
            if match:
                message = by_id.get(int(match["id"]) & ~_EXTENDED_FLAG)
                if message:
                    message.comment = match["text"]
                continue

    return database


def merge(*databases: Database) -> Database:
    """Combine several DBCs; the first definition of a frame id wins."""
    merged = Database()
    seen: dict[int, Message] = {}
    for database in databases:
        merged.source_files.extend(database.source_files)
        merged.value_tables.update(database.value_tables)
        for message in database.messages:
            if message.frame_id in seen:
                continue
            seen[message.frame_id] = message
            merged.messages.append(message)
    merged.messages.sort(key=lambda m: m.frame_id)
    return merged
