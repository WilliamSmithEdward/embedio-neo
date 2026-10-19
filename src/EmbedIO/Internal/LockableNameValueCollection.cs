using System.Collections;
using System.Collections.Specialized;

namespace EmbedIO.Internal
{
    internal sealed class LockableNameValueCollection(IEqualityComparer? comparer = null) : NameValueCollection(
        comparer
    )
    {
        public void MakeReadOnly() => IsReadOnly = true;
    }
}
