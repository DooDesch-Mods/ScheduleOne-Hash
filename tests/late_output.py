"""
Check that the hash terminal shows what a command answers after the submit returned (Hash #12, #13).

    python Hash/tests/late_output.py --slot mptest3

Needs a Debug build of Hash running in a loaded save with the hash app registered
(`session.ps1 -Build <Hash worktree> -App hash`). Prints PASS or FAIL per check and exits 1 when any check fails.

The page's own host calls are made from inside the mounted page (`s1.call('run' | 'drain', ...)`) and polled the
way app.js polls: a `run` reply, then `drain` every 75 ms while the host says `pending`, once a second while the log
view is on. The page cannot be typed into from a script (a synthetic Enter never reaches its listener), so the
transcript is rebuilt here from those replies.

The Debug-only console words used: `hashsay` answers through the game log inside the submit, `hashlate N` queues N
log lines for two frames later (what a Harmony prefix on SubmitCommand has to do).
"""
import argparse
import json
import os
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Workspace", "tools", "bridge"))
from s1 import Game  # noqa: E402

APP = "hash"
results = []
lines = []


def check(name, ok, detail=""):
    results.append(ok)
    print("%s  %s%s" % ("PASS" if ok else "FAIL", name, ("  - " + detail) if detail and not ok else ""))


def host(game, name, arg=""):
    raw = game.eval(APP, "s1.call(%s, %s)" % (json.dumps(name), json.dumps(arg)))
    return json.loads(raw) if raw else {}


def submit(game, line, watch=4.0, live_rhythm=False):
    """Run one line and keep polling like the page does until the host stops saying pending (or `watch` runs out)."""
    reply = host(game, "run", line)
    lines.extend(l["text"] for l in reply.get("lines", []))
    start = time.time()
    pending = reply.get("pending") is True
    polls = 0
    while time.time() - start < watch and (pending or live_rhythm):
        time.sleep(1.0 if live_rhythm else 0.075)
        drained = host(game, "drain")
        polls += 1
        lines.extend(l["text"] for l in drained.get("lines", []))
        pending = drained.get("pending") is True
    return reply.get("pending") is True, time.time() - start


def block(typed):
    """Everything after the last echo of `typed`."""
    last = max((i for i, t in enumerate(lines) if t.strip() == "$ " + typed), default=-1)
    return lines[last + 1:] if last >= 0 else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--slot", default=None)
    args = ap.parse_args()
    game = Game(slot=args.slot)

    # Not in log view: late lines are printed by the view itself then, and a second copy would be a bug.
    submit(game, "logs off")

    pending, _ = submit(game, "hashsay")
    b = block("hashsay")
    check("sync answer is in the block", b is not None and any("hashsay: sync answer" in t for t in b), repr(b))
    check("run reply asks the page to keep polling", pending)

    pending, took = submit(game, "hashlate 3")
    b = block("hashlate 3")
    got = [t for t in (b or []) if "hashlate: late answer line" in t]
    check("late answer arrives in the block (3 lines)", len(got) == 3, repr(b))
    check("late answer is not plain ok", b is not None and not (len(b) == 1 and b[0].strip() == "ok"), repr(b))
    check("late lines are in order", [t.split("line ")[-1] for t in got] == ["1/3", "2/3", "3/3"], repr(got))
    check("nothing from before is replayed", not any("sync answer" in t for t in (b or [])), repr(b))
    check("polling stops once the window has closed", took < 2.5, "%.1fs" % took)

    submit(game, "hashlate 1")
    b = block("hashlate 1")
    got = [t for t in (b or []) if "hashlate: late answer line" in t]
    check("a second late command gets only its own line", len(got) == 1, repr(b))

    # A command that really says nothing keeps answering ok and nothing else.
    submit(game, "hideui")
    b = block("hideui")
    # Ambient game warnings that land inside the window are attributed to it; they are not the test words' lines.
    check("silent command still answers ok",
          b is not None and b[:1] == ["ok"] and not any("hash" in t and "answer" in t for t in b), repr(b))

    # Next submit cuts the window short: the first command's late line is not lost, and is not in the second block.
    host(game, "run", "hashlate 1")
    reply = host(game, "run", "hashsay")
    time.sleep(0.5)
    drained = host(game, "drain")
    texts = [l["text"] for l in drained.get("lines", [])]
    check("late line of an interrupted command is still delivered",
          any("hashlate: late answer line" in t for t in texts + [l["text"] for l in reply.get("lines", [])]),
          repr(texts))

    # With the log view on the view prints the lines; the late path must not add them a second time.
    submit(game, "logs")
    n = len(lines)
    submit(game, "hashlate 2", watch=3.0, live_rhythm=True)
    got = [t for t in lines[n:] if "hashlate: late answer line" in t]
    check("log view on: each late line appears once", len(got) == 2, repr(lines[n:]))
    submit(game, "logs off")

    failed = results.count(False)
    print("\n%d checks, %d failed" % (len(results), failed))
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
