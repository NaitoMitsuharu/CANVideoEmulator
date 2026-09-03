"""Machine-readable progress for the desktop importer; ordinary logs stay readable."""
import json

PREFIX = "CANVIDEO_PROGRESS "


def report(phase: str, completed: int, total: int, detail: str = "") -> None:
    print(PREFIX + json.dumps(dict(phase=phase, completed=completed, total=total,
                                   detail=detail), ensure_ascii=True), flush=True)
