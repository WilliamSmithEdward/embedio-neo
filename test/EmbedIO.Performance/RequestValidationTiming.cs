using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using EmbedIO.WebApi;

internal static class RequestValidationTiming
{
    internal static bool Run(string[] args)
    {
        if (!args.Contains("--request-validation", StringComparer.Ordinal)) return false;
        Console.WriteLine($"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        var input = new Input { Name = "valid", Count = 5 };
        var start = Stopwatch.GetTimestamp();
        var scalar = new RequestValidationPlan(typeof(string), Array.Empty<ValidationAttribute>(), Array.Empty<RequestValidationMember>());
        var number = new RequestValidationPlan(typeof(int), Array.Empty<ValidationAttribute>(), Array.Empty<RequestValidationMember>());
        var plan = new RequestValidationPlan(typeof(Input), Array.Empty<ValidationAttribute>(), new[]
        {
            new RequestValidationMember("Name", null, value => ((Input)value).Name, new ValidationAttribute[] { new RequiredAttribute(), new StringLengthAttribute(32) }, () => scalar),
            new RequestValidationMember("Count", null, value => ((Input)value).Count, new ValidationAttribute[] { new RangeAttribute(1, 10) }, () => number),
        });
        var parameter = new RequestValidationParameter(plan, Array.Empty<ValidationAttribute>(), new ValidateAttribute());
        parameter.Validate(input, "input");
        Console.WriteLine($"plan-and-first-validation-us={Stopwatch.GetElapsedTime(start).TotalMicroseconds:F2}");
        for (var i = 0; i < 20000; i++) parameter.Validate(input, "input");
        const int batches = 1000;
        const int batchSize = 256;
        var samples = new double[batches];
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var batch = 0; batch < batches; batch++)
        {
            start = Stopwatch.GetTimestamp();
            for (var i = 0; i < batchSize; i++) parameter.Validate(input, "input");
            samples[batch] = Stopwatch.GetElapsedTime(start).TotalNanoseconds / batchSize;
        }
        var bytes = (GC.GetAllocatedBytesForCurrentThread() - allocated) / (double)(batches * batchSize);
        Array.Sort(samples);
        Console.WriteLine($"valid-dto-batch-average-ns p50={samples[499]:F1} p95={samples[949]:F1} p99={samples[989]:F1}; bytes/op={bytes:F1}");
        return true;
    }

    private sealed class Input
    {
        public string Name { get; init; } = string.Empty;
        public int Count { get; init; }
    }
}
