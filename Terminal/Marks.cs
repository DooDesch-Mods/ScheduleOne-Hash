namespace Hash.Terminal
{
    /// <summary>What kind of thing a mark points at, and therefore which arguments will take it.</summary>
    public enum MarkKind
    {
        /// <summary>Nothing is marked.</summary>
        None,

        Npc,
        Vehicle,
        Property,

        /// <summary>An item id - what `give` and friends take.</summary>
        Item,

        /// <summary>An id whose kind is unknown, and which therefore fits any argument.
        ///
        /// Only for the two words that come out of the transcript rather than the world: the terminal knows the
        /// TEXT of the last argument, not what it named. Letting it through is right, because a command already
        /// accepted it once - and refusing it would make `#last` useless exactly where it is wanted.</summary>
        Any,
    }

    /// <summary>
    /// One resolved mark: an id the console would accept, and what kind of thing it names.
    /// </summary>
    public readonly struct Mark
    {
        public Mark(MarkKind kind, string id, string label)
        {
            Kind = kind;
            Id = id ?? "";
            Label = label ?? "";
        }

        public static readonly Mark None = new Mark(MarkKind.None, "", "");

        public MarkKind Kind { get; }

        /// <summary>The identifier a command takes - `jessi_waters`, `shitbox`, `barn`, `ogkushseed`.</summary>
        public string Id { get; }

        /// <summary>What to call it when talking to the player - "Jessi Waters", "Shitbox". Falls back to the id.
        /// Never used as an argument.</summary>
        public string Label { get; }

        public bool Exists => Kind != MarkKind.None && Id.Length > 0;

        /// <summary>How the header and the refusal message name it: `jessi_waters (npc)`.</summary>
        public override string ToString() => Exists ? $"{Id} ({Word(Kind)})" : "nothing";

        internal static string Word(MarkKind kind) => kind switch
        {
            MarkKind.Npc => "npc",
            MarkKind.Vehicle => "vehicle",
            MarkKind.Property => "property",
            MarkKind.Item => "item",
            MarkKind.Any => "id",
            _ => "nothing",
        };
    }

    /// <summary>
    /// Where a context word gets its answer. Implemented by the game half; the shell only asks.
    ///
    /// Every one of these is EXACT or absent. Nothing here is allowed to guess, pick the nearest thing that would
    /// fit, or fall back to something plausible: a `#` that once resolved to a target the player did not choose is
    /// a `#` nobody trusts again, and the whole feature is worth less than the trust.
    /// </summary>
    public interface IMarks
    {
        /// <summary>What the player last looked at, or <see cref="Mark.None"/> when that was too long ago or was
        /// nothing a command could name.</summary>
        Mark Looked { get; }

        /// <summary>The item currently equipped.</summary>
        Mark Hand { get; }

        /// <summary>The property the player is standing in.</summary>
        Mark Here { get; }

        /// <summary>The vehicle the player is sitting in - not the one they are looking at.</summary>
        Mark Car { get; }

        /// <summary>The owned property the player most recently entered.</summary>
        Mark Home { get; }

        /// <summary>The NPC closest to the player.</summary>
        Mark Near { get; }

        /// <summary>
        /// How many of this item are in the stack that holds it - what <c>@</c> stands for.
        ///
        /// The hand first, then the first hotbar slot carrying it. One stack rather than the whole inventory,
        /// because `give #hand @` is about the stack in front of you; a total across slots would make the same
        /// line mean different things depending on how the items happen to be split up.
        ///
        /// <para>-1 when no slot has it, which is a refusal rather than a zero: a sum built on a count nobody
        /// took is exactly the kind of quiet wrong answer the rest of this file exists to avoid.</para>
        /// </summary>
        int Stack(string itemId);
    }

    /// <summary>
    /// The context words: `#` and its family.
    ///
    /// `#` is the thing the player last looked at. That is the whole idea, and the reason the app is called Hash -
    /// the console has always spoken in identifiers while the world speaks in things, and the player was the one
    /// doing the translation. `setrelationship # 5` ends that.
    ///
    /// <para>The family exists because "the thing I mean" has more than one honest answer: what I am looking at,
    /// what I am holding, where I am standing. Each member is read from the game exactly, or it does not resolve.
    /// The two that are not read from the world at all - <c>#last</c> and <c>#it</c> - come from this terminal's own
    /// transcript, which is just as exact.</para>
    /// </summary>
    public sealed class Marks
    {
        /// <summary>The character itself. Everything here starts with it.</summary>
        public const char Sigil = '#';

        private readonly IMarks _world;

        /// <summary>The last argument of the last line that ran, and the last id printed. Both are the terminal's
        /// own memory rather than the game's.</summary>
        private string _lastArgument = "";
        private string _lastPrinted = "";

        public Marks(IMarks world) => _world = world;

        /// <summary>Every word, in the order the suggestion list should offer them.</summary>
        public static readonly string[] Words =
        {
            "#", "#hand", "#here", "#last", "#it", "#car", "#home", "#near",
        };

        /// <summary>What one word resolves to right now.</summary>
        public Mark Resolve(string word)
        {
            if (string.IsNullOrEmpty(word) || word[0] != Sigil) return Mark.None;

            switch (word.ToLowerInvariant())
            {
                case "#": return _world?.Looked ?? Mark.None;
                case "#hand": return _world?.Hand ?? Mark.None;
                case "#here": return _world?.Here ?? Mark.None;
                case "#car": return _world?.Car ?? Mark.None;
                case "#home": return _world?.Home ?? Mark.None;
                case "#near": return _world?.Near ?? Mark.None;

                // Untyped on purpose: the terminal knows the text of the last argument, not what kind of thing it
                // named. Passing it through as an unkinded mark means it fits any argument, which is right - it was
                // already accepted by one command.
                case "#last": return Free(_lastArgument);
                case "#it": return Free(_lastPrinted);

                default: return Mark.None;
            }
        }

        /// <summary>How many of this item the player has in one stack, or -1. What <c>@</c> stands for; see
        /// <see cref="IMarks.Stack"/>.</summary>
        public int Stack(string itemId) => _world?.Stack(itemId) ?? -1;

        /// <summary>Whether this word is one of ours, whether or not it resolves to anything.</summary>
        public static bool IsWord(string word)
        {
            if (string.IsNullOrEmpty(word) || word[0] != Sigil) return false;

            foreach (string known in Words)
                if (string.Equals(known, word, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>Remember what a submitted line ended with, for `#last`.</summary>
        public void Ran(string line)
        {
            List<string> tokens = CommandLine.Tokenise(line ?? "");
            _lastArgument = tokens.Count > 1 ? tokens[tokens.Count - 1] : "";
        }

        /// <summary>
        /// Remember an id out of a line the game printed, for `#it`.
        ///
        /// Only a line that is ONE word, because that word is unambiguous - `jessi_waters` on its own is an id and
        /// nothing else. Picking a word out of a sentence would be guessing, and guessing is the one thing none of
        /// this is allowed to do.
        /// </summary>
        public void Printed(string text)
        {
            string one = (text ?? "").Trim();
            if (one.Length == 0 || one.IndexOf(' ') >= 0) return;

            _lastPrinted = one;
        }

        private static Mark Free(string id) =>
            string.IsNullOrEmpty(id) ? Mark.None : new Mark(MarkKind.Any, id, id);
    }
}
