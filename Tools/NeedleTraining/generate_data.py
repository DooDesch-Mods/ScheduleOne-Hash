"""Generate validated multilingual Needle fine-tuning data from a live Hash catalogue.

Qwen writes natural phrasings only. Tool names, arguments, splits, negatives and final answers are assembled and
validated locally, so a teacher hallucination can never become training ground truth.
"""

from __future__ import annotations

import argparse
import copy
from collections import Counter
import hashlib
import json
import math
import pathlib
import random
import re
import shlex
import time
import urllib.error
import urllib.request

from needle.model.finetune import render_example
from needle.model.tokenizer import get_tokenizer


ROOT = pathlib.Path(__file__).resolve().parent
LANGUAGES = {
    "en": "English",
    "de": "German",
    "es": "Spanish",
    "fr": "French",
}
WORD = re.compile(r"[a-z0-9]+")
TOKEN_BUDGET = 256
MAX_TEACHER_WORDS = 24
MAX_ACCEPTED_TEACHER_WORDS = 32
# Mirrors Terminal/NeedleProtocol.cs: MaxRouteDescriptionCharacters, MaxFullDescriptionCharacters,
# MaxEnumValues, MaxEnumCharacters. The corpus used 32 and 0, so every refinement tool reached training
# with an empty description while the runtime sends up to 96 characters of it.
ROUTE_DESCRIPTION_CHARACTERS = 48
FULL_DESCRIPTION_CHARACTERS = 96
MAX_ENUM_VALUES = 32
MAX_ENUM_CHARACTERS = 80
TEACHER_PROMPT_VERSION = 10
MULTI_MODE_POLICY_VERSION = 1
# Phrasings the teacher could not write under the uniqueness and grounding constraints, reported at the
# end of the run so a thin corpus is visible rather than silent.
UNSATISFIED_ROWS: list[tuple[str, str]] = []

# Phrasings that one command claimed before another; the later row is dropped, see the duplicate check.
AMBIGUOUS_ROWS: list[tuple[str, str, str, str]] = []

# The mod calls Init(SystemFacts(), ...) with exactly this shape (NeedleCommandTranslator.cs:521), so the
# engine always renders a system block. render_example omits the block entirely when a row has no "system"
# key, which every row had, so the adapter was trained on prompts no player ever produces. Measured on the
# first adapter: the same 4-bit weights score 40% on holdout argument rows without a system block and 17%
# with one, while the engine scores 3%. The locale varies per player, so training carries several per
# language rather than one string to memorise.
SYSTEM_LOCALES = {"en": ("en-US", "en-GB"), "de": ("de-DE", "de-AT"),
                  "es": ("es-ES", "es-MX"), "fr": ("fr-FR", "fr-CA")}


def system_facts(language: str, index: int) -> str:
    locales = SYSTEM_LOCALES.get(language, ("en-US",))
    return f"locale: {locales[index % len(locales)]}; device: phone; assistant: hash"


VARIATION_STYLES = (
    "direct imperative starting with the action verb",
    "polite request explicitly using the language's equivalent of please",
    "first-person desire starting with the language's equivalent of I want",
    "terse command fragment with no politeness or question",
    "can-you question that ends as a question",
    "indirect non-question starting with the language's equivalent of it would help if",
)

# Hash's context grammar, shared by every command. These are language features, not per-command synonyms: the
# command catalogue says which argument kind is accepted and this table says what each context token means.
MARK_MEANINGS = {
    "#": "the exact thing the player is currently looking at",
    "#hand": "the item currently equipped in the player's hand",
    "#here": "the property where the player is currently standing",
    "#last": "the last argument of the last console command that ran",
    "#it": "the last identifier printed by the console",
    "#car": "the vehicle the player is currently sitting in",
    "#home": "the owned property the player most recently entered, meaning the player's home",
    "#near": "the NPC nearest to the player",
}
MARKS_BY_KIND = {
    "item": ("#", "#hand", "#last", "#it"),
    "npc": ("#", "#near", "#last", "#it"),
    "vehicle": ("#", "#car", "#last", "#it"),
    "property": ("#", "#here", "#home", "#last", "#it"),
    "any": tuple(MARK_MEANINGS),
}
FEATURE_SPLITS = ("train", "validation", "holdout")


def normalized(text: str) -> str:
    return " ".join(str(text).casefold().split())


def console_tokens(line: str) -> list[str]:
    try:
        return shlex.split(line, posix=True)
    except ValueError:
        return line.split()


def operation_tokens(tool: dict) -> set[str]:
    text = tool.get("name", "") + " " + tool.get("description", "")
    words = WORD.findall(text.casefold())
    tokens = set(words)
    compact = "_".join(words)
    tokens.update(compact[i:i + 3] for i in range(max(0, len(compact) - 2)))
    return tokens


def nearest_tools(target: dict, tools: list[dict], count: int = 4) -> list[dict]:
    target_tokens = operation_tokens(target)
    scored = []
    for candidate in tools:
        if candidate["name"] == target["name"]:
            continue
        candidate_tokens = operation_tokens(candidate)
        union = target_tokens | candidate_tokens
        score = len(target_tokens & candidate_tokens) / max(1, len(union))
        scored.append((score, candidate["name"], candidate))
    scored.sort(key=lambda entry: (-entry[0], entry[1]))
    return [entry[2] for entry in scored[:count]]


def compact_description(tool: dict, limit: int) -> str:
    description = str(tool.get("description", "")).strip().rstrip(".")
    if len(description) > limit:
        first = description.split(".", 1)[0].strip()
        description = first if 0 < len(first) <= limit else description[:limit].rstrip(" ,;:")
    return description


def route_tool(tool: dict) -> dict:
    return {"name": tool["name"],
            "description": compact_description(tool, ROUTE_DESCRIPTION_CHARACTERS)}


def normal(value: str) -> str:
    """NeedleProtocol.Normal: letters and digits only, lowercased - so "og kush" and "ogkush" are one."""
    return "".join(character.lower() for character in value if character.isalnum())


def edit_distance_at_most_one(left: str, right: str) -> bool:
    if left == right:
        return True
    if abs(len(left) - right.__len__()) > 1:
        return False
    if len(left) == len(right):
        return sum(a != b for a, b in zip(left, right)) == 1
    shorter, longer = (left, right) if len(left) < len(right) else (right, left)
    for index in range(len(longer)):
        if longer[:index] + longer[index + 1:] == shorter:
            return True
    return False


def candidate_score(candidate: str, phrase: str) -> int:
    """NeedleTool.CandidateScore, close enough to pick the same values for the same query."""
    normalized = normal(candidate)
    if not normalized or not phrase:
        return 0
    singular = phrase[:-1] if phrase.endswith("s") else phrase
    if normalized == singular or phrase.startswith(normalized):
        return 4500
    # A two-letter phrase like "me" is inside meth, megabean and horsesemen; the runtime's matcher rejects
    # those for the same reason its comment gives - they match far too much of a thousand-item list - and
    # letting them through here spent the whole 80-character budget before the right value was reached.
    if len(phrase) >= 4 and phrase in normalized:
        return 4000 - abs(len(normalized) - len(phrase))
    # The other direction - a catalogue value hiding inside a longer word the player typed - only counts
    # when the value is most of that word. `addy` sits inside "grandaddy" and scored 3995, one notch under
    # an exact match, which is how "give me 4 grandaddy seed" came back as addy.
    if len(phrase) >= 4 and normalized in phrase and len(normalized) * 2 >= len(phrase):
        return 4000 - abs(len(normalized) - len(phrase))
    if len(phrase) >= 6 and abs(len(normalized) - len(phrase)) <= 1             and edit_distance_at_most_one(normalized, phrase):
        return 3500
    if len(phrase) >= 8 and len(phrase) * 2 >= len(normalized):
        iterator = iter(normalized)
        if all(character in iterator for character in phrase):
            return 2500 - min(len(normalized) - len(phrase), 499)
    return 0


