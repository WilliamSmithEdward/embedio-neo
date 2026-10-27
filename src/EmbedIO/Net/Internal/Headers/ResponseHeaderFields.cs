using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Net;
using System.Threading;

namespace EmbedIO.Net.Internal
{
    // The fields of a response head until something reads the response's Headers. A
    // WebHeaderCollection with the fields the engine adds to every response costs about 2 KB,
    // the largest single allocation of a small request, so the engine keeps them in this
    // list. Collection builds the WebHeaderCollection from the fields so far, in their order,
    // and from then on holds them. Only a write the collection would store unchanged stays in
    // the list: a token name without an apostrophe and a value of printable ASCII that neither
    // starts nor ends with a space. Any other write, and a read the collection would answer
    // differently, builds the collection first and goes to it, so the fields are the
    // collection's on both targets. Not thread-safe, as WebHeaderCollection is not.
    internal sealed class ResponseHeaderFields
    {
        private WebHeaderCollection? _collection;
        private Field[] _fields;
        private int _count;

        // The response sizes the list for the fields the engine writes to an ordinary head, so
        // the list grows only for the rest.
        internal ResponseHeaderFields(int capacity) => _fields = new Field[capacity];

        internal WebHeaderCollection Collection
        {
            get
            {
                var collection = Volatile.Read(ref _collection);

                if (collection != null) return collection;

                var created = new WebHeaderCollection();

                for (var index = 0; index < _count; index++)
                {
                    var entry = _fields[index];

                    if (entry.Value is string value) created.Add(entry.Name, value);

                    else
                        foreach (var each in (List<string>)entry.Value)
                            created.Add(entry.Name, each);
                }

                return Interlocked.CompareExchange(ref _collection, created, null) ?? created;
            }
        }

        internal int Count => _collection?.Count ?? _count;

        // Whether every field has one value that the collection's GetValues(name) returns as it
        // is, so a reader can take NameAt and ValueAt in place of it: a value under a name the
        // collection never splits, or one without a comma under any name but Set-Cookie, whose
        // value the collection drops unless it holds an equals sign.
        internal bool IsSimple
        {
            get
            {
                if (_collection != null) return false;

                for (var index = 0; index < _count; index++)
                {
                    var entry = _fields[index];

                    if (entry.Value is not string value) return false;

                    if (
                        !IsUnsplit(entry.Name)
                        && (EmbedIO.Internal.StringOperations.IndexOfOrdinal(value, ',') >= 0
                            || string.Equals(entry.Name, HttpHeaderNames.SetCookie, StringComparison.OrdinalIgnoreCase))
                    )
                        return false;
                }

                return true;
            }
        }

        // The collection's Get(name): every value of the field, joined with commas.
        internal string? Value(string name)
        {
            if (_collection != null) return _collection.Get(name);

            var index = Find(name);
            return index < 0 ? null : Joined(_fields[index]);
        }

        // The collection's Set(name, value), which its indexer's setter calls.
        internal void Set(string name, string? value)
        {
            if (_collection == null && value != null && IsPlainName(name) && IsPlainValue(value))
            {
                var index = Find(name);

                // An existing field keeps its position and the case of its first name.
                if (index >= 0) _fields[index] = new Field(_fields[index].Name, value);

                else Append(name, value);

                return;
            }

            Collection.Set(name, value);
        }

        internal void Add(string name, string? value)
        {
            if (_collection == null && value != null && IsPlainName(name) && IsPlainValue(value))
            {
                var index = Find(name);

                if (index < 0) Append(name, value);

                else if (_fields[index].Value is List<string> values) values.Add(value);

                else
                    _fields[index] = new Field(_fields[index].Name, new List<string>(2)
                    {
                        (string)_fields[index].Value,
                        value
                    });

                return;
            }

            Collection.Add(name, value);
        }

        internal void Remove(string name)
        {
            if (_collection == null && IsPlainName(name))
            {
                var index = Find(name);

                if (index < 0) return;

                _count--;
                Array.Copy(_fields, index + 1, _fields, index, _count - index);
                _fields[_count] = default;
                return;
            }

            Collection.Remove(name);
        }

        internal string NameAt(int index) => _collection != null ? _collection.GetKey(index) : At(index).Name;

        // The collection's Get(index): the field's values joined with commas.
        internal string? ValueAt(int index) => _collection != null ? _collection.Get(index) : Joined(At(index));

        // The collection's GetValues(name), which splits the values of some fields it knows. The
        // list answers only for names the collection leaves whole.
        internal string[]? Values(string? name)
        {
            // The collection refuses a null name here, so a null name goes to it.
            if (_collection == null && name != null)
            {
                var index = Find(name);

                if (index < 0) return null;

                if (IsUnsplit(name))
                    return _fields[index].Value is string value ? [value] : [.. (List<string>)_fields[index].Value];
            }

            // The base signature takes null; the call still reaches the collection's override.
            return ((NameValueCollection)Collection).GetValues(name);
        }

        private Field At(int index) =>
            (uint)index < (uint)_count ? _fields[index] : throw new ArgumentOutOfRangeException(nameof(index));

        private int Find(string? name)
        {
            if (name == null) return -1;

            for (var index = 0; index < _count; index++)
            {
                var candidate = _fields[index].Name;

                if (
                    candidate.Length == name.Length
                    && string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)
                )
                    return index;
            }

            return -1;
        }

        private void Append(string name, string value)
        {
            if (_count == _fields.Length) Array.Resize(ref _fields, Math.Max(4, _count * 2));

            _fields[_count++] = new Field(name, value);
        }

        private static string Joined(Field entry) =>
            entry.Value as string ?? string.Join(",", (List<string>)entry.Value);

        // Names the collection's GetValues(name) returns whole on .NET and .NET Framework alike.
        private static bool IsUnsplit(string name) =>
            string.Equals(name, HttpHeaderNames.ContentType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, HttpHeaderNames.ContentLength, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, HttpHeaderNames.Date, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, HttpHeaderNames.Server, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, HttpHeaderNames.KeepAlive, StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, HttpHeaderNames.AltSvc, StringComparison.OrdinalIgnoreCase);

        // A token (RFC 9110, Section 5.6.2) without the apostrophe, which the collection refuses.
        private static bool IsPlainName(string? name)
        {
            if (name == null || name.Length == 0) return false;

            foreach (var character in name)
            {
                if (
                    character is (>= 'a' and <= 'z')
                        or (>= 'A' and <= 'Z')
                        or (>= '0' and <= '9')
                        or '!'
                        or '#'
                        or '$'
                        or '%'
                        or '&'
                        or '*'
                        or '+'
                        or '-'
                        or '.'
                        or '^'
                        or '_'
                        or '`'
                        or '|'
                        or '~'
                )
                    continue;

                return false;
            }

            return true;
        }

        // Printable ASCII the collection stores as it is: no control character, nothing it
        // trims at either end, and not empty.
        private static bool IsPlainValue(string value)
        {
            if (value.Length == 0 || value[0] == ' ' || value[value.Length - 1] == ' ') return false;

            foreach (var character in value)
                if (character is < ' ' or > '~')
                    return false;

            return true;
        }

        // A name and its values: one as a string, or two or more as a list in the order they were
        // added.
        private readonly struct Field(string name, object value)
        {
            internal readonly string Name = name;
            internal readonly object Value = value;
        }
    }
}
