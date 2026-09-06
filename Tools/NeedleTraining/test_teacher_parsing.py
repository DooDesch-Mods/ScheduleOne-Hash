"""Self-check for the defensive teacher-reply parsing. Run: .venv/Scripts/python.exe test_teacher_parsing.py

Every shape here was either observed from a live teacher or is one .get() away from killing a run that
has already spent hours, which is why the parsing is total rather than trusting the schema.
"""
import json

from generate_data import as_items_object, extract_json_object, teacher_items

ITEMS = [{"id": "a", "query": "give me three grams"}]
WANT = {"a": "give me three grams"}

# The envelope the schema asks for.
assert extract_json_object(json.dumps({"items": ITEMS})) == {"items": ITEMS}
# A bare array is the items array without its envelope - recovered, not thrown away.
assert extract_json_object(json.dumps(ITEMS)) == {"items": ITEMS}
# Fenced, with a leading sentence and a trailing note.
assert extract_json_object('Sure:\n```json\n' + json.dumps(ITEMS) + '\n```\nHope that helps.') == {"items": ITEMS}
assert extract_json_object('Here:\n```\n' + json.dumps({"items": ITEMS}) + '\n```') == {"items": ITEMS}
# A scalar is not a reply; it falls through to the brace scanner and then raises for the retry.
for junk in ('"nope"', "42", "null", "no json here at all"):
    try:
        extract_json_object(junk)
        raise AssertionError("expected JSONDecodeError for " + junk)
    except json.JSONDecodeError:
        pass
try:
    as_items_object(42)
    raise AssertionError("expected JSONDecodeError")
except json.JSONDecodeError:
    pass

# teacher_items is total: a malformed shape is an empty mapping, which the callers reject and retry.
assert teacher_items({"items": ITEMS}) == WANT
assert teacher_items(ITEMS) == {}
assert teacher_items(None) == {}
assert teacher_items({}) == {}
assert teacher_items({"items": "not a list"}) == {}
assert teacher_items({"items": ["a bare string", {"id": "a", "query": "give me three grams"}]}) == WANT
assert teacher_items({"items": [{"id": "a"}]}) == {"a": None}

print("teacher parsing ok")


def test_recursion_terminates():
    """A teacher that can never satisfy the constraints must cost the rows, not the run.

    Before the fix this never returned: the alternatives batch of six styles failed as a batch, split back
    into single rows, and each was allowed to ask for alternatives again. It only stopped because the
    nested batch_id grew past the Windows path limit and write_text raised OSError.
    """
    import pathlib
    import tempfile
    import types

    import generate_data as g

    calls = {"n": 0}

    def never_satisfies(args, prompt, schema, seed, expected_items, model):
        calls["n"] += 1
        return {"items": []}

    original_teacher_json, g.teacher_json = g.teacher_json, never_satisfies
    original_unsatisfied = list(g.UNSATISFIED_ROWS)
    g.UNSATISFIED_ROWS.clear()
    try:
        args = types.SimpleNamespace(model="fake", fallback_model="", seed=1)
        rows = [{"id": f"give|{language}|0", "language": language, "scenario": 0,
                 "arguments": {}, "description": "give an item"} for language in g.LANGUAGES]
        with tempfile.TemporaryDirectory() as tmp:
            produced = g.teacher_batch(rows, args, pathlib.Path(tmp), "t", set())
        assert produced == {}, produced
        assert len(g.UNSATISFIED_ROWS) == len(rows), g.UNSATISFIED_ROWS
        # 3 attempts for the batch, then per row 3 attempts plus 6 alternative styles of 3.
        assert calls["n"] <= 200, calls["n"]
    finally:
        g.teacher_json = original_teacher_json
        g.UNSATISFIED_ROWS[:] = original_unsatisfied


test_recursion_terminates()
print("recursion terminates ok")


def test_context_overflow_splits_instead_of_failing():
    """A prompt too long for the teacher must split, not kill the run.

    FreeToken counts prompt + generation against its KV budget and answers HTTP 400
    context_length_exceeded. The retry clause treated that like a network blip, retried the identical
    oversized prompt three times and re-raised - the run died at command 44 of 78. It also proves
    wrong_modes is initialised: building the exhausted-attempts message would otherwise NameError.
    """
    import pathlib
    import tempfile
    import types
    import urllib.error

    import generate_data as g

    calls = {"n": 0}

    def always_too_long(args, prompt, schema, seed, expected_items, model):
        calls["n"] += 1
        raise urllib.error.URLError(
            'HTTP 400: {"error":{"message":"prompt is too long: 8643 tokens > 8254 maximum",'
            '"code":"context_length_exceeded"}}')

    original_teacher_json, g.teacher_json = g.teacher_json, always_too_long
    original_unsatisfied = list(g.UNSATISFIED_ROWS)
    g.UNSATISFIED_ROWS.clear()
    try:
        args = types.SimpleNamespace(model="fake", fallback_model="", seed=1)
        rows = [{"id": f"give|{language}|0", "language": language, "scenario": 0,
                 "arguments": {}, "description": "give an item"} for language in g.LANGUAGES]
        with tempfile.TemporaryDirectory() as tmp:
            produced = g.teacher_batch(rows, args, pathlib.Path(tmp), "t", set())
        assert produced == {}, produced
        assert len(g.UNSATISFIED_ROWS) == len(rows), g.UNSATISFIED_ROWS
        assert calls["n"] <= 200, calls["n"]
    finally:
        g.teacher_json = original_teacher_json
        g.UNSATISFIED_ROWS[:] = original_unsatisfied


