using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Sessions;

public static class Program
{
    private static int _checks;
    public static void Main()
    {
        RunAsync().GetAwaiter().GetResult();
        Directory.CreateDirectory("TestResults");
        var report = new { passed = true, assertions = _checks, target = "net472", clr = Environment.Version.ToString(),
            frameworkRelease = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", null) };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText("TestResults/legacy-cookie-validation.json", json, new UTF8Encoding(false));
        Console.WriteLine(json);
    }

    private static async Task RunAsync()
    {
        foreach (var mode in new[] { HttpListenerMode.EmbedIO, HttpListenerMode.Microsoft })
        {
            var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            var url = "http://localhost:" + ((IPEndPoint)port.LocalEndpoint).Port + "/";
            port.Stop();
            var manager = new LocalSessionManager { CookieSameSite = CookieSameSiteMode.Lax };
            using (var stop = new CancellationTokenSource())
            using (var server = new WebServer(o => o.WithUrlPrefix(url).WithMode(mode)).WithSessionManager(manager).OnGet("/", async c =>
            {
                var path = c.Request.Url.AbsolutePath;
                if (path == "/raw")
                {
                    c.Response.Headers.Add("set-cookie", "raw=value; SameSite=Strict; Max-Age=42; Path=/");
                    c.Response.Headers.Add("set-cookie", "other=two; SameSite=None; Secure; Priority=High");
                }
                else if (path.StartsWith("/session", StringComparison.Ordinal))
                {
                    c.Session["state"] = "yes";
                    if (path == "/session/delete") c.Session.Delete();
                }
                else
                {
                    var cookie = new Cookie("entry", "value", "/scope") { HttpOnly = true, Secure = path == "/None" };
                    if (path == "/default") c.Response.SetCookie(cookie);
                    else c.Response.SetCookie(cookie, (CookieSameSiteMode)Enum.Parse(typeof(CookieSameSiteMode), path.TrimStart('/')));
                }
                await c.SendStringAsync("ok", "text/plain", Encoding.UTF8);
            }))
            using (var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) })
            {
                var running = server.RunAsync(stop.Token);
                try
                {
                    foreach (var policy in new[] { "default", "Lax", "Strict", "None" })
                    using (var response = await client.GetAsync(url + policy))
                    {
                        Check(await response.Content.ReadAsStringAsync() == "ok", "HTTP response");
                        var header = response.Headers.GetValues("Set-Cookie").Single();
                        Check(header.Contains("; HttpOnly"), "HttpOnly");
                        Check(policy == "default" ? !header.Contains("SameSite") : header.Contains("; SameSite=" + policy), "SameSite");
                        Check(header.Contains("; Secure") == (policy == "None"), "Secure");
                    }
                    using (var response = await client.GetAsync(url + "raw"))
                    {
                        var headers = response.Headers.GetValues("Set-Cookie").ToArray();
                        Check(headers.Length == 2, "Independent raw headers");
                        Check(headers[0] == "raw=value; SameSite=Strict; Max-Age=42; Path=/", "Raw attributes");
                        Check(headers[1] == "other=two; SameSite=None; Secure; Priority=High", "Raw extensions");
                    }
                    string session;
                    using (var response = await client.GetAsync(url + "session"))
                    {
                        var header = response.Headers.GetValues("Set-Cookie").Single();
                        Check(header.Contains("; SameSite=Lax") && header.Contains("; HttpOnly"), "Session creation");
                        session = header.Split(';')[0];
                    }
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url + "session/delete"))
                    {
                        request.Headers.Add("Cookie", session);
                        using (var response = await client.SendAsync(request))
                            Check(response.Headers.GetValues("Set-Cookie").Last().Contains("; SameSite=Lax"), "Session deletion");
                    }
                }
                finally { stop.Cancel(); await running; }
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }
}
