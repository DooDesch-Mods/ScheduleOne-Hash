"""Pull the exact live Hash/Needle tool schemas from an isolated debug instance."""

from __future__ import annotations

import argparse
import datetime as dt
import json
import pathlib
import time
import urllib.error
import urllib.request
import uuid


HASH_BUILTINS = {
    "help", "clear", "history", "alias", "unalias", "grep", "copy", "logs", "raw", "repeat", "font"
}


def bridge_call(base: str, command: str, args: dict, timeout_ms: int = 10_000):
    payload = json.dumps({
        "id": uuid.uuid4().hex,
        "command": command,
        "args": args,
        "timeoutMs": timeout_ms,
    }).encode("utf-8")
    request = urllib.request.Request(
        base.rstrip("/") + "/command", data=payload,
        headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(request, timeout=timeout_ms / 1000 + 5) as response:
        envelope = json.loads(response.read().decode("utf-8"))
    if not envelope.get("ok"):
        error = envelope.get("error") or {}
        raise RuntimeError(f"{command} failed: {error.get('code')}: {error.get('message')}")
    return envelope.get("result")


def decode_host_json(value):
    for _ in range(2):
        if not isinstance(value, str):
            break
        try:
            value = json.loads(value)
        except json.JSONDecodeError:
            break
    return value


def hash_call(base: str, handler: str, argument: str = ""):
    code = f"s1.call({json.dumps(handler)},{json.dumps(argument)})"
    probe = bridge_call(base, "sideload_eval", {"appId": "hash", "code": code})
    if probe.get("failed"):
        raise RuntimeError(f"Hash probe failed: {probe.get('value')}")
    return decode_host_json(probe.get("value"))


def health(base: str):
    with urllib.request.urlopen(base.rstrip("/") + "/health", timeout=3) as response:
        return json.loads(response.read().decode("utf-8"))


def wait_for_health(base: str, seconds: float, predicate=lambda value: value.get("ok")):
    deadline = time.monotonic() + max(0.0, seconds)
    last = None
    while True:
        try:
            last = health(base)
            if predicate(last):
                return last
        except (OSError, urllib.error.URLError):
            pass
        if time.monotonic() >= deadline:
            raise RuntimeError(f"bridge state did not become ready within {seconds:g} seconds; last={last}")
        time.sleep(0.5)


def mount_hash(base: str, save_slot: int | None):
    current = health(base)
    if not current.get("inGame"):
        saves = bridge_call(base, "list_saves", {})
        choices = [int(entry["slot"]) for entry in saves.get("saves", []) if "slot" in entry]
        if save_slot is None:
            if not choices:
                raise RuntimeError("slot4 has no save to load")
            save_slot = choices[0]
        if save_slot not in choices:
            raise RuntimeError(f"save slot {save_slot} does not exist in slot4; found {choices}")
        print(f"loading isolated slot4 save {save_slot}...", flush=True)
        bridge_call(base, "load_save", {"slot": save_slot}, timeout_ms=90_000)
        bridge_call(base, "wait_until_loaded", {}, timeout_ms=120_000)
        wait_for_health(base, 180, lambda value: value.get("inGame") and not value.get("loading"))

    last = None
    for _ in range(8):
        try:
            last = bridge_call(base, "sideload_open_app", {"appId": "hash", "open": True})
            if last and last.get("onScreen"):
                return
        except Exception:
            pass
        time.sleep(1)
    raise RuntimeError(f"Hash app did not mount after loading slot4; last={last}")


def validate(tools: list[dict]):
    if not tools:
        raise ValueError("live catalogue returned no tools")
    names: set[str] = set()
    for index, tool in enumerate(tools):
        name = tool.get("name")
        if not isinstance(name, str) or not name:
            raise ValueError(f"tool {index} has no name")
        if name in names:
            raise ValueError(f"duplicate tool name: {name}")
        names.add(name)
        parameters = tool.get("parameters")
        if not isinstance(parameters, dict) or parameters.get("type") != "object":
            raise ValueError(f"{name}: invalid parameters schema")
        if parameters.get("additionalProperties") is not False:
            raise ValueError(f"{name}: schema permits unknown arguments")
    return names


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--bridge", default="http://127.0.0.1:6139")
    parser.add_argument("--snapshot", type=pathlib.Path,
                        help="read a Hash.Needle.Schema.json DEBUG snapshot instead of using the bridge")
    parser.add_argument("--out", type=pathlib.Path,
                        default=pathlib.Path(__file__).parent / "data" / "tools.json")
    parser.add_argument("--wait-seconds", type=float, default=60.0)
    parser.add_argument("--save", type=int, default=None,
                        help="isolated slot4 save to load (default: first local save)")
    args = parser.parse_args()

    if args.snapshot:
        snapshot = json.loads(args.snapshot.read_text(encoding="utf-8"))
        current_health = {"scene": "DEBUG snapshot"}
    else:
        current_health = wait_for_health(args.bridge, args.wait_seconds)
        if not current_health.get("ok"):
            raise RuntimeError("bridge is not healthy")

        mount_hash(args.bridge, args.save)
        current_health = health(args.bridge)
        snapshot = hash_call(args.bridge, "needle-diagnose-schema")
    tools = snapshot.get("schemas")
    names = validate(tools)
    if int(snapshot.get("count", -1)) != len(tools):
        raise ValueError("reported schema count does not match schemas array")

    missing_builtins = sorted(HASH_BUILTINS - names)
    if missing_builtins:
        raise ValueError("composite catalogue omitted Hash builtins: " + ", ".join(missing_builtins))

    values = snapshot.get("values")
    if not isinstance(values, dict):
        raise ValueError("schema snapshot omitted live provider values")
    unknown_value_commands = sorted(set(values) - names)
    if unknown_value_commands:
        raise ValueError("provider values reference unknown commands: " + ", ".join(unknown_value_commands))

    commands = snapshot.get("commands")
    if not isinstance(commands, list):
        raise ValueError("schema snapshot omitted command metadata")
    command_names = {entry.get("name") for entry in commands if isinstance(entry, dict)}
    if command_names != names:
        raise ValueError("command metadata does not match schemas: "
                         + ", ".join(sorted((command_names ^ names) - {None})))

    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(tools, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    args.out.with_name("values.json").write_text(
        json.dumps(values, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    args.out.with_name("commands.json").write_text(
        json.dumps(commands, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    metadata = {
        "generatedAtUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "bridge": args.bridge if not args.snapshot else None,
        "snapshot": str(args.snapshot.resolve()) if args.snapshot else None,
        "scene": current_health.get("scene"),
        "count": len(tools),
        "fingerprint": snapshot.get("fingerprint"),
        "hashBuiltins": sorted(HASH_BUILTINS),
    }
    args.out.with_name("catalogue.json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {len(tools)} strict live schemas -> {args.out}")
    print(f"wrote live values for {len(values)} commands -> {args.out.with_name('values.json')}")
    print(f"wrote metadata for {len(commands)} commands -> {args.out.with_name('commands.json')}")
    print("Hash builtins: " + ", ".join(sorted(HASH_BUILTINS)))


if __name__ == "__main__":
    main()
