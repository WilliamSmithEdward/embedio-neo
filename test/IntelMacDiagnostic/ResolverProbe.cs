#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
// Temporary diagnostic, run as a .NET 10 file-based app outside the repository tree.
// Times the resolver calls that System.Net.HttpListener makes while registering prefixes.
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

var output = args.Length == 1 ? args[0] : throw new ArgumentException("Usage: ResolverProbe <output.json>");
var samples = new List<Dictionary<string, object?>>();

Dictionary<string, object?> Time(string name, Func<object?> action)
{
    var stopwatch = Stopwatch.StartNew();
    object? result = null;
    string? error = null;
    try { result = action(); }
    catch (Exception e) { error = e.GetType().FullName + ": " + e.Message; }
    stopwatch.Stop();
    var sample = new Dictionary<string, object?>
    {
        ["name"] = name,
        ["milliseconds"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
        ["result"] = result,
        ["error"] = error,
    };
    samples.Add(sample);
    Console.WriteLine($"{name}: {sample["milliseconds"]} ms {(error ?? JsonSerializer.Serialize(result))}");
    return sample;
}

static int FreePort()
{
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)socket.LocalEndPoint!).Port;
}

for (var round = 1; round <= 3; round++)
{
    Time($"round{round}.Dns.GetHostName", () => Dns.GetHostName());
    Time($"round{round}.Dns.GetHostEntry(empty)", () =>
    {
        var entry = Dns.GetHostEntry(string.Empty);
        return new { entry.HostName, Addresses = entry.AddressList.Select(a => a.ToString()).ToArray() };
    });
    Time($"round{round}.Dns.GetHostEntry(localhost)", () => Dns.GetHostEntry("localhost").HostName);
    Time($"round{round}.Dns.GetHostAddresses(localhost)", () => Dns.GetHostAddresses("localhost").Select(a => a.ToString()).ToArray());
    foreach (var host in new[] { "127.0.0.1", "*", "localhost", "[::1]" })
    {
        Time($"round{round}.HttpListener.Prefixes.Add({host})", () =>
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://{host}:{FreePort()}/");
            return listener.Prefixes.Count;
        });
    }
}

var report = new Dictionary<string, object?>
{
    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    ["architecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    ["samples"] = samples,
};
File.WriteAllText(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n");
