using System.Globalization;

namespace Hash.Terminal
{
    /// <summary>What became of one token that looked like a sum.</summary>
    public readonly struct Sum
    {
        internal Sum(string text, string error)
        {
            Text = text ?? "";
            Error = error;
        }

        /// <summary>The number, rendered the way the game will read it back.</summary>
        public string Text { get; }

        /// <summary>Why there is no number, or null.</summary>
        public string Error { get; }

        public bool Failed => Error != null;
    }

    /// <summary>
    /// Doing the arithmetic the player would otherwise do in their head.
    ///
    /// `give ogkush 10-@` is the whole point: the console takes a number, and the number a player actually wants is
    /// usually a number ABOUT something - ten more than I have, twice the stack, exactly ten in total. Without this
    /// they read the count off the hotbar, subtract, and type the result, which is three steps for something the
    /// terminal already knows.
    ///
    /// <para><c>@</c> is that something: how many are in the stack this statement is about. It is resolved by
    /// <see cref="MarkExpansion"/>, which knows which argument names the item; everything here is pure arithmetic
    /// over a number it was handed.</para>
    ///
    /// <para><b>Only tokens that cannot already be read as a number are touched.</b> `changecash -500` is a valid
    /// number and goes through untouched, so a command that wants a literal can never be surprised by this. What is
    /// left - `10-@`, `@*10`, `(4+2)*3` - is not something any argument in the game accepts, so evaluating it costs
    /// nothing and refusing it loudly is better than passing it on to be misread as zero. Which is exactly what
    /// vanilla does: `int.TryParse` fails, the out value stays 0, and `give` cheerfully adds nothing.</para>
    ///
    /// <para>Double rather than integer arithmetic, because not every numeric argument is a whole number -
    /// `settimescale 1/2` has to mean 0.5 and not 0. Rendered back with the invariant culture, since a machine set
    /// to German would otherwise hand the game "0,5".</para>
    /// </summary>
    public static class Arithmetic
    {
        /// <summary>The stand-in for the stack this statement is about.</summary>
        public const char Sigil = '@';

        /// <summary>Everything a sum may be made of. A token with anything else in it is somebody's id.</summary>
        private const string Alphabet = "0123456789.@+-*/() ";

        /// <summary>
        /// Whether this token is meant as a sum.
        ///
        /// Three conditions, and the second and third are what keep this from ever touching an ordinary argument:
        /// it is built only from digits and operators, it is not already a number the game could read, and it
        /// carries something to work out.
        /// </summary>
        public static bool Looks(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;

            bool operating = false;

            foreach (char c in token)
            {
                if (Alphabet.IndexOf(c) < 0) return false;
                if (c == Sigil || c == '+' || c == '-' || c == '*' || c == '/' || c == '(') operating = true;
            }

            return operating && !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }

        /// <summary>Whether the stack count is needed to work this token out.</summary>
        public static bool NeedsStack(string token) => !string.IsNullOrEmpty(token) && token.IndexOf(Sigil) >= 0;

        /// <summary>
        /// Work one token out. <paramref name="stack"/> is what <c>@</c> stands for, or null when nothing was
        /// counted - which is only reachable for a token that does not mention it.
        /// </summary>
        public static Sum Evaluate(string token, double? stack)
        {
            var reader = new Reader(token ?? "", stack);

            try
            {
                double value = reader.Expression();
                reader.SkipSpace();

                if (!reader.Done) return Bad(token, "there is a stray '" + reader.Peek + "' in it");

                if (double.IsNaN(value) || double.IsInfinity(value)) return Bad(token, "it does not work out to a number");

                // Four places is past anything the game reads back: quantities are whole, and the two arguments that
                // take a fraction (time scale, move speed) are set in tenths.
                return new Sum(value.ToString("0.####", CultureInfo.InvariantCulture), null);
            }
            catch (BadSum e)
            {
                return Bad(token, e.Message);
            }
        }

        private static Sum Bad(string token, string why) => new Sum(null, $"{token}: {why}");

        /// <summary>Thrown by the reader and caught in <see cref="Evaluate"/>, so the grammar below reads as
        /// grammar rather than as error plumbing.</summary>
        private sealed class BadSum : Exception
        {
            internal BadSum(string message) : base(message) { }
        }

        /// <summary>
        /// The grammar, one method per level, lowest binding first:
        ///
        /// <code>
        ///   expression := term (('+' | '-') term)*
        ///   term       := unary (('*' | '/') unary)*
        ///   unary      := '-'? primary
        ///   primary    := number | '@' | '(' expression ')'
        /// </code>
        /// </summary>
        private struct Reader
        {
            private readonly string _text;
            private readonly double? _stack;
            private int _at;

            internal Reader(string text, double? stack)
            {
                _text = text;
                _stack = stack;
                _at = 0;
            }

            internal bool Done => _at >= _text.Length;

            internal char Peek => Done ? '\0' : _text[_at];

            internal void SkipSpace()
            {
                while (!Done && _text[_at] == ' ') _at++;
            }

            internal double Expression()
            {
                double left = Term();

                while (true)
                {
                    SkipSpace();
                    char op = Peek;
                    if (op != '+' && op != '-') return left;

                    _at++;
                    double right = Term();
                    left = op == '+' ? left + right : left - right;
                }
            }

            private double Term()
            {
                double left = Unary();

                while (true)
                {
                    SkipSpace();
                    char op = Peek;
                    if (op != '*' && op != '/') return left;

                    _at++;
                    double right = Unary();

                    if (op == '/' && right == 0) throw new BadSum("it divides by zero");

                    left = op == '*' ? left * right : left / right;
                }
            }

            private double Unary()
            {
                SkipSpace();

                if (Peek == '-') { _at++; return -Unary(); }
                if (Peek == '+') { _at++; return Unary(); }

                return Primary();
            }

            private double Primary()
            {
                SkipSpace();

                if (Done) throw new BadSum("it stops in the middle");

                if (Peek == Sigil)
                {
                    _at++;

                    // Unreachable through MarkExpansion, which counts the stack before it gets here. Kept so the
                    // evaluator is honest on its own, since the tests call it directly.
                    if (_stack == null) throw new BadSum("nothing was counted for '@'");

                    return _stack.Value;
                }

                if (Peek == '(')
                {
                    _at++;
                    double inner = Expression();
                    SkipSpace();

                    if (Peek != ')') throw new BadSum("a bracket is left open");

                    _at++;
                    return inner;
                }

                int start = _at;
                while (!Done && (char.IsDigit(Peek) || Peek == '.')) _at++;

                if (_at == start) throw new BadSum("'" + Peek + "' is not a number or an operator");

                string number = _text.Substring(start, _at - start);

                if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    throw new BadSum("'" + number + "' is not a number");

                return parsed;
            }
        }
    }
}
