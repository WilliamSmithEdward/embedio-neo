namespace EmbedIO
{
    /// <summary>
    /// Enumerates the different HTTP Verbs.
    /// </summary>
    public enum HttpVerbs
    {
        /// <summary>
        /// Wildcard Method
        /// </summary>
        Any,

        /// <summary>
        /// DELETE Method
        /// </summary>
        Delete,

        /// <summary>
        /// GET Method
        /// </summary>
        Get,

        /// <summary>
        /// HEAD method
        /// </summary>
        Head,

        /// <summary>
        /// OPTIONS method
        /// </summary>
        Options,

        /// <summary>
        /// PATCH method
        /// </summary>
        Patch,

        /// <summary>
        /// POST method
        /// </summary>
        Post,

        /// <summary>
        /// PUT method
        /// </summary>
        Put,

        /// <summary>
        /// QUERY method: a safe, idempotent query with request content (RFC 10008).
        /// The handler must validate its query media type and preserve these semantics.
        /// </summary>
        Query,
    }
}
