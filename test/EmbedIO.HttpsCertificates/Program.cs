using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

// Disposable test credentials, never production credentials. Only the leaf key
// is exported; the test CA private key exists solely in this process.
if (args.Length < 1) throw new ArgumentException("Specify the test certificate output directory and optional device DNS names/IPs.");
Directory.CreateDirectory(args[0]);
var start = DateTimeOffset.UtcNow.AddMinutes(-5);
var end = start.AddDays(2);
using var rootKey = RSA.Create(2048);
var rootRequest = new CertificateRequest($"CN=EmbedIO test CA {Guid.NewGuid():N}", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
using var root = rootRequest.CreateSelfSigned(start, end);
using var leafKey = RSA.Create(2048);
var leafRequest = new CertificateRequest("CN=localhost", leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
var names = new SubjectAlternativeNameBuilder();
names.AddDnsName("localhost");
names.AddIpAddress(IPAddress.Loopback);
foreach (var host in args.Skip(1))
{
    if (IPAddress.TryParse(host, out var address)) names.AddIpAddress(address);
    else names.AddDnsName(host);
}
leafRequest.CertificateExtensions.Add(names.Build());
leafRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(leafRequest.PublicKey, false));
leafRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
using var publicLeaf = leafRequest.Create(root, start, end, RandomNumberGenerator.GetBytes(16));
using var leaf = publicLeaf.CopyWithPrivateKey(leafKey);
using var publicRoot = X509CertificateLoader.LoadCertificate(root.Export(X509ContentType.Cert));
var chain = new X509Certificate2Collection { leaf, publicRoot };
File.WriteAllBytes(Path.Combine(args[0], "https-server.pfx"), chain.Export(X509ContentType.Pfx)!);
File.WriteAllBytes(Path.Combine(args[0], "https-test-root.cer"), publicRoot.RawData);
File.WriteAllText(Path.Combine(args[0], "https-test-root.pem"), publicRoot.ExportCertificatePem());
Console.WriteLine($"Generated disposable test CA {root.Thumbprint}; expires {end:O}");
