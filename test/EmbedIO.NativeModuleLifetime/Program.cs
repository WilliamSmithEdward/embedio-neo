using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;

namespace EmbedIO.NativeModuleLifetime
{
    internal static class Program
    {
        private static int Main()
        {
            CloseOwners(typeof(WebServer).Assembly);
            var mappings = OperatingSystem.IsLinux() ? NativeMappings() : null;
            var references = new List<WeakReference>();
            for (var index = 0; index < 8; index++) references.AddRange(OpenAndUnloadContext());
            for (var attempt = 0; attempt < 20 && references.Exists(reference => reference.IsAlive); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Thread.Sleep(10);
            }
            Require(references.TrueForAll(reference => !reference.IsAlive), "Managed assembly contexts were retained.");
            if (mappings != null)
            {
                Require(mappings.Count != 0, "The native Linux module mapping is missing.");
                Require(NativeMappings().SetEquals(mappings), "Module mappings changed across collectible contexts.");
            }
            Console.WriteLine(JsonSerializer.Serialize(new { contexts = 8, collectedReferences = references.Count, ownersClosed = true, linuxMappingsChecked = mappings != null, mappingsStable = mappings == null ? (bool?)null : true }));
            return 0;
        }
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static object Invoke(object owner, string name, params object[] arguments)
        {
            try
            {
                return owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(owner, arguments)
                    ?? throw new InvalidOperationException("Missing native member: " + name);
            }
            catch (TargetInvocationException error) when (error.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static void CloseOwners(Assembly assembly)
        {
            var type = assembly.GetType("EmbedIO.Net.Internal.Http3.MsQuicApi") ?? throw new InvalidOperationException("Missing native API.");
            using var api = (SafeHandle)(type.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)?.Invoke(null, null)
                ?? throw new InvalidOperationException("Missing API owner."));
            using var registration = (SafeHandle)Invoke(api, "CreateRegistration");
            using var configuration = (SafeHandle)Invoke(registration, "CreateConfiguration", new byte[] { (byte)'h', (byte)'3' });
            api.Dispose();
            registration.Dispose();
            configuration.Dispose();
            Require(configuration.IsClosed && registration.IsClosed && api.IsClosed, "Native owners did not close.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] OpenAndUnloadContext()
        {
            var context = new AssemblyLoadContext("embedio-native-lifetime-" + Guid.NewGuid().ToString("N"), true);
            var assembly = context.LoadFromAssemblyPath(typeof(WebServer).Assembly.Location);
            CloseOwners(assembly);
            var references = new[] { new WeakReference(context), new WeakReference(assembly) };
            context.Unload();
            return references;
        }

        private static HashSet<string> NativeMappings()
        {
            var mappings = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines("/proc/self/maps"))
                if (line.Contains("libmsquic.so", StringComparison.Ordinal)) mappings.Add(line);
            return mappings;
        }
    }
}
