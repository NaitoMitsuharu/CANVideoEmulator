import textwrap

import pytest

from scenario_builder import dbc

# Reference vectors from the real Toyota RAV4 DBC used by the scenarios, so the
# bit maths is pinned to the definitions that actually ship.
#   BO_ 170 WHEEL_SPEEDS: 8
#     SG_ WHEEL_SPEED_FR : 6|15@0+ (0.01,-67.67) "km/h"
#     SG_ WHEEL_SPEED_FL : 22|15@0+ (0.01,-67.67) "km/h"
#   BO_ 37 STEER_ANGLE_SENSOR: 8
#     SG_ STEER_ANGLE : 3|12@0- (1.5,0) "deg"
#     SG_ STEER_FRACTION : 39|4@0- (0.1,0) "deg"

SAMPLE = textwrap.dedent("""\
    VERSION ""

    BO_ 170 WHEEL_SPEEDS: 8 XXX
     SG_ WHEEL_SPEED_FR : 6|15@0+ (0.01,-67.67) [0|0] "km/h" AFS
     SG_ WHEEL_SPEED_FL : 22|15@0+ (0.01,-67.67) [0|0] "km/h" AFS

    BO_ 37 STEER_ANGLE_SENSOR: 8 XXX
     SG_ STEER_ANGLE : 3|12@0- (1.5,0) [-500|500] "deg" XXX
     SG_ STEER_FRACTION : 39|4@0- (0.1,0) [-0.7|0.7] "deg" XXX

    BO_ 2364540158 EXTENDED_MSG: 8 XXX
     SG_ LE_U16 : 0|16@1+ (1,0) [0|65535] "" XXX
     SG_ LE_S16 : 16|16@1- (1,0) [-32768|32767] "" XXX

    BO_ 956 GEAR_PACKET: 8 XXX
     SG_ GEAR : 13|6@0+ (1,0) [0|63] "" XXX

    CM_ SG_ 37 STEER_ANGLE "wheel angle, coarse";
    BA_ "GenMsgCycleTime" BO_ 170 12;
    VAL_ 956 GEAR 0 "P" 1 "R" 2 "N" 3 "D" 4 "B";
    """)


@pytest.fixture()
def database(tmp_path):
    path = tmp_path / "sample.dbc"
    path.write_text(SAMPLE, encoding="utf-8")
    return dbc.parse(path)


def test_parses_messages_and_signals(database):
    by_id = database.by_frame_id()
    assert set(by_id) == {170, 37, 956, 2364540158 & ~0x8000_0000}
    assert by_id[170].name == "WHEEL_SPEEDS"
    assert by_id[170].dlc == 8
    assert len(by_id[170].signals) == 2


def test_extended_flag_is_stripped_from_frame_id(database):
    message = next(m for m in database.messages if m.name == "EXTENDED_MSG")
    assert message.extended is True
    assert message.frame_id == 2364540158 & ~0x8000_0000


def test_cycle_time_comment_and_value_table(database):
    by_id = database.by_frame_id()
    assert by_id[170].cycle_time_ms == 12
    assert by_id[37].signals[0].comment == "wheel angle, coarse"
    assert by_id[956].signals[0].values == {0: "P", 1: "R", 2: "N", 3: "D", 4: "B"}


# --- bit extraction ------------------------------------------------------

def test_big_endian_unsigned_wheel_speed(database):
    # 0x1A0A = 6666 raw -> 6666*0.01 - 67.67 = -1.01 km/h
    message = database.by_frame_id()[170]
    data = bytes([0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00])
    assert message.decode(data)["WHEEL_SPEED_FR"] == pytest.approx(6666 * 0.01 - 67.67)


def test_big_endian_signed_negative(database):
    # STEER_ANGLE: 12 bits, start 3, big endian, signed, factor 1.5.
    # Raw -2 must come out as -3.0 deg.
    message = database.by_frame_id()[37]
    raw = (-2) & 0xFFF
    data = bytearray(8)
    data[0] = (raw >> 8) & 0x0F
    data[1] = raw & 0xFF
    assert message.decode(bytes(data))["STEER_ANGLE"] == pytest.approx(-3.0)


def test_little_endian_unsigned_and_signed(database):
    message = next(m for m in database.messages if m.name == "EXTENDED_MSG")
    data = bytes([0x34, 0x12, 0xFF, 0xFF, 0, 0, 0, 0])
    decoded = message.decode(data)
    assert decoded["LE_U16"] == 0x1234
    assert decoded["LE_S16"] == -1


def test_extract_signal_bit_numbering_directly():
    # Bit n is bit (n % 8) of byte (n // 8): bit 0 is the LSB of byte 0.
    data = bytes([0b0000_0001, 0, 0, 0, 0, 0, 0, 0])
    assert dbc.extract_signal(data, 0, 1, dbc.BYTE_ORDER_LITTLE, False) == 1
    assert dbc.extract_signal(data, 1, 1, dbc.BYTE_ORDER_LITTLE, False) == 0


def test_big_endian_crosses_byte_boundary_msb_first():
    # start_bit 7 is byte0 bit7; a 16-bit big-endian signal reads byte0 then byte1.
    data = bytes([0xAB, 0xCD, 0, 0, 0, 0, 0, 0])
    assert dbc.extract_signal(data, 7, 16, dbc.BYTE_ORDER_BIG, False) == 0xABCD


def test_signed_sign_extension_boundaries():
    data = bytes([0x80, 0, 0, 0, 0, 0, 0, 0])
    assert dbc.extract_signal(data, 7, 1, dbc.BYTE_ORDER_BIG, True) == -1
    assert dbc.extract_signal(data, 7, 1, dbc.BYTE_ORDER_BIG, False) == 1


def test_decode_skips_signals_that_do_not_fit_a_short_frame(database):
    """A short frame must not raise; the signals it cannot hold are just absent."""
    message = database.by_frame_id()[170]
    decoded = message.decode(b"\x1a\x0a\x00")
    assert "WHEEL_SPEED_FR" in decoded
    assert "WHEEL_SPEED_FL" not in decoded


def test_extract_signal_rejects_out_of_range_access():
    with pytest.raises(ValueError, match="frame has"):
        dbc.extract_signal(b"\x00\x00", 0, 32, dbc.BYTE_ORDER_LITTLE, False)


def test_merge_prefers_first_definition(tmp_path):
    a = tmp_path / "a.dbc"
    b = tmp_path / "b.dbc"
    a.write_text("BO_ 100 FIRST: 8 X\n SG_ S : 0|8@1+ (1,0) [0|0] \"\" X\n", encoding="utf-8")
    b.write_text("BO_ 100 SECOND: 8 X\n SG_ S : 0|8@1+ (2,0) [0|0] \"\" X\n"
                 "BO_ 200 ONLY_B: 8 X\n SG_ T : 0|8@1+ (1,0) [0|0] \"\" X\n",
                 encoding="utf-8")
    merged = dbc.merge(dbc.parse(a), dbc.parse(b))
    by_id = merged.by_frame_id()
    assert by_id[100].name == "FIRST"
    assert 200 in by_id
    assert merged.source_files == ["a.dbc", "b.dbc"]