test_context_overflow_splits_instead_of_failing()
print("context overflow splits ok")


def test_rejected_batches_are_not_re_attempted():
    """A batch that never validates must be remembered, or every restart re-pays for it.

    Only successful batches were cached, so a restart re-asked the teacher for every batch that had
    failed before - replaying 43 commands took over two hours before reaching new work.
    """
    import pathlib
    import tempfile
    import types

    import generate_data as g

    calls = {"n": 0}

    def never_satisfies(args, prompt, schema, seed, expected_items, model):
        calls["n"] += 1
        return {"items": []}

    original_teacher_json, g.teacher_json = g.teacher_json, never_satisfies
    original_unsatisfied = list(g.UNSATISFIED_ROWS)
    try:
        args = types.SimpleNamespace(model="fake", fallback_model="", seed=1)
        rows = [{"id": f"give|{language}|0", "language": language, "scenario": 0,
                 "arguments": {}, "description": "give an item"} for language in g.LANGUAGES]
        with tempfile.TemporaryDirectory() as tmp:
            cache = pathlib.Path(tmp)
            g.UNSATISFIED_ROWS.clear()
            g.teacher_batch(rows, args, cache, "t", set())
            first, calls["n"] = calls["n"], 0
            assert list(cache.glob("*.rejected")), "no rejection was recorded"
            g.UNSATISFIED_ROWS.clear()
            g.teacher_batch(rows, args, cache, "t", set())
            second = calls["n"]
        assert second < first, (first, second)
    finally:
        g.teacher_json = original_teacher_json
        g.UNSATISFIED_ROWS[:] = original_unsatisfied


test_rejected_batches_are_not_re_attempted()
print("rejected batches are remembered ok")


def test_off_topic_halving_terminates():
    """Off-topic batches had no degradation path; halving has to bottom out at single rows.

    Every split branch in teacher_batch is gated on `not off_topic`, because off-topic rows share one
    scenario and would recurse forever. So a teacher out of distinct small talk killed the run at the
    fifth batch of 32, after seven hours of teacher work.
    """
    import pathlib
    import tempfile
    import types

    import generate_data as g

    calls = {"n": 0}

    def never_satisfies(args, prompt, schema, seed, expected_items, model):
        calls["n"] += 1
        return {"items": []}

    original_teacher_json, g.teacher_json = g.teacher_json, never_satisfies
    original_unsatisfied = list(g.UNSATISFIED_ROWS)
    g.UNSATISFIED_ROWS.clear()
    try:
        args = types.SimpleNamespace(model="fake", fallback_model="", seed=1)
        rows = [{"id": f"offtopic|train|en|{index}", "language": "en", "arguments": {},
                 "offtopic": True, "variation": f"train-en-{index}",
                 "description": "A request unrelated to game console operations."} for index in range(8)]
        with tempfile.TemporaryDirectory() as tmp:
            produced = g.off_topic_chunk(rows, args, pathlib.Path(tmp), "offtopic-train-00", set())
        assert produced == {}, produced
        # 8 rows halved to singles: every row is accounted for, none silently vanishes.
        assert len(g.UNSATISFIED_ROWS) == len(rows), g.UNSATISFIED_ROWS
        # 8 + 4+4 + 2+2+2+2 + eight singles = 15 batches of 3 attempts, nowhere near unbounded.
        assert calls["n"] <= 100, calls["n"]
    finally:
        g.teacher_json = original_teacher_json
        g.UNSATISFIED_ROWS[:] = original_unsatisfied


test_off_topic_halving_terminates()
print("off-topic halving terminates ok")


