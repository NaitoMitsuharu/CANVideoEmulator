"""playlist.json generation (requirement 36).

A playlist is nothing but an ordered list of scenario ids, so the player's Next /
Previous / auto-advance logic has a single, simple source of truth.  Playlists
are generated from the tags the builder derived (and the numbers behind them),
never from a road-type guess.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Sequence

PLAYLIST_FORMAT_VERSION = 1


def _with_tag(scenarios: Sequence[dict], tag: str) -> list[str]:
    return [s["scenario_id"] for s in scenarios if tag in s.get("tags", [])]


def build(scenarios: Sequence[dict], *, featured_count: int = 10) -> dict:
    """Build playlist.json from the generated scenario manifests."""
    ordered = sorted(scenarios, key=lambda s: s["scenario_id"])
    all_ids = [s["scenario_id"] for s in ordered]

    playlists: list[dict] = [
        {
            "playlist_id": "featured",
            "title": "Featured",
            "description": "A short demo loop for the booth.",
            "scenario_ids": all_ids[:featured_count],
        },
        {
            "playlist_id": "all",
            "title": f"All {ordered[0]['vehicle_model'] if ordered else 'Scenarios'}",
            "description": "Every scenario in this directory, in build order.",
            "scenario_ids": all_ids,
        },
    ]

    for playlist_id, title, tag, description in [
        ("high_speed", "High Speed", "High Speed",
         "Median wheel speed at or above 80 km/h."),
        ("speed_change", "Speed Change", "Speed Change",
         "Wheel speed spans 20 km/h or more within the segment."),
        ("steering", "Steering Demo", "Steering Active",
         "Steering angle reaches at least 15 degrees."),
        ("winding", "Curves", "Winding",
         "Steering angle standard deviation at or above 5 degrees."),
        ("braking", "Braking", "Braking",
         "BRAKE_MODULE reports the pedal pressed for at least 2% of the segment."),
        ("acceleration", "Acceleration", "Acceleration",
         "Accelerator pedal reaches at least 25%."),
        ("cruise", "Cruise", "Cruise",
         "PCM_CRUISE reports cruise active for at least half the segment."),
    ]:
        ids = _with_tag(ordered, tag)
        if ids:
            playlists.append({
                "playlist_id": playlist_id, "title": title,
                "description": description, "scenario_ids": ids,
            })

    # A deliberately small list for bring-up on the MCP2515 device under test.
    playlists.append({
        "playlist_id": "mcp2515_test",
        "title": "MCP2515 Test",
        "description": "Two short scenarios for verifying the CAN path end to end.",
        "scenario_ids": all_ids[:2],
    })

    return {
        "format_version": PLAYLIST_FORMAT_VERSION,
        "default_playlist": "featured",
        "playlists": playlists,
    }


def write(path: Path | str, document: dict) -> None:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document, indent=2, ensure_ascii=False) + "\n",
                    encoding="utf-8")
