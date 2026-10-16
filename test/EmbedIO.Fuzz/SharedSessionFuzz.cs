using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.Sessions;

internal static class SharedSessionFuzz
{
    internal static async Task RunAsync(StateMachineTrace trace, CancellationToken token)
    {
        // No background purger or expiry clock: this campaign concerns shared identity and request ownership.
        var manager = new LocalSessionManager { SessionDuration = TimeSpan.FromDays(1) };
        var registered = new Dictionary<string, Model>(StringComparer.Ordinal);
        var observedIds = new HashSet<string>(StringComparer.Ordinal);
        var jars = new string?[3];
        var contexts = new Context?[3];
        var pending = new List<Task>();
        var releaseGates = new List<TaskCompletionSource<bool>>();

        void Ensure(Context context)
        {
            if (context.Model != null) return;
            var cookie = context.Request.Cookies[LocalSessionManager.DefaultCookieName]?.Value;
            var expected = cookie != null && registered.TryGetValue(cookie, out var known) ? known : null;
            var actual = context.Session.Id;
            if (expected != null) Check(actual == expected.Id, "session.cookie-identity", "A live cookie selected a different session.");
            else
            {
                Check(observedIds.Add(actual), "session.fresh-identity", "A new session reused a prior session identity.");
                expected = new Model(actual);
                registered.Add(actual, expected);
            }
            expected.Users++;
            context.Model = expected;
        }
        void Close(int actor)
        {
            var context = contexts[actor];
            if (context == null) return;
            context.Close();
            var model = context.Model;
            if (model != null && registered.TryGetValue(model.Id, out var current) && ReferenceEquals(current, model))
            {
                model.Users--;
                if (model.Users == 0 && model.Values.Count == 0) registered.Remove(model.Id);
            }
            contexts[actor] = null;
        }
        void SaveCookie(int actor, Context context)
        {
            if (context.Response.Cookies[LocalSessionManager.DefaultCookieName] is Cookie cookie) jars[actor] = cookie.Value;
        }
        Context Open(int actor)
        {
            var context = contexts[actor];
            if (context == null) { context = new Context(manager, jars[actor]); contexts[actor] = context; }
            Ensure(context);
            SaveCookie(actor, context);
            return context;
        }

        try
        {
            foreach (var step in trace.Steps)
            {
                token.ThrowIfCancellationRequested();
                var actor = step.Actor;
                var key = "key" + (step.Value % 4);
                if (step.Command == 0)
                {
                    Close(actor);
                    Open(actor);
                    trace.Cover("session.open-cookie-context");
                }
                else if (step.Command == 7)
                {
                    Close(actor);
                    trace.Cover("session.close-context");
                }
                else if (step.Command == 8)
                {
                    jars[(actor + 1) % 3] = jars[actor];
                    trace.Cover("session.share-cookie-jar");
                }
                else if (step.Command == 9)
                {
                    // Each actor has a distinct key, including when all contexts share one cookie.
                    var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    releaseGates.Add(start);
                    var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var waiting = 0;
                    var writes = new Task[3];
                    for (var index = 0; index < 3; index++)
                    {
                        var context = Open(index);
                        var actorKey = "actor" + index;
                        writes[index] = Task.Run(async () =>
                        {
                            if (Interlocked.Increment(ref waiting) == 3) ready.TrySetResult(true);
                            await start.Task.WaitAsync(token);
                            context.Session[actorKey] = step.Value;
                        }, token);
                        pending.Add(writes[index]);
                    }
                    await ready.Task.WaitAsync(token);
                    start.SetResult(true);
                    await Task.WhenAll(writes).WaitAsync(token);
                    for (var index = 0; index < 3; index++)
                        GetModel(Open(index)).Values["actor" + index] = step.Value;
                    trace.Cover("session.overlapping-context-writes");
                    if (ReferenceEquals(GetModel(Open(0)), GetModel(Open(1)))
                        || ReferenceEquals(GetModel(Open(0)), GetModel(Open(2)))
                        || ReferenceEquals(GetModel(Open(1)), GetModel(Open(2))))
                        trace.Cover("session.shared-cookie-overlapping-writes");
                }
                else
                {
                    var context = Open(actor);
                    var model = GetModel(context);
                    switch (step.Command)
                    {
                        case 1: context.Session[key] = step.Value; model.Values[key] = step.Value; trace.Cover("session.write"); break;
                        case 2:
                            var found = context.Session.TryGetValue(key, out var value);
                            var expected = model.Values.TryGetValue(key, out var expectedValue);
                            Check(found == expected && (!found || Equals(value, expectedValue)), "session.read", "Session value disagreed with the independent ledger.");
                            trace.Cover("session.read");
                            break;
                        case 3:
                            var removed = context.Session.TryRemove(key, out var removedValue);
                            var expectedRemoved = model.Values.Remove(key, out var oldValue);
                            Check(removed == expectedRemoved && (!removed || Equals(removedValue, oldValue)), "session.remove", "Removal returned another context's value.");
                            trace.Cover("session.remove");
                            break;
                        case 4: context.Session.Clear(); model.Values.Clear(); trace.Cover("session.clear-shared-values"); break;
                        case 5:
                        case 6:
                            // Retirement clears registered data once. Contexts already holding a retired
                            // session may still share that detached object; deleting its old ID again
                            // does not reach it through the manager's registry.
                            if (registered.Remove(model.Id)) model.Values.Clear();
                            context.Model = null;
                            if (step.Command == 5) context.Session.Delete();
                            else
                            {
                                context.Session.Regenerate();
                                // Regenerate has already created a fresh session; record it directly.
                                var id = context.Session.Id;
                                Check(observedIds.Add(id), "session.regenerate-identity", "Regeneration reused a prior identity.");
                                var fresh = new Model(id) { Users = 1 };
                                registered.Add(id, fresh);
                                context.Model = fresh;
                            }
                            SaveCookie(actor, context);
                            trace.Cover(step.Command == 5 ? "session.delete" : "session.regenerate");
                            break;
                        case 10: trace.Cover("session.inspect-shared-values"); break;
                        case 11:
                            Close(actor);
                            context.Close(); // Context completion delivers registered callbacks once.
                            trace.Cover("session.repeat-context-close");
                            break;
                    }
                }
                foreach (var context in contexts)
                {
                    if (context?.Model is not Model model) continue;
                    Check(context.Session.Id == model.Id, "session.context-identity", "An active context changed identities implicitly.");
                    var snapshot = context.Session.TakeSnapshot();
                    Check(snapshot.Count == model.Values.Count, "session.snapshot-count", "Shared session membership diverged.");
                    foreach (var pair in snapshot)
                        Check(model.Values.TryGetValue(pair.Key, out var expected) && Equals(expected, pair.Value), "session.snapshot-values", "Session data crossed identities or lost a write.");
                }
            }
        }
        finally
        {
            foreach (var gate in releaseGates) gate.TrySetResult(true);
            try
            {
                try { await Task.WhenAll(pending); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            }
            finally { for (var actor = 0; actor < 3; actor++) Close(actor); }
        }
    }

    private static Model GetModel(Context context) => context.Model ?? throw new StateMachineFailure("session.model", "Missing model for active session.");
    private static void Check(bool condition, string invariant, string detail) { if (!condition) throw new StateMachineFailure(invariant, detail); }
    private sealed class Model(string id)
    {
        internal string Id { get; } = id;
        internal int Users;
        internal Dictionary<string, int> Values { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Context : IHttpContext
    {
        private Action<IHttpContext>? _close;
        internal Model? Model;
        internal Context(LocalSessionManager manager, string? cookie)
        {
            Request = new Request();
            Response = new Response();
            if (!string.IsNullOrEmpty(cookie)) Request.Cookies.Add(new Cookie(LocalSessionManager.DefaultCookieName, cookie, "/"));
            Session = (ISessionProxy)(Activator.CreateInstance(typeof(SessionProxy), BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { this, manager }, null) ?? throw new InvalidOperationException("Missing session proxy constructor."));
        }
        internal void Close() { var callback = _close; _close = null; callback?.Invoke(this); }
        public void OnClose(Action<IHttpContext> callback) => _close += callback;
        public IHttpRequest Request { get; }
        public IHttpResponse Response { get; }
        public ISessionProxy Session { get; }
        public CancellationToken CancellationToken => CancellationToken.None;
        public string Id => "session-context";
        public IPEndPoint LocalEndPoint => throw new NotSupportedException();
        public IPEndPoint RemoteEndPoint => throw new NotSupportedException();
        public RouteMatch Route => throw new NotSupportedException();
        public string RequestedPath => "/";
        public IPrincipal User => throw new NotSupportedException();
        public bool SupportCompressedRequests => false;
        public IDictionary<object, object> Items => throw new NotSupportedException();
        public long Age => 0;
        public bool IsHandled => false;
        public void SetHandled() => throw new NotSupportedException();
        public string? GetMimeType(string extension) => throw new NotSupportedException();
        public bool TryDetermineCompression(string mimeType, out bool preferCompression) => throw new NotSupportedException();
    }

    private sealed class Cookies : ICookieCollection
    {
        private readonly CookieCollection _cookies = new();
        public Cookie? this[string name] => _cookies[name];
        public int Count => _cookies.Count;
        public object SyncRoot => ((ICollection)_cookies).SyncRoot;
        public bool IsSynchronized => false;
        public void Add(Cookie cookie) => _cookies.Add(cookie);
        public bool Contains(Cookie cookie) => _cookies.Contains(cookie);
        public void CopyTo(Cookie[] array, int index) => _cookies.CopyTo(array, index);
        public void CopyTo(Array array, int index) => _cookies.CopyTo(array, index);
        public IEnumerator<Cookie> GetEnumerator() { foreach (Cookie cookie in _cookies) yield return cookie; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class Request : IHttpRequest
    {
        public ICookieCollection Cookies { get; } = new Cookies();
        public Version ProtocolVersion => HttpVersion.Version11;
        public NameValueCollection Headers { get; } = new();
        public bool KeepAlive => true;
        public string RawTarget => "/";
        public NameValueCollection QueryString => throw new NotSupportedException();
        public string HttpMethod => "GET";
        public HttpVerbs HttpVerb => HttpVerbs.Get;
        public Uri Url => new("http://localhost/");
        public bool HasEntityBody => false;
        public Stream InputStream => Stream.Null;
        public Encoding ContentEncoding => Encoding.UTF8;
        public IPEndPoint RemoteEndPoint => throw new NotSupportedException();
        public bool IsLocal => true;
        public bool IsSecureConnection => false;
        public string? UserAgent => null;
        public bool IsWebSocketRequest => false;
        public IPEndPoint LocalEndPoint => throw new NotSupportedException();
        public string? ContentType => null;
        public long ContentLength64 => 0;
        public bool IsAuthenticated => false;
        public Uri? UrlReferrer => null;
    }
    private sealed class Response : IHttpResponse
    {
        public ICookieCollection Cookies { get; } = new Cookies();
        public Version ProtocolVersion => HttpVersion.Version11;
        public WebHeaderCollection Headers { get; } = new();
        public int StatusCode { get; set; } = 200;
        public long ContentLength64 { get; set; }
        [System.Diagnostics.CodeAnalysis.MaybeNull]
        public string ContentType { get; set; } = string.Empty;
        public Stream OutputStream => Stream.Null;
        public Encoding? ContentEncoding { get; set; }
        public bool KeepAlive { get; set; }
        public bool SendChunked { get; set; }
        [System.Diagnostics.CodeAnalysis.MaybeNull]
        public string StatusDescription { get; set; } = string.Empty;
        public void SetCookie(Cookie cookie) => Cookies.Add(cookie);
        public void Close() { }
    }
}
