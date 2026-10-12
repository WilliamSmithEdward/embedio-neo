using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

// Counts immediate throw sites and nearest owned frames, not every continuation tail.
// Each site retains one bounded representative trace and its full-content hash.
internal sealed class ExceptionSiteLedger(ConcurrentDictionary<string, long> types)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _siteTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> _examples = new(StringComparer.Ordinal);
    private long _overflow;

    internal bool Record(string type, string exceptionStack, string notificationStack)
    {
        var site = type + "\nnotification frame: " + Frame(notificationStack, true, false)
            + "\nnotification owned frame: " + Frame(notificationStack, true, true);
        lock (_gate)
        {
            var first = !types.ContainsKey(type) && types.Count < 64;
            if (_counts.TryGetValue(site, out var count))
            {
                if (_siteTypes[site] == type) _counts[site] = count + 1;
                else _overflow++;
            }
            else if (_counts.Count < 512 && site.Length <= 16384)
            {
                _counts.Add(site, 1);
                _siteTypes.Add(site, type);
                var full = "exception stack:\n" + exceptionStack + "\nthrow notification stack:\n" + notificationStack;
                var preview = "exception stack:\n" + exceptionStack[..Math.Min(exceptionStack.Length, 8000)]
                    + "\nthrow notification stack:\n" + notificationStack[..Math.Min(notificationStack.Length, 8000)];
                _examples.Add(site, new
                {
                    text = preview,
                    sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))),
                    originalCharacters = full.Length,
                    truncated = preview.Length != full.Length,
                });
            }
            else _overflow++;
            types.AddOrUpdate(type, 1, static (_, count) => count + 1);
            return first;
        }
    }

    internal object Snapshot()
    {
        lock (_gate) return new
        {
            schema = "throw-site-v2",
            counts = new Dictionary<string, long>(_counts, StringComparer.Ordinal),
            types = new Dictionary<string, long>(types, StringComparer.Ordinal),
            siteTypes = new Dictionary<string, string>(_siteTypes, StringComparer.Ordinal),
            examples = new Dictionary<string, object>(_examples, StringComparer.Ordinal),
            overflow = _overflow,
        };
    }

    private static string Frame(string stack, bool notification, bool owned)
    {
        foreach (var raw in stack.Split('\n'))
        {
            var frame = raw.Trim();
            if (!frame.StartsWith("at ", StringComparison.Ordinal)) continue;
            if (notification && (frame.StartsWith("at System.Environment.get_StackTrace", StringComparison.Ordinal)
                || frame.StartsWith("at ServerHost.", StringComparison.Ordinal))) continue;
            if (owned && !frame.StartsWith("at EmbedIO.", StringComparison.Ordinal)
                && !frame.StartsWith("at Microsoft.AspNetCore.", StringComparison.Ordinal)) continue;
            return frame;
        }

        return "<frame unavailable>";
    }
}
