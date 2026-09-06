"""Score a one-call, whole-catalogue report - the entire answer, not just its first call.

`score_as_mod.py` reads `Actual[0]` because the mod's two passes each expect exactly one call. A single
call over 78 tools does not: the model can answer with two, and a scorer that looks only at the first one
would count "the right command plus a wrong one" as correct. That flatters the setup being tested, which is
the one thing a control must not do.

Three numbers, because they answer different questions:

  routed    the right command is named, arguments ignored - can the engine find the tool at all
  exact     the right command AND arguments that TryResolveText would land on - what a player would get
  refused   an off-topic request answered with nothing, which is also a correct answer

    python score_full_catalogue.py results/full-base.json results/full-tuned.json
"""

from __future__ import annotations

import collections
import json
import pathlib
import sys

import score_as_mod as m

RESULTS = pathlib.Path(__file__).resolve().parent.parent / "NeedleBenchmark" / "results"


def report_path(name: str) -> pathlib.Path:
    candidate = pathlib.Path(name)
    return candidate if candidate.exists() else RESULTS / name


def score(report: dict) -> dict:
    counts: collections.Counter[str] = collections.Counter()
    failures: list[tuple[str, str, str]] = []

    for row in report["results"]:
        expected = row.get("Expected") or []
        actual = row.get("Actual") or []
        counts["cases"] += 1

        if not expected:
            # Off-topic. Any call at all is wrong; the correct answer is silence.
            if actual:
                failures.append((row["Id"], "(nothing)", str([call.get("name") for call in actual])))
            else:
                counts["refused"] += 1
                counts["routed"] += 1
                counts["exact"] += 1
            continue

        want = expected[0]
        # One call, and it has to be the right one. Two calls is a wrong answer even if one of them fits.
        if len(actual) != 1:
            failures.append((row["Id"], want["name"], f"{len(actual)} call(s)"))
            continue

        got = actual[0]
        if got.get("name") != want["name"]:
            failures.append((row["Id"], want["name"], str(got.get("name"))))
            continue

        counts["routed"] += 1
        if m.matches(want["name"], want.get("arguments") or {}, got.get("arguments")):
            counts["exact"] += 1
        else:
            failures.append((row["Id"], f"{want['name']} {want.get('arguments')}",
                             f"{got.get('name')} {got.get('arguments')}"))

    return {"counts": counts, "failures": failures}


def main() -> None:
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)

    for name in sys.argv[1:]:
        path = report_path(name)
        result = score(json.loads(path.read_text(encoding="utf-8-sig")))
        counts = result["counts"]
        total = counts["cases"]
        print(f"{path.name:28} routed {counts['routed']:>3}/{total}   "
              f"exact {counts['exact']:>3}/{total}   refused {counts['refused']:>2}")

        if "--failures" in sys.argv:
            for identifier, want, got in result["failures"][:25]:
                print(f"    {identifier:<26} want {want}  got {got}")


if __name__ == "__main__":
    main()