def grounds_literals(row: dict, query: str) -> bool:
    """Can the runtime still recover every argument from this phrasing?

    This used to demand the literal: opaque ids verbatim and every number as digits. That is stricter
    than the game, and it cost the adapter the phrasings players actually use - of 877 numeric training
    arguments not one was ever written as a word, while 49 of the 73 hand-written benchmark cases with
    arguments need exactly that ("gib mir zehn og kush"). An opaque sample id stays literal, because
    nobody says it any other way. A number may be its word. Everything else has to satisfy the rule the
    runtime itself applies: NeedleArgument.TryResolveText resolves what the model produced against the
    value catalogue, so a phrasing is grounded when that matcher lands on this value and nothing else.
    """
    requested = query.casefold()
    for name, value in row.get("arguments", {}).items():
        if isinstance(value, bool):
            continue
        if isinstance(value, str) and re.fullmatch(r"sample\d+", value):
            if not re.search(rf"(?<![\d.]){re.escape(value.casefold())}(?![\d.])", requested):
                return False
        elif isinstance(value, (int, float)):
            digits = format(value, "g")
            word = (NUMBER_WORDS.get(row.get("language", ""), {}).get(int(value))
                    if float(value).is_integer() else None)
            if not re.search(rf"(?<![\d.]){re.escape(digits)}(?![\d.])", requested) and not (
                    word and re.search(rf"\b{re.escape(word)}\b", requested)):
                return False
        # Everything else is left alone. Requiring the runtime's matcher to land on the value looked right
        # and is wrong: the catalogue is English and the corpus is not, so "Fuege einen Botaniker zum Stall
        # hinzu" has no lexical path to barn, and the check threw away most of the German, French and
        # Spanish rows - four in the first command alone. RelevantValues cannot translate either; that part
        # is the model's job, and forbidding the teacher to write it would teach the wrong lesson.
    return True


def enum_choices(values: list[str], query: str) -> list[str]:
    """The enum the runtime would send for this query - NeedleTool.RelevantValues.

    The first version took the first MAX_ENUM_CHARACTERS worth of values in catalogue order, so the enum
    for `give` was "acid, acunit, addy, airpot, apron, ..." and never contained ogkush. The adapter learned
    to pick from a list that could not hold the answer, and it showed: "give me 10 ogkush" came back as
    apron, "give me 4 grandaddy seed" as addy. The runtime ranks the values against what the player typed,
    so training has to do the same or it teaches a choice that never occurs.
    """
    tokens = re.findall(r"\w+", query.casefold())
    phrases = set()
    for start in range(len(tokens)):
        for count in range(1, min(6, len(tokens) - start) + 1):
            phrase = normal(" ".join(tokens[start:start + count]))
            if phrase and not phrase.isdigit():
                phrases.add(phrase)
    ranked = []
    unmatched = []
    for value in values:
        score = max((candidate_score(value, phrase) for phrase in phrases), default=0)
        if score > 0:
            ranked.append((-score, len(value), value.casefold(), value))
        else:
            unmatched.append(value)
    ranked.sort()

    # A slot whose whole vocabulary fits is not a ranking problem. Offering only what matched a word in the
    # query left the enum short and sometimes empty - "make it sunny" got heavyrain and lightrain, and
    # `clear`, one of setweather's three possible values, was not offered at all. Eight benchmark cases
    # failed exactly there.
    #
    # The fill stops where the ranking starts to matter. Padding a thousand-item catalogue with whatever
    # sorts first would put back the original bug, which is how the enum for `give` came to read
    # "acid, acunit, addy, airpot" and never contained ogkush.
    everything = sum(len(value) for value in values)
    order = [value for _, _, _, value in ranked]
    if everything <= MAX_ENUM_CHARACTERS and len(values) <= MAX_ENUM_VALUES:
        order += unmatched

    chosen: list[str] = []
    characters = 0
    for value in order:
        if chosen and characters + len(value) > MAX_ENUM_CHARACTERS:
            continue
        chosen.append(value)
        characters += len(value)
        if len(chosen) >= MAX_ENUM_VALUES:
            break
    return chosen


def refinement_tool(tool: dict, assignment: dict, live_values: dict, query: str = "") -> dict:
    """Emit the schema the mod actually sends - NeedleTool.Write in Terminal/NeedleProtocol.cs.

    This used to strip parameters.type, additionalProperties, required, and every property's type and
    description, and FULL_DESCRIPTION_CHARACTERS of 0 left the tool description empty, all to save tokens.
    The runtime sends every one of them plus an enum of live values. That parameters block is the only
    thing separating a refinement ask from a routing ask, so training on the stripped version taught the
    adapter to read a shape no player ever produces: through the engine it answered the routing answer,
    the empty argument object, for 100 % of the holdout rows that needed arguments.
    """
    del assignment
    projected = copy.deepcopy(tool)
    projected["description"] = compact_description(tool, FULL_DESCRIPTION_CHARACTERS)
    parameters = projected["parameters"]
    parameters.setdefault("type", "object")
    parameters["additionalProperties"] = False
    slots = live_values.get(tool["name"], [])
    for index, prop in enumerate(parameters.get("properties", {}).values()):
        if prop.get("type") == "string" and index < len(slots):
            choices = enum_choices(slots[index], query)
            if choices:
                prop["enum"] = choices
    return projected


def expected_assignments(cases_path: pathlib.Path, tools_by_name: dict[str, dict]):
    cases = json.loads(cases_path.read_text(encoding="utf-8")).get("cases", [])
    excluded_queries = {normalized(case.get("query", "")) for case in cases}
    assignments: dict[str, list[dict]] = {}
    for case in cases:
        tokens = console_tokens(case.get("expected", ""))
        if not tokens or tokens[0] not in tools_by_name:
            continue
        properties = list(tools_by_name[tokens[0]]["parameters"].get("properties", {}))
        if not properties:
            assignments.setdefault(tokens[0], []).append({})
            continue
        values = tokens[1:]
        row = {}
        for index, prop in enumerate(properties):
            if index >= len(values):
                break
            row[prop] = " ".join(values[index:]) if index == len(properties) - 1 else values[index]
        assignments.setdefault(tokens[0], []).append(row)
    return assignments, excluded_queries


def example_assignment(tool: dict, metadata: dict | None = None) -> dict:
    properties = list(tool["parameters"].get("properties", {}))
    if not properties:
        return {}
    usage = str((metadata or {}).get("usage", "")).strip()
    if not usage:
        match = re.search(r"(?:^|\s)Example:\s*([^.;]+)", tool.get("description", ""), re.IGNORECASE)
        if not match:
            return {}
        usage = match.group(1)
    tokens = console_tokens(re.split(r"[;,]", usage, maxsplit=1)[0])
    if tokens and normalized(tokens[0]) == normalized(tool["name"]):
        tokens = tokens[1:]
    result = {}
    for index, prop in enumerate(properties):
        if index >= len(tokens):
            break
        value = " ".join(tokens[index:]) if index == len(properties) - 1 else tokens[index]
        if any(marker in value for marker in "<>[]|"):
            continue
        result[prop] = value
    return result


def spread(values: list[str], count: int, offset: int) -> list[str]:
    if not values:
        return []
    if len(values) <= count:
        return values
    step = len(values) / count
    return [values[min(len(values) - 1, math.floor((index + 0.5) * step + offset) % len(values))]
            for index in range(count)]


def scalar(prop: dict, scenario: int):
    enum = prop.get("enum")
    if enum:
        return enum[scenario % len(enum)]
    kind = prop.get("type", "string")
    if kind == "boolean":
        return scenario % 2 == 0
    if kind in ("integer", "number"):
        candidates = [1, 4, 10, 25, 100, 1200]
        value = candidates[scenario % len(candidates)]
        if "minimum" in prop:
            value = max(value, prop["minimum"])
        if "maximum" in prop:
            value = min(value, prop["maximum"])
        return int(value) if kind == "integer" else float(value)
    return f"sample{scenario + 1}"


def coerce(prop: dict, value):
    kind = prop.get("type", "string")
    if kind == "integer" and isinstance(value, str):
        return int(float(value))
    if kind == "number" and isinstance(value, str):
        return float(value)
    if kind == "boolean" and isinstance(value, str):
        if value.casefold() in ("true", "on", "yes", "1"):
            return True
        if value.casefold() in ("false", "off", "no", "0"):
            return False
    return value


