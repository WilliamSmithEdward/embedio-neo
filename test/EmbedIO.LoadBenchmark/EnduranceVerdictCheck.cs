using System.Net.Sockets;
using System.Text.Json.Nodes;

// Checks the campaign's verdict boundary independently of live transport timing.
internal static class EnduranceVerdictCheck
{
    internal static int Run()
    {
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
