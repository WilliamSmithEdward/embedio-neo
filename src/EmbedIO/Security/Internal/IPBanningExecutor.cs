using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Diagnostics;

namespace EmbedIO.Security.Internal
{
    internal static class IPBanningExecutor
    {
        private static readonly object Sync = new object();
        private static readonly List<Registration> Configurations = new List<Registration>();

        private static readonly PeriodicTask Purger = new PeriodicTask(TimeSpan.FromMinutes(1), ct =>
        {
            Purge();
            return Task.CompletedTask;
        });

        internal static void Register(string baseRoute, IPBanningConfiguration configuration)
        {
            lock (Sync)
            {
                Configurations.RemoveAll(entry => !entry.Configuration.TryGetTarget(out _));
                Configurations.Add(new Registration(baseRoute, configuration));
            }
        }

        internal static T[] WithInstances<T>(string baseRoute, Func<IPBanningConfiguration, T> action)
        {
            if (baseRoute == null) throw new ArgumentNullException(nameof(baseRoute));
            Registration[] entries;
            lock (Sync)
            {
                entries = Configurations.Where(entry => entry.BaseRoute == baseRoute).ToArray();
            }
            var results = new List<T>();
            foreach (var entry in entries)
                if (entry.TryInvoke(action, out var result)) results.Add(result);
            if (results.Count == 0)
                throw new ArgumentException("No configuration was found for the base route provided.", nameof(baseRoute));
            return results.ToArray();
        }

        internal static T WithInstance<T>(IPBanningConfiguration configuration, Func<IPBanningConfiguration, T> action)
        {
            Registration? entry;
            lock (Sync)
            {
                entry = Configurations.FirstOrDefault(candidate => candidate.Configuration.TryGetTarget(out var target) && ReferenceEquals(target, configuration));
            }
            if (entry != null && entry.TryInvoke(action, out var result)) return result;
            throw new ObjectDisposedException(nameof(IPBanningModule));
        }

        internal static void Remove(IPBanningConfiguration configuration)
        {
            Registration? entry;
            lock (Sync)
            {
                entry = Configurations.FirstOrDefault(candidate => candidate.Configuration.TryGetTarget(out var target) && ReferenceEquals(target, configuration));
                Configurations.RemoveAll(entry => !entry.Configuration.TryGetTarget(out var target) || ReferenceEquals(target, configuration));
            }
            entry?.Dispose(configuration);
        }

        internal static void Purge()
        {
            Registration[] entries;
            lock (Sync)
            {
                Configurations.RemoveAll(entry => !entry.Configuration.TryGetTarget(out _));
                entries = Configurations.ToArray();
            }
            foreach (var entry in entries)
            {
                try { entry.TryInvoke(configuration => { configuration.Purge(); return true; }, out _); }
                catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { error.Log(nameof(IPBanningExecutor)); }
            }
        }

        private sealed class Registration
        {
            private readonly object _sync = new object();
            private bool _active = true;
            internal Registration(string baseRoute, IPBanningConfiguration configuration)
            {
                BaseRoute = baseRoute;
                Configuration = new WeakReference<IPBanningConfiguration>(configuration);
            }

            internal string BaseRoute { get; }
            internal WeakReference<IPBanningConfiguration> Configuration { get; }

            internal bool TryInvoke<T>(Func<IPBanningConfiguration, T> action, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out T result)
            {
                lock (_sync)
                {
                    if (_active && Configuration.TryGetTarget(out var configuration))
                    {
                        result = action(configuration);
                        return true;
                    }
                    result = default;
                    return false;
                }
            }

            internal void Dispose(IPBanningConfiguration configuration)
            {
                lock (_sync)
                {
                    if (!_active) return;
                    _active = false;
                    configuration.Dispose();
                }
            }
        }
    }
}
