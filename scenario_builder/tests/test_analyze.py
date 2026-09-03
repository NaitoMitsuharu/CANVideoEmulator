import json
import textwrap

import pytest

from scenario_builder import analyze, dbc, manifest
from scenario_builder.canbin import CanFrame

DBC_TEXT = textwrap.dedent("""\
    BO_ 170 WHEEL_SPEEDS: 8 XXX
     SG_ WHEEL_SPEED_FL : 22|15@0+ (0.01,-67.67) [0|0] "km/h" AFS

    BO_ 37 STEER_ANGLE_SENSOR: 8 XXX
     SG_ STEER_ANGLE : 3|12@0- (1.5,0) [-500|500] "deg" XXX
     SG_ STEER_FRACTION : 39|4@0- (0.1,0) [-0.7|0.7] "deg" XXX
    """)


@pytest.fixture()
def database(tmp_path):
    path = tmp_path / "t.dbc"
    path.write_text(DBC_TEXT, encoding="utf-8")
    return dbc.parse(path)


def segment_analysis(name, *, speed=None, speed_range=None, steering_stdev=None,
                     steering_max=None, cruise=None, fps=1100.0, frames=66000):
    """A hand-built analysis, so the recommender can be tested on its own."""
    return analyze.SegmentAnalysis(
        segment_path=name,
        route="route",
        segment_index=int(name.split("_")[-1]) if "_" in name else 0,
        dongle_id="b0c9d2329ad1606b",
        vehicle_make="Toyota",
        vehicle_model="RAV4",
        duration_sec=60.0,
        total_frame_count=frames,
        frames_per_second=fps,
        unique_can_ids=105,
        average_speed_kmh=speed,
        min_speed_kmh=None if speed is None else speed - (speed_range or 0) / 2,
        max_speed_kmh=None if speed is None else speed + (speed_range or 0) / 2,
        speed_range_kmh=speed_range,
        speed_stdev_kmh=None,
        speed_sample_count=5000 if speed is not None else 0,
        steering_stdev_deg=steering_stdev,
        steering_max_abs_deg=steering_max,
        steering_sample_count=5000 if steering_stdev is not None else 0,
        cruise_active_fraction=cruise,
    )


# --- measurement ---------------------------------------------------------

def wheel_speed_frame(t_us, kmh):
    raw = round((kmh + 67.67) / 0.01)
    data = bytearray(8)
    data[2] = (raw >> 8) & 0x7F
    data[3] = raw & 0xFF
    return CanFrame(t_us, 170, 8, bytes(data))


def test_decode_series_reads_the_expected_signal(database):
    frames = [wheel_speed_frame(i * 12_000, 50.0 + i * 0.1) for i in range(100)]
    values = analyze._decode_series(frames, database, "WHEEL_SPEEDS", "WHEEL_SPEED_FL")
    assert len(values) == 100
    assert values[0] == pytest.approx(50.0, abs=0.02)
    assert values[-1] == pytest.approx(59.9, abs=0.02)


def test_decode_series_ignores_tx_echo(database):
    frames = [CanFrame(i * 12_000, 170, 8, bytes(8), tx_echo=True) for i in range(50)]
    assert analyze._decode_series(frames, database, "WHEEL_SPEEDS", "WHEEL_SPEED_FL") == []


def test_decode_series_of_an_absent_message_is_empty(database):
    frames = [wheel_speed_frame(0, 50.0)]
    assert analyze._decode_series(frames, database, "NOT_PRESENT", "X") == []


def test_fraction_active_needs_enough_samples():
    assert analyze._fraction_active([1.0] * 10) is None
    assert analyze._fraction_active([1.0] * 50) == 1.0
    assert analyze._fraction_active([0.0] * 25 + [1.0] * 25) == 0.5


# --- recommendation ------------------------------------------------------

