import pytest

from scenario_builder.canbin import CanFrame
from scenario_builder.stress import generate_timeline, measured_wire_bps, wire_bit_count


def frame(timestamp_us=0, can_id=0x123, data=bytes(range(8)), **kwargs):
    return CanFrame(timestamp_us, can_id, len(data), data, **kwargs)


def test_wire_bit_count_includes_format_and_stuff_bits():
    alternating = frame(data=b"\x55" * 8)
    zeros = frame(data=b"\x00" * 8)
    extended = frame(can_id=0x1234567, data=b"\x55" * 8, extended=True)

    assert wire_bit_count(alternating) >= 111
    assert wire_bit_count(zeros) > wire_bit_count(alternating)
    assert wire_bit_count(extended) > wire_bit_count(alternating)


def test_generate_timeline_hits_wire_load_and_cycles_source():
    source = [
        frame(can_id=0x100, data=b"\x00" * 8, tx_echo=True),
        frame(can_id=0x200, data=b"\x55" * 8),
    ]

    generated = generate_timeline(source, duration_sec=1, target_wire_bps=450_000)

    assert [item.can_id for item in generated[:4]] == [0x100, 0x200, 0x100, 0x200]
    assert not any(item.tx_echo for item in generated)
    assert generated[-1].timestamp_us < 1_000_000
    assert measured_wire_bps(generated) == pytest.approx(450_000, abs=1)


@pytest.mark.parametrize("duration,target", [(0, 450_000), (1, 0)])
def test_generate_timeline_rejects_invalid_settings(duration, target):
    with pytest.raises(ValueError):
        generate_timeline([frame()], duration_sec=duration, target_wire_bps=target)