using UnityEngine;

namespace Hash.Game
{
    /// <summary>
    /// The system clipboard.
    ///
    /// The one thing a terminal produces that has to leave the game. An item id read off the screen is otherwise
    /// retyped by hand into Discord, and item ids are exactly the kind of string that gets one character wrong.
    ///
    /// Unity's own <c>GUIUtility.systemCopyBuffer</c> rather than anything of ours: it is one property, it is
    /// present in this runtime, and a clipboard implementation is not a thing a mod should own.
    /// </summary>
    internal static class Clipboard
    {
        internal static void Put(string value)
        {
            if (string.IsNullOrEmpty(value)) return;

            try
            {
                GUIUtility.systemCopyBuffer = value;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("the clipboard refused: " + e.Message);
            }
        }
    }
}
