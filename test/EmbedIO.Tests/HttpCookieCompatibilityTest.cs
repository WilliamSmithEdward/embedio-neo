using System;
using System.Collections.Generic;
using System.Net;
using EmbedIO.Net;
using NUnit.Framework;

namespace EmbedIO.Tests
{
    public class HttpCookieCompatibilityTest
    {
        [Test]
        public void CommentUrlIsDistinctFromThePlainCommentAttribute()
        {
            var cookies = CookieList.Parse("session=value; Version=1; CommentURL=\"https://example.invalid/about\"");
            Assert.That(cookies, Has.Count.EqualTo(1));
            Assert.That(cookies[0].CommentUri, Is.EqualTo(new Uri("https://example.invalid/about")));
            Assert.That(cookies[0].Comment, Is.Empty);
        }
        [Test]
        public void ExpiryWithoutACommaDoesNotConsumeTheFollowingPath()
        {
            var cookies = CookieList.Parse("session=value; Expires=Wed May 05 13:20:30 2027; Path=/tracked");
            Assert.That(cookies, Has.Count.EqualTo(1));
            Assert.That(cookies[0].Expires.ToUniversalTime(), Is.EqualTo(new DateTime(2027, 5, 5, 13, 20, 30, DateTimeKind.Utc)));
            Assert.That(cookies[0].Path, Is.EqualTo("/tracked"));
        }
        [Test]
        public void LookupRetainsVersionCasePathAndEqualRankSelectionWithoutMutatingTheList()
        {
            var cookies = new CookieList();
            for (var index = 0; index < 40; index++)
                cookies.Add(new Cookie("other" + index, "value", "/", "example.invalid"));
            cookies.Add(new Cookie("sid", "lowercase", "/long", "a.invalid"));
            cookies.Add(new Cookie("SID", "uppercase-short", "/", "a.invalid"));
            cookies.Add(new Cookie("SID", "uppercase-long", "/long", "a.invalid"));
            cookies.Add(new Cookie("SID", "equal-rank", "/long", "b.invalid"));
            cookies.Add(new Cookie("SID", "version-one", "/longer", "c.invalid") { Version = 1 });
            var original = cookies.ToArray();
            var ranked = new List<Cookie>(cookies);
            ranked.Sort((left, right) =>
            {
                var order = left.Version.CompareTo(right.Version);
                if (order == 0) order = string.CompareOrdinal(left.Name, right.Name);
                return order != 0 ? order : right.Path.Length.CompareTo(left.Path.Length);
            });
            Assert.That(cookies["sId"], Is.SameAs(ranked.Find(cookie => cookie.Name.Equals("sid", StringComparison.OrdinalIgnoreCase))));
            Assert.That(cookies, Is.EqualTo(original));
        }
        [Test]
        public void ReplacementRetainsItsSlotAndMutationIsVisibleToTheNextLookup()
        {
            var cookies = new CookieList { new("sid", "first", "/", "example.invalid"), new("other", "second") };
            var replacement = new Cookie("SID", "replacement", "/", "EXAMPLE.INVALID");
            cookies.Add(replacement);
            Assert.That(cookies, Has.Count.EqualTo(2));
            Assert.That(cookies[0], Is.SameAs(replacement));
            replacement.Name = "changed";
            Assert.That(cookies["sid"], Is.Null);
            Assert.That(cookies["CHANGED"], Is.SameAs(replacement));
        }
    }
}
