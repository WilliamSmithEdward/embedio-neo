using System;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Internal
{
    // The transport side of a public WebTransportSession. Only the .NET 10 HTTP/3
    // listener implements it; the public type exists on every target.
    internal interface IWebTransportSessionBackend
    {
        HttpDatagramChannel Datagrams { get; }
        Task Completion { get; }
        uint? CloseCode { get; }
        string? CloseReason { get; }

        ValueTask<WebTransportStream?> AcceptStreamAsync(CancellationToken token);

        ValueTask<WebTransportStream> OpenStreamAsync(bool bidirectional, CancellationToken token);

        Task CloseAsync(uint errorCode, string reason, CancellationToken token);

        // Closes with code 0 if still open and completes when the session has ended.
        Task DisposeAsync();
    }

    // The transport side of a public WebTransportStream, after its session header.
    internal interface IWebTransportStreamBackend
    {
        bool IsBidirectional { get; }
        bool CanRead { get; }
        bool CanWrite { get; }

        ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token);

        ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token);

        Task CompleteWritesAsync(CancellationToken token);

        void Abort(WebTransportAbortDirection direction, uint errorCode);

        ValueTask DisposeAsync();
    }
}
