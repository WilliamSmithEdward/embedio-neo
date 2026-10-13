using System;

namespace EmbedIO.Files
{
    /// <summary>
    /// The precompressed files a <see cref="FileModule"/> may serve in place of compressing a file itself:
    /// a file such as <c>app.js.br</c> or <c>app.js.gz</c> stored next to <c>app.js</c>.
    /// </summary>
    [Flags]
    public enum PrecompressedVariants
    {
        /// <summary>No precompressed files are served; this is the default.</summary>
        None = 0,

        /// <summary>Serve <c>.gz</c> files to clients that accept <c>gzip</c>.</summary>
        Gzip = 1,

        /// <summary>Serve <c>.br</c> files to clients that accept <c>br</c>. No Brotli encoder is needed, so
        /// this works on the .NET Standard asset too.</summary>
        Brotli = 2,

        /// <summary>Serve both kinds.</summary>
        All = Gzip | Brotli,
    }
}
