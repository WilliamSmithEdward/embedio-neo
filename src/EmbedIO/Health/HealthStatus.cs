namespace EmbedIO.Health
{
    /// <summary>The outcome of an application health check. Only Healthy admits readiness.</summary>
    public enum HealthStatus
    {
        /// <summary>The check failed or has not established health.</summary>
        Unhealthy,
        /// <summary>The application is functioning with a dependency or capacity limitation.</summary>
        Degraded,
        /// <summary>The check succeeded.</summary>
        Healthy,
    }
}
