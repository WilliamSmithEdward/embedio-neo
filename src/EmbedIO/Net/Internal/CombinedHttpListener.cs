#if NET10_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    internal sealed class CombinedHttpListener : IHttpListener, IGracefulHttpListener
    {
        private readonly object _sync = new();
        private readonly X509Certificate2 _certificate;
        private readonly List<string> _prefixes = new();
        private Session? _session;
        private bool _disposed;
        private bool _stopping;
        private bool _drained;
        private bool _ignoreWriteExceptions = true;

        internal CombinedHttpListener(X509Certificate2? certificate)
        {
            if (certificate == null || !certificate.HasPrivateKey)
                throw new ArgumentException("Combined hosting requires a certificate with its private key.", nameof(certificate));
            _certificate = certificate;
        }
        public string Name => "EmbedIO TCP and QUIC listener";
        public bool IsListening { get { lock (_sync) return _session?.IsRunning ?? false; } }
        public List<string> Prefixes { get { lock (_sync) return new List<string>(_prefixes); } }
        public bool IgnoreWriteExceptions
        {
            get { lock (_sync) return _ignoreWriteExceptions; }
            set
            {
                lock (_sync)
                {
                    _ignoreWriteExceptions = value;
                    _session?.SetIgnoreWriteExceptions(value);
                }
            }
        }
        public void AddPrefix(string urlPrefix)
        {
            ListenerPrefix.CheckUri(urlPrefix);
            var prefix = new ListenerPrefix(urlPrefix);
            if (!prefix.Secure || !prefix.IsValid() || prefix.Path.Contains('?', StringComparison.Ordinal) || prefix.Path.Contains('#', StringComparison.Ordinal))
                throw new ArgumentException("Combined hosting requires an HTTPS prefix with a valid path.", nameof(urlPrefix));
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_session != null) throw new InvalidOperationException("Stop the listener before changing its prefixes.");
                if (_prefixes.Contains(urlPrefix)) throw new ArgumentException("The prefix is already registered.", nameof(urlPrefix));
                _prefixes.Add(urlPrefix);
            }
        }
        public void Start()
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_stopping) throw new InvalidOperationException("The previous listener session is still stopping.");
                if (_session != null)
                {
                    if (_session.IsRunning) return;
                    throw new InvalidOperationException("Stop the failed listener session before restarting.");
                }
                if (_prefixes.Count == 0) throw new InvalidOperationException("At least one HTTPS prefix is required.");
                _session = Session.Start(_certificate, _prefixes, _ignoreWriteExceptions);
                _drained = false;
            }
        }
        public Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_session == null) return Task.CompletedTask;
                var session = _session;
                var drain = session.DrainAsync(timeout, cancellationToken);
                _stopping = true;
                _drained = true;
                return FinishDrainAsync(session, drain);
            }
        }
        private async Task FinishDrainAsync(Session session, Task drain)
        {
            try { await drain.ConfigureAwait(false); }
            finally
            {
                session.Dispose();
                lock (_sync)
                {
                    if (ReferenceEquals(_session, session)) { _session = null; _stopping = false; }
                }
            }
        }
        public void Stop()
        {
            Session? session;
            lock (_sync)
            {
                session = _session;
                if (session == null) return;
                _stopping = true;
            }
            try { session.Stop(); }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_session, session)) { _session = null; _stopping = false; }
                }
            }
        }
        public void Dispose()
        {
            lock (_sync) _disposed = true;
            Stop();
        }
        public Task<IHttpContextImpl> GetContextAsync(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_session == null && _drained) throw new ListenerDrainedException();
                return (_session ?? throw new HttpListenerException(995, "The listener is not accepting requests."))
                    .GetContextAsync(cancellationToken);
            }
        }

        private sealed class Session : IDisposable
        {
            private readonly object _sync = new();
            private readonly IHttpListener _tcp;
            private readonly IHttpListener _quic;
            private readonly CancellationTokenSource _stop = new();
            private readonly Channel<IHttpContextImpl> _contexts = Channel.CreateBounded<IHttpContextImpl>(new BoundedChannelOptions(256)
            { FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });
            private Task[] _pumps = Array.Empty<Task>();
            private Task? _shutdown;
            private CancellationTokenSource? _drainDeadline;
            private CancellationTokenRegistration _drainAdmission;
            private volatile bool _draining;
            private int _stopping;
            private int _resourcesDisposed;
            private Session(IHttpListener tcp, IHttpListener quic) { _tcp = tcp; _quic = quic; }
            internal bool IsRunning => Volatile.Read(ref _stopping) == 0;
            internal void SetIgnoreWriteExceptions(bool value)
            { _tcp.IgnoreWriteExceptions = value; _quic.IgnoreWriteExceptions = value; }
            internal static Session Start(X509Certificate2 certificate, List<string> prefixes, bool ignoreWriteExceptions)
            {
                var tcp = new HttpListener(certificate);
                var quic = new Http3.Http3Listener(certificate);
                var session = new Session(tcp, quic);
                try
                {
                    session.SetIgnoreWriteExceptions(ignoreWriteExceptions);
                    foreach (var prefix in prefixes) { tcp.AddPrefix(prefix); quic.AddPrefix(prefix); }
                    tcp.Start();
                    quic.Start();
                    // Neither accept pump can run cleanup before both task handles are published.
                    lock (session._sync)
                        session._pumps = new[] { Task.Run(() => session.PumpAsync(tcp)), Task.Run(() => session.PumpAsync(quic)) };
                    return session;
                }
                catch { session.Stop(); throw; }
            }
            private async Task PumpAsync(IHttpListener listener)
            {
                try
                {
                    while (!_stop.IsCancellationRequested)
                    {
                        var context = await listener.GetContextAsync(_stop.Token).ConfigureAwait(false);
                        if (!context.CancellationToken.IsCancellationRequested)
                            await _contexts.Writer.WriteAsync(context, _stop.Token).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (_draining && (error is ListenerDrainedException
                    || (error is HttpListenerException && !listener.IsListening)))
                {
                }
                catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                {
                    if (!_stop.IsCancellationRequested)
                    {
                        error.Log("Combined HTTP listener", "A transport stopped accepting requests.");
                        _contexts.Writer.TryComplete(error);
                        _ = BeginStop();
                    }
                }
            }
            internal async Task<IHttpContextImpl> GetContextAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    while (true)
                    {
                        var context = await _contexts.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                        if (!IsRunning)
                        {
                            if (_draining) throw new ListenerDrainedException();
                            throw new HttpListenerException(995, "The listener stopped accepting requests.");
                        }
                        if (!context.CancellationToken.IsCancellationRequested) return context;
                    }
                }
                catch (ChannelClosedException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_draining) throw new ListenerDrainedException();
                    throw new HttpListenerException(995, "The listener stopped accepting requests.");
                }
            }
            internal Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
            {
                Task shutdown;
                lock (_sync)
                {
                    if (_shutdown == null)
                    {
                        var deadline = new CancellationTokenSource(timeout);
                        _draining = true;
                        Task tcp;
                        try { tcp = ((IGracefulHttpListener)_tcp).DrainAsync(timeout, deadline.Token); }
                        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { tcp = Task.CompletedTask; }
                        catch { _draining = false; deadline.Dispose(); throw; }
                        _drainDeadline = deadline;
                        Task quic;
                        try { quic = ((IGracefulHttpListener)_quic).DrainAsync(timeout, deadline.Token); }
                        catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
                        {
                            deadline.Cancel();
                            quic = Task.FromException(error);
                        }
                        _drainAdmission = deadline.Token.Register(StopAdmission);
                        _shutdown = Task.Run(() => DrainTransportsAsync(tcp, quic, deadline.Token));
                    }
                    shutdown = _shutdown;
                }
                return AwaitDrainAsync(shutdown, cancellationToken);
            }
            private async Task AwaitDrainAsync(Task shutdown, CancellationToken cancellationToken)
            {
                using var registration = cancellationToken.Register(Abort);
                await shutdown.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            private async Task DrainTransportsAsync(Task tcp, Task quic, CancellationToken deadline)
            {
                try { await Task.WhenAll(tcp, quic).ConfigureAwait(false); }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
                finally { await ShutdownAsync().ConfigureAwait(false); }
            }
            private void StopAdmission()
            {
                Volatile.Write(ref _stopping, 1);
                try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            }
            private void Abort()
            {
                StopAdmission();
                try { _drainDeadline?.Cancel(); } catch (ObjectDisposedException) { }
            }
            internal void Stop() => Dispose();
            public void Dispose()
            {
                try { BeginStop().GetAwaiter().GetResult(); }
                finally { DisposeResources(); }
            }
            private void DisposeResources()
            {
                if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
                {
                    _drainAdmission.Dispose();
                    _stop.Dispose();
                    _drainDeadline?.Dispose();
                }
            }
            private Task BeginStop()
            {
                Task shutdown;
                lock (_sync)
                {
                    Volatile.Write(ref _stopping, 1);
                    shutdown = _shutdown ??= Task.Run(ShutdownAsync);
                }
                Abort();
                return shutdown;
            }
            private async Task ShutdownAsync()
            {
                Volatile.Write(ref _stopping, 1);
                try
                {
                    _stop.Cancel();
                    try { _tcp.Dispose(); }
                    finally { _quic.Dispose(); }
                }
                finally
                {
                    await Task.WhenAll(_pumps).ConfigureAwait(false);
                    _contexts.Writer.TryComplete();
                    // Transport disposal aborts queued exchanges. Do not send successful
                    // empty responses by calling Close on requests never dispatched.
                    while (_contexts.Reader.TryRead(out _)) { }
                    DisposeResources();
                }
            }
        }
    }
}
#endif
