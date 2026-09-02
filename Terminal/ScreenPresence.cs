namespace Hash.Terminal
{
    /// <summary>
    /// The two facts Hash owns about one appearance of its phone page.
    ///
    /// The app can be entered through either the console key or its live home-screen icon. The first path is
    /// synchronous with <c>Core.Toggle</c>; the second is only observable on the next update. Keeping the transition
    /// here makes both paths idempotent, so observing an already-open key session cannot erase where it came from.
    /// </summary>
    internal sealed class ScreenPresence
    {
        internal bool OnScreen { get; private set; }

        internal bool RaisedPhone { get; private set; }

        /// <summary>Enter once. Returns true only for the transition from off-screen to on-screen.</summary>
        internal bool Enter(bool raisedPhone)
        {
            if (OnScreen) return false;

            OnScreen = true;
            RaisedPhone = raisedPhone;
            return true;
        }

        /// <summary>Leave once. Returns true only when there was a live appearance to close.</summary>
        internal bool Leave()
        {
            if (!OnScreen) return false;

            OnScreen = false;
            RaisedPhone = false;
            return true;
        }
    }
}
