using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using EmbedIO.OpenApi;
using EmbedIO.Problems;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.AotSmoke;

internal static class ValidationChecks
{
    internal static WebApiModule CreateModule()
    {
        var module = new WebApiModule("/validation", ResponseSerializer.Json(SmokeJson.Default.Options))
            .WithController(ValidationSmokeBindings.ValidationSmokeController);
        module.HandleHttpException(ProblemDetailsHandler.ForHttpExceptions(new ProblemDetailsOptions { HtmlHttpExceptionHandler = null }));
        return module;
    }

    internal static async Task RunAsync(HttpClient client, string url, SortedDictionary<string, string> checks)
    {
        foreach (var (body, expected) in new[] { ("{}", HttpStatusCode.BadRequest), ("null", HttpStatusCode.BadRequest), ("{\"Name\":\"ok\",\"Count\":1}", HttpStatusCode.BadRequest), ("{\"Name\":\"ok\",\"Count\":2}", HttpStatusCode.OK), ("{\"Name\":\"ok\",\"Count\":2,\"Child\":{}}", HttpStatusCode.BadRequest) })
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url + "validation/value", content);
            var text = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != expected || expected == HttpStatusCode.BadRequest && !text.Contains("errors", StringComparison.Ordinal))
                throw new InvalidOperationException("AOT validation response mismatch: " + text);
        }
        var document = new OpenApiDocumentBuilder("AOT validation", "1").AddController("/validation", ValidationSmokeBindings.ValidationSmokeController).Build(SmokeJson.Default.Options);
        var shape = document.Root["paths"]?["/validation/value"]?["post"]?["requestBody"]?["content"]?["application/json"]?["schema"]?["allOf"]?[1];
        if (shape?["properties"]?["Name"]?["maxLength"]?.GetValue<int>() != 4) throw new InvalidOperationException("AOT validation schema missing.");
        checks["request_validation"] = "generated nested validation, accepted input, 400 field errors and OpenAPI constraints";
    }
}

[GeneratedControllerBinding(typeof(ValidationSmokeController), JsonSerializerContext = typeof(SmokeJson))]
internal static partial class ValidationSmokeBindings { }

internal abstract class ValidationSmokeBaseController : WebApiController
{
    [Route(HttpVerbs.Post, "/value")]
    public virtual string Value([JsonData, Validate, Required] ValidatedSmokeInput? input) => "ok";
}

internal sealed class ValidationSmokeController : ValidationSmokeBaseController
{
    public override string Value([JsonData] ValidatedSmokeInput? input) => "ok";
}

internal sealed class ValidatedSmokeInput : IValidatableObject
{
    [Required, StringLength(4, MinimumLength = 2)]
    public string? Name { get; set; }
    [Range(1, 10), SmokeCount(0, 100), SmokeCount(2, 20)]
    public int Count { get; set; }
    public ValidatedSmokeInput? Child { get; set; }
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Name == "no") yield return new ValidationResult("Use another name.", new[] { nameof(Name) });
    }
}

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
internal abstract class SmokeCountRuleAttribute : ValidationAttribute
{
    private readonly int _minimum;
    private readonly int _maximum;
    protected SmokeCountRuleAttribute(int minimum, int maximum) { _minimum = minimum; _maximum = maximum; }
    public override bool IsValid(object? value) => value is int number && number >= _minimum && number <= _maximum;
}

internal sealed class SmokeCountAttribute : SmokeCountRuleAttribute
{
    public SmokeCountAttribute(int minimum, int maximum) : base(minimum, maximum) { }
}
