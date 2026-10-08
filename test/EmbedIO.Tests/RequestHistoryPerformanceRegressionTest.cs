using System;
using System.Collections;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Security;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    [NonParallelizable]
    public class RequestHistoryPerformanceRegressionTest
    {
        private static readonly IPAddress Address = IPAddress.Parse("192.0.2.198");
        private static readonly Type CriterionType = typeof(IPBanningRequestsCriterion);
        private static IDictionary Histories => (IDictionary)((((CriterionType).GetField("Requests", BindingFlags.Static | BindingFlags.NonPublic) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(null)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));

        [SetUp]
        public void ClearSharedHistory()
        {
            using var reset = NewCriterion(int.MaxValue);
        }

        [TestCase(-1, true)]
        [TestCase(0, true)]
        [TestCase(1, true)]
        [TestCase(2, false)]
        public async Task FirstRequestPreservesThresholdBehavior(int maximum, bool expected)
        {
            using var criterion = NewCriterion(maximum);
            Assert.That(await criterion.ValidateIPAddress(Address), Is.EqualTo(expected));
        }

        [TestCase(49, 0, 0, 50, true)]
        [TestCase(48, 0, 0, 50, false)]
        [TestCase(0, 2999, 0, 50, true)]
        [TestCase(0, 2998, 0, 50, false)]
        [TestCase(1, 1000, 3000, 50, false)]
        [TestCase(0, 0, 5000, 50, false)]
        [TestCase(0, 90, 0, 2, false)]
        [TestCase(0, 119, 0, 2, true)]
        public async Task CountsPreserveSecondWindowMinuteIntegerAverageAndExpiredSamples(int second, int minute, int expired, int maximum, bool expected)
        {
            using var criterion = NewCriterion(maximum);
            var history = Seed(Address);
            Add(history, second, DateTime.Now.Ticks);
            Add(history, minute, DateTime.Now.AddSeconds(-2).Ticks);
            Add(history, expired, DateTime.Now.AddMinutes(-2).Ticks);
            Assert.That(await criterion.ValidateIPAddress(Address), Is.EqualTo(expected));
        }

        [Test]
        public async Task HistoriesRemainSharedAcrossCriteriaAndSeparateAcrossAddresses()
        {
            using var first = NewCriterion(10);
            using var second = NewCriterion(2);
            Assert.That(await first.ValidateIPAddress(Address), Is.False);
            Assert.That(await second.ValidateIPAddress(Address), Is.True);
            var other = IPAddress.Parse("192.0.2.199");
            Assert.That(await second.ValidateIPAddress(other), Is.False);
            first.ClearIPAddress(Address);
            Assert.That(await second.ValidateIPAddress(Address), Is.False);
            Assert.That(Count(other), Is.EqualTo(1));
        }

        [Test]
        public async Task DisposingACriterionRetainsExistingSharedResetBehavior()
        {
            using var first = NewCriterion(10);
            using var second = NewCriterion(2);
            await first.ValidateIPAddress(Address);
            Assert.That(await second.ValidateIPAddress(Address), Is.True);
            first.Dispose();
            Assert.That(await second.ValidateIPAddress(Address), Is.False);
        }

        [Test]
        public void PurgingRemovesOnlyExpiredSamplesAndDropsEmptyAddresses()
        {
            using var criterion = NewCriterion(50);
            var active = Seed(Address);
            Add(active, 17, DateTime.Now.AddSeconds(-10).Ticks);
            Add(active, 35, DateTime.Now.AddMinutes(-2).Ticks);
            var expiredAddress = IPAddress.Parse("192.0.2.200");
            Add(Seed(expiredAddress), 12, DateTime.Now.AddMinutes(-2).Ticks);
            criterion.PurgeData();
            Assert.That(Count(Address), Is.EqualTo(17));
            Assert.That(Histories.Contains(expiredAddress), Is.False);
            criterion.PurgeData();
            Assert.That(Count(Address), Is.EqualTo(17));
        }

        [Test]
        public async Task ConcurrentPurgesDoNotLoseNewRequests()
        {
            using var criterion = NewCriterion(int.MaxValue);
            Add(Seed(Address), 256, DateTime.Now.AddMinutes(-2).Ticks);
            using var start = new ManualResetEventSlim();
            var workers = new Task[5];
            for (var i = 0; i < 4; i++)
                workers[i] = Task.Factory.StartNew(() =>
                {
                    start.Wait();
                    for (var request = 0; request < 250; request++)
                        criterion.ValidateIPAddress(Address).GetAwaiter().GetResult();
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            workers[4] = Task.Factory.StartNew(() =>
            {
                start.Wait();
                for (var purge = 0; purge < 100; purge++) criterion.PurgeData();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            start.Set();
            await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(10));
            criterion.PurgeData();
            Assert.That(Count(Address), Is.EqualTo(1000));
        }

        [Test]
        public void NullAddressErrorContractsRemainUnchanged()
        {
            using var criterion = NewCriterion(50);
            Assert.That(Assert.Throws<ArgumentNullException>(() => TestObjects.InvalidInput.Invoke((Func<IPAddress, Task<bool>>)criterion.ValidateIPAddress, new object?[] { null })).ParamName, Is.EqualTo("key"));
            Assert.That(Assert.Throws<ArgumentNullException>(() => TestObjects.InvalidInput.Invoke((Action<IPAddress>)criterion.ClearIPAddress, new object?[] { null })).ParamName, Is.EqualTo("key"));
        }

        private static IPBanningRequestsCriterion NewCriterion(int maximum) =>
            (IPBanningRequestsCriterion)(Activator.CreateInstance(CriterionType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { maximum }, null) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        private static object Seed(IPAddress address)
        {
            var type = Histories.GetType().GetGenericArguments()[1];
            var history = Activator.CreateInstance(type);
            Histories[address] = history;
            return (history ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        }
        private static void Add(object history, int count, long ticks)
        {
            var add = ((history).GetType().GetMethod("Add") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).CreateDelegate<Action<long>>(history);
            for (var i = 0; i < count; i++) add(ticks);
        }
        private static int Count(IPAddress address)
        {
            var history = Histories[address];
            return history == null ? 0 : (int)((((history).GetType().GetProperty("Count") ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value.")).GetValue(history)) ?? throw new NUnit.Framework.AssertionException("Expected a non-null test value."));
        }
    }
}
