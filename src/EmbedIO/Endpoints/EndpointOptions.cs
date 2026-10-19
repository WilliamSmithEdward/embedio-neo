using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using EmbedIO.Authorization;
using EmbedIO.Security;

namespace EmbedIO.Endpoints
{
    /// <summary>Fluent settings shared by route groups and individual endpoints. Configuration freezes on compilation/start.</summary>
    /// <typeparam name="TSelf">The fluent builder type.</typeparam>
    public abstract class EndpointOptions<TSelf> where TSelf : EndpointOptions<TSelf>
    {
        internal readonly EndpointModule Owner;
        internal readonly EndpointRouteGroup? Parent;
        internal readonly List<AuthorizationRequirement> Requirements = [];
        internal readonly List<Func<IHttpContext, Task>> Before = [];
        internal readonly List<Func<IHttpContext, Task>> After = [];
        internal bool? Anonymous;
        internal bool HasCache, HasIdempotency, HasHeaders;
        internal string? Cache, Idempotency, Headers;
        internal BrowserProtectionMode? Browser;
        internal string[]? Tags;

        internal EndpointOptions(EndpointModule owner, EndpointRouteGroup? parent)
        {
            Owner = owner;
            Parent = parent;
        }

        private TSelf Self => (TSelf)this;

        internal void Check() => Owner.CheckMutable();

        /// <summary>Requires authentication and an optional named policy. Inherited requirements combine with AND.</summary>
        public TSelf RequireAuthorization(string? policy = null, params string[] roles)
        {
            Check();
            var requirement = new AuthorizationRequirement(policy, roles);
            Requirements.Add(requirement);
            Anonymous = false;
            return Self;
        }

        /// <summary>Explicitly permits anonymous access, overriding inherited authorization requirements.</summary>
        public TSelf AllowAnonymous()
        {
            Check();
            Anonymous = true;
            return Self;
        }

        /// <summary>Selects the named output-cache policy for this scope.</summary>
        public TSelf CacheOutput(string policy)
        {
            Check();
            CheckName(policy);
            HasCache = true;
            Cache = policy;
            return Self;
        }

        /// <summary>Disables an inherited output-cache selection.</summary>
        public TSelf DisableOutputCache()
        {
            Check();
            HasCache = true;
            Cache = null;
            return Self;
        }

        /// <summary>Selects a named idempotency policy.</summary>
        public TSelf WithIdempotency(string policy)
        {
            Check();
            CheckName(policy);
            HasIdempotency = true;
            Idempotency = policy;
            return Self;
        }

        /// <summary>Disables an inherited idempotency selection.</summary>
        public TSelf DisableIdempotency()
        {
            Check();
            HasIdempotency = true;
            Idempotency = null;
            return Self;
        }

        /// <summary>Selects a configured response security-header policy.</summary>
        public TSelf WithSecurityHeaders(string policy)
        {
            Check();
            CheckName(policy);
            HasHeaders = true;
            Headers = policy;
            return Self;
        }

        /// <summary>Overrides the browser token requirement; configured host/origin checks remain active.</summary>
        public TSelf WithBrowserProtection(BrowserProtectionMode mode)
        {
            Check();

            if (mode != BrowserProtectionMode.RequireToken && mode != BrowserProtectionMode.IgnoreToken)
                throw new ArgumentOutOfRangeException(nameof(mode));

            Browser = mode;
            return Self;
        }

        /// <summary>Adds a hook after authorization/browser checks and before binding. Outer groups run first.</summary>
        public TSelf OnBeforeHandler(Func<IHttpContext, Task> callback)
        {
            Check();
            Before.Add(callback ?? throw new ArgumentNullException(nameof(callback)));
            return Self;
        }

        /// <summary>Adds a completion hook after invocation/serialization, including failure. Inner scopes run first.</summary>
        public TSelf OnAfterHandler(Func<IHttpContext, Task> callback)
        {
            Check();
            After.Add(callback ?? throw new ArgumentNullException(nameof(callback)));
            return Self;
        }

        /// <summary>Sets OpenAPI tags for this scope; arrays are copied. Handler and endpoint declarations can override group tags.</summary>
        public TSelf WithTags(params string[] tags)
        {
            Check();

            tags = global::EmbedIO.Utilities.Validate.NotNull(nameof(tags), tags);

            foreach (var tag in tags)
                if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("Tags must be nonempty.", nameof(tags));

            Tags = (string[])tags.Clone();
            return Self;
        }

        private static void CheckName(string policy)
        {
            if (string.IsNullOrWhiteSpace(policy))
                throw new ArgumentException("A policy name is required.", nameof(policy));
        }
    }
}
