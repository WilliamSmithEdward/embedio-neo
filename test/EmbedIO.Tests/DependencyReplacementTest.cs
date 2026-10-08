using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Configuration;
using NUnit.Framework;
using Json = EmbedIO.Serialization.Json;

namespace EmbedIO.Tests
{
    public class DependencyReplacementTest
    {
        [Test]
        [NonParallelizable]
        public void TraceMessagesCannotInjectAdditionalRecords()
        {
            var write = typeof(EmbedIO.Diagnostics.Log).GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic);
            using var output = new StringWriter();
            using var listener = new TextWriterTraceListener(output);
            var source = EmbedIO.Diagnostics.Log.Source;
            var previous = source.Switch.Level;
            source.Listeners.Add(listener);
            try
            {
                source.Switch.Level = SourceLevels.All;
                (write ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(null, new object[] { TraceEventType.Warning, "source\r\nforged", "first\nsecond\rthird" });
                source.Flush();
                var text = output.ToString();
                Assert.That(text, Does.Contain("source\\r\\nforged"));
                Assert.That(text, Does.Contain("first\\nsecond\\rthird"));
                Assert.That(text.TrimEnd('\r', '\n').Split('\n'), Has.Length.EqualTo(1));
            }
            finally
            {
                source.Listeners.Remove(listener);
                source.Switch.Level = previous;
            }
        }

