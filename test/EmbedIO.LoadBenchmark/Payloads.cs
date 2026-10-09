using System.Collections.Concurrent;
using System.Globalization;

// Request paths and response bodies shared by every server engine and the client,
// so each engine runs identical application work.
internal enum RouteKind
{
    Plaintext,
    Bytes,
    Stream,
    Upload,
}

internal readonly record struct BenchmarkRoute(RouteKind Kind, int Length, int Chunk)
{
    internal const int MaxLength = 64 * 1024 * 1024;

    internal string Path => Kind switch
    {
        RouteKind.Plaintext => "/plaintext",
        RouteKind.Bytes => "/bytes/" + Length.ToString(CultureInfo.InvariantCulture),
        RouteKind.Stream => "/stream/" + Length.ToString(CultureInfo.InvariantCulture) + "/" + Chunk.ToString(CultureInfo.InvariantCulture),
        _ => "/upload",
    };

    // Expected response body length for the client's validation.
    internal int ResponseLength => Kind switch
    {
        RouteKind.Plaintext => Payloads.Plaintext.Length,
        RouteKind.Upload => Payloads.UploadAcknowledgement(Length).Length,
        _ => Length,
    };

    internal static bool TryParse(string path, out BenchmarkRoute route)
    {
        route = default;
        if (path == "/plaintext")
        {
            route = new(RouteKind.Plaintext, 0, 0);
            return true;
        }

        if (path == "/upload")
        {
            route = new(RouteKind.Upload, 0, 0);
            return true;
        }

        var segments = path.Split('/');
        if (segments.Length == 3 && segments[1] == "bytes" && TryLength(segments[2], out var length))
        {
            route = new(RouteKind.Bytes, length, 0);
            return true;
        }

        if (segments.Length == 4 && segments[1] == "stream" && TryLength(segments[2], out length)
            && TryLength(segments[3], out var chunk) && chunk > 0)
        {
            route = new(RouteKind.Stream, length, chunk);
            return true;
        }

        return false;
    }

    private static bool TryLength(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= MaxLength;
}

internal static class Payloads
{
    private static readonly ConcurrentDictionary<int, byte[]> Cache = new();

    internal static readonly byte[] Plaintext = "Hello, World!"u8.ToArray();

    internal static readonly byte[] UploadRejected = "mismatch"u8.ToArray();

    // Multiplicative hashing of the offset gives a 2^32 period, so reordered or
    // duplicated slices of a large body change the bytes the receiver checks.
    internal static byte At(long offset) => (byte)(unchecked((uint)offset * 2654435761u) >> 24);

    // Bodies are immutable static content. Every engine serves the same cached arrays.
    internal static byte[] Get(int length) => Cache.GetOrAdd(length, static size =>
    {
        var body = new byte[size];
        for (var index = 0; index < body.Length; index++) body[index] = At(index);
        return body;
    });

    internal static byte[] UploadAcknowledgement(long length)
        => System.Text.Encoding.ASCII.GetBytes(length.ToString(CultureInfo.InvariantCulture));

    // A precomputed reference makes body validation a vectorized comparison in
    // every engine. Bodies beyond it fall back to computing each byte.
    private static byte[] reference = [];

    internal static void Prepare(int length) => reference = Get(length);

    // Validates one received slice of a pattern body at its absolute offset.
    internal static bool Matches(ReadOnlySpan<byte> slice, long offset)
    {
        var expected = reference;
        if (offset + slice.Length <= expected.Length)
            return slice.SequenceEqual(expected.AsSpan((int)offset, slice.Length));

        for (var index = 0; index < slice.Length; index++)
        {
            if (slice[index] != At(offset + index)) return false;
        }

        return true;
    }
}
