using System;

namespace EmbedIO
{
    /// <summary>
    /// Represents event arguments whenever the state of a web server changes.
    /// </summary>
    /// <remarks>
    /// Initializes a new instance of the <see cref="WebServerStateChangedEventArgs"/> class.
    /// </remarks>
    /// <param name="oldState">The old state.</param>
    /// <param name="newState">The new state.</param>
    public class WebServerStateChangedEventArgs(WebServerState oldState, WebServerState newState) : EventArgs
    {
        /// <summary>
        /// Gets the state to which the application service changed.
        /// </summary>
        public WebServerState NewState { get; } = newState;

        /// <summary>
        /// Gets the old state.
        /// </summary>
        public WebServerState OldState { get; } = oldState;
    }
}
