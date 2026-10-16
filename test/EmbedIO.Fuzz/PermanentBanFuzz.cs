using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Security;

internal static class PermanentBanFuzz
{
    internal static Task RunAsync(StateMachineTrace trace, CancellationToken token) => RunCoreAsync(trace, token, false);
    internal static async Task RunCoreAsync(StateMachineTrace trace, CancellationToken token, bool injectFault)
    {
        var root = Directory.CreateTempSubdirectory("embedio-ban-fuzz-").FullName;
        var path = Path.Combine(root, "bans.json");
        var permanent = new HashSet<string>(StringComparer.Ordinal);
        var temporary = new HashSet<string>(StringComparer.Ordinal);
        var snapshots = new List<(IReadOnlyList<string> Actual, string[] Expected)>();
        var module = Open();
        try
        {
            // Every minimal trace verifies write rollback and writer lease ownership.
            module.TryBanClientPermanently("seed"); permanent.Add("seed");
            if (injectFault) File.WriteAllText(path, "{\"version\":1,\"keys\":[]}");
            Verify(); FailWrite(); Lease();
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var key = "actor" + step.Actor + "-é-" + step.Value % 4;
                switch (step.Command)
                {
                    case 0:
                        var expected = permanent.Contains(key) || temporary.Contains(key) || permanent.Count + temporary.Count < 8;
                        Require(module.TryBanClientPermanently(key) == expected, "ban.capacity", key);
                        if (expected) { permanent.Add(key); temporary.Remove(key); }
                        break;
                    case 1: Require(module.TryUnbanClientPermanently(key) == permanent.Remove(key), "ban.unban", key); break;
                    case 2:
                        expected = !permanent.Contains(key) && (temporary.Contains(key) || permanent.Count + temporary.Count < 8);
                        Require(module.TryBanClient(key, TimeSpan.FromHours(1)) == expected, "ban.temporary-capacity", key);
                        if (expected) temporary.Add(key);
                        break;
                    case 3: Require(module.TryUnbanClient(key) == temporary.Remove(key), "ban.temporary-unban", key); break;
                    case 4: module.Dispose(); module = Open(); temporary.Clear(); break;
                    case 5: snapshots.Add((module.PermanentBannedClients, permanent.ToArray())); break;
                    case 6: FailWrite(); break;
                    case 7: Lease(); break;
                    case 8:
                        var additions = Enumerable.Range(0, 3).Select(actor => "parallel-" + actor).Where(k => !permanent.Contains(k)).ToArray();
                        if (permanent.Count + temporary.Count + additions.Count(k => !temporary.Contains(k)) > 8) break;
                        await Parallel(additions.Select(k => (Action)(() => Require(module.TryBanClientPermanently(k), "ban.concurrent-add", k)))).ConfigureAwait(false);
                        permanent.UnionWith(additions); temporary.ExceptWith(additions);
                        trace.Cover("ban.concurrent-distinct-add");
                        break;
                    case 9:
                        var removals = permanent.Take(3).ToArray();
                        await Parallel(removals.Select(k => (Action)(() => Require(module.TryUnbanClientPermanently(k), "ban.concurrent-remove", k)))).ConfigureAwait(false);
                        permanent.ExceptWith(removals); trace.Cover("ban.concurrent-distinct-remove"); break;
                    case 10: break;
                    default:
                        var rejected = false;
                        try { module.TryBanClientPermanently(" "); } catch (ArgumentException) { rejected = true; }
                        Require(rejected, "ban.invalid-key", key); break;
                }
                Verify();
                trace.Cover("ban.command-" + step.Command);
            }
            module.Dispose(); module = Open(); temporary.Clear(); Verify();
            trace.Cover("ban.final-reopen");
        }
        finally { module.Dispose(); Directory.Delete(root, true); }

        ClientBanningModule Open() => new ClientBanningModule("/", _ => "request", maximumBannedClients: 8).WithPermanentBanStore(path);
        void Verify()
        {
            Require(permanent.SetEquals(module.PermanentBannedClients), "ban.memory-ledger", path);
            Require(temporary.SetEquals(module.BannedClients.Select(b => b.ClientKey)), "ban.temporary-ledger", path);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Require(document.RootElement.GetProperty("version").GetInt32() == 1
                && permanent.SetEquals(document.RootElement.GetProperty("keys").EnumerateArray().Select(k => k.GetString() ?? "")), "ban.disk-ledger", path);
            foreach (var snapshot in snapshots) Require(snapshot.Expected.ToHashSet(StringComparer.Ordinal).SetEquals(snapshot.Actual), "ban.detached-snapshot", path);
            Require(!Directory.GetFiles(root, "*.tmp").Any(), "ban.staging-cleanup", path);
        }
        void FailWrite()
        {
            // A directory at the destination forces Save to fail on all target OSes.
            var before = File.ReadAllBytes(path);
            var saved = path + ".saved";
            File.Move(path, saved); Directory.CreateDirectory(path);
            try
            {
                var key = permanent.FirstOrDefault();
                if (key == null || permanent.Count + temporary.Count >= 8) return;
                foreach (var action in new Action[] { () => module.TryBanClientPermanently("failure-new"), () => module.TryUnbanClientPermanently(key) })
                {
                    var rejected = false;
                    try { action(); } catch (IOException) { rejected = true; }
                    Require(rejected, "ban.failed-write-rejected", path);
                    Require(permanent.SetEquals(module.PermanentBannedClients) && before.AsSpan().SequenceEqual(File.ReadAllBytes(saved)), "ban.failed-write-rollback", path);
                    Require(!Directory.GetFiles(root, "*.tmp").Any(), "ban.failed-write-staging", path);
                }
                trace.Cover("ban.write-failure-rollback");
            }
            finally { Directory.Delete(path); File.Move(saved, path); }
        }
        void Lease()
        {
            using var other = new ClientBanningModule("/", _ => "other");
            var rejected = false;
            try { other.WithPermanentBanStore(path); } catch (IOException) { rejected = true; }
            Require(rejected, "ban.exclusive-writer", path); trace.Cover("ban.exclusive-writer");
        }
        async Task Parallel(IEnumerable<Action> actions)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var batch = actions.ToArray(); var entered = 0;
            var tasks = batch.Select(action => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref entered) == batch.Length) ready.TrySetResult();
                await gate.Task.WaitAsync(token).ConfigureAwait(false); action();
            }, token)).ToArray();
            try { if (tasks.Length != 0) await ready.Task.WaitAsync(token).ConfigureAwait(false); }
            finally { gate.TrySetResult(); }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
    private static void Require(bool condition, string invariant, string detail)
    { if (!condition) throw new StateMachineFailure(invariant, detail); }
}