        [Test]
        public void LogObserversRemainActiveWithTracingOffAndCannotRecursivelyNotify()
        {
            var log = typeof(EmbedIO.Diagnostics.Log);
            var write = log.GetMethod("Write", BindingFlags.Static | BindingFlags.NonPublic);
            var observerEvent = log.GetEvent("MessageWritten", BindingFlags.Static | BindingFlags.NonPublic);
            var count = 0;
            Action<string> observer = message =>
            {
                count++;
                (write ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(null, new object[] { TraceEventType.Warning, "test", "nested" });
            };
            var previous = EmbedIO.Diagnostics.Log.Source.Switch.Level;
            ((observerEvent ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetAddMethod(true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(null, new object[] { observer });
            try
            {
                EmbedIO.Diagnostics.Log.Source.Switch.Level = SourceLevels.Off;
                (write ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(null, new object[] { TraceEventType.Information, "test", "outer" });
                Assert.That(count, Is.EqualTo(1));
            }
            finally
            {
                ((observerEvent).GetRemoveMethod(true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(null, new object[] { observer });
                EmbedIO.Diagnostics.Log.Source.Switch.Level = previous;
            }
            write.Invoke(null, new object[] { TraceEventType.Information, "test", "after removal" });
            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void UntypedJsonPreservesSwanDictionaryListAndDecimalShapes()
        {
            var data = (Dictionary<string, object>)(Json.Deserialize("{\"items\":[1,1.25,null,true,\"text\"]}") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            var items = (List<object>)(data)["items"];
            Assert.That(items, Is.EqualTo(new object?[] { 1m, 1.25m, null, true, "text" }));
            Assert.That(items[0], Is.TypeOf<decimal>());
            Assert.That((Json.Deserialize<Dictionary<string, string>>("{\"n\":12,\"b\":true}") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))["n"], Is.EqualTo("12"));
        }

        [TestCase("{\"x\":")]
        [TestCase("[1,")]
        [TestCase("{\"x\":NaN}")]
        [TestCase("{\"x\":1e100}")]
        public void InvalidOrOutOfRangeUntypedJsonThrows(string json)
            => Assert.Throws<JsonException>(() => Json.Deserialize(json));

        [Test]
        public void JsonOptionsPreserveDefaultsAndAllowNamingPolicy()
        {
            var options = Json.CreateOptions();
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            var data = Json.Deserialize<JsonModel>("{\"COUNT\":\"12\"}", options);
            Assert.That((data ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Count, Is.EqualTo(12));
            Assert.That(Json.Serialize(data, options), Is.EqualTo("{\"count\":12}"));
        }

        [Test]
        public void FailedValidationDoesNotLockAndSuccessfulLockRunsOnce()
        {
            var configuration = new ConfigurationProbe { Fail = true };
            Assert.Throws<ArgumentException>(configuration.Lock);
            Assert.That(configuration.ConfigurationLocked, Is.False);
            configuration.Fail = false;
            configuration.Lock();
            configuration.Lock();
            Assert.That(configuration.Validations, Is.EqualTo(2));
            Assert.Throws<InvalidOperationException>(configuration.CheckMutable);
        }

        // Golden values captured from SWAN 3.1.0 before removing the dependency.
        [TestCase(typeof(int), "12", true, 12)]
        [TestCase(typeof(int), "", false, null)]
        [TestCase(typeof(int?), "", true, null)]
        [TestCase(typeof(bool), "1", false, null)]
        [TestCase(typeof(bool), "true", true, true)]
        [TestCase(typeof(double), "1.25", true, 1.25)]
        [TestCase(typeof(DayOfWeek), "friday", true, DayOfWeek.Friday)]
        [TestCase(typeof(Guid), "invalid", false, null)]
        [TestCase(typeof(string), null, false, null)]
        public void ScalarConversionMatchesRecordedBaseline(Type type, string? input, bool success, object? expected)
        {
            var converter = InternalType("FromString").GetMethod("TryConvertTo", BindingFlags.Static | BindingFlags.NonPublic,
                null, new[] { typeof(Type), typeof(string), typeof(object).MakeByRefType() }, null);
            var args = new object?[] { type, input, null };
            Assert.That((converter ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(null, args), Is.EqualTo(success));
            Assert.That(args[2], Is.EqualTo(expected));
        }

        [Test]
        public async Task StreamReaderAccumulatesShortReadsAndReturnsAvailableBytesAtEof()
        {
            var method = InternalType("StreamExtensions").GetMethod("ReadBytesAsync", BindingFlags.Static | BindingFlags.NonPublic);
            using var stream = new ShortReadStream(Encoding.UTF8.GetBytes("hello"));
            var bytes = await (Task<byte[]>)((method ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).Invoke(null, new object[] { stream, 10, 4 }) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("hello"));
        }

        [Test]
        public async Task PeriodicTaskRecoversFromFailureAndStopsOnDispose()
        {
            var count = 0;
            var active = 0;
            var overlap = false;
            var repeated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<CancellationToken, Task> action = async token =>
            {
                if (Interlocked.Increment(ref active) != 1) overlap = true;
                try
                {
                    if (Interlocked.Increment(ref count) == 1) throw new InvalidOperationException("Expected test failure");
                    await Task.Delay(20, token);
                    repeated.TrySetResult(true);
                }
                finally { Interlocked.Decrement(ref active); }
            };
            var type = InternalType("PeriodicTask");
            using var periodic = (IDisposable)(Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { TimeSpan.FromMilliseconds(5), action, CancellationToken.None }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
            await repeated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            (periodic).Dispose();
            await ((Task)((((type).GetProperty("Completion", BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(periodic)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."))).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(overlap, Is.False);
            Assert.That(active, Is.Zero);
            Assert.That(count, Is.GreaterThanOrEqualTo(2));
        }

        private static Type InternalType(string name) => (typeof(WebServer).Assembly.GetType("EmbedIO.Internal." + name, true) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));

        public sealed class JsonModel
        {
            public int Count { get; set; }
        }

        private sealed class ConfigurationProbe : ConfiguredObject
        {
            public bool Fail { get; set; }
            public int Validations { get; private set; }
            public void Lock() => LockConfiguration();
            public void CheckMutable() => EnsureConfigurationNotLocked();
            protected override void OnBeforeLockConfiguration()
            {
                Validations++;
                if (Fail) throw new ArgumentException("Invalid configuration");
            }
        }

        private sealed class ShortReadStream : MemoryStream
        {
            public ShortReadStream(byte[] bytes) : base(bytes) { }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);
        }
    }
}
