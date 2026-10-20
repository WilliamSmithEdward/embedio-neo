using System;
using System.Collections.Specialized;
using System.Net;
using EmbedIO.Internal;

namespace EmbedIO.Utilities
{
    /// <summary>
    /// Parses URL queries or URL-encoded HTML forms.
    /// </summary>
    public static class UrlEncodedDataParser
    {
        /// <summary>
        /// <para>Parses a URL query or URL-encoded HTML form.</para>
        /// <para>Unlike <see cref="HttpListenerRequest.QueryString" />, the returned
        /// <see cref="NameValueCollection" /> will have bracketed indexes stripped away;
        /// for example, <c>a[0]=1&amp;a[1]=2</c> will yield the same result as <c>a=1&amp;a=2</c>,
        /// i.e. a <see cref="NameValueCollection" /> with one key (<c>a</c>) associated with
        /// two values (<c>1</c> and <c>2</c>).</para>
        /// </summary>
        /// <param name="source">The string to parse.</param>
        /// <param name="groupFlags"><para>If this parameter is <see langword="true" />,
        /// tokens not followed by an equal sign (e.g. <c>this</c> in <c>a=1&amp;this&amp;b=2</c>)
        /// will be grouped as values of a <c>null</c> key.
        /// This is the same behavior as the <see cref="IHttpRequest.QueryString" /> and
        /// <see cref="HttpListenerRequest.QueryString" /> properties.</para>
        /// <para>If this parameter is <see langword="false" />, tokens not followed by an equal sign
        /// (e.g. <c>this</c> in <c>a=1&amp;this&amp;b=2</c>) will be considered keys with an empty
        /// value. This is the same behavior as the <see cref="HttpContextExtensions.GetRequestQueryData" />
        /// extension method.</para></param>
        /// <param name="mutableResult"><see langword="true" /> (the default) to return
        /// a mutable (non-read-only) collection; <see langword="false" /> to return a read-only collection.</param>
        /// <returns>A <see cref="NameValueCollection" /> containing the parsed data.</returns>
        public static NameValueCollection Parse(string source, bool groupFlags, bool mutableResult = true)
        {
            var result = new LockableNameValueCollection();

            // An empty source yields an empty collection.
            if (!string.IsNullOrEmpty(source))
            {
                // Skip the initial question mark,
                // in case source is the Query property of a Uri.
                var position = source[0] == '?' ? 1 : 0;

                while (position < source.Length)
                    position = ParsePair(source, position, groupFlags, result);
            }

            if (!mutableResult)
                result.MakeReadOnly();

            return result;
        }

        // Parses the key-value pair starting at position and returns the position of the
        // next pair, or a position past the end of source once the last pair is parsed.
        // Some pairs have only a key, some have both key and value,
        // and a repeated key really means an array.
        private static int ParsePair(string source, int position, bool groupFlags, NameValueCollection result)
        {
            var separatorPos = position;
            var equalPos = -1;

            while (separatorPos < source.Length)
            {
                var c = source[separatorPos];

                if (c == '&')
                    break;

                if (c == '=' && equalPos < 0)
                    equalPos = separatorPos;

                separatorPos++;
            }

            if (equalPos < 0)
            {
                // A token without an equal sign is either a value of the null key
                // (grouped flags) or a key with an empty value.
                var token = source.Substring(position, separatorPos - position);
                AddKeyValuePair(result, groupFlags ? null : token, groupFlags ? token : string.Empty);
            }

            else
            {
                AddKeyValuePair(
                    result,
                    source.Substring(position, equalPos - position),
                    source.Substring(equalPos + 1, separatorPos - equalPos - 1)
                );
            }

            // Edge case: if the last character in source is '&',
            // there's an empty KVP that we would otherwise skip.
            if (separatorPos == source.Length - 1)
            {
                AddKeyValuePair(result, groupFlags ? null : string.Empty, string.Empty);
                return source.Length;
            }

            // On to next KVP
            return separatorPos + 1;
        }

        private static void AddKeyValuePair(NameValueCollection result, string? key, string value)
        {
            if (key != null)
            {
                // Decode the key.
                key = WebUtility.UrlDecode(key);

                // Discard bracketed index (used e.g. by PHP)
                var bracketPos = key.IndexOf("[", StringComparison.Ordinal);

                if (bracketPos > 0)
                    key = key.Substring(0, bracketPos);
            }

            // Decode the value and add the KVP to the collection.
            result.Add(key, WebUtility.UrlDecode(value));
        }
    }
}