def coerce_assignment(tool: dict, assignment: dict) -> dict:
    properties = tool["parameters"].get("properties", {})
    return {name: coerce(properties[name], value) for name, value in assignment.items() if name in properties}


def make_assignments(tool: dict, live_values: dict, gold: list[dict], count: int,
                     metadata: dict | None = None) -> list[dict]:
    properties = tool["parameters"].get("properties", {})
    required = set(tool["parameters"].get("required", []))
    slots = live_values.get(tool["name"], [])
    example = example_assignment(tool, metadata)
    rows = []

    for raw_candidate in gold:
        candidate = coerce_assignment(tool, raw_candidate)
        try:
            validate_arguments(tool, candidate)
        except (TypeError, ValueError):
            continue
        if candidate not in rows:
            rows.append(candidate)

    scenario = 0
    while len(rows) < count:
        row = {}
        for index, (name, prop) in enumerate(properties.items()):
            if name not in required and scenario % 3 == 0:
                continue
            slot_values = slots[index] if index < len(slots) else []
            candidates = spread(slot_values, count, index)
            if candidates:
                value = coerce(prop, candidates[scenario % len(candidates)])
            elif name in example and not prop.get("enum") and not slot_values:
                value = coerce(prop, example[name])
            elif name in example and scenario == 0:
                value = coerce(prop, example[name])
            else:
                value = scalar(prop, scenario + index)
            row[name] = value
        if row not in rows or not properties:
            rows.append(row)
        scenario += 1
        if scenario > count * 8:
            if not rows:
                raise RuntimeError(f"could not construct any valid assignment for {tool['name']}")
            finite = list(rows)
            while len(rows) < count:
                rows.append(copy.deepcopy(finite[len(rows) % len(finite)]))
    return rows[:count]


def mark_kinds(tool: dict, metadata: dict | None) -> list[str]:
    """Return live catalogue kinds, with a compatibility fallback for pre-metadata snapshots."""
    properties = list(tool["parameters"].get("properties", {}).values())
    declared = list((metadata or {}).get("marks") or [])
    if len(declared) == len(properties):
        return [str(kind).casefold() for kind in declared]

    inferred = []
    for prop in properties:
        description = str(prop.get("description", "")).casefold()
        if "#=current" not in description:
            inferred.append("none")
        elif "location" in description:
            inferred.append("any")
        elif "npc" in description:
            inferred.append("npc")
        elif "vehicle" in description:
            inferred.append("vehicle")
        elif "property" in description or "business" in description:
            inferred.append("property")
        elif any(word in description for word in ("item", "product", "packaging")):
            inferred.append("item")
        else:
            inferred.append("any")
    return inferred


def mark_feature_rows(tool: dict, metadata: dict, assignments: list[dict]) -> list[dict]:
    properties = list(tool["parameters"].get("properties", {}))
    rows = []
    if not properties or not assignments:
        return rows
    for slot, kind in enumerate(mark_kinds(tool, metadata)):
        if slot >= len(properties) or kind not in MARKS_BY_KIND:
            continue
        argument = properties[slot]
        for mark_index, mark in enumerate(MARKS_BY_KIND[kind]):
            for language_index, language in enumerate(LANGUAGES):
                for split_index, split in enumerate(FEATURE_SPLITS):
                    assignment = copy.deepcopy(
                        assignments[(mark_index + language_index + split_index) % len(assignments)])
                    assignment[argument] = mark
                    try:
                        validate_arguments(tool, assignment)
                    except (TypeError, ValueError):
                        continue
                    rows.append({
                        "id": (f"feature|{tool['name']}|{argument}|{mark[1:] or 'looked'}|"
                               f"{language}|{split}|{split_index}"),
                        "command": tool["name"], "language": language,
                        "scenario": split_index, "split": split,
                        "description": metadata.get("description") or tool.get("description", ""),
                        "usage": metadata.get("usage", ""), "arguments": assignment,
                        "argument_meanings": {argument: MARK_MEANINGS[mark]},
                    })
    return rows


def validate_arguments(tool: dict, arguments: dict):
    schema = tool["parameters"]
    properties = schema.get("properties", {})
    unknown = set(arguments) - set(properties)
    missing = set(schema.get("required", [])) - set(arguments)
    if unknown or missing:
        raise ValueError(f"{tool['name']}: invalid keys unknown={unknown} missing={missing}")
    for name, value in arguments.items():
        prop = properties[name]
        kind = prop.get("type", "string")
        valid_type = ((kind == "string" and isinstance(value, str))
                      or (kind == "boolean" and isinstance(value, bool))
                      or (kind == "integer" and isinstance(value, int) and not isinstance(value, bool))
                      or (kind == "number" and isinstance(value, (int, float)) and not isinstance(value, bool)))
        if not valid_type:
            raise ValueError(f"{tool['name']}.{name}: {value!r} is not {kind}")
        if "enum" in prop and value not in prop["enum"]:
            raise ValueError(f"{tool['name']}.{name}: {value!r} is outside enum")


def grounding_reasoning(tool: dict, arguments: dict, meanings: dict | None = None) -> str:
    """A compact, label-derived source explanation for Needle's target-only grounding loss."""
    meanings = meanings or {}
    properties = tool["parameters"].get("properties", {})
    parts = []
    for name, value in arguments.items():
        label = str(properties.get(name, {}).get("description", name)).split(";", 1)[0].strip() or name
        rendered = json.dumps(value, ensure_ascii=False, separators=(",", ":"))
        if name in meanings:
            parts.append(f"{label}={rendered} means {meanings[name]}")
        else:
            parts.append(f"{label}={rendered} from request")
    return "; ".join(parts)


TEACHER_SYSTEM_PROMPT = (
    "You write precise natural-language console requests for supervised tool-calling data. "
    "Follow every supplied operation and canonical argument exactly. Return only the requested JSON.")


def teacher_json(args, prompt: str, output_schema: dict, seed: int, expected_items: int,
                 model: str | None = None):
    """One teacher call, routed to whichever local server is serving the model."""
    model = model or args.model
    if args.api == "ollama":
        return ollama_json(args.base_url, model, prompt, output_schema, seed, expected_items)
    return openai_json(args.base_url, model, prompt, seed, expected_items, args.api_key)


def as_items_object(parsed):
    """A reply that is a bare array is the "items" array without its envelope; anything else that is not
    an object is not a reply at all and falls through to the next parsing strategy."""
    if isinstance(parsed, dict):
        return parsed
    if isinstance(parsed, list):
        return {"items": parsed}
    raise json.JSONDecodeError("the teacher reply is not a JSON object", "", 0)


def teacher_items(response) -> dict:
    """id -> query from a teacher reply, tolerating every malformed shape.

    A reply the teacher got structurally wrong has to be a rejected batch, not a crash: the three callers
    all compare the result against the expected ids, so an empty mapping is retried and then split. Before
    this existed a bare array reply reached .get() and killed a run hours in."""
    items = response.get("items") if isinstance(response, dict) else None
    if not isinstance(items, list):
        return {}
    return {item.get("id"): item.get("query") for item in items if isinstance(item, dict)}


def extract_json_object(text: str) -> dict:
    """Recover the JSON object from a reply that a server could not constrain to a schema.

    FreeToken and other OpenAI-compatible engines reject response_format json_schema because they have no
    constrained decoding, so the reply is ordinary text that usually - not always - contains exactly the object
    that was asked for. A fenced block, a leading sentence or a trailing note must not cost a whole batch.
    """
    stripped = text.strip()
    try:
        return as_items_object(json.loads(stripped))
    except json.JSONDecodeError:
        pass
    fenced = re.search(r"```(?:json)?\s*(.+?)```", stripped, re.DOTALL)
    if fenced:
        try:
            return as_items_object(json.loads(fenced.group(1).strip()))
        except json.JSONDecodeError:
            pass
    start = stripped.find("{")
    while start != -1:
        depth = 0
        in_string = False
        escaped = False
        for index in range(start, len(stripped)):
            character = stripped[index]
            if in_string:
                if escaped:
                    escaped = False
                elif character == "\\":
                    escaped = True
                elif character == '"':
                    in_string = False
                continue
            if character == '"':
                in_string = True
            elif character == "{":
                depth += 1
            elif character == "}":
                depth -= 1
                if depth == 0:
                    try:
                        return json.loads(stripped[start:index + 1])
                    except json.JSONDecodeError:
                        break
        start = stripped.find("{", start + 1)
    raise json.JSONDecodeError("no JSON object in the teacher reply", stripped, 0)


