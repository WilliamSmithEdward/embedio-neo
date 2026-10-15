using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Diagnostics;
using EmbedIO.Internal;

namespace EmbedIO.Net.Internal
{
    // The endpoint owns the listening socket. This loop owns each pending/accepted
    // socket until its synchronous admission callback returns successfully.
    internal sealed class TcpAcceptLoop
    {
        private readonly BorrowedResource<Socket> _listener;
        private readonly Action<Socket> _admit;
        private readonly Func<bool> _stopped;
        private readonly Action _stopAccepting;
        private int _stopRequested;
        private const int InlineBudget = 64;
        internal TcpAcceptLoop(Socket listener, Action<Socket> admit, Func<bool> stopped, Action stopAccepting)
        {
            _listener = new BorrowedResource<Socket>(listener ?? throw new ArgumentNullException(nameof(listener)));
            _admit = admit ?? throw new ArgumentNullException(nameof(admit));
            _stopped = stopped ?? throw new ArgumentNullException(nameof(stopped));
            _stopAccepting = stopAccepting ?? throw new ArgumentNullException(nameof(stopAccepting));
        }
        internal async Task RunAsync()
        {
            var inline = 0;
            var reportedError = false;
            Socket? ready = null;
            try
            {
                while (!_stopped())
                {
                    Socket? pending = null;
                    var admissionFailed = false;
                    try
                    {
                        var listener = _listener.Value;
                        // Independently retain the Windows AcceptEx handle even when
                        // the runtime clears its completion state after failure.
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            pending = new Socket(listener.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
#if NET10_0_OR_GREATER
                        var operation = listener.AcceptAsync(pending, CancellationToken.None);
#else
                        var operation = listener.AcceptAsync(pending);
#endif
                        if (!operation.IsCompleted) inline = 0;
                        // Rearm before admission starts request parsing or a TLS handshake.
                        // The next socket remains owned until its accept is awaited.
                        try { AdmitReady(ref ready, ref reportedError); }
                        catch
                        {
                            // Even a nonrecoverable callback or diagnostic exception
                            // must not abandon the operation already armed above.
                            admissionFailed = true;
                            try { RequestOwnerStop(); }
                            finally
                            {
                                try { using var abandoned = await operation.ConfigureAwait(false); }
                                catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException) { }
                            }
                            throw;
                        }
                        ready = await operation.ConfigureAwait(false);
                        if (!ReferenceEquals(pending, ready)) pending?.Dispose();
                        pending = null;
                        if (++inline == InlineBudget)
                        {
                            inline = 0;
                            // The endpoint starts this actor on TaskScheduler.Default.
                            // A full backlog must not monopolize one completion worker.
                            await Task.Yield();
                        }
                    }
                    catch (ObjectDisposedException) when (!admissionFailed) { return; }
                    catch (SocketException) when (!admissionFailed)
                    {
                        AdmitReady(ref ready, ref reportedError);
                        if (_stopped()) return;
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                    finally { pending?.Dispose(); }
                }
            }
            catch { RequestOwnerStop(); throw; }
            finally { ready?.Dispose(); }
        }
        private void RequestOwnerStop()
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 0) _stopAccepting();
        }
        private void AdmitReady(ref Socket? ready, ref bool reportedError)
        {
            var accepted = ready; ready = null;
            if (accepted == null) return;
            try
            {
                if (_stopped()) return;
                _admit(accepted);
                accepted = null;
            }
            catch (Exception error) when (ExceptionPolicy.IsRecoverable(error))
            {
                if (!reportedError)
                {
                    "TCP connection admission failed; the accepted socket was released.".Warn();
                    reportedError = true;
                }
            }
            finally { accepted?.Dispose(); }
        }
    }
}
