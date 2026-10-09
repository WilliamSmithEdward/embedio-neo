using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// Stateful differential fuzzing of the managed WebSocket engine (issue #190).
// Each case drives the real internal socket over a scripted duplex stream that
// returns input in random chunks and can fail a chosen read or write. The result
// (messages delivered to the subscribed consumer, frames written by the server,
// close-callback count and termination) is compared with an independent batch
// model of RFC 6455 and the engine's documented failure policy.
internal static class WebSocketFuzz
{
    private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private static readonly Assembly Core = typeof(EmbedIO.WebServer).Assembly;
    private static readonly Type SocketType = Core.GetType("EmbedIO.WebSockets.Internal.WebSocket", true) ?? throw new InvalidOperationException("Missing socket type.");
    private static readonly Type EventArgsType = Core.GetType("EmbedIO.WebSockets.Internal.MessageEventArgs", true) ?? throw new InvalidOperationException("Missing message type.");
    private static readonly MethodInfo FromStream = SocketType.GetMethod("FromStream", Hidden) ?? throw new InvalidOperationException("Missing FromStream.");
    private static readonly MethodInfo SetLimit = SocketType.GetMethod("SetAcceptedMaxMessageSize", Hidden) ?? throw new InvalidOperationException("Missing limit setter.");
    private static readonly MethodInfo WaitForClose = SocketType.GetMethod("WaitForCloseAsync", Hidden) ?? throw new InvalidOperationException("Missing close observer.");
    private static readonly PropertyInfo RawData = EventArgsType.GetProperty("RawData", Hidden) ?? throw new InvalidOperationException("Missing RawData.");
    private static readonly PropertyInfo EventOpcode = EventArgsType.GetProperty("Opcode", Hidden) ?? throw new InvalidOperationException("Missing Opcode.");
    private const string FatalReason = "An exception has occurred while receiving.";

    private sealed record Message(int Opcode, string Hex);
    private sealed record Outcome(Message[] Delivered, string[] Written);