def openai_json(base: str, model: str, prompt: str, seed: int, expected_items: int,
                api_key: str = ""):
    """Teacher call against an OpenAI-compatible server without constrained decoding.

    FreeToken answers response_format json_object/json_schema with an error, so the schema is stated in the
    prompt and the reply is parsed defensively. A malformed reply raises JSONDecodeError, which teacher_batch
    already treats as a retryable attempt.
    """
    output_tokens = max(512, min(8192, expected_items * 48 + 256))
    instruction = (
        "\n\nOUTPUT FORMAT: return one JSON object and nothing else - no prose, no explanation, no markdown "
        "fence. The object has exactly one key \"items\", whose value is an array of objects with exactly the "
        "keys \"id\" and \"query\", both strings. Return every input id exactly once.")
    payload = json.dumps({
        "model": model,
        "stream": False,
        "seed": seed,
        "max_tokens": output_tokens,
        "temperature": 0.72,
        # Reasoning teachers answer with an empty string otherwise. Measured against FreeToken
        # serving Qwen3.6-35B-A3B: a 68-item batch spent all 3520 tokens on reasoning_content and
        # returned no content at all, so every one of the three attempts failed. With reasoning off
        # the same batch answers in 992 tokens. Servers without a reasoning mode ignore the field.
        "reasoning_effort": "none",
        "messages": [
            {"role": "system", "content": TEACHER_SYSTEM_PROMPT},
            {"role": "user", "content": prompt + instruction},
        ],
    }).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    if api_key:
        headers["Authorization"] = "Bearer " + api_key
    request = urllib.request.Request(
        base.rstrip("/") + "/v1/chat/completions", data=payload, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=600) as response:
            envelope = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        body = error.read().decode("utf-8", errors="replace")
        raise urllib.error.URLError(f"HTTP {error.code}: {body}") from error
    choice = envelope["choices"][0]["message"]
    content = choice.get("content")
    if not content:
        # A reasoning model can spend the whole budget in reasoning_content and answer with an empty string.
        raise json.JSONDecodeError("the teacher returned no content", "", 0)
    return extract_json_object(content)


def ollama_json(base: str, model: str, prompt: str, output_schema: dict, seed: int,
                expected_items: int):
    output_tokens = max(512, min(4096, expected_items * 32 + 128))
    payload = json.dumps({
        "model": model,
        "stream": False,
        "think": False,
        "keep_alive": -1,
        "format": output_schema,
        "messages": [
            {"role": "system", "content": TEACHER_SYSTEM_PROMPT},
            {"role": "user", "content": prompt},
        ],
        "options": {"temperature": 0.72, "seed": seed, "num_ctx": 8192,
                    "num_predict": output_tokens},
    }).encode("utf-8")
    request = urllib.request.Request(
        base.rstrip("/") + "/api/chat", data=payload,
        headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(request, timeout=180) as response:
            envelope = json.loads(response.read().decode("utf-8"))
    except urllib.error.HTTPError as error:
        body = error.read().decode("utf-8", errors="replace")
        raise urllib.error.URLError(f"HTTP {error.code}: {body}") from error
    return json.loads(envelope["message"]["content"])


OUTPUT_SCHEMA = {
    "type": "object",
    "properties": {
        "items": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {"id": {"type": "string"}, "query": {"type": "string"}},
                "required": ["id", "query"],
                "additionalProperties": False,
            },
        },
    },
    "required": ["items"],
    "additionalProperties": False,
}


