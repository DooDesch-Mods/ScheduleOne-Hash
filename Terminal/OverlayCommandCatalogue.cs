namespace Hash.Terminal
{
    /// <summary>
    /// A live primary catalogue with commands supplied by an in-process dispatcher layered underneath it.
    /// Primary commands always win, so a mod registering the same word later keeps the terminal's existing
    /// shadowing contract. Argument metadata belongs to the primary catalogue; overlay commands describe their
    /// complete shape in <see cref="CommandInfo.Signature"/>.
    /// </summary>
    internal sealed class OverlayCommandCatalogue : ICommandCatalogue
    {
        private readonly ICommandCatalogue _primary;
        private readonly IReadOnlyList<CommandInfo> _overlay;

        internal OverlayCommandCatalogue(ICommandCatalogue primary, IReadOnlyList<CommandInfo> overlay)
        {
            _primary = primary ?? throw new ArgumentNullException(nameof(primary));
            _overlay = overlay ?? Array.Empty<CommandInfo>();
        }

        public IReadOnlyList<CommandInfo> Commands
        {
            get
            {
                IReadOnlyList<CommandInfo> primary = _primary.Commands;
                var words = new HashSet<string>(primary.Select(command => command.Word),
                                                StringComparer.OrdinalIgnoreCase);
                var combined = new List<CommandInfo>(primary);

                foreach (CommandInfo command in _overlay)
                    if (words.Add(command.Word)) combined.Add(command);

                combined.Sort((left, right) => string.CompareOrdinal(left.Word, right.Word));
                return combined;
            }
        }

        public IReadOnlyList<ArgValue> ValuesFor(string command, int argIndex) =>
            IsPrimary(command) ? _primary.ValuesFor(command, argIndex) : Array.Empty<ArgValue>();

        public MarkKind KindOf(string command, int argIndex) =>
            IsPrimary(command) ? _primary.KindOf(command, argIndex) : MarkKind.None;

        public bool Owns(string command, int argIndex) =>
            IsPrimary(command) && _primary.Owns(command, argIndex);

        private bool IsPrimary(string word) => _primary.Commands.Any(command =>
            string.Equals(command.Word, word, StringComparison.OrdinalIgnoreCase));
    }
}
