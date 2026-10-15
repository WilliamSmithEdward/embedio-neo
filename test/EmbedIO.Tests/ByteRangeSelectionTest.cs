using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Reflection;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class ByteRangeSelectionTest
    {
        private static IHttpRequest Request(string method, string? range, string? validator = null)
        {
            var request = DispatchProxy.Create<IHttpRequest, RequestPreconditionsTest.RequestProxy>();
            var proxy = (RequestPreconditionsTest.RequestProxy)(object)request;
            proxy.Method = method;
            if (range != null) proxy.Fields[HttpHeaderNames.Range] = range;
            if (validator != null) proxy.Fields[HttpHeaderNames.IfRange] = validator;
            return request;
        }
        public static IEnumerable RangeCases()
        {
            var cases = new (string? Range, long Total, bool Partial, long Start, long Count)[]
            {
                (null, 100, false, 0, 100),
                ("bytes=0-0", 100, true, 0, 1),
                ("bytes=1-5", 100, true, 1, 5),
                ("bytes=90-999", 100, true, 90, 10),
                ("bytes=90-", 100, true, 90, 10),
                ("bytes=-10", 100, true, 90, 10),
                ("bytes=-999", 100, true, 0, 100),
                ("BYTES=0-1", 100, true, 0, 2),
                (" bytes= 01-02\t", 100, true, 1, 2),
                ("bytes=0-99999999999999999999999999999", 100, true, 0, 100),
                ("bytes=-99999999999999999999999999999", 100, true, 0, 100),
                ("bytes=00000000000000000000000001-00000000000000000000000002", 100, true, 1, 2),
                ("bytes=0-0,3-5", 100, false, 0, 100),
                ("items=0-1", 100, false, 0, 100),
                ("bytes=3-1", 100, false, 0, 100),
                ("bytes=99999999999999999999999999999-99999999999999999999999999998", 100, false, 0, 100),
                ("bytes=x-y", 100, false, 0, 100),
                ("bytes=1-2oops", 100, false, 0, 100),
                ("bytes=0-0", 0, false, 0, 0),
                ("bytes=-1", 0, false, 0, 0),
                ("bytes=0-9223372036854775807", long.MaxValue, true, 0, long.MaxValue),
                ("bytes=9223372036854775806-", long.MaxValue, true, long.MaxValue - 1, 1)
            };
            foreach (var method in new[] { "GET", "QUERY" })
                foreach (var item in cases)
                    yield return new object?[] { method, item.Range, item.Total, item.Partial, item.Start, item.Count };
        }
        [TestCaseSource(nameof(RangeCases))]
        public void SingleRangesAreClampedWithoutOverflow(string method, string? range, long total, bool expected, long expectedStart, long expectedCount)
        {
            var selected = Request(method, range).TryGetByteRange(total, "\"v\"", null, out var start, out var count);
            Assert.That(selected, Is.EqualTo(expected));
            Assert.That(start, Is.EqualTo(expectedStart));
            Assert.That(count, Is.EqualTo(expectedCount));
        }
        [TestCase("HEAD")]
        [TestCase("POST")]
        [TestCase("query")]
        [TestCase("OPTIONS")]
        public void OtherMethodsIgnoreRanges(string method)
        {
            Assert.That(Request(method, "bytes=0-0").TryGetByteRange(100, "\"v\"", null, out var start, out var count), Is.False);
            Assert.That(start, Is.Zero);
            Assert.That(count, Is.EqualTo(100));
        }
        [TestCase("bytes=-0")]
        [TestCase("bytes=100-101")]
        [TestCase("bytes=9999999999999999999999999999999999-")]
        public void UnsatisfiableSupportedRangesCarryTotalLength(string range)
        {
            var error = Assert.Throws<HttpRangeNotSatisfiableException>(() => Assert.That(Request("QUERY", range)
                .TryGetByteRange(100, "\"v\"", null, out _, out _), Is.True));
            Assert.That(error.ContentLength, Is.EqualTo(100));
        }
        [TestCase("\"v\"", "\"v\"", true)]
        [TestCase("W/\"v\"", "\"v\"", false)]
        [TestCase("\"v\"", "W/\"v\"", false)]
        [TestCase("\"other\"", "\"v\"", false)]
        [TestCase("\"v\", \"other\"", "\"v\"", false)]
        [TestCase("malformed", "\"v\"", false)]
        [TestCase("\"\"", "\"\"", true)]
        public void IfRangeRequiresExactStrongTag(string validator, string current, bool expected)
            => Assert.That(Request("QUERY", "bytes=0-0", validator).TryGetByteRange(100, current, null, out _, out _), Is.EqualTo(expected));
        [TestCase(false, true, false)]
        [TestCase(true, true, true)]
        [TestCase(true, false, false)]
        public void IfRangeDateRequiresExplicitStrongEvidence(bool strong, bool same, bool expected)
        {
            var modified = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(123);
            var date = (same ? modified : modified.AddDays(-1)).ToString("r", System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(Request("QUERY", "bytes=0-0", date).TryGetByteRange(100, "\"v\"", modified, out _, out _, strong), Is.EqualTo(expected));
        }
        [TestCase(7)]
        [TestCase(8)]
        [TestCase(9)]
        public void DateWeekdayPrefixesAreNotWeakTagPrefixes(int day)
        {
            var modified = new DateTimeOffset(2026, 10, day, 12, 0, 0, TimeSpan.Zero);
            var date = modified.ToString("r", System.Globalization.CultureInfo.InvariantCulture);
            Assert.That(Request("QUERY", "bytes=0-0", date).TryGetByteRange(100, "\"v\"", modified, out _, out _, true), Is.True);
        }
        [Test]
        public void FailedIfRangeIgnoresOtherwiseUnsatisfiableRange()
            => Assert.That(Request("QUERY", "bytes=999-", "\"other\"").TryGetByteRange(100, "\"v\"", null, out _, out _), Is.False);
        [Test]
        public void SeededBoundedRangesMatchIndependentArithmetic()
        {
            var random = new Random(20261009);
            for (var sample = 0; sample < 1000; sample++)
            {
                var total = random.Next(1, 1000000);
                var first = random.Next(total);
                var last = first + random.Next(0, 1000000);
                var range = FormattableString.Invariant($"bytes={first}-{last}");
                Assert.That(Request("QUERY", range).TryGetByteRange(total, "\"v\"", null, out var start, out var count), Is.True);
                Assert.That(start, Is.EqualTo(first));
                Assert.That(count, Is.EqualTo(Math.Min((long)last, total - 1) - first + 1));
            }
        }
    }
}
