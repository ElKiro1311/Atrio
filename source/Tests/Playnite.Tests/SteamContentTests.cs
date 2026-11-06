using NUnit.Framework;
using Playnite.Atrio;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;

namespace Playnite.Tests
{
    [TestFixture]
    public class SteamContentTests
    {
        [Test]
        public void AppIdComesFromTheSteamCopyTest()
        {
            var steam = new Game { PluginId = SteamContent.SteamPluginId, GameId = "367520" };
            var other = new Game { PluginId = Guid.NewGuid(), GameId = "abc" };
            Assert.AreEqual("367520", SteamContent.GetAppId(new List<Game> { other, steam }));
            Assert.IsNull(SteamContent.GetAppId(new List<Game> { other }));
            Assert.IsNull(SteamContent.GetAppId(new List<Game> { new Game { PluginId = SteamContent.SteamPluginId, GameId = "not-a-number" } }));
        }

        [Test]
        public void ExcerptLeavesOnlyTheWordsTest()
        {
            var post = "[img]{STEAM_CLAN_IMAGE}/1/a.png[/img][h1]Update[/h1] <b>New</b> [url=https://x.y]content[/url]";
            Assert.AreEqual("Update New content", SteamContent.GetExcerpt(post));
            Assert.AreEqual("abc…", SteamContent.GetExcerpt("abcdef", 3));
            Assert.AreEqual(string.Empty, SteamContent.GetExcerpt(null));
        }

        [Test]
        public void RecentMinutesAreReadPerGameTest()
        {
            var config = "\"apps\" { \"10\" { \"LastPlayed\" \"1\" \"Playtime2wks\" \"30\" \"Playtime\" \"90\" } " +
                         "\"20\" { \"LastPlayed\" \"1\" \"Playtime\" \"5\" } \"30\" { \"Playtime2wks\" \"7\" } }";
            var minutes = SteamContent.ParseRecentMinutes(config);
            Assert.AreEqual(30, minutes["10"]);
            Assert.AreEqual(7, minutes["30"]);
            Assert.IsFalse(minutes.ContainsKey("20"));
        }

        [Test]
        public void OnlyPngWithAlphaIsALogoTest()
        {
            var header = new byte[32];
            header[25] = 6;
            Assert.IsTrue(SteamContent.IsTransparentPng(header));
            header[25] = 4;
            Assert.IsTrue(SteamContent.IsTransparentPng(header));
            header[25] = 2;
            Assert.IsFalse(SteamContent.IsTransparentPng(header));
            Assert.IsFalse(SteamContent.IsTransparentPng(new byte[4]));
        }
    }
}
