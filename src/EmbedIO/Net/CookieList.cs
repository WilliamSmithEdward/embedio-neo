using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using EmbedIO.Net.Internal;

namespace EmbedIO.Net
{
    /// <summary>A mutable collection of cookies with the existing identity and lookup contract.</summary>
    public sealed class CookieList : List<Cookie>, ICookieCollection
    {
        /// <inheritdoc />
        public bool IsSynchronized => false;
        /// <inheritdoc />
        public Cookie? this[string name]
        {
            get
            {
                if (name == null) throw new ArgumentNullException(nameof(name));
                Cookie? best = null;
                var tied = false;
                foreach (var candidate in this)
                {
                    if (!candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                    var order = best == null ? -1 : Rank(candidate, best);
                    if (order < 0) { best = candidate; tied = false; }
                    else if (order == 0) tied = true;
                }
                if (!tied) return best;
                // Equal-ranked domains used the framework sort's tie selection.
                // Preserve that compatibility corner while ordinary lookups allocate nothing.
                var ranked = new List<Cookie>(this);
                ranked.Sort(Rank);
                foreach (var candidate in ranked)
                    if (candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return candidate;
                return null;
            }
        }
        /// <summary>Parses cookie fields, retaining ordered attributes and quoted separators.</summary>
        /// <param name="headerValue">One or more cookie/header values.</param>
        /// <returns>The parsed collection.</returns>
        public static CookieList Parse(string headerValue) => SetCookieFields.Read(headerValue);
        /// <inheritdoc />
        public new void Add(Cookie cookie)
        {
            if (cookie == null) throw new ArgumentNullException(nameof(cookie));
            for (var slot = Count - 1; slot >= 0; slot--)
            {
                var existing = this[slot];
                if (existing.Version == cookie.Version
                    && existing.Name.Equals(cookie.Name, StringComparison.OrdinalIgnoreCase)
                    && existing.Path.Equals(cookie.Path, StringComparison.OrdinalIgnoreCase)
                    && existing.Domain.Equals(cookie.Domain, StringComparison.OrdinalIgnoreCase))
                { this[slot] = cookie; return; }
            }
            base.Add(cookie);
        }
        /// <inheritdoc />
        public void CopyTo(Array array, int index)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "Less than zero.");
            if (array.Rank > 1) throw new ArgumentException("Multidimensional.", nameof(array));
            if (array.Length - index < Count)
                throw new ArgumentException("The number of elements in this collection is greater than the available space of the destination array.");
            if (array.GetType().GetElementType()?.IsAssignableFrom(typeof(Cookie)) != true)
                throw new InvalidCastException("The elements in this collection cannot be cast automatically to the type of the destination array.");
            ((IList)this).CopyTo(array, index);
        }
        private static int Rank(Cookie left, Cookie right)
        {
            var order = left.Version.CompareTo(right.Version);
            if (order == 0) order = string.CompareOrdinal(left.Name, right.Name);
            return order != 0 ? order : right.Path.Length.CompareTo(left.Path.Length);
        }
    }
}