    internal static async Task<bool> RunAsync(string[] args)
    {
        var replay = args.Length > 0 && args[0] == "--websocket-replay";
        if (args.Length == 0 || (args[0] != "--websocket" && !replay)) return false;
        if (replay ? args.Length != 6 : args.Length != 3)
            throw new ArgumentException("--websocket seed iterations | --websocket-replay inputHex limit readFault writeFault chunkSeed");
        var seed = replay ? 0 : int.Parse(args[1]);
        var iterations = replay ? 1 : int.Parse(args[2]);
        if (iterations < 1 || iterations > 1_000_000) throw new ArgumentOutOfRangeException(nameof(iterations));
        var random = new Random(seed);
        var unobserved = 0;
        EventHandler<UnobservedTaskExceptionEventArgs> observe = (_, e) => { Interlocked.Increment(ref unobserved); Console.Error.WriteLine(e.Exception); };
        TaskScheduler.UnobservedTaskException += observe;
        byte[] input = Array.Empty<byte>();
        int limit = 0, readFault = -1, writeFault = -1, chunkSeed = 0, iteration = 0;
        var outcomes = new Dictionary<string, int>();
        try
        {
            for (; iteration < iterations; ++iteration)
            {
                if (replay)
                {
                    input = Convert.FromHexString(args[1]);
                    limit = int.Parse(args[2]); readFault = int.Parse(args[3]);
                    writeFault = int.Parse(args[4]); chunkSeed = int.Parse(args[5]);
                }
                else
                {
                    input = Generate(random);
                    limit = random.Next(10) < 7 ? 0 : random.Next(1, 300);
                    readFault = random.Next(8) == 0 ? random.Next(input.Length + 1) : -1;
                    writeFault = random.Next(8) == 0 ? random.Next(4) : -1;
                    chunkSeed = random.Next();
                }
                // Faults truncate the observable script; the model sees the same cut.
                var (expected, kind) = Reference(readFault < 0 ? input : input.Take(readFault).ToArray(), limit, writeFault);
                outcomes[kind] = outcomes.TryGetValue(kind, out var count) ? count + 1 : 1;
                var actual = await RunEngine(input, limit, readFault, writeFault, chunkSeed);
                var a = JsonSerializer.Serialize(actual);
                var e = JsonSerializer.Serialize(expected);
                if (a != e) throw new InvalidDataException($"Oracle mismatch ({kind}).\nexpected {e}\nactual   {a}");
            }
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            if (Volatile.Read(ref unobserved) != 0) throw new InvalidDataException($"{unobserved} unobserved task exceptions escaped the engine.");
        }
        catch (Exception)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                seed,
                iteration,
                limit,
                readFault,
                writeFault,
                chunkSeed,
                inputHex = Convert.ToHexString(input),
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                assembly = Core.ManifestModule.ModuleVersionId,
            }));
            Console.Error.WriteLine($"Replay: dotnet run --project test/EmbedIO.Fuzz -c Release -- --websocket-replay {Convert.ToHexString(input)} {limit} {readFault} {writeFault} {chunkSeed}");
            throw;
        }
        finally { TaskScheduler.UnobservedTaskException -= observe; }
        Console.WriteLine($"Passed {iterations} WebSocket sessions with chunked reads and injected faults; seed {seed}. Endings: "
            + string.Join(", ", outcomes.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}={p.Value}")));
        return true;
    }

    private sealed class Collector
    {
        internal readonly ConcurrentQueue<Message> Messages = new();
        public void Handle(object? sender, EventArgs args)
            => Messages.Enqueue(new Message(Convert.ToInt32(EventOpcode.GetValue(args), System.Globalization.CultureInfo.InvariantCulture), Convert.ToHexString(RawData.GetValue(args) as byte[] ?? throw new InvalidDataException("Message has no data."))));
    }

    private static async Task<Outcome> RunEngine(byte[] input, int limit, int readFault, int writeFault, int chunkSeed)
    {
        using var transport = new ScriptedStream(input, new Random(chunkSeed), readFault, writeFault);
        var closes = 0;
        SetLimit.Invoke(null, new object[] { limit });
        object socket;
        try { socket = FromStream.Invoke(null, new object[] { transport, (Action)(() => Interlocked.Increment(ref closes)) }) ?? throw new InvalidOperationException("Missing socket."); }
        finally { SetLimit.Invoke(null, new object[] { 0 }); }
        var collector = new Collector();
        var eventInfo = SocketType.GetEvent("OnMessage") ?? throw new InvalidOperationException("Missing OnMessage.");
        eventInfo.AddEventHandler(socket, Delegate.CreateDelegate(eventInfo.EventHandlerType ?? throw new InvalidOperationException("Missing handler type."), collector, typeof(Collector).GetMethod(nameof(Collector.Handle)) ?? throw new InvalidOperationException("Missing handler.")));
        transport.Subscribed.SetResult();
        var closed = WaitForClose.Invoke(socket, new object[] { CancellationToken.None }) as Task ?? throw new InvalidOperationException("Missing close task.");
        if (await Task.WhenAny(closed, Task.Delay(TimeSpan.FromSeconds(10))) != closed)
            throw new TimeoutException("The socket never completed its close.");
        await closed;
        if (Volatile.Read(ref closes) != 1) throw new InvalidDataException($"Close callback ran {closes} times.");
        if (((EmbedIO.WebSockets.IWebSocket)socket).State != System.Net.WebSockets.WebSocketState.Closed)
            throw new InvalidDataException("Socket did not reach Closed.");
        ((IDisposable)socket).Dispose();
        if (Volatile.Read(ref closes) != 1) throw new InvalidDataException("Dispose ran the close callback again.");
        return new Outcome(collector.Messages.ToArray(), transport.Writes.ToArray());
    }

    private sealed class ScriptedStream : Stream
    {
        private readonly byte[] _input;
        private readonly Random _random;
        private readonly int _readFault;
        private readonly int _writeFault;
        private int _position;
        private int _writes;
        internal readonly TaskCompletionSource Subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentQueue<string> Writes = new();

        internal ScriptedStream(byte[] input, Random random, int readFault, int writeFault)
        {
            _input = input; _random = random; _readFault = readFault; _writeFault = writeFault;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Expected asynchronous reads.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Expected asynchronous writes.");

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            // Data queued before a consumer subscribes is discarded on close by design
            // (#556), so the script starts once the consumer is attached.
            await Subscribed.Task.ConfigureAwait(false);
            if (_random.Next(4) == 0) await Task.Yield();
            var end = _readFault >= 0 ? _readFault : _input.Length;
            if (_position >= end)
            {
                if (_readFault >= 0) throw new IOException("Injected read fault.");
                return 0;
            }
            var take = Math.Min(count, Math.Min(end - _position, 1 + _random.Next(64)));
            Buffer.BlockCopy(_input, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_random.Next(4) == 0) await Task.Yield();
            if (_writes++ == _writeFault) throw new IOException("Injected write fault.");
            Writes.Enqueue(Convert.ToHexString(buffer, offset, count));
        }
    }

    // ----- Independent model -----

    private enum End { Truncated, ProtocolFailure, PeerClose, Rejected, WriteFault }

    private static (Outcome, string) Reference(byte[] input, int limit, int writeFault)
    {
        var delivered = new List<Message>();
        var written = new List<string>();
        var position = 0;
        var closing = false;           // a close was sent; data is discarded until the peer's close
        var inContinuation = false;
        var textMessage = false;
        var utf8 = new Utf8State();
        var messageOpcode = 0;
        var messageLength = 0L;
        var buffer = new List<byte>();
        long skip = 0;
        var rejected = string.Empty;   // which close-handshake failure, if any, preceded the end

        bool Write(byte[] frame)
        {
            if (written.Count == writeFault) return false;
            written.Add(Convert.ToHexString(frame));
            return true;
        }
        (Outcome, string) Done(End end) => (new Outcome(delivered.ToArray(), written.ToArray()), end + rejected);
        // Generic failure: send the status only while open, then stop reading.
        (Outcome, string) Fail(int code, string end = "ProtocolFailure")
        {
            if (!closing && code != 1006) Write(CloseFrame(code, FatalReason));
            return (new Outcome(delivered.ToArray(), written.ToArray()), end);
        }

        while (true)
        {
            if (skip > 0)
            {
                if (input.Length - position < skip) return Done(End.Truncated);
                position += (int)skip; skip = 0;
            }
            if (input.Length - position < 2) return Done(End.Truncated);
            var b0 = input[position]; var b1 = input[position + 1];
            position += 2;
            var fin = (b0 & 0x80) != 0; var rsv1 = (b0 & 0x40) != 0; var rsv2 = (b0 & 0x20) != 0; var rsv3 = (b0 & 0x10) != 0;
            var opcode = b0 & 0x0f; var masked = (b1 & 0x80) != 0; long length = b1 & 0x7f;
            var control = opcode >= 8;
            if (!(opcode <= 2 || (opcode >= 8 && opcode <= 10))) return Fail(1002);
            if (opcode != 1 && opcode != 2 && rsv1) return Fail(1002);
            if (control && !fin) return Fail(1002);
            if (control && length > 125) return Fail(1002);
            if (opcode == 8 && length == 1) return Fail(1002);
            if (!masked) return Fail(1002);
            if (!inContinuation && opcode == 0) return Fail(1002);
            if (inContinuation && (opcode == 1 || opcode == 2)) return Fail(1002);
            if (rsv1 || rsv2 || rsv3) return Fail(1002);
            if (length >= 126)
            {
                var width = length == 126 ? 2 : 8;
                if (input.Length - position < width) return Done(End.Truncated);
                ulong value = 0;
                for (var i = 0; i < width; ++i) value = (value << 8) | input[position + i];
                position += width;
                if ((width == 8 && (value >> 63) != 0) || (width == 2 && value < 126) || (width == 8 && value < 65536)) return Fail(1002);
                if (value > int.MaxValue) return Fail(1009);
                length = (long)value;
            }
            // Message size, before the masking key or payload is read.
            if (opcode <= 2)
            {
                var total = (opcode == 0 ? messageLength : 0) + length;
                if ((limit > 0 && total > limit) || total > int.MaxValue)
                {
                    var reason = total > int.MaxValue ? "Message exceeds the supported representation." : $"Message too big. Maximum is {limit} bytes.";
                    skip = 4 + length;
                    messageLength = 0; textMessage = false;
                    if (!fin || opcode == 0) { buffer.Clear(); inContinuation = !fin; }
                    if (!closing) { closing = true; rejected = "+1009"; if (!Write(CloseFrame(1009, reason))) return Done(End.WriteFault); }
                    continue;
                }
                messageLength = fin ? 0 : total;
            }
            if (input.Length - position < 4 + length) return Done(End.Truncated);
            var key = input.Skip(position).Take(4).ToArray();
            position += 4;
            var payload = new byte[length];
            for (var i = 0; i < length; ++i) payload[i] = (byte)(input[position + i] ^ key[i % 4]);
            position += (int)length;

            if (opcode == 8 && payload.Length > 0)
            {
                var code = (payload[0] << 8) | payload[1];
                if (code < 1000 || code >= 5000 || code == 1004 || code == 1005 || code == 1006 || (code >= 1015 && code < 3000)) return Fail(1002);
                if (!IsUtf8(payload.AsSpan(2))) return Fail(1007);
            }
            // Text validation, independent of the open/closing state.
            if (opcode == 1) { textMessage = true; utf8 = new Utf8State(); }
            else if (opcode == 2) textMessage = false;
            if ((opcode == 1 || (opcode == 0 && textMessage)) && !utf8.Feed(payload, fin))
            {
                textMessage = false; messageLength = 0;
                if (!fin || opcode == 0) { buffer.Clear(); inContinuation = !fin; }
                if (!closing) { closing = true; rejected = "+1007"; if (!Write(CloseFrame(1007, "Text message is not valid UTF-8."))) return Done(End.WriteFault); }
                continue;
            }
            if (opcode == 1 && fin || opcode == 0 && fin) textMessage = false;

            if (closing && opcode != 8)
            {
                if (!fin || opcode == 0) { buffer.Clear(); inContinuation = !fin; }
                continue;
            }
            if (!fin || opcode == 0)
            {
                if (!inContinuation) { messageOpcode = opcode; buffer.Clear(); inContinuation = true; }
                buffer.AddRange(payload);
                if (fin) { delivered.Add(new Message(messageOpcode, Convert.ToHexString(buffer.ToArray()))); inContinuation = false; }
                continue;
            }
            switch (opcode)
            {
                case 1:
                case 2:
                    delivered.Add(new Message(opcode, Convert.ToHexString(payload)));
                    break;
                case 9:
                    if (!Write(Frame(0x8a, payload))) return Done(End.WriteFault);
                    break;
                case 8:
                    if (!closing) Write(Frame(0x88, payload));
                    return Done(End.PeerClose);
            }
        }
    }

    private static byte[] Frame(byte first, byte[] payload)
    {
        var header = payload.Length < 126 ? new[] { first, (byte)payload.Length }
            : payload.Length <= ushort.MaxValue ? new[] { first, (byte)126, (byte)(payload.Length >> 8), (byte)payload.Length }
            : throw new InvalidOperationException("Control payloads are short.");
        return header.Concat(payload).ToArray();
    }

    private static byte[] CloseFrame(int code, string reason)
        => Frame(0x88, new[] { (byte)(code >> 8), (byte)code }.Concat(Encoding.UTF8.GetBytes(reason)).ToArray());

    private static bool IsUtf8(ReadOnlySpan<byte> bytes)
    {
        var state = new Utf8State();
        return state.Feed(bytes.ToArray(), true);
    }

    // Table-free RFC 3629 decoder kept separate from the production validator.
    private struct Utf8State
    {
        private int _needed;
        private int _lower;
        private int _upper;

        internal bool Feed(byte[] bytes, bool final)
        {
            foreach (var value in bytes)
            {
                if (_needed > 0)
                {
                    if (value < _lower || value > _upper) return false;
                    _needed--; _lower = 0x80; _upper = 0xbf;
                    continue;
                }
                _lower = 0x80; _upper = 0xbf;
                if (value < 0x80) continue;
                if (value >= 0xc2 && value <= 0xdf) _needed = 1;
                else if (value == 0xe0) { _needed = 2; _lower = 0xa0; }
                else if (value >= 0xe1 && value <= 0xec || value == 0xee || value == 0xef) _needed = 2;
                else if (value == 0xed) { _needed = 2; _upper = 0x9f; }
                else if (value == 0xf0) { _needed = 3; _lower = 0x90; }
                else if (value >= 0xf1 && value <= 0xf3) _needed = 3;
                else if (value == 0xf4) { _needed = 3; _upper = 0x8f; }
                else return false;
            }
            return !final || _needed == 0;
        }
    }

    // ----- Generation -----

    private static readonly string[] Texts = { "", "a", "héllo", "世界", "\U0001F680 ok", "mixed é世\U0001F680" };

    private static byte[] Generate(Random random)
    {
        var wire = new List<byte>();
        var frames = random.Next(1, 9);
        for (var i = 0; i < frames; ++i)
        {
            switch (random.Next(10))
            {
                case 0:
                case 1:
                    Encode(wire, random, 0x81, Encoding.UTF8.GetBytes(Texts[random.Next(Texts.Length)]));
                    break;
                case 2:
                    Encode(wire, random, 0x82, Bytes(random, random.Next(4) == 0 ? random.Next(120, 400) : random.Next(12)));
                    break;
                case 3:
                    {
                        // Split a text or binary message at arbitrary byte boundaries,
                        // with control frames between fragments.
                        var text = random.Next(2) == 0;
                        var body = text ? Encoding.UTF8.GetBytes(Texts[random.Next(Texts.Length)] + Texts[random.Next(Texts.Length)]) : Bytes(random, random.Next(40));
                        var parts = random.Next(2, 5);
                        var offset = 0;
                        for (var p = 0; p < parts; ++p)
                        {
                            var take = p == parts - 1 ? body.Length - offset : random.Next(body.Length - offset + 1);
                            Encode(wire, random, (byte)((p == 0 ? text ? 1 : 2 : 0) | (p == parts - 1 ? 0x80 : 0)), body.Skip(offset).Take(take).ToArray());
                            offset += take;
                            if (p < parts - 1 && random.Next(3) == 0) Encode(wire, random, (byte)(random.Next(2) == 0 ? 0x89 : 0x8a), Bytes(random, random.Next(6)));
                        }
                        break;
                    }
                case 4:
                    Encode(wire, random, 0x81, Bytes(random, random.Next(1, 5)));    // usually invalid UTF-8
                    break;
                case 5:
                    Encode(wire, random, 0x89, Bytes(random, random.Next(10)));
                    break;
                case 6:
                    Encode(wire, random, 0x8a, Bytes(random, random.Next(10)));
                    break;
                case 7:
                    {
                        var codes = new[] { 1000, 1001, 1002, 1003, 1004, 1005, 1007, 1011, 1015, 2999, 3000, 4999, 5000, 999 };
                        var code = codes[random.Next(codes.Length)];
                        var reason = random.Next(3) == 0 ? new byte[] { 0xc0, 0x80 } : Encoding.UTF8.GetBytes(Texts[random.Next(Texts.Length)]);
                        var payload = random.Next(6) == 0 ? Array.Empty<byte>() : new[] { (byte)(code >> 8), (byte)code }.Concat(reason).ToArray();
                        Encode(wire, random, 0x88, payload);
                        break;
                    }
                case 8:
                    // Lengths at the 7/16/64-bit encoding boundaries, sometimes oversized.
                    Encode(wire, random, (byte)(random.Next(2) == 0 ? 0x82 : 0x02), Bytes(random, new[] { 125, 126, 127, 300 }[random.Next(4)]));
                    break;
                default:
                    {
                        var header = (byte)random.Next(256);
                        Encode(wire, random, header, Bytes(random, random.Next(8)));
                        break;
                    }
            }
        }
        for (var mutations = random.Next(3) == 0 ? random.Next(1, 4) : 0; mutations > 0 && wire.Count > 0; --mutations)
        {
            var index = random.Next(wire.Count);
            switch (random.Next(4))
            {
                case 0: wire[index] ^= (byte)(1 << random.Next(8)); break;
                case 1: wire.RemoveAt(index); break;
                case 2: wire.Insert(index, (byte)random.Next(256)); break;
                default: wire.RemoveRange(index, wire.Count - index); break;
            }
        }
        return wire.ToArray();
    }

    private static byte[] Bytes(Random random, int length)
    {
        var bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static void Encode(List<byte> wire, Random random, byte first, byte[] payload)
    {
        wire.Add(first);
        var masked = random.Next(40) != 0;
        var mask = masked ? 0x80 : 0;
        var nonminimal = random.Next(60) == 0;
        if (payload.Length < 126 && !nonminimal) wire.Add((byte)(mask | payload.Length));
        else if (payload.Length <= ushort.MaxValue && !(nonminimal && payload.Length >= 126))
        {
            wire.Add((byte)(mask | 126)); wire.Add((byte)(payload.Length >> 8)); wire.Add((byte)payload.Length);
        }
        else
        {
            wire.Add((byte)(mask | 127));
            for (var shift = 56; shift >= 0; shift -= 8) wire.Add((byte)((long)payload.Length >> shift));
        }
        var key = masked ? Bytes(random, 4) : new byte[4];
        if (masked) wire.AddRange(key);
        for (var i = 0; i < payload.Length; ++i) wire.Add((byte)(payload[i] ^ key[i % 4]));
    }
}
