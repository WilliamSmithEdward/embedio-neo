using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

public static class Program
{
    private static int _checks;

    public static void Main()
    {
        RunAsync().GetAwaiter().GetResult();
        var report = new
        {
            passed = true,
            assertions = _checks,
            target = "net472",
            clr = Environment.Version.ToString(),
            osReportedByRuntime = Environment.OSVersion.ToString(),
            frameworkRelease = Microsoft.Win32.Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", null),
            assembly = typeof(WebServer).Assembly.FullName,
        };
        Directory.CreateDirectory("TestResults");
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText("TestResults/legacy-route-validation.json", json);
        Console.WriteLine(json);
    }

    private static async Task RunAsync()
    {
        var cases = new (string Path, int Status)[]
        {
            ("u/oops", 400), ("u/65536", 400), ("u/-1", 400), ("u/65535", 200), ("u/0xFFFF", 200),
            ("u/%20", 400), ("optional", 200), ("optional/9", 200), ("choice/oNe", 200), ("choice/99", 200),
            ("u", 404), ("throws/7", 500), ("required-optional", 500), ("custom/generic-wrapper", 500),
            ("custom/bad-format", 400), ("custom/not-supported", 500), ("query?value=oops", 500),
        };
        foreach (var mode in new[] { HttpListenerMode.EmbedIO, HttpListenerMode.Microsoft })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var url = "http://127.0.0.1:" + port + "/";
            using var stop = new CancellationTokenSource();
            using var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode))
                .WithWebApi("/api", m => m.WithController<Controller>());
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var running = server.RunAsync(stop.Token);
            try
            {
                foreach (var test in cases)
                {
                    using var response = await client.GetAsync(url + "api/" + test.Path);
                    // Framework native URL normalization leaves this whitespace-only
                    // segment unmatched; managed conversion sees it. Keep both policies.
                    var expected = test.Path == "u/%20" && mode == HttpListenerMode.Microsoft ? 404 : test.Status;
                    if ((int)response.StatusCode != expected)
                        throw new InvalidOperationException(mode + " " + test.Path + ": expected " + expected
                            + ", received " + (int)response.StatusCode + ".");
                    _checks++;
                }
            }
            finally
            {
                stop.Cancel();
                if (await Task.WhenAny(running, Task.Delay(10000)) != running)
                    throw new TimeoutException("Listener shutdown exceeded ten seconds.");
                await running;
            }
        }
        if (_checks != 34) throw new InvalidOperationException("Legacy route check count changed.");
    }

    public enum Choice { One = 1 }

    [TypeConverter(typeof(CustomConverter))]
    public sealed class CustomValue
    {
        public string Value { get; set; } = string.Empty;
    }

    public sealed class CustomConverter : TypeConverter
    {
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string);

        public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            if ((string)value == "generic-wrapper")
                throw new Exception("Application wrapper.", new FormatException("Application failure."));
            if ((string)value == "bad-format") throw new FormatException("Malformed custom input.");
            if ((string)value == "not-supported") throw new NotSupportedException("Unimplemented converter.");
            return new CustomValue { Value = (string)value };
        }
    }

    public sealed class Controller : WebApiController
    {
        [Route(HttpVerbs.Get, "/u/{value}")]
        public string U(ushort value) => value.ToString(CultureInfo.InvariantCulture);

        [Route(HttpVerbs.Get, "/optional/{value?}")]
        public string Optional(ushort? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "none";

        [Route(HttpVerbs.Get, "/choice/{value}")]
        public string EnumValue(Choice value) => value.ToString();

        [Route(HttpVerbs.Get, "/required-optional/{value?}")]
        public string RequiredOptional(ushort value) => value.ToString(CultureInfo.InvariantCulture);

        [Route(HttpVerbs.Get, "/custom/{value}")]
        public string Custom(CustomValue value) => value.Value;

        [Route(HttpVerbs.Get, "/query")]
        public string Query([QueryField] ushort value) => value.ToString(CultureInfo.InvariantCulture);

        [Route(HttpVerbs.Get, "/throws/{value}")]
        public string Throws(ushort value) => throw new FormatException("Application failure.");
    }
}
