using System;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace EmbedIO.Tests.Issues
{
    // Controllers registered both through reflection and through the generated bindings
    // below, so each test can compare the two registrations request by request.
    [GeneratedControllerBinding(typeof(Issue138ParityController))]
    [GeneratedControllerBinding(typeof(Issue138TypedJsonController), JsonSerializerContext = typeof(Issue138Json))]
    [GeneratedControllerBinding(typeof(Issue138DisposableController))]
    [GeneratedControllerBinding(typeof(Issue138AliasController))]
    [GeneratedControllerBinding(typeof(Issue138OtherAliasController))]
    [GeneratedControllerBinding(typeof(Issue138NullDefaultsController))]
    internal static partial class Issue138Bindings
    {
        // Nested context classes receive their bindings inside the containing partial types.
        [GeneratedControllerBinding(typeof(Issue138DisposableController))]
        internal partial class Nested
        {
        }
    }

    // A type converted by its own TypeConverterAttribute, which TypeDescriptor and the generator both honor.
    [System.ComponentModel.TypeConverter(typeof(Issue138PointConverter))]
    public sealed class Issue138Point
    {
        public Issue138Point(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; }

        public int Y { get; }

        public override string ToString() => X.ToString(CultureInfo.InvariantCulture) + ";" + Y.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class Issue138PointConverter : System.ComponentModel.TypeConverter
    {
        public override bool CanConvertFrom(System.ComponentModel.ITypeDescriptorContext? context, Type sourceType)
            => sourceType == typeof(string);

        public override object? ConvertFrom(System.ComponentModel.ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            var parts = (value as string ?? throw new NotSupportedException()).Split(',');
            if (parts.Length != 2)
                throw new FormatException("A point is two comma-separated integers.");

            return new Issue138Point(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        }
    }

    internal sealed class Issue138Person
    {
        public string Name { get; set; } = string.Empty;

        public int Age { get; set; }
    }

    [JsonSerializable(typeof(Issue138Person))]
    internal sealed partial class Issue138Json : JsonSerializerContext
    {
    }

    // Custom request data: a typed interface the generated code calls directly.
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class Issue138HeaderAttribute : Attribute, IRequestDataAttribute<WebApiController, string>
    {
        public Issue138HeaderAttribute(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public string Suffix { get; set; } = string.Empty;

        public Task<string?> GetRequestDataAsync(WebApiController controller, string parameterName)
            => Task.FromResult<string?>((controller ?? throw new ArgumentNullException(nameof(controller))).Request.Headers[Name] + Suffix + ":" + parameterName);
    }

    // Custom request data: an untyped interface whose result is cast to the parameter type.
    [AttributeUsage(AttributeTargets.Parameter)]
    internal sealed class Issue138NumberAttribute : Attribute, IRequestDataAttribute<WebApiController>
    {
        public Task<object?> GetRequestDataAsync(WebApiController controller, Type type, string parameterName)
        {
            var text = (controller ?? throw new ArgumentNullException(nameof(controller))).Request.Headers[parameterName];
            return Task.FromResult<object?>(text == "wrong-type" ? "not a number" : text == null ? null : int.Parse(text, CultureInfo.InvariantCulture));
        }
    }

    internal abstract class Issue138ParityBase : WebApiController
    {
        [Route(HttpVerbs.Get, "/inherited")]
        public virtual string Inherited() => "base";

        [Route(HttpVerbs.Get, "/base-method")]
        public string BaseMethod() => "from base " + Route.Path;

        protected override void OnBeforeHandler()
        {
            base.OnBeforeHandler();
            Response.Headers["X-Before"] = "yes";
        }
    }

    internal sealed class Issue138ParityController : Issue138ParityBase
    {
        public override string Inherited() => "derived";

        [Route(HttpVerbs.Get, "/route/int/{id}")]
        public int RouteInt(int id) => id * 2;

        [Route(HttpVerbs.Get, "/route/types/{g}/{e}/{d}/{dt}/{b}/{n?}")]
        public string RouteTypes(Guid g, DayOfWeek e, decimal d, DateTime dt, bool b, int? n)
            => string.Join("|", g, e, d.ToString(CultureInfo.InvariantCulture), dt.ToString("O", CultureInfo.InvariantCulture), b, n?.ToString(CultureInfo.InvariantCulture) ?? "null");

        [Route(HttpVerbs.Get, "/route/more/{c}/{l}/{f}/{ts}/{u}/{v}/{dto}/{s}/{us}")]
        public string RouteMore(char c, long l, double f, TimeSpan ts, Uri u, Version v, DateTimeOffset dto, sbyte s, ushort us)
            => string.Join("|", c, l, f.ToString("R", CultureInfo.InvariantCulture), ts, u, v, dto.ToString("O", CultureInfo.InvariantCulture), s, us);

        [Route(HttpVerbs.Get, "/route/default/{x}")]
        public string RouteDefaults(string x, int notInRoute = 7, string missing = "dflt\"quoted", DayOfWeek dow = DayOfWeek.Friday, double ratio = 0.1, decimal money = 2.50m, char letter = 'q', float single = -1.25f, long big = long.MinValue)
            => string.Join("|", x, notInRoute, missing, dow, ratio.ToString("R", CultureInfo.InvariantCulture), money.ToString(CultureInfo.InvariantCulture),
                letter, single.ToString("R", CultureInfo.InvariantCulture), big.ToString(CultureInfo.InvariantCulture));

        [Route(HttpVerbs.Get, "/route/point/{p}")]
        public string RoutePoint(Issue138Point p, [QueryField] Issue138Point? q) => p + "|" + (q?.ToString() ?? "null");

        [Route(HttpVerbs.Get, "/route/keyword/{class}")]
        public string Keyword(string @class) => "class=" + @class;

        [Route(HttpVerbs.Get, "/over/int/{v}")]
        public string Over(int v) => "int " + v.ToString(CultureInfo.InvariantCulture);

        [Route(HttpVerbs.Get, "/over/text/{v}")]
        public string Over(string v) => "text " + v;

        [Route(HttpVerbs.Get, "/query")]
        public string Query(
            [QueryField] string? s,
            [QueryField("alias")] int a,
            [QueryField] int[] arr,
            [QueryField] int? opt,
            [QueryField] DayOfWeek day,
            [QueryField] string[] many,
            [QueryField(true)] string? required,
            [QueryField("g", false)] Guid? g,
            [QueryData] NameValueCollection all)
            => string.Join("|", s ?? "null", a, string.Join(",", arr), opt?.ToString(CultureInfo.InvariantCulture) ?? "null", day,
                string.Join(",", many), required ?? "null", g?.ToString() ?? "null", (all ?? throw new ArgumentNullException(nameof(all))).Count);

        [Route(HttpVerbs.Post, "/form")]
        public async Task<string> Form(
            [FormField] string? s,
            [FormField("alias")] decimal a,
            [FormField] long[] arr,
            [FormField] bool? flag,
            [FormField] string[] many,
            [FormField(true)] string? required,
            [FormData] NameValueCollection all)
        {
            await Task.Yield();
            return string.Join("|", s ?? "null", a.ToString(CultureInfo.InvariantCulture), string.Join(",", arr), flag?.ToString() ?? "null",
                string.Join(",", many), required ?? "null", (all ?? throw new ArgumentNullException(nameof(all))).Count);
        }

        [Route(HttpVerbs.Post, "/json")]
        public Task<Issue138Person?> JsonPerson([JsonData] Issue138Person? person)
            => Task.FromResult(person is null ? null : new Issue138Person { Name = person.Name.ToUpperInvariant(), Age = person.Age + 1 });

        [Route(HttpVerbs.Post, "/json/int")]
        public int JsonInt([JsonData] int value) => value + 1;

        [Route(HttpVerbs.Get, "/custom")]
        public string Custom([Issue138Header("X-Text", Suffix = "!")] string text, [Issue138Number] int number, [Issue138Number] int? optional)
            => text + "|" + number.ToString(CultureInfo.InvariantCulture) + "|" + (optional?.ToString(CultureInfo.InvariantCulture) ?? "null");

        [Route(HttpVerbs.Get, "/void")]
        public void Void() => Response.Headers["X-Void"] = "done";

        [Route(HttpVerbs.Get, "/task")]
        public Task TaskResult() => HttpContext.SendStringAsync("task body", "text/plain", System.Text.Encoding.UTF8);

        [Route(HttpVerbs.Get, "/null")]
        public object? NullResult() => null;

        [Route(HttpVerbs.Get, "/error/http")]
        public string HttpError() => throw HttpException.NotFound("missing thing");

        [Route(HttpVerbs.Get, "/error/plain")]
        public string PlainError() => throw new InvalidOperationException("plain failure");

        [Route(HttpVerbs.Get, "/error/async")]
        public async Task<string> AsyncError()
        {
            await Task.Yield();
            throw HttpException.Forbidden("async refusal");
        }

        [BaseRoute(HttpVerbs.Get, "/base/")]
        public string Base() => "sub=" + Route.SubPath;

        [Route(HttpVerbs.Any, "/any")]
        [Route(HttpVerbs.Get, "/any-alias")]
        public string AnyVerb() => "verb=" + Request.HttpVerb;

        [Route(HttpVerbs.Get, "/cancel")]
        public bool Cancel() => CancellationToken.CanBeCanceled;
    }

    internal sealed class Issue138TypedJsonController : WebApiController
    {
        [Route(HttpVerbs.Post, "/typed-json")]
        public Issue138Person Echo([JsonData] Issue138Person person)
            => new() { Name = (person ?? throw new ArgumentNullException(nameof(person))).Name + "!", Age = person.Age };
    }

    // Records the order of events, to compare disposal timing between registrations.
    internal sealed class Issue138DisposableController : WebApiController, IDisposable
    {
        public Issue138DisposableController(System.Collections.Concurrent.ConcurrentQueue<string> log)
        {
            Log = log;
        }

        private System.Collections.Concurrent.ConcurrentQueue<string> Log { get; }

        private TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Waits for disposal, at most delay milliseconds. Disposal that happens when the method
        // returns its task lets the handler finish after it; disposal after completion would
        // only follow the full delay, so the logged order shows which one happened.
        [Route(HttpVerbs.Get, "/dispose/{delay}")]
        public async Task<string> Work(int delay)
        {
            Log.Enqueue("handler-start");
            await Task.WhenAny(Disposed.Task, Task.Delay(delay)).ConfigureAwait(false);
            Log.Enqueue("handler-end");
            return "worked";
        }

        [Route(HttpVerbs.Get, "/dispose-throw")]
        public string Throw()
        {
            Log.Enqueue("handler-start");
            throw new InvalidOperationException("thrown");
        }

        public void Dispose()
        {
            Log.Enqueue("dispose");
            Disposed.TrySetResult();
        }
    }

    // Reflection registration rejects these defaults: it passes an untyped null constant.
    internal sealed class Issue138NullDefaultsController : WebApiController
    {
        [Route(HttpVerbs.Get, "/null-defaults")]
        public string Get(string? text = null, object? value = null, DateTime when = default, int? count = null)
            => string.Join("|", text ?? "null", value ?? "null", when.Ticks, count?.ToString(CultureInfo.InvariantCulture) ?? "null");
    }

    internal sealed class Issue138AliasController : WebApiController
    {
        [Route(HttpVerbs.Get, "/Alias/{id}")]
        [Route(HttpVerbs.Get, "/alias/{id}")]
        public string Get(string id) => "alias " + id;

        [Route(HttpVerbs.Post, "/Alias/{id}")]
        public string Post(string id) => "post " + id;
    }

    internal sealed class Issue138OtherAliasController : WebApiController
    {
        [Route(HttpVerbs.Get, "/ALIAS/{id}")]
        public string Get(string id) => "other " + id;
    }
}
