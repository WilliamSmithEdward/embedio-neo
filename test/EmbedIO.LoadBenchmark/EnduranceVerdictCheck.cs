using System.Net.Sockets;
using System.Text.Json.Nodes;

// Checks the campaign's verdict boundary independently of live transport timing.
internal static class EnduranceVerdictCheck
{
    internal static int Run()
    {
        EnduranceProfileCheck.Run();
        (string? Json, bool Failed)[] reports =
        [
            ("{\"listeners\":[{\"error\":null,\"runError\":null}]}", false),
            ("{\"listeners\":[{\"error\":null,\"runError\":null},{\"error\":null,\"runError\":null}]}", false),
            ("{\"listeners\":[{\"error\":\"drain failure\",\"runError\":null}]}", true),
            ("{\"listeners\":[{\"error\":null,\"runError\":\"server failure\"}]}", true),
            ("{\"listeners\":[{\"error\":null}]}", true),
            ("{\"listeners\":[]}", true),
            ("{}", true),
            ("42", true),
            (null, true),
        ];
        foreach (var (json, failed) in reports)
        {
            var record = new JsonObject();
            Endurance.RecordDrain(record, json is null ? null : JsonNode.Parse(json));
            if ((record["failed"]?.GetValue<bool>() == true) != failed)
                throw new InvalidOperationException("Incorrect verdict for drain report: " + json);
        }

        var priorFailure = new JsonObject { ["failed"] = true };
        Endurance.RecordDrain(priorFailure, JsonNode.Parse(reports[0].Json ?? throw new InvalidOperationException("Missing clean control.")));
        if (priorFailure["failed"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("A later clean report erased a failure.");

        (string Json, bool Failed)[] clients =
        [
            ("{\"errors\":0,\"workersDrainedWithin60Seconds\":true}", false),
            ("{\"errors\":0,\"workersDrainedWithin60Seconds\":false}", true),
            ("{\"errors\":1,\"workersDrainedWithin60Seconds\":true}", true),
            ("{\"errors\":0}", true),
            ("{\"errors\":0,\"workersDrainedWithin60Seconds\":\"true\"}", true),
            ("{}", true),
            ("42", true),
            ("null", true),
        ];
        foreach (var (json, failed) in clients)
            if (Endurance.ClientFailed(JsonNode.Parse(json)) != failed)
                throw new InvalidOperationException("Incorrect client drain verdict: " + json);

        (string Json, bool Failed)[] shutdowns =
        [
            ("{\"serverExitCode\":0,\"serverKilled\":false}", false),
            ("{\"serverExitCode\":1,\"serverKilled\":false}", true),
            ("{\"serverExitCode\":0,\"serverKilled\":true}", true),
            ("{\"serverExitCode\":0}", true),
            ("{}", true),
        ];
        foreach (var (json, failed) in shutdowns)
        {
            var verdict = new JsonArray();
            Recovery.RecordShutdownVerdict(JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Missing shutdown control."), verdict);
            if ((verdict.Count != 0) != failed)
                throw new InvalidOperationException("Incorrect recovery shutdown verdict: " + json);
        }

        Orchestrator.ValidateRunSettings(1, 0, 1, 0, 1, 1);
        Action[] invalidRuns =
        [
            () => Orchestrator.ValidateRunSettings(0, 0, 1, 0, 1, 1),
            () => Orchestrator.ValidateRunSettings(1, 0, 1, 0, 0, 1),
            () => Orchestrator.ValidateRunSettings(1, 0, 1, 0, 1, 0),
            () => Orchestrator.ValidateRunSettings(1, -1, 1, 0, 1, 1),
            () => Orchestrator.ValidateRunSettings(1, 0, 0, 0, 1, 1),
            () => Orchestrator.ValidateRunSettings(1, 0, 1, -1, 1, 1),
            () => Orchestrator.ValidateRunSettings(1, double.NaN, 1, 0, 1, 1),
            () => Orchestrator.ValidateRunSettings(1, 0, double.PositiveInfinity, 0, 1, 1),
        ];
        foreach (var invalid in invalidRuns)
        {
            try { invalid(); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("An empty or invalid comparison was accepted.");
        }

        if (ChildProcess.Mask("0,2-3").ToInt64() != 13 || ChildProcess.Mask("0").ToInt64() != 1)
            throw new InvalidOperationException("Valid affinity changed.");
        foreach (var invalid in new[] { "", "-1", "2-1", "0-64", "64", "0,,1", "0-1-2", "x" })
        {
            try { ChildProcess.Mask(invalid); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("Invalid affinity was accepted: " + invalid);
        }
        if (ChildProcess.ResolveHost("/runtime/dotnet", "/other/dotnet") != "/runtime/dotnet"
            || ChildProcess.ResolveHost("/runner/EmbedIO.LoadBenchmark", "/runtime/dotnet") != "/runtime/dotnet"
            || ChildProcess.ResolveHost("/runner/EmbedIO.LoadBenchmark", null) != "dotnet")
            throw new InvalidOperationException("Child host selection would relaunch the apphost with a DLL argument.");

        var stats = new WorkloadStats(new Workload(WorkKind.Small, Transport.H1, 1));
        stats.Failed(new IOException("planned reset"), true, 0);
        if (stats.Errors != 0) throw new InvalidOperationException("Planned transport interruption was counted as corruption.");
        stats.Failed(new InvalidDataException("wrong response bytes during replacement"), true, 1);
        if (stats.Errors != 1) throw new InvalidOperationException("Replacement masked byte corruption.");
        stats.Failed(new InvalidOperationException("unexpected worker failure"), true, 2);
        if (stats.Errors != 2) throw new InvalidOperationException("Replacement masked a worker failure.");
        stats.Failed(new SocketException((int)SocketError.ConnectionReset), false, 3);
        if (stats.Errors != 3) throw new InvalidOperationException("An unexpected reset was ignored.");

        Control.Write("Endurance verdict checks passed: clean/failed/malformed reports, failure retention and disruption classification.");
        return 0;
    }
}
