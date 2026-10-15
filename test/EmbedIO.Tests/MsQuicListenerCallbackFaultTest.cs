using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public sealed class MsQuicListenerCallbackFaultTest
    {
        [TestCase(false)]
        [TestCase(true)]
        public void AdmissionFailureCannotEscapeTheCallbackOrPoisonLaterStop(bool allocationFailure)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.MsQuicNativeListener+Signals");
            if (type == null) { Assert.Ignore("The retained asset has no native provider."); return; }
            var signals = Activator.CreateInstance(type, flags, null, Array.Empty<object>(), null)
                ?? throw new AssertionException("Missing native listener signals.");
            var accept = type.GetField("Accept", flags) ?? throw new AssertionException("Missing acceptance callback.");
            Exception failure = allocationFailure
                ? new OutOfMemoryException("Controlled allocation failure before ownership transfer.")
                : new IOException("Controlled admission failure before ownership transfer.");
            accept.SetValue(signals, (Func<IntPtr, uint>)(_ => throw failure));
            var callback = type.GetField("Handler", flags)?.GetValue(signals) as Delegate
                ?? throw new AssertionException("Missing native event callback.");
            var stopped = type.GetField("Stopped", flags)?.GetValue(signals) as TaskCompletionSource
                ?? throw new AssertionException("Missing listener stop signal.");
            var eventData = Marshal.AllocHGlobal(IntPtr.Size * 3);
            try
            {
                // Invoke the managed callback with the published NEW_CONNECTION
                // event layout. No native connection or real memory pressure is used.
                for (var offset = 0; offset < IntPtr.Size * 3; offset++) Marshal.WriteByte(eventData, offset, 0);
                object? result = null;
                Assert.DoesNotThrow(() => result = callback.DynamicInvoke(IntPtr.Zero, IntPtr.Zero, eventData),
                    "A failure before ownership transfer must return refusal, never escape into native code.");
                var refusal = OperatingSystem.IsWindows() ? 0x80004004u : OperatingSystem.IsMacOS() ? 89u : 125u;
                Assert.That(result, Is.EqualTo(refusal));
                Assert.That(type.GetField("AdmissionFailure", flags)?.GetValue(signals), Is.SameAs(failure),
                    "Retain the actual failure for diagnostics without allocating inside the callback.");
                Assert.That(stopped.Task.IsCompleted, Is.False,
                    "One rejected connection must not decide the result of a future listener stop.");
                Marshal.WriteInt32(eventData, 1);
                Assert.That(callback.DynamicInvoke(IntPtr.Zero, IntPtr.Zero, eventData), Is.EqualTo(0u));
                Assert.That(stopped.Task.IsCompletedSuccessfully, Is.True);
            }
            finally { Marshal.FreeHGlobal(eventData); }
        }
    }
}
