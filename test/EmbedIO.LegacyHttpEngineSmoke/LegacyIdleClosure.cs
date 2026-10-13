using System;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;

public static partial class Program
{
    private static async Task VerifyLegacyIdleClosureAsync(bool secure)
    {
        var shutdownMethod = typeof(SslStream).GetMethod("ShutdownAsync", Type.EmptyTypes);
        var publicShutdownAvailable = shutdownMethod != null && shutdownMethod.ReturnType == typeof(Task);
        using var certificate = secure ? CreateLegacyIdleCertificate() : null;
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var uri = new Uri((secure ? "https" : "http") + "://127.0.0.1:" + port + "/");
        using var listener = new EmbedIO.Net.HttpListener(certificate);
        listener.AddPrefix(uri.ToString());
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var client = new TcpClient();
        using var cancellation = deadline.Token.Register(() => client.Dispose());
        await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        using var wire = client.GetStream();
        using var tls = secure ? new SslStream(wire, true, (_, remote, _, errors) => remote != null
            && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
            && remote.GetCertHashString() == certificate?.GetCertHashString()) : null;
        if (tls != null)
            await tls.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12, false).ConfigureAwait(false);
        Stream stream = tls ?? (Stream)wire;
        var accepting = listener.GetContextAsync(deadline.Token);
        var request = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: " + uri.Authority + "\r\n\r\n");
        await stream.WriteAsync(request, 0, request.Length, deadline.Token).ConfigureAwait(false);
        var context = await accepting.ConfigureAwait(false);
        var connection = context.GetType().GetProperty("Connection", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(context)
            ?? throw new InvalidOperationException("Missing legacy transport.");
        context.Response.StatusCode = 204;
        context.Response.ContentLength64 = 0;
        context.Close();
        using var header = new MemoryStream();
        var next = new byte[1];
        while (header.Length < 8192)
        {
            if (await stream.ReadAsync(next, 0, 1, deadline.Token).ConfigureAwait(false) != 1)
                throw new EndOfStreamException("Legacy idle response ended before its head.");
            header.WriteByte(next[0]);
            if (Encoding.ASCII.GetString(header.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        var head = Encoding.ASCII.GetString(header.ToArray());
        Check(head.StartsWith("HTTP/1.1 204 ", StringComparison.Ordinal), "Legacy idle status.");
        Check(head.Contains("timeout=15"), "Legacy default idle timeout unchanged.");
        var fallback = connection.GetType().GetField("ShutdownTls", BindingFlags.Static | BindingFlags.NonPublic);
        Check(fallback != null && (fallback.GetValue(null) != null) == publicShutdownAvailable,
            "Legacy asset capability matches the actual public runtime API.");
        var eof = false;
        string observation;
        try
        {
            eof = await stream.ReadAsync(next, 0, 1, deadline.Token).ConfigureAwait(false) == 0;
            Check(eof, "Idle close must not manufacture response bytes.");
            observation = "EOF";
        }
        catch (IOException error)
        {
            if (!secure || publicShutdownAvailable) throw;
            observation = error.GetType().Name + ": " + error.Message;
            Check(true, "Legacy TLS may retain its inherited terminal reset.");
        }
        if (!secure || publicShutdownAvailable) Check(eof, "Supported legacy idle closure is orderly.");
        Console.WriteLine("Legacy idle " + (secure ? "TLS fallback" : "plain") + ": " + observation
            + "; publicShutdownAvailable=" + publicShutdownAvailable + "; framework=" + Environment.Version);
        Check(listener.IsListening, "Legacy listener remains active after idle expiry.");
        using var fresh = new TcpClient();
        using var freshCancellation = deadline.Token.Register(() => fresh.Dispose());
        await fresh.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
        using var freshWire = fresh.GetStream();
        using var freshTls = secure ? new SslStream(freshWire, true, (_, remote, _, errors) => remote != null
            && (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) == SslPolicyErrors.None
            && remote.GetCertHashString() == certificate?.GetCertHashString()) : null;
        if (freshTls != null)
            await freshTls.AuthenticateAsClientAsync("localhost", new X509CertificateCollection(), SslProtocols.Tls12, false).ConfigureAwait(false);
        Stream freshStream = freshTls ?? (Stream)freshWire;
        var healthyAccept = listener.GetContextAsync(deadline.Token);
        await freshStream.WriteAsync(request, 0, request.Length, deadline.Token).ConfigureAwait(false);
        var healthy = await healthyAccept.ConfigureAwait(false);
        healthy.Response.StatusCode = 204;
        healthy.Response.ContentLength64 = 0;
        healthy.Response.KeepAlive = false;
        healthy.Close();
        using var healthyHeader = new MemoryStream();
        while (healthyHeader.Length < 8192)
        {
            if (await freshStream.ReadAsync(next, 0, 1, deadline.Token).ConfigureAwait(false) != 1)
                throw new EndOfStreamException("Legacy fresh peer ended before its head.");
            healthyHeader.WriteByte(next[0]);
            if (Encoding.ASCII.GetString(healthyHeader.ToArray()).EndsWith("\r\n\r\n", StringComparison.Ordinal)) break;
        }
        Check(Encoding.ASCII.GetString(healthyHeader.ToArray()).StartsWith("HTTP/1.1 204 ", StringComparison.Ordinal),
            "Legacy fresh peer receives a healthy response after idle expiry.");
    }

    private static X509Certificate2 CreateLegacyIdleCertificate()
    {
        using var key = RSA.Create();
        key.KeySize = 2048;
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        return new X509Certificate2(generated.Export(X509ContentType.Pfx));
    }
}
