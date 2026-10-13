namespace EmbedIO.Internal
{
    // Implemented by modules that act when WebServer.DrainAsync starts, before the listener stops admitting work.
    internal interface IDrainAwareModule
    {
        // Called once per drain, on the thread that called DrainAsync; must return promptly and not throw.
        void OnDrainStarted();
    }
}
