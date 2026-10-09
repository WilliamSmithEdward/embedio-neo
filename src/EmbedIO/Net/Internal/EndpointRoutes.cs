using System;
using System.Net;
using System.Threading;

namespace EmbedIO.Net.Internal
{
    // Publish one complete generation. Request dispatch borrows an immutable array
    // and never holds a registration lock or combines different route generations.
    internal sealed class EndpointRoutes
    {
        private readonly struct Route
        {
            internal Route(ListenerPrefix prefix, HttpListener owner) { Prefix = prefix; Owner = owner; }
            internal ListenerPrefix Prefix { get; }
            internal HttpListener Owner { get; }
        }
        private sealed class Snapshot
        {
            internal Snapshot(Route[] named, Route[] star, Route[] plus) { Named = named; Star = star; Plus = plus; }
            internal Route[] Named { get; }
            internal Route[] Star { get; }
            internal Route[] Plus { get; }
            internal Route[] Get(string host) => host == "*" ? Star : host == "+" ? Plus : Named;
            internal Snapshot With(string host, Route[] routes)
                => host == "*" ? new Snapshot(Named, routes, Plus)
                    : host == "+" ? new Snapshot(Named, Star, routes) : new Snapshot(routes, Star, Plus);
        }
        private Snapshot _snapshot = new(Array.Empty<Route>(), Array.Empty<Route>(), Array.Empty<Route>());
        internal bool IsEmpty
        {
            get
            {
                var snapshot = Volatile.Read(ref _snapshot);
                return snapshot.Named.Length == 0 && snapshot.Star.Length == 0 && snapshot.Plus.Length == 0;
            }
        }
        internal bool IsExclusiveTo(HttpListener owner)
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return AllOwnedBy(snapshot.Named, owner) && AllOwnedBy(snapshot.Star, owner) && AllOwnedBy(snapshot.Plus, owner);
        }
        private static bool AllOwnedBy(Route[] routes, HttpListener owner)
        {
            foreach (var route in routes) if (route.Owner != owner) return false;
            return true;
        }
        internal bool Add(ListenerPrefix prefix, HttpListener owner)
        {
            while (true)
            {
                var snapshot = Volatile.Read(ref _snapshot);
                var current = snapshot.Get(prefix.Host);
                foreach (var route in current)
                {
                    if (!SameRegistration(route.Prefix, prefix)) continue;
                    if (prefix.Host is "*" or "+") throw new HttpListenerException(400, "Prefix already in use.");
                    if (route.Owner != owner) throw new HttpListenerException(400, $"There is another listener for {prefix}");
                    return false;
                }
                var next = new Route[current.Length + 1];
                // Longest paths first; newest equal-length entries first preserves
                // the previous last-match tie behavior. Lookup can stop at one match.
                var position = 0;
                while (position < current.Length && current[position].Prefix.Path.Length > prefix.Path.Length) position++;
                Array.Copy(current, 0, next, 0, position);
                next[position] = new Route(prefix, owner);
                Array.Copy(current, position, next, position + 1, current.Length - position);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, snapshot.With(prefix.Host, next), snapshot), snapshot)) return true;
            }
        }
        internal void Remove(ListenerPrefix prefix, HttpListener owner)
        {
            while (true)
            {
                var snapshot = Volatile.Read(ref _snapshot);
                var current = snapshot.Get(prefix.Host);
                var index = -1;
                for (var i = 0; i < current.Length; i++)
                    if (current[i].Owner == owner && SameRegistration(current[i].Prefix, prefix)) { index = i; break; }
                if (index < 0) return;
                var next = new Route[current.Length - 1];
                Array.Copy(current, 0, next, 0, index);
                Array.Copy(current, index + 1, next, index, current.Length - index - 1);
                if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, snapshot.With(prefix.Host, next), snapshot), snapshot)) return;
            }
        }
        private static bool SameRegistration(ListenerPrefix first, ListenerPrefix second)
            => first.Host == second.Host && first.Path == second.Path
                && (first.Host is "*" or "+" || first.Port == second.Port && first.Secure == second.Secure);
        internal HttpListener? Find(Uri uri, out ListenerPrefix? prefix)
        {
            prefix = null;
            if (uri == null) return null;
            var snapshot = Volatile.Read(ref _snapshot);
            var host = uri.Host;
            var port = uri.Port;
            var path = WebUtility.UrlDecode(uri.AbsolutePath);
            foreach (var route in snapshot.Named)
            {
                var candidate = route.Prefix;
                if (candidate.Host != host || candidate.Port != port || !Matches(path, candidate.Path, true)) continue;
                prefix = candidate;
                return route.Owner;
            }
            // Preserve the existing named, star, plus precedence. For wildcard
            // categories, an actual-path match wins before a synthetic trailing slash.
            var owner = FindWildcard(snapshot.Star, path, false, out prefix);
            if (owner == null) owner = FindWildcard(snapshot.Star, path, true, out prefix);
            if (owner == null) owner = FindWildcard(snapshot.Plus, path, false, out prefix);
            if (owner == null) owner = FindWildcard(snapshot.Plus, path, true, out prefix);
            return owner;
        }
        private static HttpListener? FindWildcard(Route[] routes, string path, bool appendSlash, out ListenerPrefix? prefix)
        {
            prefix = null;
            foreach (var route in routes)
            {
                var candidate = route.Prefix;
                if (!Matches(path, candidate.Path, appendSlash)) continue;
                prefix = candidate;
                return route.Owner;
            }
            return null;
        }
        private static bool Matches(string path, string prefix, bool appendSlash)
            => path.StartsWith(prefix, StringComparison.Ordinal)
                || appendSlash && prefix.Length == path.Length + 1 && prefix[path.Length] == '/'
                    && string.CompareOrdinal(path, 0, prefix, 0, path.Length) == 0;
    }
}
