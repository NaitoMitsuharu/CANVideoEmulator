import textwrap

import pytest

from scenario_builder import dbc, manifest, playlists, signals
from scenario_builder.canbin import CanFrame

DBC_TEXT = textwrap.dedent("""\
    BO_ 170 WHEEL_SPEEDS: 8 XXX
     SG_ WHEEL_SPEED_FL : 22|15@0+ (0.01,-67.67) [0|0] "km/h" AFS

    BO_ 37 STEER_ANGLE_SENSOR: 8 XXX
     SG_ STEER_ANGLE : 3|12@0- (1.5,0) [-500|500] "deg" XXX
     SG_ STEER_FRACTION : 39|4@0- (0.1,0) [-0.7|0.7] "deg" XXX

    BO_ 705 GAS_PEDAL: 8 XXX
     SG_ GAS_PEDAL : 55|8@0+ (0.5,0) [0|0] "%" DS1
    """)


@pytest.fixture()
def database(tmp_path):
    path = tmp_path / "t.dbc"
    path.write_text(DBC_TEXT, encoding="utf-8")
    return dbc.parse(path)


def wheel_speed_frame(t_us: int, kmh: float) -> CanFrame:
    raw = round((kmh + 67.67) / 0.01)
    data = bytearray(8)
    data[2] = (raw >> 8) & 0x7F
    data[3] = raw & 0xFF
    return CanFrame(t_us, 170, 8, bytes(data))


def gas_frame(t_us: int, percent: float) -> CanFrame:
    data = bytearray(8)
    data[6] = round(percent / 0.5)
    return CanFrame(t_us, 705, 8, bytes(data))


# --- bus statistics ------------------------------------------------------

def test_bus_statistics_counts_and_rates():
    frames = [CanFrame(i * 1000, 0x100 + (i % 3), 8, b"\x00" * 8) for i in range(1000)]
    stats = manifest.bus_statistics(0, frames)
    assert stats.frame_count == 1000
    assert stats.unique_can_ids == 3
    assert stats.average_dlc == 8.0
    assert stats.duration_sec == pytest.approx(0.999, abs=1e-3)
    assert stats.frames_per_second == pytest.approx(1000 / 0.999, rel=1e-3)


def test_bus_statistics_handles_empty_bus():
    stats = manifest.bus_statistics(1, [])
    assert stats.frame_count == 0
    assert stats.frames_per_second == 0.0
    assert stats.estimated_bus_load_bps == 0


def test_estimated_load_accounts_for_extended_ids():
    """A 29-bit frame carries 20 more bits of overhead than an 11-bit one."""
    pair = [CanFrame(0, 0x100, 8, b"\x00" * 8), CanFrame(1_000_000, 0x100, 8, b"\x00" * 8)]
    extended_pair = [CanFrame(0, 0x100, 8, b"\x00" * 8, extended=True),
                     CanFrame(1_000_000, 0x100, 8, b"\x00" * 8, extended=True)]
    standard = manifest.bus_statistics(0, pair)
    extended = manifest.bus_statistics(0, extended_pair)
    assert extended.estimated_bus_load_bps == standard.estimated_bus_load_bps + 40
    assert extended.extended_id_frame_count == 2
    assert standard.extended_id_frame_count == 0


def test_tx_echo_frames_are_counted_separately():
    frames = [CanFrame(0, 0x100, 1, b"\x00"),
              CanFrame(1000, 0x2E4, 1, b"\x00", tx_echo=True)]
    stats = manifest.bus_statistics(0, frames)
    assert stats.frame_count == 2
    assert stats.tx_echo_frame_count == 1


# --- auto tags -----------------------------------------------------------

def test_speed_change_and_high_speed_tags(database):
    frames = [wheel_speed_frame(i * 10_000, 30 + i * 0.6) for i in range(100)]
    tags, evidence = manifest.auto_tags(frames, database)
    assert "Speed Change" in tags
    assert evidence["speed_kmh"]["min"] == pytest.approx(30, abs=0.1)
    assert evidence["speed_kmh"]["samples"] == 100


def test_low_speed_tag(database):
    frames = [wheel_speed_frame(i * 10_000, 12.0) for i in range(100)]
    tags, _ = manifest.auto_tags(frames, database)
    assert "Low Speed" in tags
    assert "High Speed" not in tags


def test_acceleration_tag_uses_percent_not_fraction(database):
    """GAS_PEDAL is a percentage; 0.4% must not read as 40%."""
    quiet = [gas_frame(i * 10_000, 0.5) for i in range(100)]
    tags, evidence = manifest.auto_tags(quiet, database)
    assert "Acceleration" not in tags
    assert evidence["gas_pedal_max_percent"]["value"] == pytest.approx(0.5)

    pressed = [gas_frame(i * 10_000, 40.0) for i in range(100)]
    tags, _ = manifest.auto_tags(pressed, database)
    assert "Acceleration" in tags


