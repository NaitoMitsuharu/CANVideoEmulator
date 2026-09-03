import struct

import pytest

from scenario_builder import canbin
from scenario_builder.canbin import CanFrame


def test_roundtrip_preserves_every_field(tmp_path):
    frames = [
        CanFrame(0, 0x0AA, 8, bytes(range(8))),
        CanFrame(1500, 0x25, 3, b"\x01\x02\x03"),
        CanFrame(2000, 0x1FFFFFFF, 8, b"\xff" * 8, extended=True),
        CanFrame(2500, 0x2E4, 5, b"\x00\x00\x00\x00\x00", tx_echo=True),
        CanFrame(3000, 0x123, 0, b""),
    ]
    path = tmp_path / "bus_0.canbin"
    header = canbin.write(path, 0, frames)

    assert header.frame_count == 5
    assert header.duration_us == 3000
    assert header.has_tx_echo is True

    read_header, read_frames = canbin.read(path)
    assert read_header == header
    assert read_frames == frames


def test_trailing_zero_payload_survives_roundtrip(tmp_path):
    """The failure mode that rules out processed_log as a CAN source."""
    frame = CanFrame(10, 0x2E4, 8, b"\x00" * 8)
    path = tmp_path / "zeros.canbin"
    canbin.write(path, 0, [frame])
    _, frames = canbin.read(path)
    assert frames[0].dlc == 8
    assert frames[0].data == b"\x00" * 8


def test_file_size_is_exactly_header_plus_records(tmp_path):
    frames = [CanFrame(i, 0x100, 1, b"\x01") for i in range(37)]
    path = tmp_path / "sized.canbin"
    canbin.write(path, 2, frames)
    assert path.stat().st_size == canbin.HEADER_SIZE + 37 * canbin.RECORD_SIZE


def test_bus_index_is_stored_in_header(tmp_path):
    path = tmp_path / "bus_2.canbin"
    canbin.write(path, 2, [CanFrame(0, 1, 1, b"\x00")])
    header, _ = canbin.read(path)
    assert header.bus_index == 2


def test_rejects_unsorted_frames(tmp_path):
    frames = [CanFrame(100, 1, 0, b""), CanFrame(50, 1, 0, b"")]
    with pytest.raises(ValueError, match="sorted"):
        canbin.write(tmp_path / "x.canbin", 0, frames)


def test_rejects_bad_magic(tmp_path):
    path = tmp_path / "bad.canbin"
    canbin.write(path, 0, [CanFrame(0, 1, 0, b"")])
    data = bytearray(path.read_bytes())
    data[0:8] = b"NOTCANBN"
    path.write_bytes(bytes(data))
    with pytest.raises(ValueError, match="not a .canbin"):
        canbin.read(path)


def test_rejects_future_format_version(tmp_path):
    path = tmp_path / "future.canbin"
    canbin.write(path, 0, [CanFrame(0, 1, 0, b"")])
    data = bytearray(path.read_bytes())
    struct.pack_into("<H", data, 8, canbin.FORMAT_VERSION + 1)
    path.write_bytes(bytes(data))
    with pytest.raises(ValueError, match="unsupported .canbin format_version"):
        canbin.read(path)


def test_rejects_truncated_body(tmp_path):
    path = tmp_path / "trunc.canbin"
    canbin.write(path, 0, [CanFrame(0, 1, 8, b"\x01" * 8)] * 1)
    data = path.read_bytes()
    path.write_bytes(data[:-4])
    with pytest.raises(ValueError, match="truncated"):
        canbin.read(path)


@pytest.mark.parametrize("dlc,data", [(9, b"\x00" * 9), (3, b"\x00\x00")])
def test_rejects_invalid_dlc(dlc, data):
    with pytest.raises(ValueError):
        CanFrame(0, 0x100, dlc, data)


def test_rejects_id_wider_than_frame_format():
    with pytest.raises(ValueError, match="out of range"):
        CanFrame(0, 0x800, 0, b"", extended=False)
    CanFrame(0, 0x800, 0, b"", extended=True)  # fine as an extended frame


def test_iter_frames_matches_read(tmp_path):
    frames = [CanFrame(i * 10, 0x100 + i, 2, bytes([i, i])) for i in range(64)]
    path = tmp_path / "iter.canbin"
    canbin.write(path, 1, frames)
    assert list(canbin.iter_frames(path)) == frames


def test_jsonl_export_reports_true_dlc(tmp_path):
    import json
    frames = [CanFrame(5, 0x1AA, 6, b"\x01\x02\x00\x00\x00\x00")]
    lines = list(canbin.to_jsonl(frames, 0))
    record = json.loads(lines[0])
    assert record == {"t_us": 5, "bus": 0, "id": "0x1AA", "ext": False,
                      "dlc": 6, "data": "010200000000", "tx_echo": False, "rtr": False}
