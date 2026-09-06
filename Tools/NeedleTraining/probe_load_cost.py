"""How expensive is swapping the weights the engine holds?

Route and refinement want different adapters - three runs show one adapter cannot hold both, because every
time one phase improves the other gives way. But needle_init and needle_load are global functions with no
context handle, so the mod would have to reload between the two phases of every query. That is only
unworkable if reloading is slow, and it was dismissed on a guess rather than a number.

This times NeedleSmoke twice: once through the single-query path, which loads the engine and no weights,
and once through the benchmark path on one row, which also loads a 23 MB adapter. Both pay process start
and engine load, so the difference is one needle_load.
"""
import pathlib
import subprocess
import time

ROOT = pathlib.Path(__file__).resolve().parent
HASH = ROOT.parent.parent
SMOKE = HASH / "Tools" / "NeedleSmoke" / "bin" / "Release" / "net8.0" / "NeedleSmoke.exe"
ENGINE = HASH / "Native" / "Hash.Needle.bin"
TOOLS = HASH / "Tools" / "NeedleSmoke" / "route-tools-minimal.json"
ADAPTER = ROOT / "artifacts" / "run3-epoch1.cact"


def best_of(args: list[str], runs: int = 5) -> float:
    """The fastest run, not the average: we want the cost, not the noise of a busy machine."""
    best = None
    for _ in range(runs):
        started = time.perf_counter()
        subprocess.run([str(a) for a in args], capture_output=True, check=False)
        elapsed = time.perf_counter() - started
        best = elapsed if best is None else min(best, elapsed)
    return best


def main() -> None:
    dataset = ROOT / "data-production" / "load-probe.jsonl"
    dataset.write_text(
        '{"id":"p|route","query":"set the time to 1200","command":"settime","language":"en",'
        '"split":"probe","phase":"route","reasoning":"","tools":[{"name":"settime","description":"x"}],'
        '"answers":[{"name":"settime","arguments":{}}]}\n', encoding="utf-8")

    without = best_of([SMOKE, ENGINE, "set the time to 1200", TOOLS])
    with_weights = best_of([SMOKE, "benchmark", ENGINE, ADAPTER, dataset,
                            ROOT / "artifacts" / "load-probe.json"])

    print(f"engine only            {without * 1000:7.0f} ms")
    print(f"engine + 23 MB adapter {with_weights * 1000:7.0f} ms")
    print(f"\nneedle_load costs at most {(with_weights - without) * 1000:.0f} ms")
    print("A two-adapter mod pays that on every phase switch, against ~100 ms of inference per phase.")


if __name__ == "__main__":
    main()
