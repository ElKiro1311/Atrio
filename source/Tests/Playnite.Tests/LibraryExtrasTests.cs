using NUnit.Framework;
using Playnite.Atrio;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace Playnite.Tests
{
    [TestFixture]
    public class LibraryExtrasTests
    {
        private const string Manifest = @"""AppState""
{
	""appid""		""3419430""
	""name""		""Bongo Cat""
	""StateFlags""		""6""
	""installdir""		""BongoCat""
	""SizeOnDisk""		""866764647""
	""BytesToDownload""		""2000""
	""BytesDownloaded""		""500""
	""ScheduledAutoUpdate""		""0""
	""UserConfig""
	{
		""name""		""Other""
	}
}";

        [Test]
        public void ManifestUpdateTest()
        {
            var manifest = SteamAppManifest.Parse(Manifest);
            Assert.AreEqual("3419430", manifest.AppId);
            Assert.AreEqual("Bongo Cat", manifest.Name);
            Assert.IsTrue(manifest.NeedsUpdate);
            Assert.IsFalse(manifest.IsUpdating);
            Assert.IsFalse(manifest.IsInstalled);

            var installed = SteamAppManifest.Parse(Manifest.Replace(@"""6""", @"""4"""));
            Assert.IsFalse(installed.NeedsUpdate);
            Assert.IsTrue(installed.IsInstalled);

            // Being installed for the first time isn't an update.
            var first = SteamAppManifest.Parse(Manifest.Replace(@"""6""", @"""1026"""));
            Assert.IsFalse(first.NeedsUpdate);

            var running = SteamAppManifest.Parse(Manifest.Replace(@"""6""", @"""1030"""));
            Assert.IsTrue(running.NeedsUpdate);
            Assert.IsTrue(running.IsUpdating);

            var pending = SteamUpdates.GetPending(new[] { manifest, installed, manifest }, DateTime.UtcNow);
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual(1500, pending[0].BytesLeft);
            Assert.IsNull(pending[0].Scheduled);
        }

        [Test]
        public void UpdateWaitingWhileOpenTest()
        {
            var steam = new Game { PluginId = SteamContent.SteamPluginId, GameId = "3419430" };
            var other = new Game { PluginId = Guid.NewGuid(), GameId = "3419430" };
            var reads = 0;
            IEnumerable<string> pending()
            {
                reads++;
                return new[] { "3419430", null, "" };
            }

            var raised = 0;
            EventHandler counter = (s, e) => raised++;
            SteamUpdates.WaitingChanged += counter;
            try
            {
                // Nothing open: the files of Steam aren't even read.
                SteamUpdates.Refresh(false, pending);
                Assert.AreEqual(0, reads);
                Assert.IsFalse(SteamUpdates.IsWaiting(new[] { steam }));

                Assert.IsTrue(SteamUpdates.Refresh(true, pending));
                Assert.AreEqual(1, raised);
                Assert.IsTrue(SteamUpdates.IsWaiting(new[] { other, steam }));

                // Only the copy of Steam counts, whatever the id of the others.
                Assert.IsFalse(SteamUpdates.IsWaiting(new[] { other }));

                // The same again doesn't tell anyone.
                Assert.IsFalse(SteamUpdates.Refresh(true, pending));
                Assert.AreEqual(1, raised);

                // Once the game is closed Steam installs it, and the mark goes away.
                Assert.IsTrue(SteamUpdates.Refresh(false, pending));
                Assert.AreEqual(2, raised);
                Assert.IsFalse(SteamUpdates.IsWaiting(new[] { steam }));
            }
            finally
            {
                SteamUpdates.WaitingChanged -= counter;
                SteamUpdates.Refresh(false, null);
            }
        }

        [Test]
        public void RenderThreadFailureTest()
        {
            var render = new System.Runtime.InteropServices.COMException("UCEERR_RENDERTHREADFAILURE", unchecked((int)0x88980406));
            Assert.IsTrue(PlayniteApplication.IsRenderThreadFailure(render));
            Assert.IsTrue(PlayniteApplication.IsRenderThreadFailure(new InvalidOperationException("x", render)));
            Assert.IsFalse(PlayniteApplication.IsRenderThreadFailure(new System.Runtime.InteropServices.COMException("other", unchecked((int)0x80004005))));
            Assert.IsFalse(PlayniteApplication.IsRenderThreadFailure(null));
        }

        private static string NewFolder()
        {
            var folder = Path.Combine(Path.GetTempPath(), "atrio-pruebas-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            return folder;
        }

        [Test]
        public void LeftoversTest()
        {
            var root = NewFolder();
            try
            {
                var library = Path.Combine(root, "steam");
                var apps = Path.Combine(library, "steamapps");
                Directory.CreateDirectory(Path.Combine(apps, "common", "Installed Game"));
                Directory.CreateDirectory(Path.Combine(apps, "common", "Old Game"));
                Directory.CreateDirectory(Path.Combine(apps, "common", "Steam Controller Configs"));
                Directory.CreateDirectory(Path.Combine(apps, "common", "Added By Hand"));
                Directory.CreateDirectory(Path.Combine(apps, "shadercache", "100"));
                Directory.CreateDirectory(Path.Combine(apps, "shadercache", "200"));
                Directory.CreateDirectory(Path.Combine(apps, "workshop", "content", "241100"));
                Directory.CreateDirectory(Path.Combine(apps, "workshop", "content", "300"));
                Directory.CreateDirectory(Path.Combine(apps, "downloading", "400"));
                File.WriteAllText(Path.Combine(apps, "appmanifest_100.acf"), "\"installdir\" \"Installed Game\"");
                File.WriteAllText(Path.Combine(apps, "common", "Old Game", "save.dat"), "12345");

                var epic = Path.Combine(root, "Epic Games");
                Directory.CreateDirectory(Path.Combine(epic, "Fortnite", "Fortnite"));
                Directory.CreateDirectory(Path.Combine(epic, "Dauntless"));
                Directory.CreateDirectory(Path.Combine(epic, "Launcher"));
                Directory.CreateDirectory(Path.Combine(epic, "UE_5.3"));

                var sources = new LeftoverSources
                {
                    SteamLibraries = new List<SteamLibraryContents> { new SteamLibraryContents { Library = library, InstallDirs = new List<string> { "Installed Game" } } },
                    EpicInstalls = new List<string> { Path.Combine(epic, "Fortnite", "Fortnite") },
                    EpicRoots = new List<string> { Leftovers.FindAncestorNamed(Path.Combine(epic, "Fortnite", "Fortnite"), "Epic Games") },
                    Claimed = new List<string> { Path.Combine(apps, "common", "Added By Hand") + "\\" }
                };

                var found = Leftovers.Find(sources);
                var names = found.Select(a => a.Kind + ":" + a.Name).OrderBy(a => a).ToList();
                CollectionAssert.AreEqual(new[]
                {
                    "EpicGame:Dauntless",
                    "SteamDownload:400",
                    "SteamGame:Old Game",
                    "SteamShaders:200",
                    "SteamWorkshop:300"
                }, names);

                // A game the library has as installed keeps its things, even without a file of Steam.
                sources.InstalledSteamApps = new List<string> { "300" };
                Assert.IsFalse(Leftovers.Find(sources).Any(a => a.AppId == "300"));

                var measure = Leftovers.MeasureFolder(Path.Combine(apps, "common", "Old Game"), CancellationToken.None);
                Assert.AreEqual(5, measure.Size);
                Assert.IsNotNull(measure.LastChange);

                // Never a whole disk, nor what isn't there. (Sending to the real bin was checked once by hand:
                // it would leave a folder in the bin of whoever runs the tests.)
                Assert.IsFalse(Leftovers.SendToBin(Path.Combine(apps, "common", "Not There")));
                Assert.IsFalse(Leftovers.SendToBin("C:\\"));
                Assert.IsFalse(Leftovers.SendToBin(""));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void OverlapsTest()
        {
            Assert.IsTrue(Leftovers.Overlaps(@"D:\steam\steamapps\common\X", @"d:\STEAM\steamapps\common\x\"));
            Assert.IsTrue(Leftovers.Overlaps(@"D:\steam", @"D:\steam\steamapps"));
            Assert.IsFalse(Leftovers.Overlaps(@"D:\steam\a", @"D:\steam\ab"));
            Assert.IsFalse(Leftovers.Overlaps(null, @"D:\steam"));
        }

        [Test]
        public void WikiTest()
        {
            // As the pages of the wiki write it today: «120 fps».
            var text = "{{Video\n|ultrawidescreen            = hackable\n|ultrawidescreen notes      = A mod.\n|hdr                        = true\n|60 fps                     = true\n|120 fps                    = false\n}}\n{{Input\n|controller support        = true\n|full controller           = true\n}}";
            Assert.AreEqual("true", PcGamingWiki.ParseWikitext("|120+ fps = true").Fps120, "The older name still counts.");
            Assert.IsTrue(PcGamingWiki.IsAnswer("{\"idlookup\":[]}", "idlookup"));
            Assert.IsFalse(PcGamingWiki.IsAnswer("{\"error\":{\"code\":\"ratelimited\"}}", "idlookup"));
            Assert.IsFalse(PcGamingWiki.IsAnswer("<html>Too many requests</html>", "query"));
            Assert.IsFalse(PcGamingWiki.IsAnswer(null, "query"));
            var info = PcGamingWiki.ParseWikitext(text);
            Assert.AreEqual("hackable", info.Ultrawide);
            Assert.AreEqual("true", info.Hdr);
            Assert.AreEqual("true", info.Fps60);
            Assert.AreEqual("false", info.Fps120);
            Assert.AreEqual("true", info.Controller);
            Assert.AreEqual("true", info.FullController);

            var page = PcGamingWiki.ParsePage("{\"query\":{\"pages\":[{\"pageid\":1,\"title\":\"Elden Ring\",\"revisions\":[{\"slots\":{\"main\":{\"content\":" +
                Newtonsoft.Json.JsonConvert.ToString(text) + "}}}]}]}}");
            Assert.AreEqual("Elden Ring", page.Page);
            Assert.AreEqual("https://www.pcgamingwiki.com/wiki/Elden_Ring", page.PageUrl);
            Assert.AreEqual("hackable", page.Ultrawide);

            Assert.IsFalse(PcGamingWiki.ParsePage("{\"query\":{\"pages\":[{\"title\":\"Nope\",\"missing\":true}]}}").HasPage);
            Assert.AreEqual("Stardew Valley", PcGamingWiki.ParsePageName("{\"idlookup\":[{\"title\":{\"Page\":\"Stardew Valley\"}}]}"));
            Assert.IsNull(PcGamingWiki.ParsePageName("{\"idlookup\":[]}"));

            var now = DateTime.UtcNow;
            Assert.IsTrue(PcGamingWiki.IsFresh(new PcgwInfo { Page = "A", Checked = now.AddDays(-20) }, now));
            Assert.IsFalse(PcGamingWiki.IsFresh(new PcgwInfo { Page = "A", Checked = now.AddDays(-40) }, now));
            Assert.IsFalse(PcGamingWiki.IsFresh(new PcgwInfo { Checked = now.AddDays(-8) }, now));
        }

        [Test]
        public void WikiKeyTest()
        {
            var steam = new Game("A") { PluginId = SteamContent.SteamPluginId, GameId = "570" };
            var gog = new Game("B") { PluginId = PcGamingWiki.GogPluginId, GameId = "1207658924" };
            var other = new Game("Valorant") { PluginId = Guid.NewGuid(), GameId = "valorant" };
            Assert.AreEqual("steam:570", PcGamingWiki.GetKey(new[] { gog, steam }, "A"));
            Assert.AreEqual("gog:1207658924", PcGamingWiki.GetKey(new[] { gog, other }, "B"));
            Assert.AreEqual("name:" + GameCaptures.Normalize("Valorant"), PcGamingWiki.GetKey(new[] { other }, "Valorant"));

            // A game added by hand isn't asked for by its name.
            Assert.IsNull(PcGamingWiki.GetKey(new[] { new Game("Mi juego casero") }, "Mi juego casero"));
        }

        [Test]
        public void ControllerTest()
        {
            var json = "{\"response\":{\"store_items\":[" +
                "{\"appid\":1245620,\"success\":1,\"categories\":{\"controller_categoryids\":[28]}}," +
                "{\"appid\":570,\"success\":1,\"categories\":{\"feature_categoryids\":[29]}}," +
                "{\"appid\":10,\"success\":1,\"categories\":{\"controller_categoryids\":[18]}}," +
                "{\"id\":99,\"success\":2}]}}";
            var levels = SteamControllerSupport.ParseItems(json, new[] { "1245620", "570", "10", "99", "5" });
            Assert.AreEqual(SteamControllerSupport.Full, levels["1245620"]);
            Assert.AreEqual(SteamControllerSupport.None, levels["570"]);
            Assert.AreEqual(SteamControllerSupport.Partial, levels["10"]);
            Assert.AreEqual(SteamControllerSupport.Unknown, levels["99"]);
            Assert.AreEqual(SteamControllerSupport.Unknown, levels["5"]);
        }

        [Test]
        public void FreeGamesTest()
        {
            var json = @"{""data"":{""Catalog"":{""searchStore"":{""elements"":[
                {""title"":""Flotsam"",""productSlug"":null,""catalogNs"":{""mappings"":[{""pageSlug"":""flotsam-266287""}]},
                 ""keyImages"":[{""type"":""Thumbnail"",""url"":""https://x/t.jpg""},{""type"":""OfferImageWide"",""url"":""https://x/w.jpg""}],
                 ""price"":{""totalPrice"":{""discountPrice"":0,""originalPrice"":2499}},
                 ""promotions"":{""promotionalOffers"":[{""promotionalOffers"":[{""startDate"":""2026-11-05T16:00:00.000Z"",""endDate"":""2026-11-12T16:00:00.000Z"",""discountSetting"":{""discountPercentage"":0}}]}],""upcomingPromotionalOffers"":[]}},
                {""title"":""Knightica"",""catalogNs"":{""mappings"":[]},""offerMappings"":[{""pageSlug"":""knightica-3be45c""}],""keyImages"":[],
                 ""price"":{""totalPrice"":{""discountPrice"":1499,""originalPrice"":1499}},
                 ""promotions"":{""promotionalOffers"":[],""upcomingPromotionalOffers"":[{""promotionalOffers"":[{""startDate"":""2026-11-12T16:00:00.000Z"",""endDate"":""2026-11-19T16:00:00.000Z"",""discountSetting"":{""discountPercentage"":0}}]}]}},
                {""title"":""LISA"",""catalogNs"":{""mappings"":[{""pageSlug"":""lisa""}]},""keyImages"":[],
                 ""price"":{""totalPrice"":{""discountPrice"":1499,""originalPrice"":2499}},
                 ""promotions"":{""promotionalOffers"":[{""promotionalOffers"":[{""startDate"":""2026-11-01T06:00:00.000Z"",""endDate"":""2026-11-10T06:59:00.000Z"",""discountSetting"":{""discountPercentage"":60}}]}],""upcomingPromotionalOffers"":[]}},
                {""title"":""Them's Fightin' Herds"",""price"":{""totalPrice"":{""discountPrice"":1999}},""promotions"":null}
            ]}}}}";
            var now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
            var games = FreeGames.Parse(json, now, "es-ES");
            Assert.AreEqual(2, games.Count);
            Assert.AreEqual("Flotsam", games[0].Title);
            Assert.IsTrue(games[0].IsNow);
            Assert.AreEqual("https://store.epicgames.com/es-ES/p/flotsam-266287", games[0].Url);
            Assert.AreEqual("https://x/w.jpg", games[0].ImageUrl);
            Assert.AreEqual(new DateTime(2026, 11, 12, 16, 0, 0, DateTimeKind.Utc), games[0].End);
            Assert.AreEqual("Knightica", games[1].Title);
            Assert.IsFalse(games[1].IsNow);
            Assert.AreEqual("https://store.epicgames.com/es-ES/p/knightica-3be45c", games[1].Url);

            // A week later the first one is over.
            Assert.AreEqual(0, FreeGames.Parse(json, now.AddDays(11), "es-ES").Count);
            Assert.AreEqual("en-US", FreeGames.GetStoreLocale(CultureInfo.InvariantCulture));
            Assert.AreEqual("es-ES", FreeGames.GetStoreLocale(new CultureInfo("es-ES")));
        }

        [Test]
        public void WishlistParseTest()
        {
            var culture = new CultureInfo("es-ES");
            var search = Wishlist.ParseSteamSearch("{\"items\":[{\"type\":\"app\",\"id\":367520,\"name\":\"Hollow Knight\",\"price\":{\"currency\":\"USD\",\"initial\":1499,\"final\":749},\"tiny_image\":\"https://x/c.jpg\"},{\"type\":\"sub\",\"id\":1,\"name\":\"Pack\"}]}", culture);
            Assert.AreEqual(1, search.Count);
            Assert.AreEqual("steam:367520", search[0].Key);
            Assert.AreEqual(749, search[0].Price);
            Assert.AreEqual(50, search[0].Discount);
            // Dollars as a country of dollars writes them, in Spanish if there is one: "$7.49" in es-US, "7,49 US$" otherwise.
            StringAssert.Contains("7", search[0].PriceText);
            StringAssert.Contains("49", search[0].PriceText);
            StringAssert.Contains("$", search[0].PriceText);
            Assert.AreEqual("12,50 €", Wishlist.FormatPrice(1250, "EUR", culture).Replace(' ', ' '));
            Assert.AreEqual("12.50 XYZ", Wishlist.FormatPrice(1250, "XYZ", CultureInfo.InvariantCulture));

            var gog = Wishlist.ParseGogSearch("{\"products\":[{\"id\":\"1308320804\",\"slug\":\"hollow_knight\",\"title\":\"Hollow Knight\",\"coverHorizontal\":\"https://x/g.png\",\"productState\":\"default\",\"price\":{\"final\":\"$14.99\",\"base\":\"$14.99\",\"finalMoney\":{\"amount\":\"14.99\"},\"baseMoney\":{\"amount\":\"14.99\"}}}]}");
            Assert.AreEqual("gog:1308320804", gog[0].Key);
            Assert.AreEqual(1499, gog[0].Price);
            Assert.AreEqual(0, gog[0].Discount);
            Assert.AreEqual("https://www.gog.com/game/hollow_knight", gog[0].StoreUrl);

            var items = Wishlist.ParseSteamItems("{\"response\":{\"store_items\":[" +
                "{\"appid\":3527290,\"success\":1,\"name\":\"PEAK\",\"release\":{\"steam_release_date\":1750093261},\"assets\":{\"asset_url_format\":\"steam/apps/3527290/${FILENAME}?t=1\",\"header\":\"header.jpg\"}," +
                "\"best_purchase_option\":{\"final_price_in_cents\":\"495\",\"original_price_in_cents\":\"799\",\"formatted_final_price\":\"$4.95\",\"formatted_original_price\":\"$7.99\",\"discount_pct\":38,\"active_discounts\":[{\"discount_end_date\":1794736800}]}}," +
                "{\"appid\":570,\"success\":1,\"name\":\"Dota 2\",\"is_free\":true}," +
                "{\"appid\":42,\"success\":1,\"name\":\"Soon\",\"release\":{\"is_coming_soon\":true}}]}}");
            Assert.AreEqual(495, items["3527290"].Price);
            Assert.AreEqual(799, items["3527290"].BasePrice);
            Assert.AreEqual(38, items["3527290"].Discount);
            Assert.AreEqual("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/3527290/header.jpg?t=1", items["3527290"].ImageUrl);
            Assert.IsNotNull(items["3527290"].DiscountEnds);
            Assert.IsTrue(items["570"].IsFree);
            Assert.AreEqual(0, items["570"].Price);
            Assert.IsTrue(items["42"].ComingSoon);
            Assert.IsNull(items["42"].Price);

            var prices = Wishlist.ParseGogPrices("{\"_embedded\":{\"prices\":[{\"currency\":{\"code\":\"USD\"},\"basePrice\":\"1499 USD\",\"finalPrice\":\"999 USD\"}]}}", culture);
            Assert.AreEqual(999, prices.Price);
            Assert.AreEqual(33, prices.Discount);
            StringAssert.Contains("9", prices.PriceText);
            StringAssert.Contains("$", prices.PriceText);

            var loginUsers = "\"users\"\n{\n\t\"76561198000000001\"\n\t{\n\t\t\"AccountName\"\t\t\"a\"\n\t\t\"MostRecent\"\t\t\"0\"\n\t}\n\t\"76561198000000002\"\n\t{\n\t\t\"MostRecent\"\t\t\"1\"\n\t}\n}";
            Assert.AreEqual("76561198000000002", Wishlist.FindSteamId(loginUsers));

            // A public name with braces doesn't break the blocks.
            var braces = "\"users\"\n{\n\t\"76561198000000003\"\n\t{\n\t\t\"PersonaName\"\t\t\"{Jugador}\"\n\t\t\"MostRecent\"\t\t\"1\"\n\t}\n}";
            Assert.AreEqual("76561198000000003", Wishlist.FindSteamId(braces));
            Assert.IsNull(Wishlist.FindSteamId(""));
            CollectionAssert.AreEqual(new[] { "10", "20" }, Wishlist.ParseSteamWishlist("{\"response\":{\"items\":[{\"appid\":10,\"priority\":1},{\"appid\":20}]}}"));
            Assert.AreEqual(0, Wishlist.ParseSteamWishlist("{\"response\":{}}").Count);
        }

        [Test]
        public void WishlistApplyTest()
        {
            var now = DateTime.UtcNow;
            var item = new WishlistItem { Store = "steam", StoreId = "1", Title = "A", Price = 2000, PriceText = "$20", Lowest = 2000, LowestText = "$20" };
            Assert.IsNull(Wishlist.Apply(item, new WishlistItem { Price = 2000, PriceText = "$20" }, now));

            var cheaper = Wishlist.Apply(item, new WishlistItem { Price = 1000, BasePrice = 2000, Discount = 50, PriceText = "$10" }, now);
            Assert.AreEqual(WishlistChangeKind.Cheaper, cheaper.Kind);
            Assert.AreEqual(1000, item.Lowest);
            Assert.AreEqual("$10", item.LowestText);

            // Back up: nothing to tell, and the lowest stays.
            Assert.IsNull(Wishlist.Apply(item, new WishlistItem { Price = 2000, PriceText = "$20" }, now));
            Assert.AreEqual(1000, item.Lowest);
            Assert.AreEqual("A", item.Title);

            // Prices of another country start again: no notice, and the lowest is the new one.
            item.Country = "US";
            Assert.IsNull(Wishlist.Apply(item, new WishlistItem { Price = 1500, PriceText = "15 €", Country = "ES" }, now));
            Assert.AreEqual(1500, item.Lowest);
            Assert.AreEqual("ES", item.Country);
            item.Price = 2000;

            Assert.AreEqual(WishlistChangeKind.Free, Wishlist.Apply(item, new WishlistItem { Price = 0, IsFree = true }, now).Kind);

            var soon = new WishlistItem { Store = "steam", StoreId = "2", ComingSoon = true };
            Assert.IsNull(Wishlist.Apply(soon, new WishlistItem { ComingSoon = true }, now));
            Assert.AreEqual(WishlistChangeKind.Released, Wishlist.Apply(soon, new WishlistItem { ComingSoon = false, Price = 3000, PriceText = "$30" }, now).Kind);
        }

        [Test]
        public void QueueTest()
        {
            var now = new DateTime(2026, 11, 9, 22, 0, 0, DateTimeKind.Utc);
            var game = new Game("A") { IsInstalled = false };
            Assert.AreEqual(QueueStep.Start, InstallQueue.Decide(game, false, now, now, now, null));
            Assert.AreEqual(QueueStep.Wait, InstallQueue.Decide(game, false, now, now, now, now.AddHours(4)));
            Assert.AreEqual(QueueStep.Start, InstallQueue.Decide(game, false, now.AddHours(5), now, now, now.AddHours(4)));
            Assert.AreEqual(QueueStep.Done, InstallQueue.Decide(null, false, now, now, now, null));
            Assert.AreEqual(QueueStep.Done, InstallQueue.Decide(new Game("B") { IsInstalled = true }, false, now, now, now, null));

            // Asked a moment ago, not installing yet: it waits; long after, it didn't start.
            Assert.AreEqual(QueueStep.Wait, InstallQueue.Decide(game, true, now.AddSeconds(10), now, now, null));
            Assert.AreEqual(QueueStep.Failed, InstallQueue.Decide(game, true, now.AddMinutes(2), now, now, null));

            // However long it takes, the next one doesn't start while it installs.
            var installing = new Game("C") { IsInstalling = true };
            Assert.AreEqual(QueueStep.Wait, InstallQueue.Decide(installing, true, now.AddHours(3), now, now.AddHours(2), null));
            Assert.AreEqual(QueueStep.Wait, InstallQueue.Decide(installing, true, now.AddHours(30), now, now.AddHours(2), null));
            Assert.AreEqual(QueueStep.Done, InstallQueue.Decide(new Game("D") { IsInstalled = true }, true, now, now, now, null));

            // Taken out of the library while installing: not counted as installed.
            Assert.AreEqual(QueueStep.Failed, InstallQueue.Decide(null, true, now, now, now, null));

            var local = new DateTime(2026, 11, 9, 22, 30, 0, DateTimeKind.Local);
            Assert.AreEqual(new DateTime(2026, 11, 10, 3, 0, 0, DateTimeKind.Local).ToUniversalTime(), InstallQueue.NextTime(3, local));
            Assert.AreEqual(new DateTime(2026, 11, 9, 23, 0, 0, DateTimeKind.Local).ToUniversalTime(), InstallQueue.NextTime(23, local));
            Assert.IsFalse(InstallQueue.CanQueue(new Game("E") { IsInstalled = true }));
            Assert.IsTrue(InstallQueue.CanQueue(new Game("F") { PluginId = Guid.NewGuid(), GameId = "1" }));
        }

        private static Dictionary<string, string> Values(string fav = "0", string hidden = "0", string status = "", string cats = "", string score = "")
        {
            return new Dictionary<string, string>
            {
                [SyncMerge.Favorite] = fav,
                [SyncMerge.Hidden] = hidden,
                [SyncMerge.Status] = status,
                [SyncMerge.Collections] = cats,
                [SyncMerge.Score] = score
            };
        }

        private static SyncMachineFile Export(string machine, SyncPlan plan)
        {
            return new SyncMachineFile { Machine = machine, Games = SyncMerge.ToExport(plan.Snapshot) };
        }

        [Test]
        public void SyncFirstTimeTest()
        {
            var t0 = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);

            // Before synchronizing, each PC had its own favorites and collections: both stay on both.
            var a = new Dictionary<string, Dictionary<string, string>> { ["g1"] = Values(fav: "1", cats: "Sofá"), ["g2"] = Values() };
            var b = new Dictionary<string, Dictionary<string, string>> { ["g1"] = Values(cats: "RPG"), ["g2"] = Values(status: "Beaten") };

            var planA = SyncMerge.Plan(a, null, null, null, t0);
            Assert.AreEqual(0, planA.Changes.Count);
            var fileA = Export("A", planA);
            Assert.IsFalse(fileA.Games.ContainsKey("g2"), "Nothing but defaults: nothing to tell.");

            var planB = SyncMerge.Plan(b, null, null, new[] { fileA }, t0);
            var changesB = planB.Changes.ToDictionary(c => c.Key + "/" + c.Field, c => c.Value);
            Assert.AreEqual("1", changesB["g1/fav"]);
            Assert.AreEqual("RPG\nSofá", changesB["g1/cats"]);
            Assert.IsFalse(changesB.ContainsKey("g2/status"));

            var planA2 = SyncMerge.Plan(a, planA.Snapshot, a.Keys.ToList(), new[] { Export("B", planB) }, t0.AddMinutes(10));
            var changesA = planA2.Changes.ToDictionary(c => c.Key + "/" + c.Field, c => c.Value);
            Assert.AreEqual("RPG\nSofá", changesA["g1/cats"]);
            Assert.AreEqual("Beaten", changesA["g2/status"]);
            Assert.IsFalse(changesA.ContainsKey("g1/fav"));
        }

        [Test]
        public void SyncAutomaticStatusTest()
        {
            // The states the library sets by itself are read as none: a state set by hand on the other PC always wins.
            var t0 = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
            var careful = new Dictionary<string, Dictionary<string, string>>
            {
                ["g1"] = Values(status: "Completed"),
                ["g2"] = Values(status: "Abandoned"),
                ["g3"] = Values(status: "Plan to Play")
            };
            var fresh = new Dictionary<string, Dictionary<string, string>> { ["g1"] = Values(), ["g2"] = Values(), ["g3"] = Values() };

            var planA = SyncMerge.Plan(careful, null, null, null, t0);
            var planB = SyncMerge.Plan(fresh, null, null, new[] { Export("A", planA) }, t0);
            Assert.AreEqual(3, planB.Changes.Count);
            Assert.AreEqual("Completed", planB.Changes.Single(c => c.Key == "g1").Value);

            var planA2 = SyncMerge.Plan(careful, planA.Snapshot, careful.Keys.ToList(), new[] { Export("B", planB) }, t0.AddMinutes(10));
            Assert.AreEqual(0, planA2.Changes.Count, "The careful PC keeps every state.");

            // The same with the automatic states written as the library has them.
            var auto = new Dictionary<string, Dictionary<string, string>> { ["g1"] = Values(status: "~Not Played"), ["g2"] = Values(status: "~Played"), ["g3"] = Values(status: "~Played") };
            var planC = SyncMerge.Plan(auto, null, null, new[] { Export("A", planA) }, t0);
            CollectionAssert.AreEquivalent(new[] { "Completed", "Abandoned", "Plan to Play" }, planC.Changes.Select(c => c.Value).ToList());
            var planA3 = SyncMerge.Plan(careful, planA.Snapshot, careful.Keys.ToList(), new[] { Export("C", planC) }, t0.AddMinutes(20));
            Assert.AreEqual(0, planA3.Changes.Count);

            // The library moving from one automatic state to the other by itself isn't a change of the person.
            var played = new Dictionary<string, Dictionary<string, string>> { ["g1"] = Values(status: "~Not Played") };
            var start = SyncMerge.Plan(played, null, null, null, t0);
            played["g1"][SyncMerge.Status] = "~Played";
            var after = SyncMerge.Plan(played, start.Snapshot, new[] { "g1" }, null, t0.AddMinutes(10));
            Assert.AreEqual(0, after.Changes.Count);
            Assert.IsFalse(SyncMerge.ToExport(after.Snapshot).ContainsKey("g1"), "Nothing to tell.");

            // The person taking a state set by hand to an automatic one is a change, and it travels by its name.
            var manual = new Dictionary<string, Dictionary<string, string>> { ["g1"] = Values(status: "Playing") };
            var before = SyncMerge.Plan(manual, null, null, null, t0);
            manual["g1"][SyncMerge.Status] = "~Played";
            var moved = SyncMerge.Plan(manual, before.Snapshot, new[] { "g1" }, null, t0.AddMinutes(10));
            var told = SyncMerge.ToExport(moved.Snapshot)["g1"][SyncMerge.Status];
            Assert.AreEqual("~Played", told.V);
            Assert.AreEqual(t0.AddMinutes(10), told.T);
            var elsewhere = new Dictionary<string, Dictionary<string, string>> { ["g1"] = Values(status: "Playing") };
            var there = SyncMerge.Plan(elsewhere, SyncMerge.Plan(elsewhere, null, null, null, t0).Snapshot, new[] { "g1" },
                new[] { new SyncMachineFile { Machine = "A", Games = SyncMerge.ToExport(moved.Snapshot) } }, t0.AddMinutes(20));
            Assert.AreEqual("~Played", there.Changes.Single().Value);
            Assert.AreEqual("Played", SyncMerge.StatusName(there.Changes.Single().Value));
        }

        [Test]
        public void ReadStatusTest()
        {
            var notPlayed = Guid.NewGuid();
            var played = Guid.NewGuid();
            var settings = new Playnite.Database.CompletionStatusSettings { DefaultStatus = notPlayed, PlayedStatus = played };
            Assert.AreEqual("~Not Played", PcSync.ReadStatus(notPlayed, "Not Played", settings));
            Assert.AreEqual("~Played", PcSync.ReadStatus(played, "Played", settings));
            Assert.AreEqual("Beaten", PcSync.ReadStatus(Guid.NewGuid(), "Beaten", settings));
            Assert.AreEqual(string.Empty, PcSync.ReadStatus(Guid.Empty, null, settings));
            Assert.IsTrue(SyncMerge.Same(SyncMerge.Status, "~Not Played", "~Played"));
            Assert.IsTrue(SyncMerge.Same(SyncMerge.Status, "~Playing", "playing"), "Automatic on one PC, by hand on the other: the same state.");
            Assert.IsFalse(SyncMerge.Same(SyncMerge.Status, "~Played", "Playing"));
        }

        [Test]
        public void RemoteSittingsTest()
        {
            var previous = PlayHistory.FilePath;
            var folder = NewFolder();
            try
            {
                PlayHistory.FilePath = Path.Combine(folder, PlayHistory.FileName);
                var game = Guid.NewGuid();
                var start = DateTime.UtcNow.AddHours(-3);
                PlayHistory.Record(game, start, start.AddHours(1), 3600, false);

                var mine = new PlaySession { GameId = game, Start = start, End = start.AddHours(1), Seconds = 3600, Machine = "B" };
                var theirs = new PlaySession { GameId = game, Start = start.AddHours(2), End = start.AddHours(2.5), Seconds = 1800, Machine = "B" };

                // The one that is this PC's own sitting isn't added; the other one is.
                var recorded = PlayHistory.AddRemoteMany(new[] { mine, theirs });
                Assert.AreEqual(1, recorded.Count);
                Assert.AreEqual(1800, recorded[0].Seconds);

                // Already there from that PC (a round cut in half): it's given back, to finish it, but not added twice.
                Assert.AreEqual(1, PlayHistory.AddRemoteMany(new[] { theirs }).Count);
                Assert.AreEqual(2, PlayHistory.GetSessions(DateTime.MinValue, DateTime.MaxValue).Count);

                // The same sitting from another PC is another one's: left out.
                Assert.AreEqual(0, PlayHistory.AddRemoteMany(new[] { new PlaySession { GameId = game, Start = theirs.Start, End = theirs.End, Seconds = 1800, Machine = "C" } }).Count);

                PlayHistory.RemoveRemote(new[] { theirs });
                Assert.AreEqual(1, PlayHistory.GetSessions(DateTime.MinValue, DateTime.MaxValue).Count);
                Assert.IsNull(PlayHistory.GetSessions(DateTime.MinValue, DateTime.MaxValue)[0].Machine);
            }
            finally
            {
                PlayHistory.FilePath = previous;
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void SyncReimportedGameTest()
        {
            // Removed and imported again before the next round: it comes back with defaults, as a new game.
            var t0 = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
            var local = new Dictionary<string, Dictionary<string, string>> { ["g1"] = Values(fav: "1", cats: "Sofá") };
            var first = SyncMerge.Plan(local, null, null, null, t0);

            var again = new Dictionary<string, Dictionary<string, string>> { ["g1"] = Values() };
            var plan = SyncMerge.Plan(again, first.Snapshot, new List<string>(), null, t0.AddMinutes(10));
            var changes = plan.Changes.ToDictionary(c => c.Field, c => c.Value);
            Assert.AreEqual("1", changes[SyncMerge.Favorite], "Its favorite comes back.");
            Assert.AreEqual("Sofá", changes[SyncMerge.Collections]);
            Assert.IsFalse(SyncMerge.ToExport(plan.Snapshot)["g1"].Values.Any(v => v.T > SyncMerge.Initial), "Nothing is told as a change.");
        }

        [Test]
        public void SyncLaterChangesTest()
        {
            var t0 = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
            var local = new Dictionary<string, Dictionary<string, string>> { ["g1"] = Values(fav: "1") };
            var first = SyncMerge.Plan(local, null, null, null, t0);

            // Unfavorited here: it travels with a date.
            local["g1"][SyncMerge.Favorite] = "0";
            var second = SyncMerge.Plan(local, first.Snapshot, new[] { "g1" }, null, t0.AddMinutes(10));
            Assert.AreEqual(0, second.Changes.Count);
            var file = Export("A", second);
            Assert.AreEqual("0", file.Games["g1"][SyncMerge.Favorite].V);
            Assert.AreEqual(t0.AddMinutes(10), file.Games["g1"][SyncMerge.Favorite].T);

            // The other PC still had it as a favorite from before: the newer change wins there.
            var other = new Dictionary<string, Dictionary<string, string>> { ["g1"] = Values(fav: "1") };
            var otherSnapshot = SyncMerge.Plan(other, null, null, null, t0).Snapshot;
            var otherPlan = SyncMerge.Plan(other, otherSnapshot, new[] { "g1" }, new[] { file }, t0.AddMinutes(20));
            Assert.AreEqual("0", otherPlan.Changes.Single().Value);

            // An older remote value doesn't undo a newer local one.
            var stale = new SyncMachineFile { Machine = "C", Games = new Dictionary<string, Dictionary<string, SyncValue>> { ["g1"] = new Dictionary<string, SyncValue> { [SyncMerge.Favorite] = new SyncValue { V = "1", T = t0.AddMinutes(5) } } } };
            Assert.AreEqual(0, SyncMerge.Plan(local, second.Snapshot, new[] { "g1" }, new[] { stale }, t0.AddMinutes(30)).Changes.Count);

            // A collection written with other capitals is the same one: no change, nothing goes back and forth.
            var cats = new Dictionary<string, Dictionary<string, string>> { ["g1"] = Values(cats: "RPG") };
            var lower = new SyncMachineFile { Machine = "D", Games = new Dictionary<string, Dictionary<string, SyncValue>> { ["g1"] = new Dictionary<string, SyncValue> { [SyncMerge.Collections] = new SyncValue { V = "rpg", T = t0.AddMinutes(40) } } } };
            Assert.AreEqual(0, SyncMerge.Plan(cats, null, null, new[] { lower }, t0.AddMinutes(41)).Changes.Count);
            Assert.IsTrue(SyncMerge.Same(SyncMerge.Collections, "RPG\nSofá", "sofá\nrpg"));
            Assert.IsFalse(SyncMerge.Same(SyncMerge.Favorite, "1", "0"));
        }

        [Test]
        public void SyncNewGameTest()
        {
            var t0 = new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc);
            var remote = new SyncMachineFile { Machine = "B", Games = new Dictionary<string, Dictionary<string, SyncValue>> { ["g9"] = new Dictionary<string, SyncValue> { [SyncMerge.Hidden] = new SyncValue { V = "1", T = t0 } } } };

            // The game isn't in this library yet: nothing to change, but its value is remembered.
            var local = new Dictionary<string, Dictionary<string, string>>();
            var plan = SyncMerge.Plan(local, null, null, new[] { remote }, t0.AddMinutes(1));
            Assert.AreEqual(0, plan.Changes.Count);

            // It arrives with the next sync of the store: the dated value of the other PC wins over its default.
            local["g9"] = Values();
            var next = SyncMerge.Plan(local, plan.Snapshot, new List<string>(), new[] { remote }, t0.AddMinutes(11));
            Assert.AreEqual("1", next.Changes.Single().Value);
            Assert.AreEqual(SyncMerge.Hidden, next.Changes.Single().Field);
        }

        [Test]
        public void SyncTieTest()
        {
            Assert.AreEqual("1", SyncMerge.Tie(SyncMerge.Favorite, "0", "1"));
            Assert.AreEqual("0", SyncMerge.Tie(SyncMerge.Hidden, "0", "0"));
            Assert.AreEqual("A\nB\nC", SyncMerge.Tie(SyncMerge.Collections, "C\nA", "B\nA"));
            Assert.AreEqual(SyncMerge.Tie(SyncMerge.Collections, "RPG", "rpg"), SyncMerge.Tie(SyncMerge.Collections, "rpg", "RPG"));
            Assert.AreEqual(1, SyncMerge.Tie(SyncMerge.Collections, "RPG", "rpg").Split('\n').Length);
            Assert.AreEqual("80", SyncMerge.Tie(SyncMerge.Score, "80", "45"));
            Assert.AreEqual("Beaten", SyncMerge.Tie(SyncMerge.Status, "", "Beaten"));
            Assert.AreEqual(SyncMerge.Tie(SyncMerge.Status, "Playing", "Beaten"), SyncMerge.Tie(SyncMerge.Status, "Beaten", "Playing"));
            Assert.AreEqual("m|steam:1|" + new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks,
                SyncMerge.SessionId("m", new SyncSession { Game = "steam:1", Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) }));
        }

        [Test]
        public void SyncKeyTest()
        {
            var steam = new Game("A") { PluginId = SteamContent.SteamPluginId, GameId = "570" };
            Assert.AreEqual(SteamContent.SteamPluginId.ToString("N") + ":570", PcSync.GetKey(steam));
            Assert.AreEqual("custom:" + GameCaptures.Normalize("Mi Juego"), PcSync.GetKey(new Game("Mi Juego")));
            Assert.IsNull(PcSync.GetKey(new Game("")));
            Assert.IsNull(PcSync.GetKey(null));
        }

        [Test]
        public void RareTest()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            var list = new[]
            {
                new RareAchievement { GameId = a, Name = "a1", Percent = 0.5 },
                new RareAchievement { GameId = a, Name = "a2", Percent = 0.7 },
                new RareAchievement { GameId = a, Name = "a3", Percent = 0.9 },
                new RareAchievement { GameId = b, Name = "b1", Percent = 5 },
                new RareAchievement { GameId = b, Name = "b0", Percent = 0 },
                new RareAchievement { GameId = b, Name = "", Percent = 1 }
            };

            CollectionAssert.AreEqual(new[] { "a1", "a2", "b1" }, RareAchievements.Choose(list, 8).Select(c => c.Name).ToList());
            CollectionAssert.AreEqual(new[] { "a1" }, RareAchievements.Choose(list, 1).Select(c => c.Name).ToList());
        }
    }
}
