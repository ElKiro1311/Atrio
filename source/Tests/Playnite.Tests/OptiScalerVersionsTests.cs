using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Playnite.Atrio;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class OptiScalerVersionsTests
    {
        private static JObject Version(string id, string channel, bool latest = false)
        {
            return new JObject { ["id"] = id, ["channel"] = channel, ["latest"] = latest };
        }

        /// <summary>As OptiScaler Client gives them: the finished ones first, with some test versions of the past among them.</summary>
        private static OptiScalerVersions Published(params JObject[] more)
        {
            return new OptiScalerVersions(new JArray(new[]
            {
                Version("0.9.5", "stable", true), Version("0.9.4", "stable"), Version("0.7-old_nightly", "stable"), Version("0.7.9", "stable"),
                Version("nightly", "stable"), Version("0.7.7-pre9", "stable"), Version(".0.6.1", "stable"), Version("release", "stable"),
                Version("0.9.5-pre4", "beta", true), Version("0.9.5-pre3", "beta"),
                Version("nightly-20261123", "nightly", true), Version("nightly-20261122", "nightly"), Version("nightly-20261120", "nightly")
            }.Concat(more).Cast<object>().ToArray()));
        }

        [Test]
        public void EachTypeHasItsVersionsTest()
        {
            var versions = Published(Version("mine", "custom"));
            Assert.AreEqual(new[] { "stable", "beta", "nightly", "custom" }, versions.Channels.ToArray());
            Assert.AreEqual(new[] { "0.9.5", "0.9.4", "0.7-old_nightly", "0.7.9", "0.7.7-pre9", ".0.6.1", "release" }, versions.Get("stable").ToArray());
            Assert.AreEqual(new[] { "0.9.5-pre4", "0.9.5-pre3" }, versions.Get("beta").ToArray());
            Assert.AreEqual(new[] { "nightly-20261123", "nightly-20261122", "nightly-20261120" }, versions.Get("nightly").ToArray());
            Assert.AreEqual(new[] { "mine" }, versions.Get("custom").ToArray());
            Assert.IsFalse(versions.Contains("nightly"), "The tag that moved from build to build has nothing to download.");
            Assert.IsTrue(versions.Contains("NIGHTLY-20261122"));
            Assert.IsFalse(versions.Contains(null));
            Assert.IsTrue(versions.IsLatest("nightly-20261123"));
            Assert.IsFalse(versions.IsLatest("nightly-20261122"));
            Assert.IsFalse(versions.IsLatest(null));
        }

        [Test]
        public void OnlyTheTypesWithSomethingAreOfferedTest()
        {
            Assert.AreEqual(new[] { "stable" }, new OptiScalerVersions(new JArray(Version("0.9.5", "stable"))).Channels.ToArray());
            Assert.IsEmpty(new OptiScalerVersions(null).Channels);
            Assert.IsEmpty(new OptiScalerVersions(null).Get("stable"));
            Assert.IsNull(new OptiScalerVersions(null).GetLatest("nightly"));

            // A client that doesn't tell them apart, or with types of its own: one list, as it was.
            var versions = new OptiScalerVersions(new JArray(Version("0.9.5", "stable"), Version("nightly-20261123", "stable"), Version("0.9.5-pre4", "weekly"), Version(null, "stable")));
            Assert.AreEqual(new[] { "stable" }, versions.Channels.ToArray());
            Assert.AreEqual(3, versions.Get("stable").Count);

            // A tag that moves from build to build is one more when the client knows it as such.
            Assert.IsTrue(new OptiScalerVersions(new JArray(Version("nightly", "nightly"))).Contains("nightly"));
        }

        [Test]
        public void TheSameVersionIsOfferedOnceTest()
        {
            var versions = new OptiScalerVersions(new JArray(Version("0.9.5-pre4", "beta", true), Version("0.9.5-PRE4", "stable")));
            Assert.AreEqual(new[] { "0.9.5-pre4" }, versions.Get("beta").ToArray());
            Assert.AreEqual(new[] { "beta" }, versions.Channels.ToArray());
        }

        [Test]
        public void LatestTest()
        {
            var versions = Published(Version("mine", "custom"), Version("other", "custom"));
            Assert.AreEqual("0.9.5", versions.GetLatest("stable"));
            Assert.AreEqual("0.9.5-pre4", versions.GetLatest("beta"));
            Assert.AreEqual("nightly-20261123", versions.GetLatest("nightly"));
            Assert.AreEqual("mine", versions.GetLatest("custom"));
            Assert.IsNull(versions.GetLatest("other"));
            Assert.AreEqual("0.9.4", new OptiScalerVersions(new JArray(Version("0.9.4", "stable"), Version("0.9.3", "stable"))).GetLatest("stable"));
        }

        [Test]
        public void TheTypeOfTheOneInTheGameTest()
        {
            var versions = Published(Version("mine", "custom"));
            Assert.AreEqual("stable", versions.GetChannel("0.9.4"));
            Assert.AreEqual("beta", versions.GetChannel("0.9.5-pre3"));
            Assert.AreEqual("nightly", versions.GetChannel("nightly-20261120"));
            Assert.AreEqual("custom", versions.GetChannel("mine"));

            // Put by hand: the number of the code in progress hasn't come out.
            Assert.AreEqual("nightly", versions.GetChannel("10.0.0"));
            Assert.AreEqual("nightly", versions.GetChannel("10.0.0-pre1"));
            Assert.AreEqual("stable", versions.GetChannel("0.8.1"));
            Assert.AreEqual("beta", versions.GetChannel("0.8.1-pre2"));
            Assert.AreEqual("stable", versions.GetChannel("?"));

            // One that isn't offered anymore.
            Assert.AreEqual("nightly", versions.GetChannel("nightly-20260901"));
            Assert.AreEqual("stable", versions.GetChannel(null));

            // Nothing is known of what is published, or there aren't nightly ones.
            Assert.AreEqual("stable", new OptiScalerVersions(null).GetChannel("10.0.0"));
            Assert.AreEqual("stable", new OptiScalerVersions(new JArray(Version("0.9.5", "stable"))).GetChannel("10.0.0"));
            Assert.AreEqual("nightly", new OptiScalerVersions(new JArray(Version("nightly-20261123", "nightly"))).GetChannel(null));
        }

        [Test]
        public void OneByHandIsNeverTakenForAFinishedOneThatIsNotOutTest()
        {
            // Its number comes without what goes after the hyphen: a beta of that number is out, and the finished one isn't.
            var versions = Published(Version("10.0.0-pre1", "beta"));
            Assert.AreEqual("beta", versions.GetChannel("10.0.0"));
            Assert.AreEqual("beta", versions.GetChannel("0.9.6"));
            Assert.AreEqual("nightly", versions.GetChannel("10.1.0"));

            // A test version published with the finished ones says nothing of what is finished.
            versions = Published(Version("10.0.0-pre1", "stable"));
            Assert.AreEqual("nightly", versions.GetChannel("10.0.0"));
        }

        [Test]
        public void TheOldOnesDoNotCountAsNightlyTest()
        {
            var versions = new OptiScalerVersions(new JArray(Version("0.9.5", "stable", true), Version("0.7-old_nightly", "stable")));
            Assert.AreEqual(new[] { "stable" }, versions.Channels.ToArray());
            Assert.AreEqual("stable", versions.GetChannel("10.0.0"));
            Assert.AreEqual("0.9.5", versions.GetLatest("stable"));
        }

        [Test]
        public void TheUpdateIsOfTheSameTypeTest()
        {
            var versions = Published(Version("mine", "custom"), Version("other", "custom"));
            Assert.AreEqual("0.9.5", versions.GetUpdate("0.9.4", "0.9.5"));
            Assert.IsNull(versions.GetUpdate("0.9.5", "0.9.5"));
            Assert.AreEqual("nightly-20261123", versions.GetUpdate("nightly-20261120", "0.9.5"));
            Assert.AreEqual("nightly-20261123", versions.GetUpdate("nightly-20260901", "0.9.5"));
            Assert.IsNull(versions.GetUpdate("nightly-20261123", "0.9.5"));
            Assert.IsNull(versions.GetUpdate("nightly-20270101", "0.9.5"));
            Assert.AreEqual("0.9.5-pre4", versions.GetUpdate("0.9.5-pre3", "0.9.5"));
            Assert.IsNull(versions.GetUpdate("0.9.5-pre4", "0.9.5"));
            Assert.IsNull(versions.GetUpdate("0.8.0-pre1", "0.9.5"), "One that isn't in the list has no place in it.");
            Assert.IsNull(versions.GetUpdate("other", "mine"));
            Assert.IsNull(versions.GetUpdate(null, "0.9.5"));
            Assert.IsNull(new OptiScalerVersions(null).GetUpdate("0.9.4", null));
        }

        [Test]
        public void TheFinishedOneTheClientRecommendsIsTheOneToGoToTest()
        {
            var versions = Published();
            Assert.IsNull(versions.GetUpdate("0.9.4", "0.9.4"), "The person chose to stay with it.");
            Assert.AreEqual("0.9.5", versions.GetUpdate("0.9.4", "nightly-20261123"), "The nightly one is of another type.");
            Assert.AreEqual("0.9.5", versions.GetUpdate("0.9.4", null));
            Assert.AreEqual("0.9.4", versions.GetUpdate("0.7.9", "0.9.4"));
        }

        [Test]
        public void CompatibilityUrlTest()
        {
            const string page = "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List";
            Assert.AreEqual(page, OptiScalerClient.GetCompatibilityUrl(null, null));
            Assert.AreEqual(page, OptiScalerClient.GetCompatibilityUrl("", " "));
            Assert.AreEqual($"{page}#:~:text=Lies%20of%20P", OptiScalerClient.GetCompatibilityUrl(null, " Lies of P "));
            Assert.AreEqual($"{page}#:~:text=Black%20Myth%3A%20Wukong", OptiScalerClient.GetCompatibilityUrl(page, "Black Myth: Wukong"));
            Assert.AreEqual($"{page}#:~:text=Half%2DLife%2C%20A%20%26%20B", OptiScalerClient.GetCompatibilityUrl("not an address", "Half-Life, A & B"));
            Assert.AreEqual("https://x.org/list#games:~:text=Control", OptiScalerClient.GetCompatibilityUrl("https://x.org/list#games", "Control"));
            Assert.AreEqual($"{page}#:~:text=Control", OptiScalerClient.GetCompatibilityUrl("file:///c:/x.html", "Control"));
        }
    }
}
