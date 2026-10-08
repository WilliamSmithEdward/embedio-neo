using System;
using System.IO;
using System.Reflection;
using System.Runtime.Versioning;
using EmbedIO;

internal static class Program
{
    private static int Main()
    {
        try
        {
            var target = typeof(WebServer).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
            if (target != ".NETStandard,Version=v2.0") throw new InvalidOperationException("Expected the legacy-compatible core asset.");
            var requests = 0;
            foreach (var layered in new[] { false, true })
                requests += EmbedIO.PlatformTests.BasicAuthenticationSmoke.RunAsync(HttpListenerMode.Microsoft, "http://localhost:21087/", layered).GetAwaiter().GetResult();
            if (requests != 20) throw new InvalidOperationException("Incomplete authentication checks.");
            Directory.CreateDirectory("TestResults");
            var result = EmbedIO.Serialization.Json.Serialize(new
            {
                passed = true,
                requests,
                core_target = target,
                clr_version = Environment.Version.ToString(),
                framework_release = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full", "Release", null),
                core = typeof(WebServer).Assembly.Location
            });
            File.WriteAllText("TestResults/legacy-authentication.json", result);
            Console.WriteLine(result);
            return 0;
        }
        catch (Exception error) when (EmbedIO.Internal.ExceptionPolicy.IsRecoverable(error)) { Console.Error.WriteLine(error); return 1; }
    }
}
