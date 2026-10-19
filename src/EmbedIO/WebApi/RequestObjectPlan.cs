using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using EmbedIO.Utilities;

namespace EmbedIO.WebApi
{
    /// <summary>A cached, reflection-free binding plan used by generated controllers.</summary>
    /// <typeparam name="T">The request DTO.</typeparam>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class RequestObjectPlan<T> where T : class
    {
        private readonly Func<T> _create;
        private readonly RequestObjectMember<T>[] _members;
        private readonly HashSet<string> _names;
        private readonly bool _rejectUnknown;

        /// <summary>Creates an immutable binding plan.</summary>
        /// <param name="create">DTO factory.</param>
        /// <param name="members">Explicit property accessors.</param>
        /// <param name="rejectUnknown">Whether to reject unknown fields.</param>
        public RequestObjectPlan(Func<T> create, RequestObjectMember<T>[] members, bool rejectUnknown)
        {
            _create = create ?? throw new ArgumentNullException(nameof(create));
            _members = (RequestObjectMember<T>[])(members ?? throw new ArgumentNullException(nameof(members))).Clone();

            if (_members.Length == 0 || _members.Length > RequestBindingValues.MaxFields)
                throw new ArgumentException("A request DTO must have 1–128 bindable properties.", nameof(members));

            _names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var member in _members)
                if (member == null || !_names.Add(member.Name))
                    throw new ArgumentException("Request field names must be unique, ignoring case.", nameof(members));

            _rejectUnknown = rejectUnknown;
        }

        /// <summary>Binds one request, preserving initializers for absent properties.</summary>
        /// <param name="data">Decoded source fields.</param>
        /// <param name="parameterName">CLR error path prefix.</param>
        /// <returns>The bound DTO.</returns>
        public T Bind(NameValueCollection data, string parameterName)
        {
            data = Validate.NotNull(nameof(data), data);

            var values = RequestBindingValues.Read(data);

            if (_rejectUnknown)
                foreach (var name in values.Names)
                    if (!_names.Contains(name))
                        throw RequestBindingValues.Failure(parameterName, "An unknown request field was supplied.");

            var result = _create() ?? throw new InvalidOperationException("The request DTO factory returned null.");

            foreach (var member in _members) member.Bind(result, values, parameterName + "." + member.PropertyName);

            return result;
        }
    }

    /// <summary>A typed property setter and parser used by generated request binding.</summary>
    /// <typeparam name="T">The DTO type.</typeparam>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public abstract class RequestObjectMember<T> where T : class
    {
        private protected RequestObjectMember(string name, string propertyName)
        {
            Name = RequestBindingValues.CheckName(name);
            PropertyName = propertyName ?? throw new ArgumentNullException(nameof(propertyName));
        }

        internal string Name { get; }
        internal string PropertyName { get; }

        internal abstract void Bind(T target, RequestBindingValues values, string path);

        /// <summary>Creates a typed property binding.</summary>
        /// <typeparam name="TValue">Property type.</typeparam>
        /// <param name="name">Source field name.</param>
        /// <param name="propertyName">CLR property name for errors.</param>
        /// <param name="set">Property setter.</param>
        /// <param name="parse">Value parser.</param>
        /// <param name="required">Whether presence is required.</param>
        /// <param name="many">Whether every repeated value is consumed.</param>
        /// <param name="duplicates">Scalar duplicate handling.</param>
        /// <returns>A cached member binding.</returns>
        public static RequestObjectMember<T> Create<TValue>(
            string name,
            string propertyName,
            Action<T, TValue> set,
            Func<string[], TValue> parse,
            bool required,
            bool many,
            RequestDuplicateValues duplicates
        ) => new Member<TValue>(name, propertyName, set, parse, required, many, duplicates);

        private sealed class Member<TValue> : RequestObjectMember<T>
        {
            private readonly Action<T, TValue> _set;
            private readonly Func<string[], TValue> _parse;
            private readonly bool _required;
            private readonly bool _many;
            private readonly RequestDuplicateValues _duplicates;

            internal Member(
                string name,
                string propertyName,
                Action<T, TValue> set,
                Func<string[], TValue> parse,
                bool required,
                bool many,
                RequestDuplicateValues duplicates
            ) : base(name, propertyName)
            {
                _set = set ?? throw new ArgumentNullException(nameof(set));
                _parse = parse ?? throw new ArgumentNullException(nameof(parse));
                RequestBindingValues.CheckDuplicates(duplicates);
                _required = required;
                _many = many;
                _duplicates = duplicates;
            }

            internal override void Bind(T target, RequestBindingValues values, string path)
            {
                var raw = values.Get(Name, _required, _many, _duplicates, path);

                if (raw == null) return;

                TValue parsed;

                try
                {
                    parsed = _parse(raw);
                }

                catch (Exception e) when (e is FormatException || e is OverflowException || e is ArgumentException)
                {
                    throw RequestBindingValues.Failure(path, "The field value is invalid.");
                }

                _set(target, parsed);
            }
        }
    }
}
