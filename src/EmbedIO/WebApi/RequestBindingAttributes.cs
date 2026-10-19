using System;

namespace EmbedIO.WebApi
{
    /// <summary>How an explicitly bound scalar handles repeated field values.</summary>
    public enum RequestDuplicateValues
    {
        /// <summary>Reject repeated scalar values, even when they are equal.</summary>
        Reject,
        /// <summary>Use the first value.</summary>
        First,
        /// <summary>Use the last value.</summary>
        Last
    }

    /// <summary>Binds a flat request DTO from query fields. Missing properties keep their initializers.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class QueryObjectAttribute : Attribute
    {
        /// <summary>Gets or sets whether unrecognized fields are rejected.</summary>
        public bool RejectUnknownFields { get; set; }
    }

    /// <summary>Binds a flat request DTO from an URL-encoded form.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class FormObjectAttribute : Attribute
    {
        /// <summary>Gets or sets whether unrecognized fields are rejected.</summary>
        public bool RejectUnknownFields { get; set; }
    }

    /// <summary>Configures an explicitly bound DTO property.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class RequestFieldAttribute : Attribute
    {
        /// <summary>Creates field options using the property name.</summary>
        public RequestFieldAttribute() { }

        /// <summary>Creates field options using an explicit field name.</summary>
        /// <param name="name">The source field name.</param>
        public RequestFieldAttribute(string name) => Name = RequestBindingValues.CheckName(name);

        /// <summary>Gets the optional field name.</summary>
        public string? Name { get; }

        /// <summary>Gets or sets whether a field must be present.</summary>
        public bool Required { get; set; }

        /// <summary>Gets or sets scalar duplicate handling. Arrays retain every value.</summary>
        public RequestDuplicateValues DuplicateValues { get; set; }
    }

    /// <summary>Opts a route parameter into invariant parsing, including custom TryParse types.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class RouteFieldAttribute : Attribute
    {
        /// <summary>Uses the controller parameter name.</summary>
        public RouteFieldAttribute() { }

        /// <summary>Uses a named route placeholder.</summary>
        /// <param name="name">The route placeholder name.</param>
        public RouteFieldAttribute(string name) => Name = RequestBindingValues.CheckName(name);

        /// <summary>Gets the optional placeholder name.</summary>
        public string? Name { get; }

        /// <summary>Gets or sets whether a missing placeholder is rejected. Defaults to true.</summary>
        public bool Required { get; set; } = true;
    }

    /// <summary>Binds a header without splitting commas. This does not establish authentication.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class HeaderFieldAttribute : Attribute
    {
        /// <summary>Creates a header binding using the parameter name.</summary>
        public HeaderFieldAttribute() { }

        /// <summary>Creates a binding for a named header.</summary>
        /// <param name="name">The header name.</param>
        public HeaderFieldAttribute(string name) =>
            Name = RequestBindingValues.CheckToken(name ?? throw new ArgumentNullException(nameof(name)));

        /// <summary>Gets the optional header name.</summary>
        public string? Name { get; }

        /// <summary>Gets or sets whether the header must be present.</summary>
        public bool Required { get; set; }

        /// <summary>Gets or sets scalar duplicate handling.</summary>
        public RequestDuplicateValues DuplicateValues { get; set; }
    }

    /// <summary>Binds a case-sensitive cookie name without URL decoding. This does not establish authentication.</summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class CookieFieldAttribute : Attribute
    {
        /// <summary>Creates a cookie binding using the parameter name.</summary>
        public CookieFieldAttribute() { }

        /// <summary>Creates a binding for a named cookie.</summary>
        /// <param name="name">The cookie name.</param>
        public CookieFieldAttribute(string name) =>
            Name = RequestBindingValues.CheckToken(name ?? throw new ArgumentNullException(nameof(name)));

        /// <summary>Gets the optional cookie name.</summary>
        public string? Name { get; }

        /// <summary>Gets or sets whether the cookie must be present.</summary>
        public bool Required { get; set; }

        /// <summary>Gets or sets scalar duplicate handling.</summary>
        public RequestDuplicateValues DuplicateValues { get; set; }
    }
}