def test_categories_match_on_measured_thresholds():
    by_name = {c.name: c for c in analyze.CATEGORIES}

    assert by_name["High Speed"].matches(segment_analysis("a", speed=95))
    assert not by_name["High Speed"].matches(segment_analysis("b", speed=60))

    assert by_name["Slow Driving"].matches(segment_analysis("c", speed=20))
    assert not by_name["Slow Driving"].matches(segment_analysis("d", speed=60))

    assert by_name["Speed Changes"].matches(segment_analysis("e", speed=60, speed_range=40))
    assert not by_name["Speed Changes"].matches(segment_analysis("f", speed=60, speed_range=5))

    assert by_name["Curves"].matches(segment_analysis("g", steering_stdev=8))
    assert by_name["Steering Active"].matches(segment_analysis("h", steering_max=30))
    assert by_name["Cruise"].matches(segment_analysis("i", cruise=0.9))
    assert by_name["High CAN Rate"].matches(segment_analysis("j", fps=2500))


def test_a_segment_with_no_decoded_signals_matches_no_speed_category():
    blank = segment_analysis("blank")
    by_name = {c.name: c for c in analyze.CATEGORIES}
    assert not by_name["High Speed"].matches(blank)
    assert not by_name["Slow Driving"].matches(blank)
    assert not by_name["Curves"].matches(blank)


def test_recommend_returns_the_requested_count():
    analyses = [segment_analysis(f"s_{i:03d}", speed=40 + i, speed_range=10 + i,
                                 steering_stdev=i * 0.5, steering_max=i)
                for i in range(60)]
    assert len(analyze.recommend(analyses, count=20)) == 20
    assert len(analyze.recommend(analyses, count=5)) == 5


def test_recommend_never_repeats_a_segment():
    analyses = [segment_analysis(f"s_{i:03d}", speed=40 + i, speed_range=10 + i,
                                 steering_stdev=i * 0.4, steering_max=i)
                for i in range(40)]
    picked = [r.segment_path for r in analyze.recommend(analyses, count=20)]
    assert len(picked) == len(set(picked))


def test_recommend_spreads_across_categories_rather_than_taking_one_kind():
    # Thirty near-identical motorway minutes plus a handful of other driving.
    analyses = [segment_analysis(f"fast_{i:03d}", speed=100 + i * 0.1, speed_range=3,
                                 steering_stdev=0.4, steering_max=3)
                for i in range(30)]
    analyses += [
        segment_analysis("slow_001", speed=18, speed_range=30, steering_stdev=9,
                         steering_max=40),
        segment_analysis("curvy_001", speed=55, speed_range=28, steering_stdev=12,
                         steering_max=60),
        segment_analysis("cruise_001", speed=88, speed_range=6, steering_stdev=1.2,
                         steering_max=8, cruise=0.95),
    ]

    picked = analyze.recommend(analyses, count=8)
    categories = {r.category for r in picked}
    paths = {r.segment_path for r in picked}

    assert len(categories) >= 3, f"only reached categories {categories}"
    # The distinctive segments must not be crowded out by the near-duplicates.
    assert "slow_001" in paths
    assert "curvy_001" in paths


def test_diversity_keeps_near_duplicates_out_while_it_can():
    # Twelve identical segments; only a few should survive the separation rule
    # before it has to be relaxed to fill the list.
    analyses = [segment_analysis(f"same_{i:03d}", speed=100, speed_range=4,
                                 steering_stdev=0.5, steering_max=4)
                for i in range(12)]
    assert len(analyze.recommend(analyses, count=3)) == 3


def test_recommend_falls_back_to_frame_count_when_categories_run_out():
    # Two segments, twenty requested: it must return what exists, not fail.
    analyses = [segment_analysis("a", speed=100, speed_range=4),
                segment_analysis("b", speed=20, speed_range=40)]
    picked = analyze.recommend(analyses, count=20)
    assert len(picked) == 2


def test_recommend_ignores_unusable_segments():
    good = segment_analysis("good", speed=90, speed_range=20)
    broken = segment_analysis("broken", speed=90, speed_range=20)
    broken.error = "no CAN frames"

    picked = analyze.recommend([good, broken], count=10)
    assert [r.segment_path for r in picked] == ["good"]


def test_recommend_on_an_empty_list_is_empty():
    assert analyze.recommend([], count=10) == []


def test_recommendations_are_ranked_from_one():
    analyses = [segment_analysis(f"s_{i}", speed=50 + i, speed_range=30)
                for i in range(6)]
    ranks = [r.scenario_rank for r in analyze.recommend(analyses, count=5)]
    assert ranks == [1, 2, 3, 4, 5]


