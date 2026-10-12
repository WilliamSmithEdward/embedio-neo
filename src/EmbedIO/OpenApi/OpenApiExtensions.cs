namespace EmbedIO.OpenApi
{
    /// <summary>Provides extension methods for serving OpenAPI documents.</summary>
    public static class OpenApiExtensions
    {
        /// <summary>
        /// Adds an <see cref="OpenApiDocumentModule"/> that serves <paramref name="document"/> at
        /// <paramref name="path"/>. Add it before modules whose base routes include the path.
        /// </summary>
        /// <typeparam name="TContainer">The type of the module container.</typeparam>
        /// <param name="this">The module container, typically a web server.</param>
        /// <param name="path">The document's URL path, such as <c>/openapi.json</c>.</param>
        /// <param name="document">The document.</param>
        /// <returns><paramref name="this"/> with the module added.</returns>
        public static TContainer WithOpenApiDocument<TContainer>(this TContainer? @this, string path, OpenApiDocument document)
            where TContainer : class, IWebModuleContainer
            => @this.WithModule(new OpenApiDocumentModule(path, document));
    }
}
