using EmbedIO;

// Listener selection is independent of client transport filtering. Parse before any
// output/certificate/host creation so rejected ordinary-only runs cannot start QUIC.
internal sealed record EnduranceProfile(string Name, string Engine, string? Transports)
{
    internal bool TcpOnly => Name == "tcp-only";

    internal HttpListenerMode TlsMode => TcpOnly ? HttpListenerMode.EmbedIO : HttpListenerMode.EmbedIOCombined;

    internal string TlsListenerDisplay => Engine == "kestrel" ? "Kestrel:Http1AndHttp2AndHttp3" : TlsMode.ToString();

    internal static HashSet<Transport> ParseTransports(string text)
    {
        var result = new HashSet<Transport>();
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Enum.TryParse<Transport>(item, ignoreCase: true, out var transport) || !Enum.IsDefined(transport))
                throw new ArgumentException("The transport list contains an unknown transport.");
            result.Add(transport);
        }
        if (result.Count == 0)
            throw new ArgumentException("The transport list must contain at least one transport.");
        return result;
    }

    internal static EnduranceProfile Parse(CommandLine options)
    {
        var name = options.Text("--listener-profile", "combined");
        if (name is not ("combined" or "tcp-only"))
            throw new ArgumentException("--listener-profile must be combined or tcp-only.");
        var engine = options.Text("--engine", "embedio");
        if (engine is not ("embedio" or "kestrel"))
            throw new ArgumentException("Unknown engine " + engine);
        if (name == "tcp-only" && engine != "embedio")
            throw new ArgumentException("The tcp-only profile requires the embedio engine.");
        var transports = options.Optional("--transports");
        if (transports is not null)
        {
            var parsed = ParseTransports(transports);
            if (name == "tcp-only" && parsed.Contains(Transport.H3))
                throw new ArgumentException("The tcp-only profile does not support HTTP/3 transports.");
        }
        else if (name == "tcp-only")
        {
            transports = "h1,h1tls,h2c,h2tls";
        }

        return new EnduranceProfile(name, engine, transports);
    }
}

internal static class EnduranceProfileCheck
{
    internal static int Run()
    {
        var combined = EnduranceProfile.Parse(new CommandLine([]));
        if (combined.Name != "combined" || combined.TcpOnly || combined.Engine != "embedio"
            || combined.TlsMode != HttpListenerMode.EmbedIOCombined || combined.Transports is not null)
            throw new InvalidOperationException("Default combined profile changed.");
        var tcp = EnduranceProfile.Parse(new CommandLine(["--listener-profile", "tcp-only"]));
        if (!tcp.TcpOnly || tcp.TlsMode != HttpListenerMode.EmbedIO || tcp.Transports != "h1,h1tls,h2c,h2tls")
            throw new InvalidOperationException("TCP-only profile enables an unexpected listener or workload.");
        var subset = EnduranceProfile.Parse(new CommandLine(["--listener-profile", "tcp-only", "--transports", "H1Tls,H2Tls"]));
        if (subset.Transports != "H1Tls,H2Tls")
            throw new InvalidOperationException("Explicit TCP transport subset changed.");
        var kestrel = EnduranceProfile.Parse(new CommandLine(["--engine", "kestrel", "--transports", "h1,h2tls,h3"]));
        if (kestrel.Engine != "kestrel" || kestrel.TcpOnly || kestrel.TlsListenerDisplay != "Kestrel:Http1AndHttp2AndHttp3")
            throw new InvalidOperationException("Combined Kestrel control changed.");
        var compatible = EnduranceProfile.Parse(new CommandLine(["--transports", " h1 ,H2Tls,,4,"]));
        if (!EnduranceProfile.ParseTransports(compatible.Transports ?? throw new InvalidOperationException("Missing filter.")).SetEquals([Transport.H1, Transport.H2Tls, Transport.H3]))
            throw new InvalidOperationException("Legacy combined transport syntax changed.");
        var numericTcp = EnduranceProfile.Parse(new CommandLine(["--listener-profile", "tcp-only", "--transports", "0, 1,2,3,"]));
        if (!EnduranceProfile.ParseTransports(numericTcp.Transports ?? throw new InvalidOperationException("Missing TCP filter.")).SetEquals([Transport.H1, Transport.H1Tls, Transport.H2c, Transport.H2Tls])
            || numericTcp.TlsListenerDisplay != HttpListenerMode.EmbedIO.ToString())
            throw new InvalidOperationException("Defined numeric TCP transport syntax changed.");
        string[][] rejected =
        [
            ["--listener-profile", "unknown"],
            ["--listener-profile"],
            ["--listener-profile", "tcp-only", "--engine", "kestrel"],
            ["--listener-profile", "tcp-only", "--engine", "native"],
            ["--listener-profile", "tcp-only", "--transports", "h3"],
            ["--listener-profile", "tcp-only", "--transports", "h1,H3"],
            ["--listener-profile", "tcp-only", "--transports", ""],
            ["--listener-profile", "tcp-only", "--transports", ",,,"],
            ["--listener-profile", "tcp-only", "--transports", "4"],
            ["--listener-profile", "tcp-only", "--transports", "http2"],
            ["--transports", "99"],
            ["--transports", " "],
        ];
        foreach (var arguments in rejected)
        {
            try { EnduranceProfile.Parse(new CommandLine(arguments)); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Invalid endurance profile was accepted.");
        }

        Control.Write("Endurance profile controls passed: 6 accepted configurations, 12 rejected configurations; no host started.");
        return 0;
    }
}
