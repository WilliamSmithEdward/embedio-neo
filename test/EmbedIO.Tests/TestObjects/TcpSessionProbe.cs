using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace EmbedIO.Tests.TestObjects
{
    internal static class TcpSessionProbe
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static object Lifetime(object session)
            => session.GetType().GetField("_lifetime", Hidden)?.GetValue(session)
                ?? throw new AssertionException("Missing transport lifetime owner.");
        internal static Timer Timer(object session)
        {
            var owner = Lifetime(session);
            return owner.GetType().GetProperty("DeadlineTimer", Hidden)?.GetValue(owner) as Timer
                ?? throw new AssertionException("Missing owned deadline timer.");
        }
        internal static bool Closed(object session)
        {
            var owner = Lifetime(session);
            return (bool)(owner.GetType().GetProperty("CloseFinished", Hidden)?.GetValue(owner)
                ?? throw new AssertionException("Missing cleanup completion state."));
        }
        internal static void Arm(object session, int milliseconds)
        {
            var owner = Lifetime(session);
            (owner.GetType().GetMethod("ArmDeadline", Hidden)
                ?? throw new AssertionException("Missing deadline operation.")).Invoke(owner, new object[] { milliseconds });
        }
    }
}
