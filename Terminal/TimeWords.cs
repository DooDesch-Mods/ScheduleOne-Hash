using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hash.Terminal
{
    /// <summary>
    /// The clock vocabulary for a time-of-day argument, and the conversion from a word or a clock reading to
    /// the console's own <c>hhmm</c> token.
    ///
    /// <para>A time argument is the one slot where the model has nothing to work with. Over the eleven
    /// <c>settime</c> cases in the benchmark it answers <c>8</c> for "8am" and <c>0</c> for noon, in every
    /// language and with or without the tuned adapter - it reads the hour correctly and then has no way to
    /// know that this game counts time as 800 and that noon is 1200. Neither is a fact about language, so
    /// neither is something to train: the hour-to-hhmm step is arithmetic, and noon is a value this mod
    /// knows and the model cannot.</para>
    ///
    /// <para>So the slot is declared as a word the model can pick - the same shape that already works for
    /// items and weather, where offering the whole short list of values raised recall from 32 of 53 to 40 -
    /// and every answer, word or number, is converted here. Rule-based normalisation over a recognised
    /// expression is what HeidelTime and SUTime have always done for this, and a fixed target format is
    /// exactly the case where it still beats asking a model to produce the number itself.</para>
    /// </summary>
    internal static class TimeWords
    {
        /// <summary>The words offered whatever the player wrote, so the enum is never empty.</summary>
        private static readonly string[] CoreWords =
            { "midnight", "dawn", "morning", "noon", "afternoon", "evening", "night" };

        /// <summary>How many words one slot offers. The vocabulary is fixed and short, unlike a live catalogue.</summary>
        private const int MaxWords = 12;

        /// <summary>
        /// Every word this understands, folded, with the time it means.
        ///
        /// The hours are clock conventions rather than game events: a player who asks for "evening" wants a
        /// rough time of day, and any hour inside the band answers them. Only noon, midnight and the game's
        /// own "day" have one right answer.
        /// </summary>
        private static readonly Dictionary<string, int> Table = new(StringComparer.Ordinal)
        {
            // English
            ["midnight"] = 0, ["dawn"] = 600, ["sunrise"] = 600, ["morning"] = 800, ["noon"] = 1200,
            ["midday"] = 1200, ["day"] = 1200, ["daytime"] = 1200, ["afternoon"] = 1500,
            ["evening"] = 1900, ["sunset"] = 2000, ["dusk"] = 2000, ["night"] = 2300,
            // German
            ["mitternacht"] = 0, ["morgengrauen"] = 600, ["sonnenaufgang"] = 600, ["morgens"] = 800,
            ["morgen"] = 800, ["vormittag"] = 1000, ["mittag"] = 1200, ["tag"] = 1200,
            ["nachmittag"] = 1500, ["abend"] = 1900, ["abends"] = 1900, ["sonnenuntergang"] = 2000,
            ["nacht"] = 2300, ["nachts"] = 2300,
            // Spanish
            ["medianoche"] = 0, ["amanecer"] = 600, ["manana"] = 800, ["mediodia"] = 1200, ["dia"] = 1200,
            ["tarde"] = 1500, ["atardecer"] = 2000, ["anochecer"] = 2000, ["noche"] = 2300,
            // French
            ["minuit"] = 0, ["aube"] = 600, ["matin"] = 800, ["midi"] = 1200, ["jour"] = 1200,
            ["journee"] = 1200, ["apresmidi"] = 1500, ["soir"] = 1900, ["soiree"] = 1900,
            ["coucherdusoleil"] = 2000, ["nuit"] = 2300,
        };

        /// <summary>True when this argument label names a time of day.</summary>
        internal static bool Owns(string label) =>
            !string.IsNullOrEmpty(label) && label.IndexOf("hhmm", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// What to offer for this request: a clock reading the player wrote, then the words their wording
        /// contains, then the core.
        ///
        /// A German player writes "mittag" and needs that word on the list, but forty words across four
        /// languages is a list nobody reads. Matching by containment is enough for a fixed vocabulary - the
        /// fuzzy ranking that a thousand-item catalogue needs would only put "tag" on an English list.
        /// </summary>
        internal static IReadOnlyList<string> Choices(string query)
        {
            var chosen = new List<string>();
            string folded = Fold(query ?? "");

            // A clock reading the player wrote goes on the list first, as the reading the console wants.
            // Without it the word list pulls the answer away from a number that was there all along:
            // offered midnight and dawn beside nothing else, "set the time to 8am" is answered midnight.
            foreach (string reading in Readings(query))
            {
                if (chosen.Count >= MaxWords) break;
                if (!chosen.Contains(reading)) chosen.Add(reading);
            }

            if (folded.Length > 0)
            {
                foreach (KeyValuePair<string, int> entry in Table)
                {
                    if (chosen.Count >= MaxWords) break;
                    if (folded.IndexOf(entry.Key, StringComparison.Ordinal) >= 0 && !chosen.Contains(entry.Key))
                        chosen.Add(entry.Key);
                }
            }

            foreach (string word in CoreWords)
            {
                if (chosen.Count >= MaxWords) break;
                if (!chosen.Contains(word)) chosen.Add(word);
            }

            return chosen;
        }

        /// <summary>
        /// Every console reading the player's own words carry - "8am", "8 uhr", "20:00".
        ///
        /// Read in pairs as well as singly, because the hour and its unit are usually two words.
        /// </summary>
        private static IEnumerable<string> Readings(string query)
        {
            string[] parts = (query ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            for (int at = 0; at < parts.Length; at++)
            {
                if (TryToken(parts[at], out string one) && Fold(parts[at]).Length > 0
                    && !Table.ContainsKey(Fold(parts[at])))
                    yield return one;
                if (at + 1 < parts.Length && TryToken(parts[at] + " " + parts[at + 1], out string two)
                    && !Table.ContainsKey(Fold(parts[at] + parts[at + 1])))
                    yield return two;
            }
        }

        /// <summary>
        /// The console token for a word or a clock reading, or false when this is not a time at all.
        ///
        /// Accepts what the model and the player actually produce: <c>noon</c>, <c>8</c>, <c>8am</c>,
        /// <c>8h30</c>, <c>20:00</c> and the game's own <c>1530</c>. A bare hour becomes the full reading,
        /// which is the conversion the model never makes on its own.
        /// </summary>
        internal static bool TryToken(string value, out string token)
        {
            token = null;
            string text = (value ?? "").Trim();
            if (text.Length == 0) return false;

            if (TryClock(text, out int reading))
            {
                token = reading.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            if (Table.TryGetValue(Fold(text), out int known))
            {
                token = known.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Read a clock expression: digits, then an optional separator and minutes, then an optional am/pm.
        ///
        /// Three or four digits with no separator are the console's own form and stay as they are, so
        /// <c>1530</c> is half past three and not fifteen hundred hours past midnight.
        /// </summary>
        private static bool TryClock(string text, out int reading)
        {
            reading = 0;
            int at = 0;

            int hours = 0, digits = 0;
            while (at < text.Length && char.IsDigit(text[at]) && digits < 4)
            {
                hours = (hours * 10) + (text[at] - '0');
                digits++;
                at++;
            }

            if (digits == 0) return false;

            int minutes = 0;
            bool split = false;

            if (at < text.Length && (text[at] == ':' || text[at] == '.' || text[at] == 'h' || text[at] == 'H'))
            {
                int separator = at++;
                int minuteDigits = 0;
                while (at < text.Length && char.IsDigit(text[at]) && minuteDigits < 2)
                {
                    minutes = (minutes * 10) + (text[at] - '0');
                    minuteDigits++;
                    at++;
                }

                // "8h" is eight o'clock; "8:" is a stray colon and the reading stops being one.
                if (minuteDigits == 0 && text[separator] == ':') return false;
                split = true;
            }
            else if (digits >= 3)
            {
                minutes = hours % 100;
                hours /= 100;
                split = true;
            }

            string suffix = Fold(text.Substring(at));
            if (suffix == "pm" && hours < 12) hours += 12;
            else if (suffix == "am" && hours == 12) hours = 0;
            else if (suffix.Length > 0 && suffix != "am" && suffix != "pm" && suffix != "uhr" && suffix != "h")
                return false;

            if (hours > 24 || minutes > 59) return false;
            if (hours == 24 && minutes > 0) return false;
            if (!split && digits > 2) return false;

            reading = ((hours % 24) * 100) + minutes;
            return true;
        }

        /// <summary>
        /// Letters and digits, lower case, without accents.
        ///
        /// Written out rather than taken from <c>string.Normalize</c>, which needs the globalization data
        /// MelonLoader starts the runtime without.
        /// </summary>
        private static string Fold(string value)
        {
            var sb = new StringBuilder();
            foreach (char raw in value ?? "")
            {
                char c = char.ToLowerInvariant(raw);
                switch (c)
                {
                    case 'á': case 'à': case 'â': case 'ä': case 'ã': case 'å': c = 'a'; break;
                    case 'é': case 'è': case 'ê': case 'ë': c = 'e'; break;
                    case 'í': case 'ì': case 'î': case 'ï': c = 'i'; break;
                    case 'ó': case 'ò': case 'ô': case 'ö': case 'õ': c = 'o'; break;
                    case 'ú': case 'ù': case 'û': case 'ü': c = 'u'; break;
                    case 'ñ': c = 'n'; break;
                    case 'ç': c = 'c'; break;
                    case 'ß': sb.Append("ss"); continue;
                }

                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
