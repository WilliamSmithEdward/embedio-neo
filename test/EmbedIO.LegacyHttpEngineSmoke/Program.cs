using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;

public static class Program
{
    private static int _checks;

    public static void Main()
    {
        RunAsync().GetAwaiter().GetResult();
        var assembly = typeof(WebServer).Assembly;
        var target = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        Check(target == ".NETStandard,Version=v2.0", "Fixture must load the retained .NET Standard asset.");
        using var sha = SHA256.Create();
        var report = JsonSerializer.Serialize(new
        {
            passed = true,
            assertions = _checks,
            fixtureTarget = "net472",
            loadedTarget = target,
            clr = Environment.Version.ToString(),
            frameworkRelease = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", null),
            engine = assembly.Location,
            engineSha256 = BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(assembly.Location))).Replace("-", ""),
            informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        }, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory("TestResults");
        File.WriteAllText("TestResults/legacy-http-engine.json", report);
        Console.WriteLine(report);
    }

    private static async Task RunAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var url = "http://127.0.0.1:" + port + "/";
        using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(HttpListenerMode.EmbedIO))
            .WithModule(new ActionModule("/plain", HttpVerbs.Get, context =>
            {
                context.Response.Headers["X-Peer-Port"] = context.Request.RemoteEndPoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return context.SendStringAsync("legacy-engine", "text/plain", WebServer.Utf8NoBomEncoding);
            }))
            .WithModule(new ActionModule("/echo", HttpVerbs.Post, async context =>
            {
                using var body = new MemoryStream();
                await context.Request.InputStream.CopyToAsync(body).ConfigureAwait(false);
                context.Response.ContentLength64 = body.Length;
                var bytes = body.ToArray();
                await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }));
        using var stop = new CancellationTokenSource();
        var running = server.RunAsync(stop.Token);
        try
        {
            using var handler = new HttpClientHandler { UseProxy = false };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var peers = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < 256; i++)
            {
                using var response = await client.GetAsync(url + "plain").ConfigureAwait(false);
                Check(response.StatusCode == HttpStatusCode.OK, "HTTP/1 status.");
                Check(await response.Content.ReadAsStringAsync().ConfigureAwait(false) == "legacy-engine", "HTTP/1 body.");
                peers.Add(response.Headers.GetValues("X-Peer-Port").Single());
            }
            Check(peers.Count == 1, "256 requests must retain one connection beyond the former 100-request cap.");
            var payload = Enumerable.Range(0, 65537).Select(i => (byte)(i * 31)).ToArray();
            using (var request = new HttpRequestMessage(HttpMethod.Post, url + "echo"))
            {
                request.Headers.TransferEncodingChunked = true;
                request.Content = new StreamContent(new MemoryStream(payload, false));
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                Check(response.StatusCode == HttpStatusCode.OK, "Chunked upload status.");
                Check((await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).SequenceEqual(payload), "Chunked upload bytes.");
            }
            RejectAmbiguousFraming(port);
            PriorKnowledgeHttp2(port);
        }
        finally
        {
            stop.Cancel();
            if (await Task.WhenAny(running, Task.Delay(10000)).ConfigureAwait(false) != running)
                throw new TimeoutException("Legacy engine shutdown exceeded ten seconds.");
            await running.ConfigureAwait(false);
        }
    }

    private static TcpClient Connect(int port)
    {
        var client = new TcpClient { NoDelay = true, ReceiveTimeout = 10000, SendTimeout = 10000 };
        client.Connect(IPAddress.Loopback, port);
        return client;
    }

    private static void RejectAmbiguousFraming(int port)
    {
        using var client = Connect(port);
        using var wire = client.GetStream();
        var request = Encoding.ASCII.GetBytes("POST /echo HTTP/1.1\r\nHost: 127.0.0.1:" + port
            + "\r\nContent-Length: 4\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n");
        wire.Write(request, 0, request.Length);
        using var response = new MemoryStream();
        wire.CopyTo(response);
        Check(Encoding.ASCII.GetString(response.ToArray()).StartsWith("HTTP/1.1 400 ", StringComparison.Ordinal), "Ambiguous framing must be rejected and closed.");
    }

    private static void PriorKnowledgeHttp2(int port)
    {
        using var client = Connect(port);
        using var wire = client.GetStream();
        var preface = Encoding.ASCII.GetBytes("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n");
        wire.Write(preface, 0, preface.Length);
        Frame(wire, 4, 0, 0, Array.Empty<byte>());
        using var block = new MemoryStream();
        block.WriteByte(0x82); // Static indexed GET.
        block.WriteByte(0x86); // Static indexed http.
        Literal(block, 1, "127.0.0.1:" + port); // :authority.
        Literal(block, 4, "/plain"); // :path.
        Frame(wire, 1, 5, 1, block.ToArray());
        using var response = new MemoryStream();
        var headersReceived = false;
        for (var frames = 0; frames < 100; frames++)
        {
            var header = Exact(wire, 9);
            var length = (header[0] << 16) | (header[1] << 8) | header[2];
            Check(length <= 16384, "Peer must respect the default frame size.");
            var payload = Exact(wire, length);
            var stream = ((header[5] & 127) << 24) | (header[6] << 16) | (header[7] << 8) | header[8];
            Check(header[3] != 7 && !(header[3] == 3 && stream == 1), "Healthy HTTP/2 request must not receive GOAWAY or reset.");
            if (header[3] == 4 && (header[4] & 1) == 0) Frame(wire, 4, 1, 0, Array.Empty<byte>());
            if (stream != 1) continue;
            if (header[3] == 1) headersReceived = true;
            if (header[3] == 0)
            {
                Check((header[4] & 8) == 0, "This fixture expects unpadded DATA.");
                response.Write(payload, 0, payload.Length);
            }
            if ((header[3] == 0 || header[3] == 1) && (header[4] & 1) != 0)
            {
                Check(headersReceived, "HTTP/2 response requires HEADERS.");
                Check(Encoding.UTF8.GetString(response.ToArray()) == "legacy-engine", "HTTP/2 response bytes.");
                return;
            }
        }
        throw new InvalidDataException("HTTP/2 response did not finish within 100 frames.");
    }

    private static void Literal(Stream output, byte index, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length >= 127) throw new ArgumentOutOfRangeException(nameof(value));
        output.WriteByte(index);
        output.WriteByte((byte)bytes.Length);
        output.Write(bytes, 0, bytes.Length);
    }

    private static void Frame(Stream output, byte type, byte flags, int stream, byte[] payload)
    {
        var header = new byte[] { (byte)(payload.Length >> 16), (byte)(payload.Length >> 8), (byte)payload.Length, type, flags,
            (byte)(stream >> 24), (byte)(stream >> 16), (byte)(stream >> 8), (byte)stream };
        output.Write(header, 0, header.Length);
        output.Write(payload, 0, payload.Length);
    }

    private static byte[] Exact(Stream input, int length)
    {
        var bytes = new byte[length];
        for (var read = 0; read < length;)
        {
            var count = input.Read(bytes, read, length - read);
            if (count == 0) throw new EndOfStreamException("HTTP/2 frame ended early.");
            read += count;
        }
        return bytes;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}