def test_leakage_is_repaired_on_the_training_side():
    """Getting the victim wrong here silently corrupts the measurement, so it is worth pinning down."""
    import generate_data as g

    def rows(*queries):
        return [{"query": query, "answers": [], "phase": "route"} for query in queries]

    splits = {"train": rows("give me kush", "shared phrasing"),
              "validation": rows("shared phrasing", "validation only"),
              "holdout": rows("holdout only")}
    removed = g.resolve_split_leakage(splits)
    assert removed == [("train", "shared phrasing")], removed
    # The evaluation split keeps every row, because validate_dataset demands full command coverage there.
    assert [row["query"] for row in splits["validation"]] == ["shared phrasing", "validation only"]
    assert [row["query"] for row in splits["train"]] == ["give me kush"]

    # Between two evaluation splits neither side is preferable, so the later one gives way.
    splits = {"train": rows("train only"),
              "validation": rows("both evals"),
              "holdout": rows("both evals", "holdout only")}
    removed = g.resolve_split_leakage(splits)
    assert removed == [("holdout", "both evals")], removed
    assert [row["query"] for row in splits["validation"]] == ["both evals"]
    assert [row["query"] for row in splits["holdout"]] == ["holdout only"]

    # Nothing shared, nothing touched.
    splits = {"train": rows("a"), "validation": rows("b"), "holdout": rows("c")}
    assert g.resolve_split_leakage(splits) == []
    assert sum(len(rows_) for rows_ in splits.values()) == 3

    # Whatever the input, the property the check defends actually holds afterwards.
    splits = {"train": rows("x", "y", "z"), "validation": rows("x", "y"), "holdout": rows("y", "z", "w")}
    g.resolve_split_leakage(splits)
    seen = {name: {row["query"] for row in rows_} for name, rows_ in splits.items()}
    for left, right in (("train", "validation"), ("train", "holdout"), ("validation", "holdout")):
        assert not (seen[left] & seen[right]), (left, right, seen)


test_leakage_is_repaired_on_the_training_side()
print("split leakage repair ok")


def test_validate_dataset_filters_data_but_still_raises_on_structure():
    """One unusable phrasing must cost that phrasing; a broken generator must still stop the run."""
    import generate_data as g

    tokenizer = g.get_tokenizer()
    tools_by_name = {"give": {"name": "give", "parameters": {"properties": {"item": {"type": "string"}}}}}

    def route(identifier, query):
        return {"id": identifier, "query": query, "phase": "route", "language": "en", "reasoning": "",
                "tools": [{"name": "give"}], "answers": [{"name": "give", "arguments": {}}]}

    rows = [route("a", "give me kush"),
            route("b", "give me 10 ogkush"),      # a hand-written benchmark query
            route("c", "give me kush"),           # duplicate of a
            route("d", ""),                       # empty
            route("e", "put kush in my bag")]
    kept, coverage, rejected = g.validate_dataset(
        rows, tools_by_name, {g.normalized("give me 10 ogkush")}, tokenizer)

    assert [row["id"] for row in kept] == ["a", "e"], [row["id"] for row in kept]
    assert [reason for reason, _ in rejected] == [
        "collides with a benchmark query", "duplicate phrasing", "empty phrasing"]
    # Coverage survives because a kept row still provides it.
    assert coverage == {("give", "en", "route")}, coverage

    # Rejecting every row that carried a triple must drop that triple, not keep it from a rejected row.
    kept, coverage, rejected = g.validate_dataset(
        [route("b", "give me 10 ogkush")], tools_by_name, {g.normalized("give me 10 ogkush")}, tokenizer)
    assert kept == [] and coverage == set() and len(rejected) == 1

    # A structural defect is a generator bug and still stops everything.
    broken = route("f", "give me kush")
    broken["answers"] = [{"name": "give", "arguments": {"item": "kush"}}]
    try:
        g.validate_dataset([broken], tools_by_name, set(), tokenizer)
        raise AssertionError("a route row grounding arguments must raise")
    except ValueError as error:
        assert "route answers must not ground arguments" in str(error), error


test_validate_dataset_filters_data_but_still_raises_on_structure()
print("validate_dataset filter/raise split ok")


def test_spoken_variants_keep_the_answer_and_change_only_the_wording():
    """Players write "zehn" and "og kush"; the corpus never did, and the first three adapters scored 0 %
    on the 49 hand-written cases that need exactly that."""
    import random

    import generate_data as g

    rng = random.Random(7)
    row = {"query": "gib mir 10 ogkush", "language": "de",
           "answers": [{"name": "give", "arguments": {"arg1": "ogkush", "arg2": 10.0}}]}
    variants = g.spoken_variants(row, rng)
    # Only the number word. Spacing is left alone on purpose: the runtime normalises "og kush" to ogkush
    # before ranking the enum, and the random split points used earlier taught noise that cost five
    # end-to-end cases.
    assert variants == ["gib mir zehn ogkush"], variants

    # A number with no word form and a short value produce nothing rather than nonsense.
    assert g.spoken_variants({"query": "set the time to 1200", "language": "en",
                              "answers": [{"name": "settime", "arguments": {"arg1": 1200.0}}]}, rng) == []
    assert g.spoken_variants({"query": "give me rv", "language": "en",
                              "answers": [{"name": "give", "arguments": {"arg1": "rv"}}]}, rng) == []
    # A digit that is part of another token must not be rewritten.
    assert g.spoken_variants({"query": "use sample10 now", "language": "en",
                              "answers": [{"name": "x", "arguments": {"arg1": 10.0}}]}, rng) == []


