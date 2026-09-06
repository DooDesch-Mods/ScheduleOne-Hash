namespace Hash.Terminal
{
    /// <summary>
    /// What happened to one natural-language request, written to a file the player can read and choose to send.
    ///
    /// The reason this exists is a measurement problem, not a product one. The adapter is judged against 79
    /// hand-written benchmark cases, and 79 cases cannot tell apart runs that scored 14, 16 and 18 of them - every
    /// further change is being decided inside the noise. A few hundred real requests fix that, and the requests
    /// players actually type are the one thing a teacher model cannot invent.
    ///
    /// <para>The valuable field is <c>outcome</c>. A raw query has no ground truth and would have to be labelled by
    /// hand, but the shell can see for free whether the console refused the command it produced, whether the player
    /// typed a different one straight afterwards, and whether they rephrased and asked again. The second case
    /// supplies the correct answer outright, and a file of labelled failures is worth more than ten times as many
    /// unlabelled queries.</para>
    ///
    /// <para><c>corrected</c> is a hint rather than a verdict: the next line a player runs may simply be the next
    /// thing they wanted. That is why <c>actual</c> is always written beside it - whoever reads the file decides,
    /// and can decide only if the evidence is there.</para>
    ///
    /// <para>Off unless the player turns it on, written locally, never sent anywhere. Nothing identifies the player
    /// or the save, and there is no timestamp: the file is in order, which is all the model needs, and a clock is
    /// one more thing to have to explain before someone hands the file over.</para>
    /// </summary>
    public sealed class UsageCapture
    {
        public const string FileName = "queries.jsonl";

        private readonly IStore _store;
        private readonly Func<bool> _enabled;

        private Record _open;

        public UsageCapture(IStore store, Func<bool> enabled)
        {
            _store = store;
            _enabled = enabled ?? (() => false);
        }

        /// <summary>Whether anything is written at all. Read per call, because it is a live setting.</summary>
        private bool On => _store != null && _enabled();

        /// <summary>
        /// The player submitted a request.
        ///
        /// Closes whatever was open first. A request that follows one Hash could not answer is the player asking
        /// again; a request that follows one it did answer is simply the next thing they wanted.
        /// </summary>
        public void Asked(string query)
        {
            Flush(_open != null && _open.Failed ? "retried" : null, null);

            if (!On) return;

            _open = new Record(query);
        }

        /// <summary>Hash produced an answer but nothing has run yet.</summary>
        public void Answered(NaturalCommandTranslation translation)
        {
            if (_open == null || translation == null) return;

            _open.Commands = translation.Commands;
            _open.Confidence = translation.Confidence;
            _open.Proven = translation.Proven;
            _open.Constrained = translation.Constrained;
            if (translation.Error.Length > 0) _open.Error = translation.Error;
        }

        /// <summary>
        /// The request never reached the console - refused, unavailable, or below the confidence to run.
        ///
        /// The record stays open. A refusal is already a failure, but what the player does next says what the
        /// answer should have been, and that is the part worth having.
        /// </summary>
        public void Refused(string reason)
        {
            if (_open == null) return;

            if (!string.IsNullOrEmpty(reason)) _open.Error = reason;
            _open.Outcome = "rejected";
        }

        /// <summary>Hash ran the batch. The console's own answer decides whether it was right.</summary>
        public void Ran(IReadOnlyList<NaturalCommandExecution> executions)
        {
            if (_open == null) return;

            bool rejected = false;
            for (int i = 0; i < (executions?.Count ?? 0); i++)
            {
                if (executions[i].Success) continue;

                rejected = true;
                if (_open.Error.Length == 0) _open.Error = executions[i].Output;
                break;
            }

            _open.Outcome = rejected ? "rejected" : "accepted";
        }

        /// <summary>
        /// The player ran a command line themselves.
        ///
        /// This is the correction signal. After an answer that ran, the line they typed instead is what the request
        /// should have produced; after one that failed, it is the answer the failure was missing. Either way it is
        /// recorded, and either way the record closes here.
        /// </summary>
        public void PlayerRan(string line)
        {
            if (_open == null) return;

            Flush(_open.Failed ? "rejected" : "corrected", line);
        }

        /// <summary>The terminal closed, or the game is going down. Whatever is open is accepted as it stands.</summary>
        public void Close() => Flush(null, null);

        private void Flush(string outcome, string actual)
        {
            Record record = _open;
            _open = null;

            if (record == null || !On) return;

            var json = new Json();
            json.Str("query", record.Query);
            json.Strings("commands", record.Commands);
            json.Num("confidence", record.Confidence);
            json.Bool("proven", record.Proven);
            json.Bool("constrained", record.Constrained);
            json.Str("error", record.Error);
            json.Str("outcome", outcome ?? record.Outcome);
            if (!string.IsNullOrEmpty(actual)) json.Str("actual", actual);

            _store.Append(StoreScope.Global, FileName, json.Done());
        }

        private sealed class Record
        {
            internal Record(string query) => Query = query ?? "";

            internal string Query { get; }

            internal IReadOnlyList<string> Commands { get; set; } = Array.Empty<string>();

            internal double? Confidence { get; set; }

            internal bool Proven { get; set; }

            internal bool Constrained { get; set; }

            internal string Error { get; set; } = "";

            /// <summary>What the record says if nothing further happens. Only running commands can make it accepted.</summary>
            internal string Outcome { get; set; } = "rejected";

            internal bool Failed => Outcome != "accepted";
        }
    }
}
