"""The clock vocabulary, mirroring Terminal/TimeWords.cs.

Two sides have to agree about a time slot or the measurement is about the mismatch rather than about the
model: the corpus and the benchmark render the schema here, and the mod resolves the answer there. Keep
the table, the folding and the clock reader identical - a word this file knows and the mod does not is a
case that scores right and fails in the game.

    from time_words import owns, choices, token, word_for, recoverable
"""

from __future__ import annotations

# The words offered whatever the player wrote, so the enum is never empty.
CORE = ["midnight", "dawn", "morning", "noon", "afternoon", "evening", "night"]

# How many words one slot offers. The vocabulary is fixed and short, unlike a live catalogue.
MAX_WORDS = 12

# What the model is told the slot is. "hhmm" beside a list of words would ask for two answers at once.
DESCRIPTION = "time of day"

# Every word this understands, folded, with the time it means. The hours are clock conventions rather than
# game events: only noon, midnight and the game's own "day" have one right answer.
TABLE = {
    # English
    "midnight": 0, "dawn": 600, "sunrise": 600, "morning": 800, "noon": 1200,
    "midday": 1200, "day": 1200, "daytime": 1200, "afternoon": 1500,
    "evening": 1900, "sunset": 2000, "dusk": 2000, "night": 2300,
    # German
    "mitternacht": 0, "morgengrauen": 600, "sonnenaufgang": 600, "morgens": 800,
    "morgen": 800, "vormittag": 1000, "mittag": 1200, "tag": 1200,
    "nachmittag": 1500, "abend": 1900, "abends": 1900, "sonnenuntergang": 2000,
    "nacht": 2300, "nachts": 2300,
    # Spanish
    "medianoche": 0, "amanecer": 600, "manana": 800, "mediodia": 1200, "dia": 1200,
    "tarde": 1500, "atardecer": 2000, "anochecer": 2000, "noche": 2300,
    # French
    "minuit": 0, "aube": 600, "matin": 800, "midi": 1200, "jour": 1200,
    "journee": 1200, "apresmidi": 1500, "soir": 1900, "soiree": 1900,
    "coucherdusoleil": 2000, "nuit": 2300,
}

ACCENTS = str.maketrans({
    "á": "a", "à": "a", "â": "a", "ä": "a", "ã": "a", "å": "a",
    "é": "e", "è": "e", "ê": "e", "ë": "e",
    "í": "i", "ì": "i", "î": "i", "ï": "i",
    "ó": "o", "ò": "o", "ô": "o", "ö": "o", "õ": "o",
    "ú": "u", "ù": "u", "û": "u", "ü": "u",
    "ñ": "n", "ç": "c", "ß": "ss",
})


def fold(value: str) -> str:
    """Letters and digits, lower case, without accents."""
    return "".join(c for c in str(value or "").lower().translate(ACCENTS) if c.isalnum())


def owns(label: str) -> bool:
    """True when this argument label names a time of day."""
    return "hhmm" in str(label or "").lower()


def has_clock_marker(text: str) -> bool:
    """Whether this text says it is a time rather than merely being a number that could be read as one."""
    folded = fold(text)
    if sum(c.isdigit() for c in folded) >= 3:
        return True
    if ":" in text or "." in text:
        return True
    suffix = folded.lstrip("0123456789")
    return suffix in ("am", "pm", "uhr") or suffix.startswith("h")


def readings(query: str) -> list[tuple[str, bool]]:
    """Every console reading the player's own words carry - "8am", "8 uhr", "20:00" - and whether it was marked.

    Read in pairs as well as singly, because the hour and its unit are usually two words.
    """
    found: list[tuple[str, bool]] = []
    parts = str(query or "").split()
    for at, part in enumerate(parts):
        pair = part + " " + parts[at + 1] if at + 1 < len(parts) else None
        for text in (part, pair):
            if text is None or fold(text) in TABLE:
                continue
            reading = token(text)
            if reading is not None:
                # Both spellings are reported, duplicates and all. "8" and "8 uhr" are the same reading and only
                # the second says it is a clock, so dropping the duplicate here would drop the marker with it.
                found.append((reading, has_clock_marker(text)))
    return found


