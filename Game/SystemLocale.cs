using UnityEngine;

namespace Hash.Game
{
    /// <summary>
    /// What language this game is in, for the one line of system context Needle is given and for the shared
    /// request log.
    ///
    /// <para>Not <c>CultureInfo.CurrentUICulture</c>, which is what the mod used and which is empty here.
    /// MelonLoader starts the runtime with <c>System.Globalization.Invariant=true</c>, so every player sent
    /// <c>locale: </c> to the model and every shared record carried <c>"locale":""</c> - including the ones the
    /// language field was added for. The corpus meanwhile renders <c>locale: de-DE</c> on every German row, so
    /// the one thing the system block exists to say was the one thing it never said.</para>
    ///
    /// <para><see cref="Application.systemLanguage"/> is an engine enum and needs no globalization data. It says
    /// less than a culture would - a language and no region - so the four the corpus covers are answered with the
    /// region it trained on, and anything else with its bare language code rather than a guess.</para>
    /// </summary>
    internal static class SystemLocale
    {
        /// <summary>The tag to send, or an empty string when even the engine will not say.</summary>
        internal static string Tag()
        {
            try
            {
                return Of(Application.systemLanguage);
            }
            catch (Exception e)
            {
                Core.Log?.Warning("could not read the game's language, so requests go out without one: " + e.Message);
                return "";
            }
        }

        private static string Of(SystemLanguage language) => language switch
        {
            // The four the corpus covers, with the region those rows were rendered with.
            SystemLanguage.English => "en-US",
            SystemLanguage.German => "de-DE",
            SystemLanguage.Spanish => "es-ES",
            SystemLanguage.French => "fr-FR",

            // Everything else says what it is. A player on Polish is not served by being told they are American.
            SystemLanguage.Danish => "da",
            SystemLanguage.Dutch => "nl",
            SystemLanguage.Finnish => "fi",
            SystemLanguage.Italian => "it",
            SystemLanguage.Japanese => "ja",
            SystemLanguage.Korean => "ko",
            SystemLanguage.Norwegian => "no",
            SystemLanguage.Polish => "pl",
            SystemLanguage.Portuguese => "pt",
            SystemLanguage.Russian => "ru",
            SystemLanguage.Swedish => "sv",
            SystemLanguage.Turkish => "tr",
            SystemLanguage.Ukrainian => "uk",
            SystemLanguage.ChineseSimplified => "zh-Hans",
            SystemLanguage.ChineseTraditional => "zh-Hant",
            SystemLanguage.Chinese => "zh",
            SystemLanguage.Unknown => "",
            _ => "",
        };
    }
}
