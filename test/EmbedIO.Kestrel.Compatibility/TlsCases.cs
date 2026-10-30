using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.Kestrel;
using EmbedIO.Net;
using EmbedIO.PlatformTests;

internal static class TlsCases
{
    internal static async Task RunAsync(string engine)
    {
        using var first = Certificate("first", true);
        using var second = Certificate("second", true);
        using var clientCertificate = Certificate("client", false);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var source = new ServerCertificateSource(first);
        var options = new WebServerOptions()
            .WithUrlPrefix("https://localhost:" + LoopbackPorts.Free() + "/")
            .WithMode(HttpListenerMode.EmbedIOCombined)
            .WithCertificateSource(source);
        options.TlsPolicy = new ServerTlsPolicy
        {
            ClientCertificateMode = EmbedIO.Net.ClientCertificateMode.Require,
            ClientCertificateValidator = (actual, _, _) => actual.Thumbprint == clientCertificate.Thumbprint
        };

        if (engine == "kestrel") options.WithKestrel();

        var calls = 0;
        using var server = new WebServer(options);
        server.WithModule(new ActionModule(context =>
            {
                Interlocked.Increment(ref calls);
                var certificate = context.Request.GetClientCertificate();
                return context.SendStringAsync(context.Request.ProtocolVersion.Major + ":" + certificate?.Thumbprint, "text/plain", Encoding.UTF8);
            }));
        var running = server.RunAsync(lifetime.Token);
        var cases = new List<string>();
        var versions = new[] { HttpVersion.Version11, HttpVersion.Version20, HttpVersion.Version30 };

        try
        {
            foreach (var version in versions)
            {
                await RequestAsync(options.UrlPrefixes[0], version, first, clientCertificate, lifetime.Token);
                cases.Add("mutual-tls-" + version);
            }

            source.Publish(second);

            foreach (var version in versions)
            {
                await RequestAsync(options.UrlPrefixes[0], version, second, clientCertificate, lifetime.Token);
                cases.Add("rotated-certificate-" + version);
            }

            foreach (var version in versions)
            {
                var before = Volatile.Read(ref calls);
                var refused = false;

                try
                {
                    await RequestAsync(options.UrlPrefixes[0], version, second, null, lifetime.Token);
                }

                catch (HttpRequestException)
                {
                    refused = true;
                }

                if (!refused || Volatile.Read(ref calls) != before)
                    throw new InvalidOperationException(
                        "A request without its required client certificate reached the application: " + version
                    );

                cases.Add("missing-client-certificate-refused-" + version);
            }
        }

        finally
        {
            lifetime.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(4));
        }

        Console.WriteLine(
            JsonSerializer.Serialize(
                new CompatibilityReport(engine, "tls", cases.Count, cases),
                JsonSerializerOptions.Web
            )
        );
    }

    private static async Task RequestAsync(
        string prefix,
        Version version,
        X509Certificate2 server,
        X509Certificate2? certificate,
        CancellationToken token
    )
    {
        using var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback = (_, actual, _, _) => actual?.Thumbprint == server.Thumbprint
        };

        if (certificate != null) handler.ClientCertificates.Add(certificate);

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
        using var request = new HttpRequestMessage(HttpMethod.Get, prefix)
        {
            Version = version,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        using var response = await client.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);

        if (
            !response.IsSuccessStatusCode
                || response.Version != version
                || body != version.Major + ":" + certificate?.Thumbprint
        )
            throw new InvalidOperationException(
                "TLS request did not preserve its protocol and client identity: "
                    + version
                    + " "
                    + response.StatusCode
                    + " "
                    + body
            );
    }

    private static X509Certificate2 Certificate(string name, bool server)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        var usages = new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1)
        );
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            null,
            X509KeyStorageFlags.Exportable
        );
    }
}