def test_tx_echo_frames_do_not_drive_tags(database):
    frames = [CanFrame(i * 10_000, 170, 8, bytes(8), tx_echo=True) for i in range(200)]
    tags, evidence = manifest.auto_tags(frames, database)
    assert tags == []
    assert evidence == {}


def test_no_tags_when_there_is_too_little_data(database):
    tags, evidence = manifest.auto_tags([wheel_speed_frame(0, 50)], database)
    assert tags == []
    assert evidence == {}


# --- signals.json --------------------------------------------------------

def test_signals_document_shape(database):
    frames = [wheel_speed_frame(i * 12_000, 50.0) for i in range(100)]
    frames += [CanFrame(i * 12_000, 0x799, 8, b"\x00" * 8) for i in range(100)]
    frames.sort(key=lambda f: f.timestamp_us)

    document = signals.build(database, frames, profile="p", vehicle="Toyota RAV4",
                             bus_index=0)
    assert document["format_version"] == signals.SIGNALS_FORMAT_VERSION
    assert document["bus"] == 0
    names = {m["name"] for m in document["messages"]}
    assert names == {"WHEEL_SPEEDS"}          # only messages actually present
    assert [u["can_id_hex"] for u in document["undecoded_can_ids"]] == ["0x799"]

    message = document["messages"][0]
    assert message["cycle_time_ms_dbc"] is None
    assert message["observed_cycle_time_ms"] == pytest.approx(12.0)
    assert message["effective_cycle_time_ms"] == 12
    assert message["effective_cycle_time_source"] == "observed"
    signal = message["signals"][0]
    assert signal["byte_order"] == "big_endian"
    assert signal["factor"] == 0.01
    assert signal["offset"] == -67.67
    assert signal["unit"] == "km/h"


def test_signals_include_tx_echo_messages(database):
    """Echoed frames go on the wire, so Android needs their definitions."""
    frames = [CanFrame(i * 10_000, 170, 8, bytes(8), tx_echo=True) for i in range(50)]
    document = signals.build(database, frames, profile="p", vehicle="v", bus_index=0)
    assert {m["name"] for m in document["messages"]} == {"WHEEL_SPEEDS"}


def test_dbc_cycle_time_wins_over_observation(tmp_path):
    path = tmp_path / "c.dbc"
    path.write_text('BO_ 100 M: 8 X\n SG_ S : 0|8@1+ (1,0) [0|0] "" X\n'
                    'BA_ "GenMsgCycleTime" BO_ 100 20;\n', encoding="utf-8")
    database = dbc.parse(path)
    frames = [CanFrame(i * 5_000, 100, 8, bytes(8)) for i in range(50)]
    document = signals.build(database, frames, profile="p", vehicle="v", bus_index=0)
    message = document["messages"][0]
    assert message["cycle_time_ms_dbc"] == 20
    assert message["observed_cycle_time_ms"] == pytest.approx(5.0)
    assert message["effective_cycle_time_ms"] == 20
    assert message["effective_cycle_time_source"] == "dbc"


def test_default_cycle_time_when_nothing_is_known(database):
    frames = [wheel_speed_frame(0, 50.0)]
    document = signals.build(database, frames, profile="p", vehicle="v", bus_index=0)
    message = document["messages"][0]
    assert message["effective_cycle_time_ms"] == signals.DEFAULT_CYCLE_TIME_MS
    assert message["effective_cycle_time_source"] == "default"


# --- playlists -----------------------------------------------------------

def test_playlists_group_by_tag():
    scenarios = [
        {"scenario_id": "rav4_001", "vehicle_model": "RAV4", "tags": ["High Speed"]},
        {"scenario_id": "rav4_002", "vehicle_model": "RAV4", "tags": ["Braking"]},
        {"scenario_id": "rav4_003", "vehicle_model": "RAV4", "tags": ["High Speed",
                                                                     "Steering Active"]},
    ]
    document = playlists.build(scenarios)
    by_id = {p["playlist_id"]: p for p in document["playlists"]}
    assert by_id["all"]["scenario_ids"] == ["rav4_001", "rav4_002", "rav4_003"]
    assert by_id["high_speed"]["scenario_ids"] == ["rav4_001", "rav4_003"]
    assert by_id["braking"]["scenario_ids"] == ["rav4_002"]
    assert "winding" not in by_id                 # no scenario carries that tag
    assert document["default_playlist"] == "all"
