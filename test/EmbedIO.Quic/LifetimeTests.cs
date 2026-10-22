namespace EmbedIO.Quic
{
    internal static class LifetimeTests
    {
        internal static void Run()
        {
            var state = new ConnectionLifetime { IdleTimeout = 10 };
            state.Authenticated(5);
            state.SentEliciting(8);
            state.SentEliciting(20);
            state.Tick(37, 30);
            Check(state.Phase == ConnectionPhase.Active, "idle floor is three PTOs");
            state.Tick(38, 30);
            Check(
                state.IdleExpired && state.Phase == ConnectionPhase.Closed,
                "repeated local traffic cannot keep an idle peer alive"
            );
            state = new ConnectionLifetime();
            state.Close(100, 300);
            Check(state.TakeCloseResponse(100), "initial close response");
            state.Authenticated(150);
            Check(!state.TakeCloseResponse(150), "close response rate bound");
            state.Authenticated(200);
            Check(state.TakeCloseResponse(200), "authenticated peer traffic allows bounded close retry");
            state.Authenticated(250);
            Check(!state.TakeCloseResponse(250), "close response backoff");
            state.Drain(300, 1000);
            state.Authenticated(350);
            Check(!state.TakeCloseResponse(350), "draining never transmits");
            state.Tick(400, 1000);
            Check(state.Phase == ConnectionPhase.Closed, "draining does not extend existing close deadline");
            state = new ConnectionLifetime();
            state.Drain(5, 90);
            state.Drain(50, 900);
            state.Tick(94, 90);
            Check(state.Phase == ConnectionPhase.Draining, "peer close retains state");
            state.Tick(95, 90);
            Check(state.Phase == ConnectionPhase.Closed, "duplicate close cannot extend draining");
        }

        private static void Check(bool success, string name)
        {
            if (!success) throw new InvalidOperationException("Connection lifetime regression: " + name);
        }
    }
}
