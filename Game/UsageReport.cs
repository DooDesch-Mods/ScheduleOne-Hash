using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Hash.Terminal;

namespace Hash.Game
{
    /// <summary>
    /// Sends the usage file, and only when the player has said so.
    ///
    /// Recording and sharing are two different questions and they used to be one setting, which answered both
    /// wrongly: a player who switches sharing on has nothing to share, because the recording starts at the same
    /// moment, and a player who leaves it off never sees what they would have been sending.
    /// <see cref="UsageCapture"/> therefore always writes, and this is the only thing that ever moves the file.
    ///
    /// <para>Two rules follow from the player owning their copy. The file is cleared only after the server has said
    /// it stored the records - a 503, a timeout or a broken connection leaves it exactly where it was, to go next
    /// time. And while sharing is off it is still trimmed, because a file nobody sends must not grow without end.</para>
    /// </summary>
    internal sealed class UsageReport
    {
        /// <summary>The ingest. One constant: a setting here would be a URL players could be talked into changing.</summary>
        private const string Endpoint = "https://hash.doomods.com/api/telemetry";

        /// <summary>Records per upload. The server refuses more, and a bigger batch only risks a bigger failure.</summary>
        private const int BatchLimit = 2000;

        /// <summary>How many records are kept while sharing is off. Roughly a megabyte of ordinary requests.</summary>
        private const int KeepWhileOff = 5000;

        private readonly IStore _store;
        private readonly IUsageSharing _sharing;

        // Written on the upload thread, read on the game thread. Not volatile would let the game thread keep
        // seeing a stale "true" and never upload again - a wedge with no error anywhere.
        private volatile bool _running;

        internal UsageReport(IStore store, IUsageSharing sharing)
        {
            _store = store;
            _sharing = sharing;
        }

        /// <summary>
        /// Look at the file and do whatever the setting allows, off the game thread.
        ///
        /// Called where the terminal closes and where the game goes down. Overlapping runs are refused rather than
        /// queued: the second would read a file the first is about to clear.
        /// </summary>
        internal void Pump()
        {
            if (_store == null || _running) return;

            string content = _store.Read(StoreScope.Global, UsageCapture.FileName);
            if (string.IsNullOrEmpty(content)) return;

            string[] records = content.Split('\n');
            var pending = new List<string>(records.Length);
            foreach (string record in records)
            {
                string line = record.Trim();
                if (line.Length > 0) pending.Add(line);
            }

            if (pending.Count == 0) return;

            if (_sharing?.Enabled != true)
            {
                Trim(pending);
                return;
            }

            _running = true;
            Task.Run(() => Send(pending));
        }

        /// <summary>
        /// Drop the oldest records once the file is longer than anyone will ever send.
        ///
        /// Only rewrites when there is something to drop. A player who never turns sharing on still keeps the most
        /// recent few thousand requests, which is what they would want if they ever looked.
        /// </summary>
        private void Trim(List<string> pending)
        {
            if (pending.Count <= KeepWhileOff) return;

            int dropped = pending.Count - KeepWhileOff;
            _store.Write(StoreScope.Global, UsageCapture.FileName,
                         string.Join("\n", pending.GetRange(dropped, KeepWhileOff)) + "\n");
            Core.Log?.Msg($"usage log trimmed: dropped the oldest {dropped} of {pending.Count} records "
                          + "(sharing is off, so nothing was sent).");
        }

        private async Task Send(List<string> pending)
        {
            try
            {
                int count = Math.Min(pending.Count, BatchLimit);
                string body = Body(pending, count);

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await client.PostAsync(Endpoint, content);

                if (!response.IsSuccessStatusCode)
                {
                    Core.Log?.Warning($"usage log not sent: the server answered {(int)response.StatusCode} "
                                      + $"{response.ReasonPhrase}. The file is unchanged and will go next time.");
                    return;
                }

                // Only what was actually sent is cleared. Anything written while the request was in flight stays.
                string content2 = _store.Read(StoreScope.Global, UsageCapture.FileName) ?? "";
                var remaining = new List<string>();
                foreach (string record in content2.Split('\n'))
                {
                    string line = record.Trim();
                    if (line.Length > 0) remaining.Add(line);
                }

                int keep = Math.Max(0, remaining.Count - count);
                _store.Write(StoreScope.Global, UsageCapture.FileName,
                             keep == 0 ? "" : string.Join("\n", remaining.GetRange(count, keep)) + "\n");

                Core.Log?.Msg($"usage log sent: {count} records shared, {keep} still waiting.");
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"usage log not sent: {e.Message}. The file is unchanged and will go next time.");
            }
            finally
            {
                _running = false;
            }
        }

        /// <summary>
        /// The upload envelope. Every record is already a JSON object written by the shell, so it goes in raw -
        /// re-parsing it here would be a second, differently-broken reader of a format this mod owns.
        /// </summary>
        private static string Body(List<string> pending, int count)
        {
            string game = "";
            try { game = UnityEngine.Application.version ?? ""; }
            catch (Exception e) { Core.Log?.Warning($"could not read the game version for the usage log: {e.Message}"); }

            var sb = new StringBuilder("{");
            sb.Append("\"mod\":\"hash\",");
            sb.Append("\"version\":").Append(Json.Quote(DooDesch.ModVersion.Current)).Append(',');
            sb.Append("\"game\":").Append(Json.Quote(game)).Append(',');
            sb.Append("\"records\":[");

            for (int i = 0; i < count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(pending[i]);
            }

            return sb.Append("]}").ToString();
        }
    }
}
