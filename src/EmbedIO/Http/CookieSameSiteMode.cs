namespace EmbedIO
{
    /// <summary>Controls whether a cookie is included in cross-site requests.</summary>
    public enum CookieSameSiteMode
    {
        /// <summary>Allows cross-site use; the cookie must also be Secure.</summary>
        None = 0,
        /// <summary>Allows same-site use and certain top-level cross-site navigations.</summary>
        Lax = 1,
        /// <summary>Allows same-site use only.</summary>
        Strict = 2,
    }
}