def teacher_batch(rows: list[dict], args, cache_dir: pathlib.Path, batch_id: str,
                  forbidden: set[str] | None = None, allow_alternatives: bool = True):
    forbidden = forbidden or set()
    off_topic = all(row.get("offtopic") for row in rows)
    cache_input = {"prompt_version": TEACHER_PROMPT_VERSION, "model": args.model, "rows": rows}

    def unselected_documented_modes(row: dict) -> list[str]:
        """Find quoted subcommand choices which are not selected by this row's canonical arguments."""
        phrases = re.findall(r"['\"]([^'\"]+)['\"]", str(row.get("description", "")))
        modes = []
        for phrase in phrases:
            first = re.sub(r"[^\w-]", "", phrase.split(maxsplit=1)[0], flags=re.UNICODE).casefold()
            if first and first not in modes:
                modes.append(first)
        if len(modes) < 2:
            return []
        selected = {normalized(str(value)) for value in row.get("arguments", {}).values()
                    if isinstance(value, str)}
        return [mode for mode in modes if normalized(mode) not in selected]

    excluded_modes = {row["id"]: unselected_documented_modes(row) for row in rows}
    excluded_modes = {key: value for key, value in excluded_modes.items() if value}
    batch_languages = {row.get("language") for row in rows if row.get("language") in LANGUAGES}
    required_language = next(iter(batch_languages)) if len(batch_languages) == 1 else None
    language_guard = ""
    if required_language:
        language_name = LANGUAGES[required_language]
        language_guard = (
            f"MANDATORY OUTPUT LANGUAGE: {language_name}. Write every ordinary action and connective word in "
            f"{language_name}; do not echo the English operation description. Only opaque game identifiers or "
            "canonical subcommand tokens may remain untranslated.\n"
        )
    if excluded_modes:
        cache_input["multi_mode_policy"] = MULTI_MODE_POLICY_VERSION
    if forbidden:
        cache_input["forbidden"] = sorted(forbidden)
    cache_key = hashlib.sha256(json.dumps(cache_input, sort_keys=True, ensure_ascii=False)
                               .encode("utf-8")).hexdigest()
    cache_path = cache_dir / f"{batch_id}-{cache_key[:16]}.json"
    # Only successful batches are cached, so every restart re-paid the attempts for batches that never
    # validate - the 68-row batch has not validated once in 43 commands, and replaying 43 of them cost
    # over two hours before any new work happened. This marker records "the direct ask does not work for
    # these exact rows", which sends a restart straight to the split that produced the cached parts.
    rejected_path = cache_dir / f"{batch_id}-{cache_key[:16]}.rejected"
    expected_ids = {row["id"] for row in rows}
    rows_by_id = {row["id"]: row for row in rows}

    def valid(response: dict) -> bool:
        found = teacher_items(response)
        return (set(found) == expected_ids
                and all(isinstance(query, str) and query.strip()
                        and len(query.split()) <= MAX_ACCEPTED_TEACHER_WORDS
                        for query in found.values())
                and len({normalized(query) for query in found.values()}) == len(found)
                and all(grounds_literals(rows_by_id[key], query)
                        for key, query in found.items() if key in rows_by_id and isinstance(query, str))
                and all(not (set(normalized(query).split()) & set(excluded_modes.get(key, [])))
                        for key, query in found.items() if isinstance(query, str))
                and not ({normalized(query) for query in found.values()}
                         & {normalized(query) for query in forbidden}))

    response = None
    split_duplicate_batch = False
    cache_candidates = [cache_path]
    cache_candidates.extend(path for path in cache_dir.glob(f"*-{cache_key[:16]}.json")
                            if path != cache_path)
    for candidate in cache_candidates:
        if not candidate.exists():
            continue
        cached = json.loads(candidate.read_text(encoding="utf-8"))
        if valid(cached):
            response = cached
            break
        if candidate == cache_path and not off_topic and len(rows) > len(LANGUAGES):
            cached_queries = [normalized(item.get("query", "")) for item in cached.get("items", [])]
            split_duplicate_batch = len(set(cached_queries)) < len(cached_queries)
    if response is None:
        if split_duplicate_batch:
            result = {}
            used = set(forbidden)
            scenarios = sorted({row.get("scenario", 0) for row in rows})
            for scenario in scenarios:
                group = [row for row in rows if row.get("scenario", 0) == scenario]
                generated_group = teacher_batch(
                    group, args, cache_dir, f"{batch_id}-variation-{scenario}", used)
                result.update(generated_group)
                used.update(generated_group.values())
            return result
        if off_topic:
            compact_rows = [{
                "id": row["id"],
                "language": LANGUAGES[row["language"]],
                "variation_key": row["variation"],
            } for row in rows]
            prompt = language_guard + (
                "For every input row, invent one distinct, ordinary user request unrelated to games, game consoles, "
                "commands, software control, or the other rows. Write it in the named language. Use the opaque "
                "variation_key only to force variety; never mention or translate the key. Do not copy these "
                f"instructions. Use at most {MAX_TEACHER_WORDS} whitespace-separated words and return every id "
                "exactly once."
                + ("\nDo not repeat any of these prior requests:\n"
                   + json.dumps(sorted(forbidden), ensure_ascii=False) if forbidden else "")
                + "\nINPUT:\n" + json.dumps(compact_rows, ensure_ascii=False))
        else:
            compact_rows = [{
                "id": row["id"],
                "language": LANGUAGES[row["language"]],
                "operation": row["description"],
                "usage_example": row.get("usage", ""),
                "canonical_arguments": row["arguments"],
                "natural_argument_meanings": row.get("argument_meanings", {}),
                "unselected_documented_modes": excluded_modes.get(row["id"], []),
                "variation_key": row["id"].rsplit("|", 1)[-1],
                "wording_style": VARIATION_STYLES[int(row["id"].rsplit("|", 1)[-1])
                                                    % len(VARIATION_STYLES)],
            } for row in rows]
            prompt = language_guard + (
                "Write exactly one distinct user request for every input row. The request must be in the named "
                "language, express only that operation, and explicitly convey every canonical argument. Humanize "
                "opaque game identifiers when natural (for example a joined item id may be written as normal "
                "spaced words), but do not change their meaning. Follow wording_style naturally. Use variation_key "
                "only to vary wording; never mention either metadata field. No two query strings may be identical, "
                "When natural_argument_meanings supplies a meaning, express that meaning naturally instead of "
                "copying its canonical context token. "
                "If unselected_documented_modes is nonempty, the operation documents several subcommands: express "
                "only the mode selected by canonical_arguments and usage_example, and never mention any unselected "
                "mode. "
                "even when operation and arguments repeat. Any canonical argument matching sample plus digits is "
                "an opaque one-token value and must appear exactly unchanged in the request. A numeric "
                "canonical argument may be written as digits or spelled out as a word in the request "
                "language, whichever a player would actually say. Any other canonical argument may be "
                "written the natural way a player would say it rather than as the exact identifier. "
                f"Use at most {MAX_TEACHER_WORDS} whitespace-separated words per request. Do not include '#', JSON, "
                "explanations, or invent extra intent. Return every id exactly once.\nINPUT:\n"
                + json.dumps(compact_rows, ensure_ascii=False)
                + ("\nDo not reuse any of these prior request strings:\n"
                   + json.dumps(sorted(forbidden), ensure_ascii=False) if forbidden else ""))
        retry_note = ""
        rejected_duplicates: set[str] = set()
        # Only assigned once a reply has been scored, but the exhausted-attempts message below reads it,
        # so a batch whose every attempt raised would die of NameError instead of the real error.
        wrong_modes: dict[str, list[str]] = {}
        for attempt in range(0 if rejected_path.exists() else 3):
            try:
                teacher_model = args.fallback_model if attempt >= 2 and args.fallback_model else args.model
                response = teacher_json(args, prompt + retry_note, OUTPUT_SCHEMA,
                                        args.seed + int(cache_key[:8], 16) + attempt, len(rows),
                                        teacher_model)
                if valid(response):
                    break
                found = teacher_items(response)
                normalized_queries = [normalized(query) for query in found.values()
                                      if isinstance(query, str)]
                duplicates = sorted(query for query, count in Counter(normalized_queries).items()
                                    if count > 1)
                blocked = sorted(set(normalized_queries) & {normalized(query) for query in forbidden})
                blocked_ids = [key for key, query in found.items()
                               if isinstance(query, str) and normalized(query) in set(blocked)]
                rejected_duplicates.update(duplicates)
                rejected_duplicates.update(blocked)
                overlong = [key for key, query in found.items()
                            if isinstance(query, str)
                            and len(query.split()) > MAX_ACCEPTED_TEACHER_WORDS]
                ungrounded = [key for key, query in found.items()
                              if key in rows_by_id and isinstance(query, str)
                              and not grounds_literals(rows_by_id[key], query)]
                wrong_modes = {key: sorted(set(normalized(query).split())
                                           & set(excluded_modes.get(key, [])))
                               for key, query in found.items() if isinstance(query, str)}
                wrong_modes = {key: value for key, value in wrong_modes.items() if value}
                retry_note = (
                    "\nCORRECTION FOR THIS RETRY: Return the complete list again. Do not use any of these exact "
                    f"duplicate strings: {json.dumps(sorted(rejected_duplicates), ensure_ascii=False)}. Make these IDs shorter: "
                    f"{json.dumps(overlong, ensure_ascii=False)}. Copy every sample+digits argument exactly for "
                    f"these IDs: {json.dumps(ungrounded, ensure_ascii=False)}. Copy their numeric arguments as "
                    f"digits too. Remove these unselected documented modes: "
                    f"{json.dumps(wrong_modes, ensure_ascii=False)}. "
                    + (f"For these repeated IDs, ignore their original wording_style and use a clearly different "
                       f"grammatical construction, voice, and word order: "
                       f"{json.dumps(blocked_ids, ensure_ascii=False)}. " if blocked_ids else "")
                    + (f"Rewrite ordinary wording in {LANGUAGES[required_language]}; do not copy the English "
                       "operation description. " if required_language else "")
                    + "Preserve all other requirements."
                )
            except (OSError, urllib.error.URLError, json.JSONDecodeError, KeyError) as failure:
                # A prompt that does not fit the teacher's context will not fit on a retry either. Letting
                # the attempts run out reaches the split below, which asks the same rows in small batches
                # that do fit - the behaviour the code already has for a batch the teacher cannot answer.
                if "context_length_exceeded" in str(failure):
                    continue
                if attempt == 2:
                    raise
                time.sleep(2)
        else:
            rejected_path.write_text("", encoding="utf-8")
            found = teacher_items(response)
            normalized_queries = [normalized(query) for query in found.values() if isinstance(query, str)]
            duplicates = sorted(query for query, count in Counter(normalized_queries).items() if count > 1)
            if not off_topic and len(rows) > len(LANGUAGES):
                result = {}
                used = set(forbidden)
                scenarios = sorted({row.get("scenario", 0) for row in rows})
                for scenario in scenarios:
                    group = [row for row in rows if row.get("scenario", 0) == scenario]
                    generated_group = teacher_batch(
                        group, args, cache_dir, f"{batch_id}-variation-{scenario}", used,
                        allow_alternatives)
                    result.update(generated_group)
                    used.update(generated_group.values())
                return result
            if not off_topic and len(rows) > 1:
                result = {}
                used = set(forbidden)
                for row in rows:
                    # Some rows are genuinely unsatisfiable: by variation 15 of 17 a simple command has
                    # no phrasing left in that language that is also distinct from all 60 already used.
                    # That has to cost one phrasing out of ~9,400, not an eight-hour run.
                    try:
                        generated_row = teacher_batch(
                            [row], args, cache_dir, f"{batch_id}-language-{row['language']}", used,
                            allow_alternatives)
                    except RuntimeError as exhausted:
                        UNSATISFIED_ROWS.append((row["id"], str(exhausted)))
                        print(f"  dropped {row['id']}: teacher has no phrasing left", flush=True)
                        continue
                    result.update(generated_row)
                    used.update(generated_row.values())
                return result
            if not off_topic and len(rows) == 1 and allow_alternatives:
                # One candidate per call, and a single row with alternatives disabled can reach neither
                # split branch, so this is provably the last level of the recursion. Asking for all six
                # styles in one batch meant one bad style failed the batch, which split back into single
                # rows that were allowed to ask for alternatives again: the cycle that produced
                # "-alternatives-variation-6-alternatives-variation-6-..." until a Windows path limit
                # raised OSError. On Linux it would not have stopped at all.
                original = rows[0]
                for variation in range(len(VARIATION_STYLES)):
                    candidate = copy.deepcopy(original)
                    candidate["id"] = original["id"].rsplit("|", 1)[0] + f"|{variation + 6}"
                    candidate["scenario"] = variation + 6
                    try:
                        alternative = teacher_batch(
                            [candidate], args, cache_dir, f"{batch_id}-alternative-{variation}",
                            forbidden, allow_alternatives=False)
                    except RuntimeError:
                        continue
                    return {original["id"]: next(iter(alternative.values()))}
            raise RuntimeError(
                f"teacher rejected for {batch_id}: missing={sorted(expected_ids - set(found))[:5]} "
                f"extra={sorted(set(found) - expected_ids)[:5]} duplicates={duplicates[:5]} "
                f"blocked={sorted(set(normalized_queries) & {normalized(query) for query in forbidden})[:5]} "
                f"wrong_modes={wrong_modes} "
                f"overlong={[key for key, query in found.items() if isinstance(query, str) and len(query.split()) > MAX_ACCEPTED_TEACHER_WORDS][:5]} "
                f"ungrounded={[key for key, query in found.items() if key in rows_by_id and isinstance(query, str) and not grounds_literals(rows_by_id[key], query)][:5]}")
        cache_path.write_text(json.dumps(response, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return {item["id"]: item["query"].strip() for item in response["items"]}


def resolve_split_leakage(splits: dict[str, list[dict]]) -> list[tuple[str, str]]:
    """Remove queries that landed in two splits at once, returning what was removed and from where.

    Near-synonymous commands ("disableterrain" and "disable terrain") get phrased identically, and the
    duplicate guard during row building only compares within one split. A query in both train and an
    evaluation split makes that split measure memorisation, so it has to go - but validate_dataset also
    demands that validation and holdout cover every command, language and phase, so removing it there
    would only trade this failure for the next one. Training is where a lost variation costs nothing:
    there are per_language - 2 * eval_per_language of them per command and language. When both sides are
    evaluation splits neither is preferable and the later one gives way.
    """
    removed: list[tuple[str, str]] = []
    split_queries = {name: {normalized(row["query"]) for row in rows} for name, rows in splits.items()}
    for left, right in (("train", "validation"), ("train", "holdout"),
                        ("validation", "holdout")):
        overlap = split_queries[left] & split_queries[right]
        if not overlap:
            continue
        victim = left if left == "train" else right
        removed.extend((victim, query) for query in sorted(overlap))
        splits[victim] = [row for row in splits[victim]
                          if normalized(row["query"]) not in overlap]
        split_queries[victim] = {normalized(row["query"]) for row in splits[victim]}
    return removed


def off_topic_chunk(rows: list[dict], args, cache_dir: pathlib.Path, batch_id: str,
                    seen: set[str]) -> dict:
    """Ask for off-topic negatives, halving the batch whenever the teacher runs out of distinct ones.

    Off-topic rows carry no scenario and no per-language grouping, so every split branch in teacher_batch
    is gated off for them - splitting by scenario would put all 32 in one group and recurse forever. That
    left them with no degradation at all: the teacher started repeating itself around the fifth batch of
    32 and killed the run. Halving is the split that does apply here, it bottoms out after five levels,
    and a smaller ask has fewer phrasings to keep apart in the first place.
    """
    try:
        return teacher_batch(rows, args, cache_dir, batch_id, seen)
    except RuntimeError:
        if len(rows) == 1:
            UNSATISFIED_ROWS.append((rows[0]["id"], "no distinct off-topic phrasing left"))
            print(f"  dropped {rows[0]['id']}: no distinct off-topic phrasing left", flush=True)
            return {}
        middle = len(rows) // 2
        produced = off_topic_chunk(rows[:middle], args, cache_dir, f"{batch_id}a", seen)
        produced.update(off_topic_chunk(rows[middle:], args, cache_dir, f"{batch_id}b",
                                        seen | set(produced.values())))
        return produced


NUMBER_WORDS = {
    "en": {1: "one", 2: "two", 3: "three", 4: "four", 5: "five", 6: "six", 7: "seven", 8: "eight",
           9: "nine", 10: "ten", 12: "twelve", 15: "fifteen", 20: "twenty", 50: "fifty", 100: "a hundred"},
    "de": {1: "eins", 2: "zwei", 3: "drei", 4: "vier", 5: "fünf", 6: "sechs", 7: "sieben", 8: "acht",
           9: "neun", 10: "zehn", 12: "zwölf", 15: "fünfzehn", 20: "zwanzig", 50: "fünfzig", 100: "hundert"},
    "es": {1: "uno", 2: "dos", 3: "tres", 4: "cuatro", 5: "cinco", 6: "seis", 7: "siete", 8: "ocho",
           9: "nueve", 10: "diez", 12: "doce", 15: "quince", 20: "veinte", 50: "cincuenta", 100: "cien"},
    "fr": {1: "un", 2: "deux", 3: "trois", 4: "quatre", 5: "cinq", 6: "six", 7: "sept", 8: "huit",
           9: "neuf", 10: "dix", 12: "douze", 15: "quinze", 20: "vingt", 50: "cinquante", 100: "cent"},
}


def spoken_variants(row: dict, rng: random.Random) -> list[dict]:
    """Rows where the argument is not spelled the way the schema spells it.

    The teacher prompt demands every numeric argument appear "exactly as digits" and grounds_literals
    enforces it, so out of 877 numeric training arguments exactly zero were ever written as a word, and a
    concatenated value practically never appeared with a space. Players write neither way: 49 of the 73
    hand-written benchmark cases with arguments say "zehn" for 10 or "og kush" for ogkush, and the first
    three adapters scored 0 % on them. These variants are derived from rows the teacher already wrote, so
    they cost no teacher call, and they carry the same answer - only the wording changes.
    """
    query = row["query"]
    produced = []
    for value in row["answers"][0]["arguments"].values():
        if isinstance(value, bool):
            continue
        if isinstance(value, (int, float)) and float(value).is_integer():
            word = NUMBER_WORDS.get(row["language"], {}).get(int(value))
            digits = format(value, "g")
            if word and re.search(rf"(?<![\w.]){re.escape(digits)}(?![\w.])", query):
                produced.append(re.sub(rf"(?<![\w.]){re.escape(digits)}(?![\w.])", word, query, count=1))
        # No spacing variants: the runtime normalises "og kush" and "b2s xgranddaddykush grapeape seed"
        # down to the canonical token before ranking the enum, so teaching that mapping is redundant. It
        # was worse than redundant - the split points were random, so rows like "gran ddaddypurpleseed"
        # taught noise, and the adapter trained with them lost five end-to-end cases. Number words are the
        # one form normalisation cannot reach: "zehn" never becomes 10.
    return produced


def write_jsonl(path: pathlib.Path, rows: list[dict]):
    path.write_text("".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n" for row in rows),
                    encoding="utf-8")


def rendered_tokens(row: dict, tokenizer) -> int:
    prompt, target = render_example(row)
    return len(tokenizer.encode(prompt)) + len(tokenizer.encode(target)) + 2


def fit_route_budget(row: dict, tokenizer) -> dict:
    gold = row["answers"][0]["name"] if row["answers"] else None
    while rendered_tokens(row, tokenizer) > TOKEN_BUDGET and len(row["tools"]) > 1:
        removable = next((index for index in range(len(row["tools"]) - 1, -1, -1)
                          if row["tools"][index]["name"] != gold), None)
        if removable is None:
            break
        row["tools"].pop(removable)
    return row


def validate_dataset(rows: list[dict], tools_by_name: dict[str, dict], excluded: set[str], tokenizer):
    """Keep the rows that are usable, and say which were not and why.

    Two kinds of check live here and they deserve different answers. A structural invariant - a route row
    grounding arguments, a refine row without its tool, an unknown phase - means the generator itself is
    wrong, and still raises. A data-quality filter only means one teacher phrasing is unusable: it collides
    with a hand-written benchmark query, repeats another row, or does not fit Needle's 256-token window.
    Those are properties of one row out of thousands, and aborting the run over them threw away hours of
    teacher work each time.
    """
    seen = set()
    kept = []
    rejected: list[tuple[str, str]] = []
    for row in rows:
        query = normalized(row["query"])
        if not query:
            rejected.append(("empty phrasing", row["query"]))
            continue
        if query in excluded:
            # Training on a benchmark phrasing would make that benchmark measure memorisation.
            rejected.append(("collides with a benchmark query", row["query"]))
            continue
        identity = (query, row["phase"])
        if identity in seen:
            rejected.append(("duplicate phrasing", row["query"]))
            continue
        seen.add(identity)
        answers = row["answers"]
        if answers:
            answer = answers[0]
            tool = tools_by_name[answer["name"]]
            if row["phase"] == "route":
                if answer["arguments"] != {}:
                    raise ValueError("route answers must not ground arguments")
            else:
                validate_arguments(tool, answer["arguments"])
        if len(row["tools"]) > 5:
            raise ValueError("training rows must stay outside Needle's >5-tool retrieval path")
        if row["phase"] == "route":
            if any("parameters" in tool for tool in row["tools"]):
                raise ValueError("route rows must use compact parameterless tools")
        elif row["phase"] == "refine":
            if len(row["tools"]) != 1 or "parameters" not in row["tools"][0]:
                raise ValueError("refinement rows must contain exactly one full tool")
        else:
            raise ValueError(f"unknown phase: {row['phase']}")

        token_count = rendered_tokens(row, tokenizer)
        if token_count > TOKEN_BUDGET:
            # OUR budget, not the engine's. The archive records kv_window 256 and max_seq_len 2048, and the
            # documented window slides with the tools pinned as KV sinks - it is not a 256-token cap on a
            # serialised example. Keeping the cap is defensible; it was used once to justify stripping the
            # schemas, and that cost 24 points of routing before anyone checked what the number meant.
            rejected.append((f"{token_count} tokens over the {TOKEN_BUDGET} budget", row["query"]))
            continue
        kept.append(row)
    # Built from the kept rows, so a rejected row cannot take coverage another row still provides.
    coverage = {(row["answers"][0]["name"], row["language"], row["phase"])
                for row in kept if row["answers"]}
    return kept, coverage, rejected


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--tools", type=pathlib.Path, default=ROOT / "data" / "tools.json")
    parser.add_argument("--values", type=pathlib.Path, default=ROOT / "data" / "values.json")
    parser.add_argument("--commands", type=pathlib.Path, default=ROOT / "data" / "commands.json")
    parser.add_argument("--cases", type=pathlib.Path, default=ROOT.parent / "NeedleBenchmark" / "cases.json")
    parser.add_argument("--out", type=pathlib.Path, default=ROOT / "data")
    parser.add_argument("--cache", type=pathlib.Path, default=None,
                        help="teacher response cache (default: <out>/teacher-cache)")
    parser.add_argument("--api", choices=("ollama", "openai"), default="ollama",
                        help="teacher transport: Ollama's /api/chat with a hard JSON schema, or an "
                             "OpenAI-compatible /v1/chat/completions (FreeToken, vLLM, llama.cpp)")
    parser.add_argument("--base-url", default="",
                        help="teacher server root (default: http://127.0.0.1:11434 for ollama, "
                             "http://127.0.0.1:1919 for openai)")
    parser.add_argument("--ollama", dest="base_url", help=argparse.SUPPRESS)
    parser.add_argument("--api-key", default="",
                        help="bearer token for the OpenAI-compatible server; local servers need none")
    parser.add_argument("--model", default="qwen3:8b")
    parser.add_argument("--fallback-model", default="",
                        help="stronger local teacher used only after three rejected attempts")
    parser.add_argument("--per-language", type=int, default=6)
    parser.add_argument("--eval-per-language", type=int, default=1,
                        help="number of variants per language reserved for each of validation and holdout")
    parser.add_argument("--batch-tools", type=int, default=1)
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--exclude-external-dev-sources", action="store_true",
                        help="exclude commands from third-party development builds while retaining all Hash commands")
    parser.add_argument("--dry-run", action="store_true",
                        help="skip Ollama and use deterministic structural-test queries")
    parser.add_argument("--seed", type=int, default=1701)
    args = parser.parse_args()

    if not args.base_url:
        args.base_url = ("http://127.0.0.1:11434" if args.api == "ollama"
                         else "http://127.0.0.1:1919")

    if args.eval_per_language < 1 or args.per_language <= 2 * args.eval_per_language:
        parser.error("--per-language must leave at least one training variant after both eval splits")
    args.out.mkdir(parents=True, exist_ok=True)
    tools = json.loads(args.tools.read_text(encoding="utf-8"))
    values = json.loads(args.values.read_text(encoding="utf-8"))
    command_metadata = ({entry["name"]: entry for entry in
                         json.loads(args.commands.read_text(encoding="utf-8"))}
                        if args.commands.exists() else {})
    if args.exclude_external_dev_sources:
        tools = [tool for tool in tools
                 if not ("-dev" in str(command_metadata.get(tool["name"], {}).get("source", "")).casefold()
                         and not str(command_metadata.get(tool["name"], {}).get("source", ""))
                         .casefold().startswith("hash"))]
    if args.limit:
        tools = tools[:args.limit]
    tools_by_name = {tool["name"]: tool for tool in tools}
    gold, excluded = expected_assignments(args.cases, tools_by_name)
    cache = args.cache or args.out / "teacher-cache"
    cache.mkdir(parents=True, exist_ok=True)
    rng = random.Random(args.seed)
    tokenizer = get_tokenizer()

    pending = []
    feature_pending = []
    context_by_command = {}
    for tool in tools:
        metadata = command_metadata.get(tool["name"], {})
        assignments = make_assignments(tool, values, gold.get(tool["name"], []), args.per_language,
                                       metadata)
        context = [route_tool(tool)] + [route_tool(candidate) for candidate in nearest_tools(tool, tools)]
        rng.shuffle(context)
        context_by_command[tool["name"]] = context
        for language in LANGUAGES:
            for scenario, assignment in enumerate(assignments):
                validate_arguments(tool, assignment)
                pending.append({
                    "id": f"{tool['name']}|{language}|{scenario}",
                    "command": tool["name"], "language": language, "scenario": scenario,
                    "description": metadata.get("description") or tool.get("description", ""),
                    "usage": metadata.get("usage", ""), "arguments": assignment,
                })
        feature_pending.extend(mark_feature_rows(tool, metadata, assignments))

    if args.dry_run:
        generated = {
            row["id"]: (f"test {index} {row['description'][:48]} "
                        f"with {json.dumps(row['arguments'], ensure_ascii=False)}")
            for index, row in enumerate(pending + feature_pending)
        }
    else:
        generated = {}
        rows_per_batch = max(1, args.batch_tools) * len(LANGUAGES) * args.per_language
        for offset in range(0, len(pending), rows_per_batch):
            batch = pending[offset:offset + rows_per_batch]
            print(f"teacher {offset // rows_per_batch + 1}/{math.ceil(len(pending) / rows_per_batch)} "
                  f"({len(batch)} phrasings)...", flush=True)
            generated.update(teacher_batch(batch, args, cache, f"commands-{offset // rows_per_batch:03d}"))
        for offset in range(0, len(feature_pending), rows_per_batch):
            batch = feature_pending[offset:offset + rows_per_batch]
            print(f"teacher Hash features {offset // rows_per_batch + 1}/"
                  f"{math.ceil(len(feature_pending) / rows_per_batch)} ({len(batch)} phrasings)...", flush=True)
            generated.update(teacher_batch(batch, args, cache,
                                           f"features-{offset // rows_per_batch:03d}", excluded))

    pending.extend(feature_pending)

    splits = {"train": [], "validation": [], "holdout": []}
    positive_seen: dict[tuple[str, str], str] = {}
    if UNSATISFIED_ROWS:
        print(f"{len(UNSATISFIED_ROWS)} phrasings dropped as unsatisfiable "
              f"(of {len(pending)} requested)", flush=True)
    for row in pending:
        query = generated.get(row["id"])
        if query is None:
            continue
        if "split" in row:
            split = row["split"]
        elif row["scenario"] < args.per_language - 2 * args.eval_per_language:
            split = "train"
        elif row["scenario"] < args.per_language - args.eval_per_language:
            split = "validation"
        else:
            split = "holdout"
        identity = (split, normalized(query))
        signature = json.dumps([row["command"], row["arguments"]], sort_keys=True, ensure_ascii=False)
        if identity in positive_seen:
            if positive_seen[identity] != signature:
                # Real catalogue ambiguity, not a teacher slip: the game has both "enable <thing>" and
                # "enableterrain", and "Please enable terrain" is the natural phrasing for either. Training
                # one sentence towards two different answers is worse than training it towards one, so the
                # first mapping wins and the later row is dropped - but it is counted, because a corpus
                # with many of these is describing a command set players cannot address unambiguously.
                AMBIGUOUS_ROWS.append((row["id"], query, positive_seen[identity], signature))
            continue
        positive_seen[identity] = signature
        common = {"command": row["command"], "language": row["language"], "split": split,
            "query": query,
            "system": system_facts(row["language"], row.get("scenario", 0)),
            "reasoning": ""}
        route = {**common,
            "id": row["id"] + "|route", "phase": "route",
            "tools": list(context_by_command[row["command"]]),
            "answers": [{"name": row["command"], "arguments": {}}],
        }
        splits[split].append(fit_route_budget(route, tokenizer))
        refine = {**common,
            "id": row["id"] + "|refine", "phase": "refine",
            # The query is not optional here. Without it enum_choices ranks nothing and the enum comes out
            # EMPTY, so the corpus taught the model to fill an argument with no candidates while the runtime
            # hands it a ranked list - the exact failure enum_choices was written to prevent, left in the one
            # path that builds the training rows. refresh_enums.py repairs rows after the fact and the
            # pipeline never calls it.
            "tools": [refinement_tool(tools_by_name[row["command"]], row["arguments"], values, query)],
            "reasoning": grounding_reasoning(tools_by_name[row["command"]], row["arguments"],
                                               row.get("argument_meanings")),
            "answers": [{"name": row["command"], "arguments": row["arguments"]}],
        }
        # A derivation is valuable only when it does not truncate the actual call in Needle's 256-token window.
        if rendered_tokens(refine, tokenizer) > TOKEN_BUDGET:
            refine["reasoning"] = ""
        # Training only: the evaluation splits stay exactly what the teacher wrote, so the holdout and the
        # hand-written benchmark remain independent yardsticks for whether this augmentation helped.
        if split == "train" and row["arguments"]:
            variants = spoken_variants(refine, rng)
            if variants:
                spoken = {**refine, "id": refine["id"] + "|spoken",
                          "query": rng.choice(variants)}
                if rendered_tokens(spoken, tokenizer) <= TOKEN_BUDGET:
                    splits[split].append(spoken)
        splits[split].append(refine)

    if AMBIGUOUS_ROWS:
        print(f"{len(AMBIGUOUS_ROWS)} phrasings dropped as ambiguous between two commands:", flush=True)
        for identifier, query, existing, current in AMBIGUOUS_ROWS[:20]:
            print(f"  {identifier}: {query!r} -> kept {existing}, dropped {current}", flush=True)
        if len(AMBIGUOUS_ROWS) > 20:
            print(f"  ... and {len(AMBIGUOUS_ROWS) - 20} more", flush=True)

    # Off-topic negatives are generated by the same teacher without hand-authored topic lists.
    off_topic_seen: set[str] = set()
    for split, per_language in (("train", max(1, math.ceil(len(splits["train"]) / 2 / 8 / 4))),
                                ("validation", 8), ("holdout", 8)):
        off_rows = [{
            "id": f"offtopic|{split}|{language}|{index}", "language": language, "arguments": {},
            "offtopic": True, "variation": f"{split}-{language}-{index}",
            "description": "A request unrelated to game console operations; it must not match any supplied tool.",
        } for language in LANGUAGES for index in range(per_language)]
        if args.dry_run:
            off = {item["id"]: f"[{item['language']} {split} {index}] discuss an unrelated topic"
                   for index, item in enumerate(off_rows)}
        else:
            off = {}
            for offset in range(0, len(off_rows), 32):
                chunk = off_rows[offset:offset + 32]
                print(f"teacher off-topic {split} {offset // 32 + 1}/{math.ceil(len(off_rows) / 32)} "
                      f"({len(chunk)} phrasings)...", flush=True)
                generated_chunk = off_topic_chunk(
                    chunk, args, cache, f"offtopic-{split}-{offset // 32:02d}", off_topic_seen)
                off.update(generated_chunk)
                off_topic_seen.update(generated_chunk.values())
        for item in off_rows:
            if item["id"] not in off:
                continue
            context = [route_tool(tool) for tool in rng.sample(tools, min(5, len(tools)))]
            negative = {
                "id": item["id"], "command": "", "language": item["language"], "split": split,
                "system": system_facts(item["language"], off_rows.index(item)),
                "phase": "route",
                "query": off[item["id"]], "tools": context,
                "reasoning": "", "answers": [],
            }
            splits[split].append(fit_route_budget(negative, tokenizer))

    expected_coverage = {(tool["name"], language, phase) for tool in tools for language in LANGUAGES
                         for phase in ("route", "refine")}
    leaked = resolve_split_leakage(splits)
    if leaked:
        print(f"{len(leaked)} queries appeared in two splits and were removed from one:", flush=True)
        for victim, query in leaked[:20]:
            print(f"  removed from {victim}: {query!r}", flush=True)
        if len(leaked) > 20:
            print(f"  ... and {len(leaked) - 20} more", flush=True)
    manifest = {"model": "dry-run" if args.dry_run else args.model,
                "fallbackModel": None if args.dry_run else args.fallback_model,
                "seed": args.seed, "perLanguage": args.per_language,
                "evalPerLanguage": args.eval_per_language,
                "excludeExternalDevSources": args.exclude_external_dev_sources,
                "commands": len(tools), "languages": list(LANGUAGES), "splits": {}}
    for split in list(splits):
        rng.shuffle(splits[split])
        rows, coverage, rejected = validate_dataset(splits[split], tools_by_name, excluded, tokenizer)
        splits[split] = rows
        if rejected:
            print(f"{split}: {len(rejected)} rows rejected", flush=True)
            for reason, query in rejected[:10]:
                print(f"  {reason}: {query!r}", flush=True)
            if len(rejected) > 10:
                print(f"  ... and {len(rejected) - 10} more", flush=True)
        # A hole here used to end the run. It is a reporting defect, not a corrupt corpus: the gate simply
        # measures less than the whole catalogue, and saying which combinations are missing is more use
        # than producing no adapter at all. Recorded in the manifest so the report can carry it.
        missing = sorted("|".join(entry) for entry in expected_coverage - coverage)
        if split != "train" and missing:
            print(f"WARNING: {split} covers {len(coverage)} of {len(expected_coverage)} "
                  f"command/language/phase combinations; missing {missing[:5]}"
                  + (f" and {len(missing) - 5} more" if len(missing) > 5 else ""), flush=True)
        path = args.out / f"{split}.jsonl"
        write_jsonl(path, rows)
        manifest["splits"][split] = {
            "rows": len(rows),
            "positive": sum(bool(row["answers"]) for row in rows),
            "negative": sum(not row["answers"] for row in rows),
            "route": sum(row["phase"] == "route" for row in rows),
            "refine": sum(row["phase"] == "refine" for row in rows),
            "rejected": len(rejected),
            "missingCoverage": missing if split != "train" else [],
        }
        print(f"{split}: {len(rows)} validated rows -> {path}")
    (args.out / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
