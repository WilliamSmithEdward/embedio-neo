using System.Collections.Generic;

namespace EmbedIO.Net.Internal
{
    internal class HttpListenerPrefixCollection : List<string>
    {
        private readonly HttpListener _listener;

        internal HttpListenerPrefixCollection(HttpListener listener)
        {
            _listener = listener;
        }

        public new void Add(string uriPrefix)
        {
            ListenerPrefix.CheckUri(uriPrefix);
            if (Contains(uriPrefix))
            {
                return;
            }

            if (_listener.IsListening)
            {
                EndPointManager.AddPrefix(uriPrefix, _listener);
            }

            // A failed live registration must remain retryable.
            base.Add(uriPrefix);
        }
    }
}
