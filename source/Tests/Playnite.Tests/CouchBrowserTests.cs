using NUnit.Framework;
using Playnite.Atrio;
using System.Windows;

namespace Playnite.Tests
{
    [TestFixture]
    public class CouchBrowserTests
    {
        [Test]
        public void TargetTest()
        {
            Assert.IsNull(CouchBrowser.GetTarget(null));
            Assert.IsNull(CouchBrowser.GetTarget("   "));
            Assert.AreEqual("https://store.steampowered.com/app/10", CouchBrowser.GetTarget("https://store.steampowered.com/app/10"));
            Assert.AreEqual("https://pcgamingwiki.com", CouchBrowser.GetTarget(" pcgamingwiki.com "));
            Assert.AreEqual("https://es.wikipedia.org/wiki/Hollow_Knight", CouchBrowser.GetTarget("es.wikipedia.org/wiki/Hollow_Knight"));
            Assert.AreEqual("https://localhost:8080/x", CouchBrowser.GetTarget("localhost:8080/x"));
            Assert.AreEqual(CouchBrowser.SearchUrl + "hollow%20knight%20gu%C3%ADa", CouchBrowser.GetTarget("hollow knight guía"));
            Assert.AreEqual(CouchBrowser.SearchUrl + "elden.ring%20jefes", CouchBrowser.GetTarget("elden.ring jefes"), "with spaces it's a search");
            Assert.AreEqual(CouchBrowser.SearchUrl + "celeste", CouchBrowser.GetTarget("celeste"));
        }

        [Test]
        public void GuideSearchTest()
        {
            Assert.AreEqual(CouchBrowser.SearchUrl + "Hades%20gu%C3%ADa", CouchBrowser.GetGuideSearch("Hades", "guía"));
        }

        [Test]
        public void PointerSpeedTest()
        {
            Assert.AreEqual(new Vector(0, 0), CouchBrowser.GetPointerSpeed(0.1, -0.1, 1000), "resting stick");

            var full = CouchBrowser.GetPointerSpeed(1, 0, 1000);
            Assert.AreEqual(1000, full.X, 0.001);
            Assert.AreEqual(0, full.Y, 0.001);

            var half = CouchBrowser.GetPointerSpeed(0, 0.59, 1000);
            Assert.AreEqual(0, half.X, 0.001);
            Assert.That(half.Y, Is.InRange(200, 300), "a half push is precise");

            var diagonal = CouchBrowser.GetPointerSpeed(-1, -1, 1000);
            Assert.AreEqual(1000, diagonal.Length, 0.001, "never faster than the most");
            Assert.Less(diagonal.X, 0);
            Assert.Less(diagonal.Y, 0);
        }

        [Test]
        public void ScrollGoesWithTheStickTest()
        {
            var up = CouchBrowser.GetScrollSpeed(0, -1);
            Assert.AreEqual(CouchBrowser.ScrollSpeed, up.Y, 0.001, "stick up shows what's above");
            Assert.AreEqual(new Vector(0, 0), CouchBrowser.GetScrollSpeed(0.05, 0.05));
        }

        [Test]
        public void StartZoomTest()
        {
            Assert.AreEqual(0, CouchBrowser.GetStartZoom(1));
            Assert.AreEqual(0, CouchBrowser.GetStartZoom(0.8));
            Assert.AreEqual(3.8, CouchBrowser.GetStartZoom(2), 0.001, "4K without scaling reads like 1080");
            Assert.AreEqual(2.2, CouchBrowser.GetStartZoom(1.5), 0.001);
            Assert.AreEqual(5, CouchBrowser.GetStartZoom(8));
        }

        [Test]
        public void ClampTest()
        {
            Assert.AreEqual(new Point(0, 0), CouchBrowser.Clamp(new Point(-20, -5), 800, 600));
            Assert.AreEqual(new Point(799, 599), CouchBrowser.Clamp(new Point(2000, 900), 800, 600));
            Assert.AreEqual(new Point(10, 20), CouchBrowser.Clamp(new Point(10, 20), 800, 600));
        }
    }
}
