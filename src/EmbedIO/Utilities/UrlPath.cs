using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EmbedIO.Utilities
{
    /// <summary>
    /// Provides utility methods to work with URL paths.
    /// </summary>
    public static class UrlPath
    {
        /// <summary>
        /// The root URL path value, i.e. <c>"/"</c>.
        /// </summary>
        public const string Root = "/";

        private static readonly Regex MultipleSlashRegex = new Regex("//+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Determines whether a string is a valid URL path.
        /// </summary>
        /// <param name="requestPath">The URL path.</param>
        /// <returns>
        /// <see langword="true"/> if the specified URL path is valid; otherwise, <see langword="false"/>.
        /// </returns>
        /// <remarks>
        /// <para>For a string to be a valid URL path, it must not be <see langword="null"/>,
        /// must not be empty, and must start with a slash (<c>/</c>) character.</para>
        /// <para>To ensure that a method parameter is a valid URL path, use <see cref="Validate.RoutePath"/>.</para>
        /// </remarks>
        /// <seealso cref="Normalize"/>
        /// <seealso cref="UnsafeNormalize"/>
        /// <seealso cref="Validate.RoutePath"/>
        public static bool IsValid(string? requestPath) => ValidateInternal(nameof(requestPath), requestPath) == null;

        /// <summary>
        /// Normalizes the specified URL path.
        /// </summary>
        /// <param name="requestPath">The URL path.</param>
        /// <param name="isBasePath">if set to <see langword="true"/>, treat the URL path
        /// as a base path, i.e. ensure it ends with a slash (<c>/</c>) character;
        /// otherwise, ensure that it does NOT end with a slash character.</param>
        /// <returns>The normalized path.</returns>
        /// <exception cref="ArgumentException">
        /// <paramref name="requestPath"/> is not a valid URL path.
        /// </exception>
        /// <remarks>
        /// <para>A normalized URL path is one where each run of two or more slash
        /// (<c>/</c>) characters has been replaced with a single slash character.</para>
        /// <para>This method does NOT try to decode URL-encoded characters.</para>
        /// <para>If you are sure that <paramref name="requestPath"/> is a valid URL path,
        /// for example because you have called <see cref="IsValid"/> and it returned
        /// <see langword="true"/>, then you may call <see cref="UnsafeNormalize"/>
        /// instead of this method. <see cref="UnsafeNormalize"/> is slightly faster because
        /// it skips the initial validity check.</para>
        /// <para>There is no need to call this method for a method parameter
        /// for which you have already called <see cref="Validate.RoutePath"/>.</para>
        /// </remarks>
        /// <seealso cref="UnsafeNormalize"/>
        /// <seealso cref="IsValid"/>
        /// <seealso cref="Validate.RoutePath"/>
        public static string Normalize(string? requestPath, bool isBasePath)
        {
            var exception = ValidateInternal(nameof(requestPath), requestPath);
            if (exception != null)
                throw exception;

            return UnsafeNormalize(Validate.NotNull(nameof(requestPath), requestPath), isBasePath);
        }

        /// <summary>
        /// Normalizes the specified URL path, assuming that it is valid.
        /// </summary>
        /// <param name="requestPath">The URL path.</param>
        /// <param name="isBasePath">if set to <see langword="true"/>, treat the URL path
        /// as a base path, i.e. ensure it ends with a slash (<c>/</c>) character;
        /// otherwise, ensure that it does NOT end with a slash character.</param>
        /// <returns>The normalized path.</returns>
        /// <remarks>
        /// <para>A normalized URL path is one where each run of two or more slash
        /// (<c>/</c>) characters has been replaced with a single slash character.</para>
        /// <para>This method does NOT try to decode URL-encoded characters.</para>
        /// <para>If <paramref name="requestPath"/> is not valid, the behavior of
        /// this method is unspecified. You should call this method only after
        /// <see cref="IsValid"/> has returned <see langword="true"/>
        /// for the same <paramref name="requestPath"/>.</para>
        /// <para>You should call <see cref="Normalize"/> instead of this method
        /// if you are not sure that <paramref name="requestPath"/> is valid.</para>
        /// <para>There is no need to call this method for a method parameter
        /// for which you have already called <see cref="Validate.RoutePath"/>.</para>
        /// </remarks>
        /// <seealso cref="Normalize"/>
        /// <seealso cref="IsValid"/>
        /// <seealso cref="Validate.RoutePath"/>
        public static string UnsafeNormalize(string? requestPath, bool isBasePath)
        {
            // Replace each run of multiple slashes with a single slash
            requestPath = MultipleSlashRegex.Replace(Validate.NotNull(nameof(requestPath), requestPath), "/");

            // The root path needs no further checking.
            var length = requestPath.Length;
            if (length == 1)
                return requestPath;

            // Base URL paths must end with a slash;
            // non-base URL paths must NOT end with a slash.
            // The final slash is irrelevant for the URL itself
            // (it has to map the same way with or without it)
            // but makes comparing and mapping URLs a lot simpler.
            var finalPosition = length - 1;
            var endsWithSlash = requestPath[finalPosition] == '/';
            return isBasePath
                ? (endsWithSlash ? requestPath : requestPath + "/")
                : (endsWithSlash ? requestPath.Substring(0, finalPosition) : requestPath);
        }

        /// <summary>
        /// Determines whether the specified URL path is prefixed by the specified base URL path.
        /// </summary>
        /// <param name="requestPath">The URL path.</param>
        /// <param name="basePath">The base URL path.</param>
        /// <returns>
        /// <see langword="true"/> if <paramref name="requestPath"/> is prefixed by <paramref name="basePath"/>;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="requestPath"/> is not a valid URL path.</para>
        /// <para>- or -</para>
        /// <para><paramref name="basePath"/> is not a valid base URL path.</para>
        /// </exception>
        /// <remarks>
        /// <para>This method returns <see langword="true"/> even if the two URL paths are equivalent,
        /// for example if both are <c>"/"</c>, or if <paramref name="requestPath"/> is <c>"/download"</c> and
        /// <paramref name="basePath"/> is <c>"/download/"</c>.</para>
        /// <para>If you are sure that both <paramref name="requestPath"/> and <paramref name="basePath"/>
        /// are valid and normalized, for example because you have called <see cref="Validate.RoutePath"/>,
        /// then you may call <see cref="UnsafeHasPrefix"/> instead of this method. <see cref="UnsafeHasPrefix"/>
        /// is slightly faster because it skips validity checks.</para>
        /// </remarks>
        /// <seealso cref="UnsafeHasPrefix"/>
        /// <seealso cref="Normalize"/>
        /// <seealso cref="StripPrefix"/>
        /// <seealso cref="Validate.RoutePath"/>
        public static bool HasPrefix(string? requestPath, string? basePath)
            => UnsafeHasPrefix(
                Validate.RoutePath(nameof(requestPath), requestPath, false),
                Validate.RoutePath(nameof(basePath), basePath, true));

        /// <summary>
        /// Determines whether the specified URL path is prefixed by the specified base URL path,
        /// assuming both paths are valid and normalized.
        /// </summary>
        /// <param name="requestPath">The URL path.</param>
        /// <param name="basePath">The base URL path.</param>
        /// <returns>
        /// <see langword="true"/> if <paramref name="requestPath"/> is prefixed by <paramref name="basePath"/>;
        /// otherwise, <see langword="false"/>.
        /// </returns>
        /// <remarks>
        /// <para>Unless both <paramref name="requestPath"/> and <paramref name="basePath"/> are valid,
        /// normalized URL paths, the behavior of this method is unspecified. You should call this method
        /// only after calling either <see cref="Normalize"/> or <see cref="Validate.RoutePath"/>
        /// to check and normalize both parameters.</para>
        /// <para>If you are not sure about the validity and/or normalization of parameters,
        /// call <see cref="HasPrefix"/> instead of this method.</para>
        /// <para>This method returns <see langword="true"/> even if the two URL paths are equivalent,
        /// for example if both are <c>"/"</c>, or if <paramref name="requestPath"/> is <c>"/download"</c> and
        /// <paramref name="basePath"/> is <c>"/download/"</c>.</para>
        /// </remarks>
        /// <seealso cref="HasPrefix"/>
        /// <seealso cref="Normalize"/>
        /// <seealso cref="StripPrefix"/>
        /// <seealso cref="Validate.RoutePath"/>
        public static bool UnsafeHasPrefix(string? requestPath, string? basePath)
        {
            if (requestPath is null) throw new System.NullReferenceException();
            if (basePath is null) throw new System.NullReferenceException();
            return requestPath.StartsWith(basePath, StringComparison.Ordinal)
            || (requestPath.Length == basePath.Length - 1 && basePath.StartsWith(requestPath, StringComparison.Ordinal));
        }

        /// <summary>
        /// Strips a base URL path fom a URL path, obtaining a relative path.
        /// </summary>
        /// <param name="requestPath">The URL path.</param>
        /// <param name="basePath">The base URL path.</param>
        /// <returns>The relative path, or <see langword="null"/> if <paramref name="requestPath"/>
        /// is not prefixed by <paramref name="basePath"/>.</returns>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="requestPath"/> is not a valid URL path.</para>
        /// <para>- or -</para>
        /// <para><paramref name="basePath"/> is not a valid base URL path.</para>
        /// </exception>
        /// <remarks>
        /// <para>The returned relative path is NOT prefixed by a slash (<c>/</c>) character.</para>
        /// <para>If <paramref name="requestPath"/> and <paramref name="basePath"/> are equivalent,
        /// for example if both are <c>"/"</c>, or if <paramref name="requestPath"/> is <c>"/download"</c>
        /// and <paramref name="basePath"/> is <c>"/download/"</c>, this method returns an empty string.</para>
        /// <para>If you are sure that both <paramref name="requestPath"/> and <paramref name="basePath"/>
        /// are valid and normalized, for example because you have called <see cref="Validate.RoutePath"/>,
        /// then you may call <see cref="UnsafeStripPrefix"/> instead of this method. <see cref="UnsafeStripPrefix"/>
        /// is slightly faster because it skips validity checks.</para>
        /// </remarks>
        /// <seealso cref="UnsafeStripPrefix"/>
        /// <seealso cref="Normalize"/>
        /// <seealso cref="HasPrefix"/>
        /// <seealso cref="Validate.RoutePath"/>
        public static string? StripPrefix(string? requestPath, string? basePath)
            => UnsafeStripPrefix(
                Validate.RoutePath(nameof(requestPath), requestPath, false),
                Validate.RoutePath(nameof(basePath), basePath, true));

        /// <summary>
        /// Strips a base URL path fom a URL path, obtaining a relative path,
        /// assuming both paths are valid and normalized.
        /// </summary>
        /// <param name="requestPath">The URL path.</param>
        /// <param name="basePath">The base URL path.</param>
        /// <returns>The relative path, or <see langword="null"/> if <paramref name="requestPath"/>
        /// is not prefixed by <paramref name="basePath"/>.</returns>
        /// <remarks>
        /// <para>Unless both <paramref name="requestPath"/> and <paramref name="basePath"/> are valid,
        /// normalized URL paths, the behavior of this method is unspecified. You should call this method
        /// only after calling either <see cref="Normalize"/> or <see cref="Validate.RoutePath"/>
        /// to check and normalize both parameters.</para>
        /// <para>If you are not sure about the validity and/or normalization of parameters,
        /// call <see cref="StripPrefix"/> instead of this method.</para>
        /// <para>The returned relative path is NOT prefixed by a slash (<c>/</c>) character.</para>
        /// <para>If <paramref name="requestPath"/> and <paramref name="basePath"/> are equivalent,
        /// for example if both are <c>"/"</c>, or if <paramref name="requestPath"/> is <c>"/download"</c>
        /// and <paramref name="basePath"/> is <c>"/download/"</c>, this method returns an empty string.</para>
        /// </remarks>
        /// <seealso cref="StripPrefix"/>
        /// <seealso cref="Normalize"/>
        /// <seealso cref="HasPrefix"/>
        /// <seealso cref="Validate.RoutePath"/>
        public static string? UnsafeStripPrefix(string? requestPath, string? basePath)
        {
            if (requestPath is null) throw new System.NullReferenceException();
            if (basePath is null) throw new System.NullReferenceException();
            if (!UnsafeHasPrefix(requestPath, basePath))
                return null;

            // The only case where UnsafeHasPrefix returns true for a requestPath shorter than basePath
            // is requestPath == (basePath minus the final slash).
            return requestPath.Length < basePath.Length
                ? string.Empty
                : requestPath.Substring(basePath.Length);
        }

        /// <summary>
        /// Splits the specified URL path into segments.
        /// </summary>
        /// <param name="requestPath">The URL path.</param>
        /// <returns>An enumeration of path segments.</returns>
        /// <exception cref="ArgumentException"><paramref name="requestPath"/> is not a valid URL path.</exception>
        /// <remarks>
        /// <para>A root URL path (<c>/</c>) will result in an empty enumeration.</para>
        /// <para>The returned enumeration will be the same whether <paramref name="requestPath"/> is a base URL path or not.</para>
        /// <para>If you are sure that <paramref name="requestPath"/> is valid and normalized,
        /// for example because you have called <see cref="Validate.RoutePath"/>,
        /// then you may call <see cref="UnsafeSplit"/> instead of this method. <see cref="UnsafeSplit"/>
        /// is slightly faster because it skips validity checks.</para>
        /// </remarks>
        /// <seealso cref="UnsafeSplit"/>
        /// <seealso cref="Normalize"/>
        /// <seealso cref="Validate.RoutePath"/>
        public static IEnumerable<string> Split(string? requestPath)
            => UnsafeSplit(Validate.RoutePath(nameof(requestPath), requestPath, false));

        /// <summary>
        /// Splits the specified URL path into segments, assuming it is valid and normalized.
        /// </summary>
        /// <param name="requestPath">The URL path.</param>
        /// <returns>An enumeration of path segments.</returns>
        /// <remarks>
        /// <para>Unless <paramref name="requestPath"/> is a valid, normalized URL path,
        /// the behavior of this method is unspecified. You should call this method
        /// only after calling either <see cref="Normalize"/> or <see cref="Validate.RoutePath"/>
        /// to check and normalize both parameters.</para>
        /// <para>If you are not sure about the validity and/or normalization of <paramref name="requestPath"/>,
        /// call <see cref="StripPrefix"/> instead of this method.</para>
        /// <para>A root URL path (<c>/</c>) will result in an empty enumeration.</para>
        /// <para>The returned enumeration will be the same whether <paramref name="requestPath"/> is a base URL path or not.</para>
        /// </remarks>
        /// <seealso cref="Split"/>
        /// <seealso cref="Normalize"/>
        /// <seealso cref="Validate.RoutePath"/>
        public static IEnumerable<string> UnsafeSplit(string? requestPath)
        {
            if (requestPath is null) throw new System.NullReferenceException();
            var length = requestPath.Length;
            var position = 1; // Skip initial slash
            while (position < length)
            {
                var slashPosition = requestPath.IndexOf('/', position);
                if (slashPosition < 0)
                {
                    yield return requestPath.Substring(position);
                    break;
                }

                yield return requestPath.Substring(position, slashPosition - position);
                position = slashPosition + 1;
            }
        }

        internal static Exception? ValidateInternal(string argumentName, string? value)
        {
            if (value == null)
                return new ArgumentNullException(argumentName);

            if (value.Length == 0)
                return new ArgumentException("URL path is empty.", argumentName);

            if (value[0] != '/')
                return new ArgumentException("URL path does not start with a slash.", argumentName);

            return null;
        }
    }
}