def choices(query: str) -> list[str]:
    """A clock reading the player wrote, then the words their wording contains, then the core.

    The reading comes first because the word list otherwise pulls the answer away from a number that was
    there all along, and the core words are dropped entirely once the query carries a clock marker - offered
    beside "800", the model still answered midnight.
    """
    chosen: list[str] = []
    on_the_clock = False
    for reading, marker in readings(query):
        if len(chosen) >= MAX_WORDS:
            break
        on_the_clock = on_the_clock or marker
        if reading not in chosen:
            chosen.append(reading)
    folded = fold(query)

    # Longest first, so "nachmittag" is offered before the "mittag" inside it and "apresmidi" before "midi".
    if folded:
        for word in sorted((w for w in TABLE if w in folded), key=lambda w: (-len(w), w)):
            if len(chosen) >= MAX_WORDS:
                break
            if word not in chosen:
                chosen.append(word)

    # The core words are for a request that names no time at all. Offered beside a reading the player wrote,
    # they win it: given ["800", "midnight", "dawn", ...] the model answers midnight.
    if not on_the_clock:
        for word in CORE:
            if len(chosen) >= MAX_WORDS:
                break
            if word not in chosen:
                chosen.append(word)

    return chosen


def token(value) -> str | None:
    """The console token for a word or a clock reading, or None when this is not a time at all."""
    text = str(value if value is not None else "").strip()
    if not text:
        return None

    reading = _clock(text)
    if reading is not None:
        return str(reading)

    known = TABLE.get(fold(text))
    return None if known is None else str(known)


def _clock(text: str) -> int | None:
    """Digits, then an optional separator and minutes, then an optional am/pm.

    Three or four digits with no separator are the console's own form and stay as they are, so 1530 is half
    past three rather than fifteen hundred hours past midnight.
    """
    at, hours, digits = 0, 0, 0
    while at < len(text) and text[at].isdigit() and digits < 4:
        hours = hours * 10 + int(text[at])
        digits += 1
        at += 1

    if digits == 0:
        return None

    minutes, split = 0, False

    if at < len(text) and text[at] in ":.hH":
        separator = text[at]
        at += 1
        minute_digits = 0
        while at < len(text) and text[at].isdigit() and minute_digits < 2:
            minutes = minutes * 10 + int(text[at])
            minute_digits += 1
            at += 1
        # "8h" is eight o'clock; "8:" is a stray colon and the reading stops being one.
        if minute_digits == 0 and separator == ":":
            return None
        split = True
    elif digits >= 3:
        hours, minutes = divmod(hours, 100)
        split = True

    suffix = fold(text[at:])
    if suffix == "pm" and hours < 12:
        hours += 12
    elif suffix == "am" and hours == 12:
        hours = 0
    elif suffix and suffix not in ("am", "pm", "uhr", "h"):
        return None

    if hours > 24 or minutes > 59:
        return None
    if hours == 24 and minutes > 0:
        return None
    if not split and digits > 2:
        return None

    return (hours % 24) * 100 + minutes


def word_for(query: str, reading: str | None) -> str | None:
    """The table word in this query that means this reading, if the player used one."""
    if reading is None:
        return None
    folded = fold(query)
    for word, value in TABLE.items():
        if str(value) == reading and word in folded:
            return word
    return None


def recoverable(query: str, reading: str | None) -> bool:
    """True when the runtime could reach this reading from the player's own words.

    Either a word from the table, or a clock expression in the query itself - "8am", "8 uhr", "20:00". The
    pair is read as well as the single token because the hour and its unit are usually two words.
    """
    if reading is None:
        return False
    if word_for(query, reading) is not None:
        return True

    parts = str(query or "").split()
    for at, part in enumerate(parts):
        if token(part) == reading:
            return True
        if at + 1 < len(parts) and token(part + " " + parts[at + 1]) == reading:
            return True
    return False