test_spoken_variants_keep_the_answer_and_change_only_the_wording()
print("spoken variants ok")


def test_grounding_accepts_what_the_runtime_can_resolve():
    """The rule that decides which phrasings the teacher is allowed to write.

    It used to demand the literal - opaque ids verbatim, every number as digits - which is stricter than
    the game and left the corpus without a single spelled-out number in 877 numeric arguments, while 49
    of the 73 hand-written benchmark cases with arguments need one. It now asks what the runtime asks:
    a number may be its word, and any other value has to be the one NeedleArgument.TryResolveText would
    land on for this phrasing.
    """
    import generate_data as g

    give = {"command": "give", "language": "de", "arguments": {"arg1": "ogkush", "arg2": 10.0}}

    assert g.grounds_literals(give, "gib mir 10 ogkush")
    # The two forms the corpus never contained: the number as a word, the value as a player writes it.
    assert g.grounds_literals(give, "gib mir zehn og kush")
    assert g.grounds_literals({**give, "language": "en", "arguments": {"arg1": "speedgrow", "arg2": 10.0}},
                              "give me ten speed grow")
    # A dropped quantity is a dropped argument.
    assert not g.grounds_literals(give, "gib mir og kush")
    # The value itself is not checked against the catalogue: it is English and the corpus is not, so
    # "Fuege einen Botaniker zum Stall hinzu" has no lexical path to barn. Translating is the model's job.
    assert g.grounds_literals({"command": "addemployee", "language": "de",
                               "arguments": {"arg1": "botanist", "arg2": "barn"}},
                              "Fuege einen Botaniker zum Stall hinzu")
    # 1200 has no word form in the table, so "noon" cannot be verified and stays rejected.
    settime = {"command": "settime", "language": "en", "arguments": {"arg1": 1200.0}}
    assert g.grounds_literals(settime, "set the time to 1200")
    assert not g.grounds_literals(settime, "set the time to noon")
    # An opaque id is still literal: nobody says sample17 any other way.
    sample = {"command": "setqueststate", "language": "en", "arguments": {"arg1": "sample17"}}
    assert g.grounds_literals(sample, "set sample17 to failed")
    assert not g.grounds_literals(sample, "set the quest to failed")


test_grounding_accepts_what_the_runtime_can_resolve()
print("grounding rule ok")


def test_an_unreadable_reply_costs_the_batch_not_the_run():
    """One empty reply from the teacher ended a seven-hour run at command 65 of 78.

    An unparseable answer is a property of that one request; the batch splits and the smaller asks
    usually succeed. A transport error is different - the server is gone - and that still stops the run,
    because dropping nine thousand rows one at a time would be the worse failure.
    """
    import json
    import pathlib
    import tempfile
    import types
    import urllib.error

    import generate_data as g

    args = types.SimpleNamespace(model="fake", fallback_model="", seed=1)
    rows = [{"id": f"give|{language}|0", "language": language, "scenario": 0,
             "arguments": {}, "description": "give an item"} for language in g.LANGUAGES]

    def empty_reply(*_):
        raise json.JSONDecodeError("no JSON object in the teacher reply", "", 0)

    def server_gone(*_):
        raise urllib.error.URLError("[WinError 10061] connection refused")

    original, unsatisfied = g.teacher_json, list(g.UNSATISFIED_ROWS)
    try:
        g.teacher_json = empty_reply
        g.UNSATISFIED_ROWS.clear()
        with tempfile.TemporaryDirectory() as tmp:
            assert g.teacher_batch(rows, args, pathlib.Path(tmp), "t", set()) == {}
        assert len(g.UNSATISFIED_ROWS) == len(rows), g.UNSATISFIED_ROWS

        g.teacher_json = server_gone
        g.UNSATISFIED_ROWS.clear()
        with tempfile.TemporaryDirectory() as tmp:
            try:
                g.teacher_batch(rows, args, pathlib.Path(tmp), "t", set())
                raise AssertionError("an unreachable server must stop the run")
            except urllib.error.URLError:
                pass
    finally:
        g.teacher_json = original
        g.UNSATISFIED_ROWS[:] = unsatisfied


test_an_unreadable_reply_costs_the_batch_not_the_run()
print("unreadable reply splits, dead server stops ok")