def test_every_recommendation_carries_the_reason_it_was_picked():
    analyses = [segment_analysis("a", speed=100, speed_range=30, steering_stdev=8,
                                 steering_max=40, cruise=0.9)]
    for item in analyze.recommend(analyses, count=1):
        assert item.reason
        assert item.category in {c.name for c in analyze.CATEGORIES}


# --- document round trip --------------------------------------------------

def test_document_round_trips(tmp_path):
    analyses = [segment_analysis(f"s_{i}", speed=50 + i * 5, speed_range=20)
                for i in range(4)]
    document = analyze.build_document(
        analyses, analyze.recommend(analyses, count=2),
        source="/data/chunk_1", dbc_profile="toyota_rav4_2017")

    path = tmp_path / "analysis.json"
    analyze.write(path, document)
    reloaded = analyze.read(path)

    assert reloaded["format_version"] == analyze.ANALYSIS_FORMAT_VERSION
    assert reloaded["source"] == "/data/chunk_1"
    assert reloaded["segment_count"] == 4
    assert reloaded["usable_segment_count"] == 4
    assert len(reloaded["recommendations"]) == 2
    assert len(reloaded["segments"]) == 4


def test_document_counts_failures_separately(tmp_path):
    good = segment_analysis("good", speed=60, speed_range=20)
    broken = segment_analysis("broken")
    broken.error = "corrupt raw_log.bz2"

    document = analyze.build_document([good, broken], [],
                                      source="x", dbc_profile="p")
    assert document["segment_count"] == 2
    assert document["usable_segment_count"] == 1
    assert document["failed_segment_count"] == 1


def test_reading_an_unsupported_version_is_refused(tmp_path):
    path = tmp_path / "analysis.json"
    path.write_text(json.dumps({"format_version": 99}), encoding="utf-8")
    with pytest.raises(ValueError, match="format_version 99"):
        analyze.read(path)


def test_selected_segments_honours_the_rank_order_and_count(tmp_path):
    analyses = [segment_analysis(f"s_{i}", speed=50 + i * 5, speed_range=20 + i)
                for i in range(6)]
    document = analyze.build_document(
        analyses, analyze.recommend(analyses, count=4), source="x", dbc_profile="p")

    everything = analyze.selected_segments(document)
    assert len(everything) == 4

    top_two = analyze.selected_segments(document, count=2)
    assert [str(p) for p in top_two] == [str(p) for p in everything[:2]]


# --- bitrate split --------------------------------------------------------

def test_original_bitrate_is_unknown_and_playback_has_a_default():
    assert manifest.UNKNOWN_ORIGINAL_BITRATE is None
    assert manifest.DEFAULT_PLAYBACK_BITRATE == 500_000


def test_scenario_json_keeps_the_two_bitrates_separate():
    document = manifest.build_scenario_json(
        scenario_id="rav4_001", title="t", vehicle_make="Toyota",
        vehicle_model="RAV4", vehicle_year=None, dataset="comma2k19",
        route="r", segment=40, duration_sec=60.0, video="video.mp4",
        thumbnail="thumbnail.jpg", available_buses=[0], default_bus=0,
        default_bus_reason="test",
        original_bitrate=None, playback_bitrate=500_000,
        video_can_offset_ms=-37.472, dbc_profile="p", tags=[], tag_evidence={},
        description="d", can_files={0: "bus_0.canbin"}, bus_stats=[],
        video_fps=20.0, video_frame_count=1200, source_dongle_id="x")

    assert document["original_bitrate"] is None
    assert document["playback_bitrate"] == 500_000
    # The pre-split key must be gone, so nothing can read it ambiguously.
    assert "bitrate" not in document


def test_a_known_original_bitrate_is_preserved():
    document = manifest.build_scenario_json(
        scenario_id="x", title="t", vehicle_make="m", vehicle_model="v",
        vehicle_year=None, dataset="d", route="r", segment=0, duration_sec=1.0,
        video="", thumbnail="", available_buses=[0], default_bus=0,
        default_bus_reason="", original_bitrate=125_000, playback_bitrate=500_000,
        video_can_offset_ms=0, dbc_profile="p", tags=[], tag_evidence={},
        description="", can_files={0: "bus_0.canbin"}, bus_stats=[],
        video_fps=20.0, video_frame_count=None, source_dongle_id="x")

    assert document["original_bitrate"] == 125_000
    assert document["playback_bitrate"] == 500_000
