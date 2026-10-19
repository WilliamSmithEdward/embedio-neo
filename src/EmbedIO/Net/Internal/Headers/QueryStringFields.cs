using System.Collections.Specialized;
using System.Net;

namespace EmbedIO.Net.Internal
{
    // Both request models receive Uri.Query. Keep collection creation with the
    // caller so eager parsing and lazy empty-collection publication stay unchanged.
    internal static class QueryStringFields
    {
        internal static NameValueCollection Parse(string query)
        {
            var result = new NameValueCollection();
            AddTo(query, result);
            return result;
        }

        internal static void AddTo(string query, NameValueCollection result)
        {
            if (query.Length == 0) return;

            for (var start = query[0] == '?' ? 1 : 0; ;)
            {
                var end = query.IndexOf('&', start);

                if (end < 0) end = query.Length;

                var equals = query.IndexOf('=', start, end - start);

                if (equals < 0) result.Add(null, WebUtility.UrlDecode(query.Substring(start, end - start)));

                else
                    result.Add(
                        WebUtility.UrlDecode(query.Substring(start, equals - start)),
                        WebUtility.UrlDecode(query.Substring(equals + 1, end - equals - 1))
                    );

                if (end == query.Length) return;

                start = end + 1;
            }
        }
    }
}
