using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EmbedIO.OpenApi;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.Tests.Issues
{
    [GeneratedControllerBinding(typeof(Issue139PeopleController), JsonSerializerContext = typeof(Issue139Json))]
    [GeneratedControllerBinding(typeof(Issue139ProblemController))]
    [GeneratedControllerBinding(typeof(Issue139HiddenController))]
    [GeneratedControllerBinding(typeof(Issue139ValuesController))]
    internal static partial class Issue139Bindings
    {
    }

    [JsonSerializable(typeof(Issue139Person))]
    [JsonSerializable(typeof(List<Issue139Person>))]
    [JsonSerializable(typeof(Issue139Error))]
    [JsonSerializable(typeof(Issue139Tree))]
    [JsonSerializable(typeof(string))]
    internal sealed partial class Issue139Json : JsonSerializerContext
    {
    }

    public enum Issue139Status
    {
        Active,
        Retired,
    }

    public sealed class Issue139Person
    {
        public string Name { get; set; } = string.Empty;

        public int Age { get; set; }

        public Issue139Status Status { get; set; }
    }

    public sealed class Issue139Error
    {
        public string Message { get; set; } = string.Empty;
    }

    // Recursive, so the exported schema references itself.
    public sealed class Issue139Tree
    {
        public string Name { get; set; } = string.Empty;

        public List<Issue139Tree> Children { get; set; } = new();
    }

    internal sealed class Issue139PeopleController : WebApiController
    {
        internal static readonly List<Issue139Person> People = new() { new Issue139Person { Name = "Ada", Age = 36, Status = Issue139Status.Active } };

        [Route(HttpVerbs.Get, "/people")]
        [OpenApiOperation(Summary = "List people", Tags = new[] { "People", "Directory" })]
        public List<Issue139Person> List([QueryField] int? limit, [QueryField("status")] Issue139Status[] statuses, [QueryData] NameValueCollection query)
            => People.FindAll(p => statuses.Length == 0 || Array.IndexOf(statuses, p.Status) >= 0).GetRange(0, Math.Min(limit ?? People.Count, People.Count));

        [Route(HttpVerbs.Get, "/people/{index}")]
        [OpenApiResponse(404, Type = typeof(Issue139Error), Description = "No person has that index.")]
        public Issue139Person Get(int index)
            => index >= 0 && index < People.Count ? People[index] : throw HttpException.NotFound();

        [Route(HttpVerbs.Post, "/people")]
        [OpenApiResponse(201, Type = typeof(Issue139Person), Description = "Created.")]
        public Task<Issue139Person> Create([JsonData] Issue139Person person)
        {
            Response.StatusCode = 201;
            return Task.FromResult(person ?? throw new ArgumentNullException(nameof(person)));
        }

        [Route(HttpVerbs.Put, "/people/{index}/name")]
        public void Rename(int index, [FormField(true)] string name, [FormField] string[] aliases)
            => People[index].Name = name + (aliases.Length == 0 ? string.Empty : " (" + string.Join(", ", aliases) + ")");

        [Route(HttpVerbs.Delete, "/people/{key?}")]
        [OpenApiOperation(OperationId = "RemovePerson", Deprecated = true, Description = "Removed by name.")]
        public Task Remove(string key)
        {
            People.RemoveAll(p => p.Name == key);
            return Task.CompletedTask;
        }

        [Route(HttpVerbs.Get, "/tree")]
        public Issue139Tree Tree() => new() { Name = "root", Children = { new Issue139Tree { Name = "leaf" } } };

        [Route(HttpVerbs.Get, "/internal")]
        [OpenApiIgnore]
        public string Internal() => "not documented";
    }

    // Routes an OpenAPI document cannot describe faithfully.
    internal sealed class Issue139ProblemController : WebApiController
    {
        [BaseRoute(HttpVerbs.Get, "/files/")]
        public string Files() => Route.SubPath ?? string.Empty;

        [Route(HttpVerbs.Any, "/any")]
        public string AnyVerb() => Request.HttpVerb.ToString();

        [Route(HttpVerbs.Get, "/custom")]
        public string Custom([Issue138Header("X-Text")] string text) => text;

        [Route(HttpVerbs.Get, "/people")]
        public string Duplicate() => "same path and method as List";

        [Route(HttpVerbs.Get, "/fine")]
        public string Fine() => "fine";
    }

    // Values EmbedIO's default JSON options convert with their own converters.
    public sealed class Issue139Values
    {
        public bool Flag { get; set; }

        public bool? MaybeFlag { get; set; }

        public Issue139Status Status { get; set; }

        public DateTime When { get; set; }

        public DateTimeOffset? Offset { get; set; }

        public int Count { get; set; }
    }

    internal sealed class Issue139ValuesController : WebApiController
    {
        [Route(HttpVerbs.Get, "/values")]
        public Issue139Values Values() => new();
    }

    [OpenApiIgnore]
    internal sealed class Issue139HiddenController : WebApiController
    {
        [Route(HttpVerbs.Get, "/hidden")]
        public string Hidden() => "hidden";
    }
}
