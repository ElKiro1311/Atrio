using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class SteamCardsTests
    {
        private const string Client = @"[[""achievements"",{""version"":2,""data"":{}}],[""badge"",{""version"":1,""data"":{
            ""strName"":""Ganker"",""strIconURL"":""https://shared.akamai.steamstatic.com/items/570/a.png"",""nLevel"":2,""nMaxLevel"":5,""nXP"":200,""bMaxed"":null,
            ""strNextLevelName"":""Carry"",""nNextLevelXP"":300,""dtNextRetry"":null,""rgCards"":[
            {""strName"":""Riki"",""strTitle"":""Riki"",""strImgURL"":""https://community.akamai.steamstatic.com/economy/image/1"",""strArtworkURL"":""https://cdn/1.jpg"",""strMarketHash"":""570-Riki"",""nOwned"":0},
            {""strName"":""Razor"",""strTitle"":"""",""strImgURL"":""https://community.akamai.steamstatic.com/economy/image/2"",""strArtworkURL"":""https://cdn/2.jpg"",""strMarketHash"":""570-Razor"",""nOwned"":3}]}}]]";

        private static string Page(string current, string cards, string drops = "")
        {
            return $@"<html><body><div class=""badge_row depressed badge_gamecard_page""><div class=""badge_title_stats_drops"">{drops}</div>
                <div class=""badge_current"">{current}</div><div class=""badge_card_set_cards"">{cards}</div></div></body></html>";
        }

        private const string Cards = @"
            <div class=""badge_card_set_card owned""><img class=""gamecard"" src=""https://community.akamai.steamstatic.com/economy/image/1"">
              <div class=""badge_card_set_text badge_card_set_title ellipsis""><div class=""badge_card_set_text_qty"">(2)</div>
              Riki <div style=""clear: inline-end""></div></div><div class=""badge_card_set_text ellipsis"">1 de 2, serie 1</div></div>
            <div class=""badge_card_set_card unowned""><img class=""gamecard"" src=""https://community.akamai.steamstatic.com/economy/image/2"">
              <div class=""badge_card_set_text badge_card_set_title ellipsis"">Razor</div></div>";

        private const string Made = @"<div class=""badge_info""><div class=""badge_info_image""><img src=""https://x/icon.png"" class=""badge_icon""></div>
            <div class=""badge_info_description""><div class=""badge_info_title"">Ganker</div><div> Nivel 5, 500 EXP </div><div class=""badge_info_unlocked"">Se desbloqueó el 1 AGO 2019</div></div></div>";

        private const string NotMade = @"<div class=""badge_info""><div class=""badge_empty_circle""></div><div class=""badge_empty_right""><div class=""badge_empty_name"">City 17</div><div class=""badge_empty_name"">100 EXP</div></div></div>";

        [Test]
        public void ClientTest()
        {
            var badge = SteamCards.ParseClient(Client, "570");
            Assert.AreEqual("Ganker", badge.Name);
            Assert.AreEqual(2, badge.Level);
            Assert.AreEqual(200, badge.Xp);
            Assert.AreEqual("Carry", badge.NextLevelName);
            Assert.AreEqual(2, badge.Cards.Count);
            Assert.AreEqual(1, badge.OwnedKinds);
            Assert.AreEqual("Razor", badge.Cards[1].Name, "Without a title, its name.");
            Assert.AreEqual(3, badge.Cards[1].Owned);
            Assert.AreEqual("570-Razor", badge.Cards[1].MarketHash);
            Assert.IsFalse(badge.CanCraft);
            Assert.IsNull(SteamCards.ParseClient(@"[[""badge"",{""version"":1,""data"":{""rgCards"":[]}}]]", "1"));
            Assert.IsNull(SteamCards.ParseClient(@"[[""achievements"",{}]]", "1"));
            Assert.IsNull(SteamCards.ParseClient(@"{""a"":1}", "1"));
        }

        [Test]
        public void PageWithTheBadgeTest()
        {
            var badge = SteamCards.ParsePage(Page(Made, Cards), "570");
            Assert.AreEqual("Ganker", badge.Name);
            Assert.AreEqual(5, badge.Level);
            Assert.AreEqual(500, badge.Xp);
            Assert.IsTrue(badge.IsMaxed);
            Assert.AreEqual("https://x/icon.png", badge.IconUrl);
            Assert.AreEqual("Se desbloqueó el 1 AGO 2019", badge.UnlockedText);
            Assert.AreEqual(2, badge.Cards.Count);
            Assert.AreEqual("Riki", badge.Cards[0].Name);
            Assert.AreEqual(2, badge.Cards[0].Owned);
            Assert.AreEqual(0, badge.Cards[1].Owned);
            Assert.IsNull(badge.DropsLeft, "A page anyone can see doesn't say it.");
        }

        [Test]
        public void PageWithoutTheBadgeTest()
        {
            var badge = SteamCards.ParsePage(Page(NotMade, Cards, @"<span class=""progress_info_bold"">Quedan 3 cromos por obtener</span>"), "220");
            Assert.AreEqual(0, badge.Level);
            Assert.IsNull(badge.Name);
            Assert.AreEqual("City 17", badge.NextLevelName);
            Assert.AreEqual(100, badge.NextLevelXp);
            Assert.AreEqual(3, badge.DropsLeft);
            Assert.AreEqual(0, SteamCards.ParsePage(Page(NotMade, Cards, @"<span class=""progress_info_bold"">No card drops remaining</span>"), "220").DropsLeft);
            Assert.IsNull(SteamCards.ParsePage("<html><div class=\"profile_badges_header\"></div></html>", "220"));
            Assert.IsNull(SteamCards.ParsePage(null, "220"));
        }

        [Test]
        public void GamesWithoutCardsGoToTheListOfBadgesTest()
        {
            Assert.IsTrue(SteamCards.IsBadgeList(new Uri("https://steamcommunity.com/id/someone/badges")));
            Assert.IsTrue(SteamCards.IsBadgeList(new Uri("https://steamcommunity.com/profiles/76561197960287930/badges/?l=spanish")));
            Assert.IsFalse(SteamCards.IsBadgeList(new Uri("https://steamcommunity.com/id/someone/gamecards/570/")));
            Assert.IsFalse(SteamCards.IsBadgeList(new Uri("https://steamcommunity.com/id/someone")));
            Assert.IsFalse(SteamCards.IsBadgeList(null));
        }

        [Test]
        public void StoreCategoriesTest()
        {
            Assert.AreEqual(SteamCardsState.Private, SteamCards.ParseCategories(@"{""response"":{""store_items"":[{""appid"":570,""categories"":{""supported_player_categoryids"":[1],""feature_categoryids"":[29,30]}}]}}"));
            Assert.AreEqual(SteamCardsState.None, SteamCards.ParseCategories(@"{""response"":{""store_items"":[{""appid"":1,""categories"":{""feature_categoryids"":[30]}}]}}"));
            Assert.AreEqual(SteamCardsState.Unknown, SteamCards.ParseCategories(@"{""response"":{""store_items"":[{""appid"":1}]}}"));
            Assert.AreEqual(SteamCardsState.Unknown, SteamCards.ParseCategories(@"{""response"":{}}"));
        }

        [Test]
        public void TheNewestCountsTest()
        {
            var client = SteamCards.ParseClient(Client, "570");
            client.Read = new DateTime(2026, 1, 1);
            var page = SteamCards.ParsePage(Page(Made, Cards), "570");
            page.Read = new DateTime(2026, 2, 1);
            var merged = SteamCards.Merge(client, page);
            Assert.AreEqual(5, merged.Level);
            Assert.AreEqual("https://cdn/1.jpg", merged.Cards[0].ArtworkUrl, "The big picture only the client has.");
            Assert.AreEqual("570-Razor", merged.Cards[1].MarketHash);
            Assert.IsNull(merged.NextLevelName, "There's no level after the last one.");

            client = SteamCards.ParseClient(Client, "570");
            client.Read = new DateTime(2026, 3, 1);
            page.DropsLeft = 1;
            merged = SteamCards.Merge(client, page);
            Assert.AreEqual(2, merged.Level);
            Assert.IsNull(merged.DropsLeft, "There's one more card than when the account said how many were left.");
            page.Cards[0].Owned = 3;
            client = SteamCards.ParseClient(Client, "570");
            client.Read = new DateTime(2026, 3, 1);
            Assert.AreEqual(1, SteamCards.Merge(client, page).DropsLeft);
            Assert.AreEqual(page, SteamCards.Merge(null, page));
            Assert.AreEqual(client, SteamCards.Merge(client, null));
            Assert.IsNull(SteamCards.Merge(null, null));
        }

        [Test]
        public void TheFileOfTheClientChangesForOtherThingsTest()
        {
            var page = SteamCards.ParsePage(Page(Made, Cards), "570");
            page.Read = new DateTime(2026, 2, 1);
            var client = SteamCards.ParseClient(Client, "570");
            client.Read = new DateTime(2026, 3, 1);

            // The achievements were written again, with the same old badge next to them.
            Assert.AreEqual(5, SteamCards.Merge(client, page, client.Stamp).Level);
            Assert.AreEqual(client.Stamp, SteamCards.ParseClient(Client.Replace(@"""version"":2", @"""version"":3"), "570").Stamp, "Only the badge counts.");
            var other = SteamCards.ParseClient(Client.Replace(@"""nLevel"":2", @"""nLevel"":3"), "570");
            other.Read = new DateTime(2026, 3, 1);
            Assert.AreNotEqual(client.Stamp, other.Stamp);
            Assert.AreEqual(3, SteamCards.Merge(other, page, client.Stamp).Level);
        }

        [Test]
        public void AProfileThatHidesItsCardsTest()
        {
            // The page of the badge, without any card: what the client has is better than nothing.
            var page = SteamCards.ParsePage(Page(Made, string.Empty), "570");
            Assert.AreEqual(0, page.Cards.Count);
            page.Read = new DateTime(2026, 5, 1);
            var client = SteamCards.ParseClient(Client, "570");
            client.Read = new DateTime(2026, 1, 1);
            Assert.AreEqual(client, SteamCards.Merge(client, page));
            Assert.AreEqual(2, SteamCards.Merge(client, page).Cards.Count);
            Assert.AreEqual(page, SteamCards.Merge(null, page));
        }

        [Test]
        public void TheNextLevelIsOfTheSameLevelTest()
        {
            var page = SteamCards.ParsePage(Page(Made.Replace("Nivel 5, 500", "Nivel 4, 400"), Cards), "570");
            page.Read = new DateTime(2026, 2, 1);
            var client = SteamCards.ParseClient(Client, "570");
            client.Read = new DateTime(2026, 1, 1);
            Assert.IsNull(SteamCards.Merge(client, page).NextLevelName, "The client says the one after the second.");
            page = SteamCards.ParsePage(Page(Made.Replace("Nivel 5, 500", "Nivel 2, 200"), Cards), "570");
            page.Read = new DateTime(2026, 2, 1);
            Assert.AreEqual("Carry", SteamCards.Merge(client, page).NextLevelName);
        }

        [Test]
        public void TheFileOfTheClientMayHaveAnythingTest()
        {
            var root = Path.Combine(Path.GetTempPath(), $"atrio-cards-{Guid.NewGuid():N}");
            try
            {
                var folder = Path.Combine(root, "userdata", "22202", "config", "librarycache");
                Directory.CreateDirectory(folder);
                SteamCards.TestSteamPath = root;
                var file = Path.Combine(folder, "570.json");
                foreach (var text in new[] { "", "[", "5", Client.Replace(@"""nXP"":200", @"""nXP"":"""""), Client.Replace(@"""strTitle"":""Riki""", @"""strTitle"":{""a"":1}"), Client.Replace(@"""nLevel"":2", @"""nLevel"":99999999999") })
                {
                    File.WriteAllText(file, text);
                    Assert.IsNull(SteamCards.ReadClient("22202", "570"), text);
                }

                File.WriteAllText(file, Client);
                Assert.AreEqual(2, SteamCards.ReadClient("22202", "570").Level);
            }
            finally
            {
                SteamCards.TestSteamPath = null;
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void CraftTest()
        {
            var badge = new SteamBadge { Cards = new List<SteamCard> { new SteamCard { Owned = 1 }, new SteamCard { Owned = 2 } } };
            Assert.IsTrue(badge.CanCraft);
            badge.Level = 5;
            Assert.IsFalse(badge.CanCraft);
            badge.Level = 1;
            badge.Cards[0].Owned = 0;
            Assert.IsFalse(badge.CanCraft);
        }

        [Test]
        public void OnlyTheSameAccountTest()
        {
            Assert.AreEqual("76561197960287930", SteamCards.GetSteamId("22202"));
            Assert.IsNull(SteamCards.GetSteamId("0"));
            Assert.IsNull(SteamCards.GetSteamId("x"));
            Assert.IsTrue(SteamCards.IsSameAccount("x", "g_steamID = \"76561197960287930\";", "22202"));
            Assert.IsFalse(SteamCards.IsSameAccount("x", "g_steamID = \"76561197960287931\";", "22202"));
            Assert.IsFalse(SteamCards.IsSameAccount("x", "g_steamID = false;", "22202"));
        }

        [Test]
        public void ClientFolderTest()
        {
            var root = Path.Combine(Path.GetTempPath(), $"atrio-cards-{Guid.NewGuid():N}");
            try
            {
                foreach (var account in new[] { "11", "22202" })
                {
                    Directory.CreateDirectory(Path.Combine(root, "userdata", account, "config", "librarycache"));
                    File.WriteAllText(Path.Combine(root, "userdata", account, "config", "localconfig.vdf"), string.Empty);
                }

                File.SetLastWriteTimeUtc(Path.Combine(root, "userdata", "11", "config", "localconfig.vdf"), DateTime.UtcNow.AddDays(-2));
                File.WriteAllText(Path.Combine(root, "userdata", "22202", "config", "librarycache", "570.json"), Client);
                File.WriteAllText(Path.Combine(root, "userdata", "22202", "config", "librarycache", "10.json"), "not json");
                SteamCards.TestSteamPath = root;
                Assert.AreEqual("22202", SteamCards.FindAccountId());
                Assert.AreEqual(2, SteamCards.ReadClient("22202", "570").Cards.Count);
                Assert.IsNull(SteamCards.ReadClient("22202", "10"));
                Assert.IsNull(SteamCards.ReadClient("22202", "..\\570"));
                Assert.IsNull(SteamCards.ReadClient("11", "570"));
            }
            finally
            {
                SteamCards.TestSteamPath = null;
                Directory.Delete(root, true);
            }
        }
    }
}
