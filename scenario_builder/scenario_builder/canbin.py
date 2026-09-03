"""Binary CAN timeline format (.canbin) reader/writer.

The exhibition player must not re-parse large JSON on every scenario load, so a
CAN timeline is stored as a fixed-size record file that can be memory-mapped and
binary-searched directly (see docs/scenario_package.md).  The identical layout is
implemented by CanReplayPlayer.Core/Can/CanBinFile.cs -- keep them in sync.

Layout (all little-endian)::

    header, 64 bytes
      0  .. 7   magic  b"CANBIN\0\0"
      8  .. 9   uint16 format_version
      10 .. 11  uint16 header_size    (64)
      12 .. 15  uint32 record_size    (20)
      16 .. 19  uint32 frame_count
      20 .. 27  uint64 duration_us
      28        uint8  bus_index
      29        uint8  flags          bit0 = file contains TX-echo frames
      30 .. 63  reserved, zero

    record, 20 bytes, sorted by timestamp_us ascending
      0  .. 3   uint32 timestamp_us   offset from scenario t=0
      4  .. 7   uint32 can_id         bit31 = extended (29-bit) identifier
      8         uint8  dlc            0..8, classical CAN only
      9         uint8  flags          bit0 = TX echo, bit1 = RTR
      10 .. 17  uint8  data[8]        bytes beyond dlc are zero
      18 .. 19  reserved, zero

The bus number is *not* stored per record: each bus lives in its own file, which
keeps the record at 20 bytes and makes "send exactly one recorded bus" the
natural default (requirement 26).
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path
from typing import BinaryIO, Iterable, Iterator, Sequence

MAGIC = b"CANBIN\x00\x00"
FORMAT_VERSION = 1
HEADER_SIZE = 64
RECORD_SIZE = 20

FLAG_FILE_HAS_TX_ECHO = 0x01

FLAG_FRAME_TX_ECHO = 0x01
FLAG_FRAME_RTR = 0x02

CAN_ID_EXTENDED = 0x8000_0000

MAX_TIMESTAMP_US = 0xFFFF_FFFF

_HEADER = struct.Struct("<8sHHIIQBBH")
_RECORD = struct.Struct("<IIBB8sH")


@dataclass(frozen=True, slots=True)
class CanFrame:
    """One classical-CAN frame on a single recorded bus."""

    timestamp_us: int
    can_id: int
    dlc: int
    data: bytes
    extended: bool = False
    tx_echo: bool = False
    rtr: bool = False

    def __post_init__(self) -> None:
        if not 0 <= self.timestamp_us <= MAX_TIMESTAMP_US:
            raise ValueError(f"timestamp_us out of range: {self.timestamp_us}")
        if not 0 <= self.dlc <= 8:
            raise ValueError(f"classical CAN DLC must be 0..8, got {self.dlc}")
        if len(self.data) != self.dlc:
            raise ValueError(f"data length {len(self.data)} != dlc {self.dlc}")
        limit = 0x1FFF_FFFF if self.extended else 0x7FF
        if not 0 <= self.can_id <= limit:
            raise ValueError(f"CAN id 0x{self.can_id:X} out of range for "
                             f"{'extended' if self.extended else 'standard'} frame")


@dataclass(frozen=True, slots=True)
class CanBinHeader:
    format_version: int
    frame_count: int
    duration_us: int
    bus_index: int
    has_tx_echo: bool


def _pack_record(frame: CanFrame) -> bytes:
    can_id = frame.can_id | (CAN_ID_EXTENDED if frame.extended else 0)
    flags = (FLAG_FRAME_TX_ECHO if frame.tx_echo else 0) | (FLAG_FRAME_RTR if frame.rtr else 0)
    return _RECORD.pack(frame.timestamp_us, can_id, frame.dlc, flags,
                        frame.data.ljust(8, b"\x00"), 0)


def _unpack_record(buf: bytes, offset: int = 0) -> CanFrame:
    ts, raw_id, dlc, flags, data, _ = _RECORD.unpack_from(buf, offset)
    extended = bool(raw_id & CAN_ID_EXTENDED)
    return CanFrame(
        timestamp_us=ts,
        can_id=raw_id & ~CAN_ID_EXTENDED,
        dlc=dlc,
        data=data[:dlc],
        extended=extended,
        tx_echo=bool(flags & FLAG_FRAME_TX_ECHO),
        rtr=bool(flags & FLAG_FRAME_RTR),
    )


def write(path: Path | str, bus_index: int, frames: Sequence[CanFrame]) -> CanBinHeader:
    """Write ``frames`` (already sorted by timestamp) to ``path``."""
    frames = list(frames)
    for a, b in zip(frames, frames[1:]):
        if b.timestamp_us < a.timestamp_us:
            raise ValueError("frames must be sorted by timestamp_us")
    duration_us = frames[-1].timestamp_us if frames else 0
    has_echo = any(f.tx_echo for f in frames)
    header = CanBinHeader(FORMAT_VERSION, len(frames), duration_us, bus_index, has_echo)

    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("wb") as fh:
        fh.write(_HEADER.pack(MAGIC, FORMAT_VERSION, HEADER_SIZE, RECORD_SIZE,
                              len(frames), duration_us, bus_index,
                              FLAG_FILE_HAS_TX_ECHO if has_echo else 0, 0))
        fh.write(b"\x00" * (HEADER_SIZE - _HEADER.size))
        for frame in frames:
            fh.write(_pack_record(frame))
    return header


def read_header(fh: BinaryIO) -> CanBinHeader:
    raw = fh.read(HEADER_SIZE)
    if len(raw) < HEADER_SIZE:
        raise ValueError("truncated .canbin header")
    magic, version, header_size, record_size, count, duration_us, bus, flags, _ = \
        _HEADER.unpack_from(raw)
    if magic != MAGIC:
        raise ValueError(f"not a .canbin file (magic={magic!r})")
    if version != FORMAT_VERSION:
        raise ValueError(f"unsupported .canbin format_version {version} "
                         f"(this build understands {FORMAT_VERSION})")
    if header_size != HEADER_SIZE or record_size != RECORD_SIZE:
        raise ValueError(f"unexpected header_size/record_size {header_size}/{record_size}")
    return CanBinHeader(version, count, duration_us, bus, bool(flags & FLAG_FILE_HAS_TX_ECHO))


def read(path: Path | str) -> tuple[CanBinHeader, list[CanFrame]]:
    with Path(path).open("rb") as fh:
        header = read_header(fh)
        body = fh.read(header.frame_count * RECORD_SIZE)
    if len(body) != header.frame_count * RECORD_SIZE:
        raise ValueError("truncated .canbin body")
    frames = [_unpack_record(body, i * RECORD_SIZE) for i in range(header.frame_count)]
    return header, frames


def iter_frames(path: Path | str) -> Iterator[CanFrame]:
    with Path(path).open("rb") as fh:
        header = read_header(fh)
        for _ in range(header.frame_count):
            yield _unpack_record(fh.read(RECORD_SIZE))


def to_jsonl(frames: Iterable[CanFrame], bus_index: int) -> Iterator[str]:
    """Debug export (requirement 32): one JSON object per frame."""
    import json
    for f in frames:
        yield json.dumps({
            "t_us": f.timestamp_us,
            "bus": bus_index,
            "id": f"0x{f.can_id:X}",
            "ext": f.extended,
            "dlc": f.dlc,
            "data": f.data.hex(),
            "tx_echo": f.tx_echo,
            "rtr": f.rtr,
        }, separators=(",", ":"))
