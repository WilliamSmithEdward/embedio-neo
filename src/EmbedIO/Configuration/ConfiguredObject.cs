using System;

namespace EmbedIO.Configuration
{
    /// <summary>Base class for configuration that becomes read-only when started.</summary>
    public abstract class ConfiguredObject
    {
        private readonly object _configurationLock = new object();
        private volatile bool _configurationLocked;

        /// <summary>Gets whether configuration has been locked.</summary>
        public bool ConfigurationLocked => _configurationLocked;

        /// <summary>Validates and locks configuration. Repeated calls have no effect.</summary>
        protected void LockConfiguration()
        {
            lock (_configurationLock)
            {
                if (_configurationLocked) return;
                OnBeforeLockConfiguration();
                _configurationLocked = true;
            }
        }

        /// <summary>Validates configuration and locks contained objects before locking.</summary>
        protected virtual void OnBeforeLockConfiguration() { }

        /// <summary>Throws if the configuration is already locked.</summary>
        protected void EnsureConfigurationNotLocked()
        {
            if (_configurationLocked)
                throw new InvalidOperationException("The configuration is locked.");
        }
    }
}
