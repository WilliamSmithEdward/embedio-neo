using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;

namespace EmbedIO
{
    /// <summary>
    /// Provides extension methods for types implementing <see cref="IWebServer"/>.
    /// </summary>
    public static partial class WebServerExtensions
    {
        /// <summary>
        /// Starts a web server by calling <see cref="IWebServer.RunAsync"/>
        /// in another thread.
        /// </summary>
        /// <param name="this">The <see cref="IWebServer"/> on which this method is called.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> used to stop the web server.</param>
        /// <exception cref="NullReferenceException"><paramref name="this"/> is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">The web server has already been started.</exception>
        public static void Start(this IWebServer @this, CancellationToken cancellationToken = default)
        {
            // Listen before dispatching RunAsync so synchronous readiness cannot be missed.
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStateChanged(object sender, WebServerStateChangedEventArgs e)
            {
                if (e.NewState >= WebServerState.Listening)
                    ready.TrySetResult(true);
            }

            @this.StateChanged += OnStateChanged;
            try
            {
                var running = Task.Run(() => @this.RunAsync(cancellationToken));
                _ = running.ContinueWith(task =>
                {
                    // Start has historically returned on startup failure. RunAsync carries the error.
                    _ = task.Exception;
                    ready.TrySetResult(true);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                using (cancellationToken.Register(() => ready.TrySetCanceled(cancellationToken)))
                {
                    if (@this.State < WebServerState.Listening)
                        ready.Task.ConfigureAwait(false).GetAwaiter().GetResult();
                }
            }
            finally
            {
                @this.StateChanged -= OnStateChanged;
            }
        }
    }
}
