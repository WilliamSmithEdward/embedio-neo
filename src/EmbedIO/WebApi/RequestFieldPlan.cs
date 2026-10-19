using System;
using System.ComponentModel;
using EmbedIO.Utilities;

namespace EmbedIO.WebApi
{
    /// <summary>A cached parser for a generated header or cookie parameter.</summary>
    /// <typeparam name="T">Parameter type.</typeparam>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class RequestFieldPlan<T>
    {
        private readonly string _name;
        private readonly bool _cookie;
        private readonly bool _required;
        private readonly bool _many;
        private readonly RequestDuplicateValues _duplicates;
        private readonly Func<string[], T> _parse;
        private readonly Func<T> _missing;

        /// <summary>Creates a binding plan for an explicit header/cookie field.</summary>
        /// <param name="name">Source name.</param>
        /// <param name="cookie">Whether the source is cookies.</param>
        /// <param name="required">Whether presence is required.</param>
        /// <param name="many">Whether every value is consumed.</param>
        /// <param name="duplicates">Scalar duplicate handling.</param>
        /// <param name="parse">Parser.</param>
        /// <param name="missing">Default-value factory.</param>
        public RequestFieldPlan(
            string name,
            bool cookie,
            bool required,
            bool many,
            RequestDuplicateValues duplicates,
            Func<string[], T> parse,
            Func<T> missing
        )
        {
            _name = RequestBindingValues.CheckName(name ?? throw new ArgumentNullException(nameof(name)));
            _cookie = cookie;
            _required = required;
            _many = many;
            RequestBindingValues.CheckDuplicates(duplicates);
            _duplicates = duplicates;
            _parse = parse ?? throw new ArgumentNullException(nameof(parse));
            _missing = missing ?? throw new ArgumentNullException(nameof(missing));
        }

        /// <summary>Binds an explicitly opted-in route placeholder.</summary>
        /// <param name="route">Matched route.</param>
        /// <param name="parameterName">Error path.</param>
        /// <returns>The parsed placeholder or its declared default.</returns>
        public T BindRoute(EmbedIO.Routing.RouteMatch route, string parameterName)
        {
            if (!ControllerBindingServices.TryGetRouteValue(route, _name, out var value))
            {
                if (_required) throw RequestBindingValues.Failure(parameterName, "The route value is required.");

                return _missing();
            }

            var values = new RequestBindingValues();
            values.Add(_name, value);

            try
            {
                return _parse([value]);
            }

            catch (Exception error) when (
                error is FormatException || error is OverflowException || error is ArgumentException
            )
            {
                throw RequestBindingValues.Failure(parameterName, "The route value is invalid.");
            }
        }

        /// <summary>Binds one explicit request field.</summary>
        /// <param name="controller">Current controller.</param>
        /// <param name="parameterName">Error path.</param>
        /// <returns>The parsed field or its declared default.</returns>
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0057:Use range operator",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Range or System.Index."
        )]
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Style",
            "IDE0056:Use index operator",
            Justification = "The shared implementation targets netstandard2.0, which has no System.Range or System.Index."
        )]
        public T Bind(WebApiController controller, string parameterName)
        {
            controller = Validate.NotNull(nameof(controller), controller);

            var headers = controller.HttpContext.Request.Headers;
            var values = new RequestBindingValues(_cookie);

            if (_cookie)
            {
                var fields = headers.GetValues("Cookie") ?? [];
                var bytes = 0;

                foreach (var field in fields)
                {
                    bytes = checked(bytes + field.Length);

                    if (bytes > RequestBindingValues.MaxBytes)
                        throw RequestBindingValues.Failure(parameterName, "The cookie binding limit was exceeded.");

                    var offset = 0;
                    var parts = 0;

                    while (offset < field.Length)
                    {
                        if (++parts > RequestBindingValues.MaxValues)
                            throw RequestBindingValues.Failure(parameterName, "Too many cookie fields.");

                        var end = field.IndexOf(';', offset);

                        if (end < 0) end = field.Length;

                        var part = field.Substring(offset, end - offset);
                        offset = end + 1;

                        if (string.IsNullOrWhiteSpace(part)) continue;

                        var equals = EmbedIO.Internal.StringOperations.IndexOfOrdinal(part, '=');

                        if (equals <= 0) throw RequestBindingValues.Failure(parameterName, "Invalid cookie field.");

                        var name = part
                            .Substring(0, equals)
                            .Trim();
                        var value = part
                            .Substring(equals + 1)
                            .Trim();

                        if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"')
                            value = value.Substring(1, value.Length - 2);

                        try
                        {
                            RequestBindingValues.CheckToken(name);
                        }

                        catch (ArgumentException)
                        {
                            throw RequestBindingValues.Failure(parameterName, "Invalid cookie name.");
                        }

                        foreach (var c in value)
                            if (
                                !(c == 0x21
                                    || c >= 0x23 && c <= 0x2b
                                    || c >= 0x2d && c <= 0x3a
                                    || c >= 0x3c && c <= 0x5b
                                    || c >= 0x5d && c <= 0x7e)
                            )
                                throw RequestBindingValues.Failure(parameterName, "Invalid cookie value.");

                        values.Add(name, value);
                    }
                }
            }

            else
            {
                if (headers.Count > RequestBindingValues.MaxValues)
                    throw RequestBindingValues.Failure(parameterName, "Too many header fields.");

                foreach (var key in headers.AllKeys)
                    if (string.Equals(key, _name, StringComparison.OrdinalIgnoreCase))
                    {
                        var raw = headers.GetValues(key);

                        if (raw != null) foreach (var value in raw) values.Add(_name, value ?? string.Empty);
                    }
            }

            var selected = values.Get(_name, _required, _many, _duplicates, parameterName);

            if (selected == null) return _missing();

            try
            {
                return _parse(selected);
            }

            catch (Exception e) when (e is FormatException || e is OverflowException || e is ArgumentException)
            {
                throw RequestBindingValues.Failure(parameterName, "The field value is invalid.");
            }
        }
    }
}
