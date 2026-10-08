using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class Http3PriorityStateTest
    {
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly Type StateType = typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.Http3.Http3PriorityState", true)
            ?? throw new AssertionException("Missing state.");
        private static object Create(int capacity) => Activator.CreateInstance(StateType, Flags, null, new object[] { capacity }, null)
            ?? throw new AssertionException("Missing state instance.");
        private static object? Call(object owner, string name, params object[] args)
        {
            try { return owner.GetType().GetMethod(name, Flags)?.Invoke(owner, args); }
            catch (TargetInvocationException error) { ExceptionDispatchInfo.Capture(error.InnerException ?? error).Throw(); throw; }
        }
        private static object Priority(int urgency, bool incremental) => Activator.CreateInstance(
            typeof(WebServer).Assembly.GetType("EmbedIO.Net.Internal.HttpPriority", true) ?? throw new AssertionException("Missing priority."),
            Flags, null, new object[] { urgency, incremental }, null) ?? throw new AssertionException("Missing priority value.");
        private static object Open(object state, long id) => Call(state, "Open", id) ?? throw new AssertionException("Missing entry.");
        private static (int, bool) Value(object entry)
        {
            var value = entry.GetType().GetProperty("Value", Flags)?.GetValue(entry) ?? throw new AssertionException("Missing entry value.");
            return ((int)(value.GetType().GetProperty("Urgency")?.GetValue(value) ?? throw new AssertionException("Missing urgency.")),
                (bool)(value.GetType().GetProperty("Incremental")?.GetValue(value) ?? throw new AssertionException("Missing incremental.")));
        }
        private static int Count(object state, string name) => (int)(StateType.GetProperty(name)?.GetValue(state) ?? throw new AssertionException("Missing count."));

        [Test]
        public void EarlyUpdateOverridesHeadersAndLaterUpdatesReplaceWholePriority()
        {
            var state = Create(2);
            Call(state, "Update", 4L, Priority(0, true));
            var entry = Open(state, 4);
            Call(state, "Headers", 4L, "u=7");
            Assert.That(Value(entry), Is.EqualTo((0, true)));
            Call(state, "Update", 4L, Priority(3, false));
            Assert.That(Value(entry), Is.EqualTo((3, false)));
            Assert.That(Count(state, "PendingCount"), Is.Zero);
        }
        [Test]
        public void HeaderDefaultsAndInvalidDictionariesDoNotRetainPartialValues()
        {
            var state = Create(2); var entry = Open(state, 0);
            Assert.That(Value(entry), Is.EqualTo((3, false)));
            Call(state, "Headers", 0L, "u=6,i");
            Assert.That(Value(entry), Is.EqualTo((6, true)));
            Call(state, "Headers", 0L, "u=0,i,");
            Assert.That(Value(entry), Is.EqualTo((3, false)));
            Call(state, "Update", 0L, Priority(1, true));
            Call(state, "Headers", 0L, "broken,");
            Assert.That(Value(entry), Is.EqualTo((1, true)));
        }
        [Test]
        public void PendingEvictionRetainsMostRecentlyUpdatedAndNeverEvictsActive()
        {
            var state = Create(2); var active = Open(state, 0);
            Call(state, "Update", 4L, Priority(1, true));
            Call(state, "Update", 8L, Priority(2, false));
            Call(state, "Update", 4L, Priority(7, false));
            Call(state, "Update", 12L, Priority(0, true));
            Assert.That(Count(state, "PendingCount"), Is.EqualTo(2));
            Assert.That(Value(Open(state, 4)), Is.EqualTo((7, false)));
            Assert.That(Value(active), Is.EqualTo((3, false)));
            Assert.Throws<InvalidOperationException>(() => Open(state, 0));
            Call(state, "Close", 4L);
            Assert.That(Value(Open(state, 8)), Is.EqualTo((3, false)));
        }
        [Test]
        public void ClosedUpdatesCannotReviveRecentStreamAndChurnRemainsBounded()
        {
            var state = Create(4); var entry = Open(state, 0);
            Call(state, "Close", 0L);
            Call(state, "Update", 0L, Priority(0, true));
            Assert.That(Count(state, "PendingCount"), Is.Zero);
            Assert.That(Value(entry), Is.EqualTo((3, false)));
            for (long id = 4; id < 40000; id += 4)
            {
                Open(state, id); Call(state, "Close", id);
                Call(state, "Update", id + 40000, Priority(2, true));
            }
            Assert.That(Count(state, "ActiveCount"), Is.Zero);
            Assert.That(Count(state, "ClosedCount"), Is.EqualTo(4));
            Assert.That(Count(state, "PendingCount"), Is.EqualTo(4));
            Call(state, "Clear");
            Assert.That(Count(state, "ClosedCount") + Count(state, "ActiveCount") + Count(state, "PendingCount"), Is.Zero);
        }
        [Test]
        public void ActiveCapacityRejectsNewStateWithoutEvictingAnExistingEntry()
        {
            var state = Create(1); var entry = Open(state, 0);
            var error = Assert.Catch<System.IO.IOException>(() => Open(state, 4));
            Assert.That(error.GetType().GetProperty("ErrorCode")?.GetValue(error), Is.EqualTo(0x107));
            Assert.That(Count(state, "ActiveCount"), Is.EqualTo(1));
            Assert.That(Value(entry), Is.EqualTo((3, false)));
        }
        [Test]
        public async Task ConcurrentPriorityReadsNeverObserveMixedFields()
        {
            var state = Create(1); var entry = Open(state, 0);
            var first = Priority(0, true); var second = Priority(7, false);
            Call(state, "Update", 0L, first);
            var writer = Task.Run(() => { for (var i = 0; i < 10000; i++) Call(state, "Update", 0L, (i & 1) == 0 ? second : first); });
            try { for (var i = 0; i < 10000; i++) Assert.That(Value(entry), Is.EqualTo((0, true)).Or.EqualTo((7, false))); }
            finally { await writer; }
        }
    }
}
