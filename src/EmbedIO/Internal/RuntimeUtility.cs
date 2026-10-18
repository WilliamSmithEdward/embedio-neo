namespace EmbedIO.Internal
{
    internal enum Endianness
    {
        Big,
        Little
    }

    internal static class SelfCheck
    {
        internal static EmbedIOInternalErrorException Failure(string message) =>
            new(message);
    }
}
