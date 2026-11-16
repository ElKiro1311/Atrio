using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class FreeGamesTests
    {
        private static readonly DateTime now = new DateTime(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

        private static long Unix(DateTime date) => new DateTimeOffset(date).ToUnixTimeSeconds();

        [Test]
        public void EpicSaysStorePageAndPriceTest()
        {
            var json = @"{""data"":{""Catalog"":{""searchStore"":{""elements"":[
                {""title"":""Knightica"",""catalogNs"":{""mappings"":[{""pageSlug"":""knightica-3be45c""}]},""keyImages"":[{""type"":""OfferImageWide"",""url"":""https://x/k.jpg""}],
                 ""price"":{""totalPrice"":{""discountPrice"":0,""originalPrice"":1499,""fmtPrice"":{""originalPrice"":""14,99 US$"",""discountPrice"":""0""}}},
                 ""promotions"":{""promotionalOffers"":[{""promotionalOffers"":[{""startDate"":""2026-11-12T16:00:00.000Z"",""endDate"":""2026-11-19T16:00:00.000Z"",""discountSetting"":{""discountPercentage"":0}}]}],""upcomingPromotionalOffers"":[]}},
                {""title"":""Alien: Isolation"",""productSlug"":""alien-isolation"",""keyImages"":[],
                 ""price"":{""totalPrice"":{""discountPrice"":3999,""originalPrice"":3999,""fmtPrice"":{""originalPrice"":""39,99 US$""}}},
                 ""promotions"":{""promotionalOffers"":[],""upcomingPromotionalOffers"":[{""promotionalOffers"":[{""startDate"":""2026-11-19T16:00:00.000Z"",""endDate"":""2026-11-26T16:00:00.000Z"",""discountSetting"":{""discountPercentage"":0}}]}]}},
                {""title"":""Ghostrunner 2"",""price"":{""totalPrice"":{""discountPrice"":3999,""originalPrice"":3999}},
                 ""promotions"":{""promotionalOffers"":[],""upcomingPromotionalOffers"":[{""promotionalOffers"":[{""startDate"":""2026-12-10T17:00:00.000Z"",""endDate"":""2027-01-07T23:59:00.000Z"",""discountSetting"":{""discountPercentage"":20}}]}]}},
                {""title"":""Mystery Game"",""price"":{""totalPrice"":{""discountPrice"":0,""originalPrice"":0,""fmtPrice"":{""originalPrice"":""0""}}},
                 ""promotions"":{""promotionalOffers"":[],""upcomingPromotionalOffers"":[{""promotionalOffers"":[{""startDate"":""2026-11-26T16:00:00.000Z"",""endDate"":""2026-12-03T16:00:00.000Z"",""discountSetting"":{""discountPercentage"":0}}]}]}}
            ]}}}}";
            var games = FreeGames.Parse(json, now, "es-ES");
            Assert.AreEqual(3, games.Count);
            var knightica = games[0];
            Assert.AreEqual("Knightica", knightica.Title);
            Assert.AreEqual(FreeGame.Epic, knightica.Store);
            Assert.AreEqual("knightica-3be45c", knightica.StoreId);
            Assert.AreEqual(FreeGameKind.Keep, knightica.Kind);
            Assert.IsTrue(knightica.IsNow);
            Assert.AreEqual("14,99 US$", knightica.PriceText);
            Assert.AreEqual(new DateTime(2026, 11, 19, 16, 0, 0, DateTimeKind.Utc), knightica.End.Value);
            Assert.AreEqual(now, knightica.Seen);

            var alien = games[1];
            Assert.AreEqual("Alien: Isolation", alien.Title);
            Assert.IsFalse(alien.IsNow);
            Assert.AreEqual("https://store.epicgames.com/es-ES/p/alien-isolation", alien.Url);
            Assert.AreEqual("39,99 US$", alien.PriceText);

            // A game whose price Epic doesn't know yet says no price.
            Assert.AreEqual("Mystery Game", games[2].Title);
            Assert.IsNull(games[2].PriceText);
        }

        private static string SteamItems(params string[] items)
        {
            return @"{""response"":{""store_items"":[" + string.Join(",", items) + "]}}";
        }

        private const string Assets = @"""assets"":{""asset_url_format"":""steam/apps/APPID/${FILENAME}?t=1"",""main_capsule"":""abc/capsule_616x353.jpg"",""header"":""abc/header.jpg""}";

        [Test]
        public void SteamGiftsAndFreeWeekendsTest()
        {
            var ends = Unix(now.AddDays(2));
            var json = SteamItems(
                // Free to keep, as Steam says it: a way to buy it that is free and stays, until a day.
                @"{""item_type"":0,""id"":101,""success"":1,""visible"":true,""name"":""Kept Game"",""appid"":101,""store_url_path"":""app/101/Kept_Game"",
                  " + Assets.Replace("APPID", "101") + @",
                  ""best_purchase_option"":{""packageid"":5,""final_price_in_cents"":""0"",""original_price_in_cents"":""1999"",""formatted_final_price"":""Free"",""formatted_original_price"":""$19.99"",
                    ""discount_pct"":100,""is_free_to_keep"":true,""free_to_keep_ends"":" + ends + @",""active_discounts"":[{""discount_amount"":""1999"",""discount_end_date"":" + (ends + 60) + @"}]}}",
                // The whole price taken off, without the mark: also a gift, until the discount ends.
                @"{""id"":102,""success"":1,""name"":""Whole Discount"",""appid"":102,
                  ""best_purchase_option"":{""final_price_in_cents"":""0"",""original_price_in_cents"":""1499"",""formatted_original_price"":""$14.99"",""discount_pct"":100,
                    ""active_discounts"":[{""discount_end_date"":" + Unix(now.AddDays(3)) + @"}]}}",
                // A free weekend with a discount to buy it.
                @"{""id"":103,""success"":1,""name"":""Weekend Game"",""appid"":103,
                  ""free_weekend"":{""start_time"":" + Unix(now.AddDays(-1)) + @",""end_time"":" + Unix(now.AddDays(1)) + @"},
                  ""best_purchase_option"":{""final_price_in_cents"":""3499"",""original_price_in_cents"":""4999"",""formatted_final_price"":""$34.99"",""discount_pct"":30}}",
                // A free weekend that comes next week.
                @"{""id"":104,""success"":1,""name"":""Next Weekend"",""appid"":104,
                  ""free_weekend"":{""start_time"":" + Unix(now.AddDays(5)) + @",""end_time"":" + Unix(now.AddDays(8)) + @"},
                  ""best_purchase_option"":{""final_price_in_cents"":""999"",""formatted_final_price"":""$9.99""}}",
                // Over: neither shows.
                @"{""id"":105,""success"":1,""name"":""Old Weekend"",""appid"":105,""free_weekend"":{""start_time"":" + Unix(now.AddDays(-4)) + @",""end_time"":" + Unix(now.AddDays(-2)) + @"}}",
                @"{""id"":106,""success"":1,""name"":""Old Gift"",""appid"":106,""best_purchase_option"":{""is_free_to_keep"":true,""free_to_keep_ends"":" + Unix(now.AddHours(-1)) + @"}}",
                // Just cheaper, free to play, not visible, or not found: none is a gift.
                @"{""id"":107,""success"":1,""name"":""On Sale"",""appid"":107,""best_purchase_option"":{""final_price_in_cents"":""149"",""original_price_in_cents"":""2999"",""discount_pct"":95}}",
                @"{""id"":108,""success"":1,""name"":""Always Free"",""appid"":108,""is_free"":true}",
                @"{""id"":109,""success"":1,""visible"":false,""name"":""Hidden"",""appid"":109,""best_purchase_option"":{""is_free_to_keep"":true}}",
                @"{""id"":110,""success"":2,""appid"":110}",
                // The gift is another way to buy it, not the best one.
                @"{""id"":111,""success"":1,""name"":""Gift Option"",""appid"":111,
                  ""best_purchase_option"":{""final_price_in_cents"":""2999"",""formatted_final_price"":""$29.99""},
                  ""purchase_options"":[{""packageid"":9,""final_price_in_cents"":""0"",""original_price_in_cents"":""999"",""formatted_original_price"":""$9.99"",""discount_pct"":100,""is_free_to_keep"":true,""free_to_keep_ends"":" + ends + @"}]}");

            var games = FreeGames.ParseSteamItems(json, now, true);
            Assert.AreEqual(new[] { "Kept Game", "Whole Discount", "Weekend Game", "Next Weekend", "Gift Option" }, games.Select(a => a.Title).ToArray());

            var kept = games[0];
            Assert.AreEqual(FreeGame.Steam, kept.Store);
            Assert.AreEqual("101", kept.StoreId);
            Assert.AreEqual(FreeGameKind.Keep, kept.Kind);
            Assert.IsTrue(kept.IsNow);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(ends).UtcDateTime, kept.End.Value);
            Assert.AreEqual("$19.99", kept.PriceText);
            Assert.AreEqual("https://store.steampowered.com/app/101/Kept_Game", kept.Url);
            Assert.AreEqual("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/101/abc/capsule_616x353.jpg?t=1", kept.ImageUrl);

            Assert.AreEqual(now.AddDays(3), games[1].End.Value);
            Assert.AreEqual("$14.99", games[1].PriceText);
            Assert.AreEqual("https://store.steampowered.com/app/102/", games[1].Url);

            var weekend = games[2];
            Assert.AreEqual(FreeGameKind.Trial, weekend.Kind);
            Assert.IsTrue(weekend.IsNow);
            Assert.AreEqual(now.AddDays(-1), weekend.Start);
            Assert.AreEqual(now.AddDays(1), weekend.End.Value);
            Assert.AreEqual("$34.99", weekend.PriceText);

            Assert.AreEqual(FreeGameKind.Trial, games[3].Kind);
            Assert.IsFalse(games[3].IsNow);

            Assert.AreEqual(FreeGameKind.Keep, games[4].Kind);
            Assert.AreEqual("$9.99", games[4].PriceText);

            // Without the games to try, only the gifts.
            Assert.AreEqual(new[] { "Kept Game", "Whole Discount", "Gift Option" }, FreeGames.ParseSteamItems(json, now, false).Select(a => a.Title).ToArray());

            // A free weekend is never taken for a gift, even if Steam calls the game free for now.
            var both = SteamItems(@"{""id"":120,""success"":1,""name"":""Both"",""appid"":120,""is_free_temporarily"":true,
                ""free_weekend"":{""start_time"":" + Unix(now.AddDays(-1)) + @",""end_time"":" + Unix(now.AddDays(1)) + @"}}");
            Assert.AreEqual(FreeGameKind.Trial, FreeGames.ParseSteamItems(both, now, true).Single().Kind);
            Assert.AreEqual(0, FreeGames.ParseSteamItems(both, now, false).Count);

            // Nothing, or something else, is no list.
            Assert.AreEqual(0, FreeGames.ParseSteamItems("{}", now, true).Count);
            Assert.AreEqual(0, FreeGames.ParseSteamItems(@"{""response"":{}}", now, true).Count);
        }

        [Test]
        public void SteamListsTest()
        {
            var search = @"{""success"":1,""results_html"":""<a href=\""https://store.steampowered.com/app/2996430/Urban/\"" data-ds-appid=\""2996430\"" data-ds-itemkey=\""App_2996430\"">x</a>" +
                @"<a href=\""https://store.steampowered.com/sub/1/\"" data-ds-packageid=\""1\"" data-ds-appid=\""730,10\"">y</a>"",""total_count"":2}";
            Assert.AreEqual(new[] { "2996430", "730" }, FreeGames.ParseSteamSearchIds(search).ToArray());
            Assert.AreEqual(0, FreeGames.ParseSteamSearchIds(@"{""success"":1,""results_html"":""\n<!-- List Items -->\n"",""total_count"":0}").Count);

            var query = @"{""response"":{""metadata"":{""total_matching_records"":3},""ids"":[{""appid"":5558800},{""appid"":5559850},{""packageid"":1},{""appid"":5558800}]}}";
            Assert.AreEqual(new[] { "5558800", "5559850" }, FreeGames.ParseSteamQueryIds(query).ToArray());
            Assert.AreEqual(0, FreeGames.ParseSteamQueryIds(@"{""response"":{}}").Count);

            var featured = @"{""0"":{""id"":""cat_spotlight"",""name"":""Spotlights"",""items"":[{""name"":""Free Weekend"",""url"":""https://store.steampowered.com/app/949230""}]},
                ""1"":{""id"":""cat_spotlight"",""items"":[{""name"":""FRANCHISE SALE"",""url"":""https://store.steampowered.com/sale/COHFranchiseSale2026""}]},
                ""specials"":{""id"":""cat_specials"",""items"":[{""id"":2406770,""type"":0,""name"":""Bodycam""},{""id"":123,""type"":1,""name"":""A package""}]},
                ""genres"":{""id"":""cat_genres"",""items"":[]},""status"":1}";
            Assert.AreEqual(new[] { "949230", "2406770" }, FreeGames.ParseSteamFeaturedIds(featured).ToArray());
        }

        [Test]
        public void GogTest()
        {
            var catalog = @"{""productCount"":3,""products"":[
                {""id"":""1"",""slug"":""gift_game"",""title"":""Gift Game"",""storeLink"":""https://www.gog.com/en/game/gift_game"",""coverHorizontal"":""https://images.gog-statics.com/a.png"",
                 ""price"":{""final"":""$0.00"",""base"":""$9.99"",""discount"":""-100%"",""finalMoney"":{""amount"":""0.00"",""currency"":""USD""},""baseMoney"":{""amount"":""9.99"",""currency"":""USD""}}},
                {""id"":""2"",""slug"":""free_game"",""title"":""Free Game"",""price"":{""final"":""$0.00"",""base"":""$0.00"",""finalMoney"":{""amount"":""0.00""},""baseMoney"":{""amount"":""0.00""}}},
                {""id"":""3"",""slug"":""cheap"",""title"":""Cheap"",""price"":{""final"":""$0.49"",""base"":""$9.99"",""finalMoney"":{""amount"":""0.49""},""baseMoney"":{""amount"":""9.99""}}},
                {""id"":""4"",""slug"":""odd"",""title"":""Odd Link"",""storeLink"":""https://elsewhere.example/odd"",
                 ""price"":{""base"":""$5.00"",""finalMoney"":{""amount"":""0""},""baseMoney"":{""amount"":""5""}}}
            ]}";
            var games = FreeGames.ParseGogCatalog(catalog, now);
            Assert.AreEqual(new[] { "Gift Game", "Odd Link" }, games.Select(a => a.Title).ToArray());
            Assert.AreEqual(FreeGame.Gog, games[0].Store);
            Assert.AreEqual("1", games[0].StoreId);
            Assert.AreEqual("$9.99", games[0].PriceText);
            Assert.IsNull(games[0].End);
            Assert.AreEqual("https://www.gog.com/en/game/gift_game", games[0].Url);
            Assert.AreEqual("https://www.gog.com/en/game/odd", games[1].Url);

            // Without an end, shown for some hours after it was seen.
            Assert.IsTrue(FreeGames.IsCurrent(games[0], now.AddHours(11)));
            Assert.IsFalse(FreeGames.IsCurrent(games[0], now.AddHours(13)));

            var page = @"{""sections"":[{""sectionType"":""BIG_SPOT_V2_SECTION"",""sectionId"":""a""},{""sectionType"":""GIVEAWAY_SECTION"",""sectionId"":""b-c""}]}";
            Assert.AreEqual("b-c", FreeGames.ParseGogGiveawaySection(page));
            Assert.IsNull(FreeGames.ParseGogGiveawaySection(@"{""sections"":[{""sectionType"":""PRODUCTS_SECTION"",""sectionId"":""a""}]}"));

            var section = @"{""properties"":{""endDate"":""2026-11-17T13:00:00+00:00"",""product"":{""title"":""FlatOut"",""slug"":""flatout"",""coverHorizontal"":""https://images.gog-statics.com/f.png""}}}";
            var gift = FreeGames.ParseGogGiveaway(section, now);
            Assert.AreEqual("FlatOut", gift.Title);
            Assert.AreEqual("flatout", gift.StoreId);
            Assert.AreEqual(new DateTime(2026, 11, 17, 13, 0, 0, DateTimeKind.Utc), gift.End.Value);
            Assert.AreEqual(FreeGames.GogGiveawayPage, gift.Url);
            Assert.IsNull(FreeGames.ParseGogGiveaway(section, now.AddDays(3)));
            Assert.IsNull(FreeGames.ParseGogGiveaway(@"{""properties"":{}}", now));
        }

        private static string Item(string title, string link, DateTime published, string teaser, string image = "https://img.gg.deals/90/04/a_608xt.jpg?utm_source=rss")
        {
            return "<item><title>" + System.Security.SecurityElement.Escape(title) + "</title>" +
                "<description><![CDATA[<p><a href=\"" + link + "\"><img src=\"" + image.Replace("&", "&amp;") + "\" alt=\"x\" /></a></p>\n<p>" + teaser + "</p>]]></description>" +
                "<pubDate>" + published.ToString("ddd, dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " +0000</pubDate>" +
                "<link>" + link + "</link></item>";
        }

        [Test]
        public void GgDealsNewsTest()
        {
            var feed = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\"><channel><title>GG.deals - Game freebies</title>" +
                Item("New FREE games this weekend (November 14-15, 2026): your comprehensive roundup", "https://gg.deals/freebie/roundup/", now.AddDays(-1),
                    "Looking for something new to play this weekend? Plenty of fresh free games have popped up.") +
                Item("Battlefield 6 FREE trial makes a comeback with four days of nonstop shooting", "https://gg.deals/freebie/bf6/", now.AddDays(-2), "EA’s flagship FPS is yours to try until November 16.") +
                Item("The new Epic Games mobile FREEBIE probably looks very familiar", "https://gg.deals/freebie/mobile/", now.AddDays(-3), "Available to claim and keep on iOS and Android.") +
                Item("Epic Games is giving away a colorful autobattler worth $14.99 for FREE", "https://gg.deals/freebie/autobattler/", now.AddDays(-3), "Begin conquest.") +
                Item("The FREE Hellraiser: Revival demo is now available on consoles!", "https://gg.deals/freebie/demo/", now.AddDays(-3), "A demon of a demo.") +
                Item("Westlanders has a FREE Steam playtest for you to join today!", "https://gg.deals/freebie/playtest/", now.AddDays(-3), "Cowboys.") +
                Item("FREE Mail Mole Steam keys are ready to grab via Lenovo Gaming RIGHT NOW!", "https://gg.deals/freebie/keys/", now.AddDays(-4), "Complete these simple steps.") +
                Item("Grab Sadball FREE on Steam before it slides into the gutter!", "https://gg.deals/freebie/f2p/", now.AddDays(-5), "This free-to-play title will be delisted from Steam soon.") +
                Item("Raccoin’s FREE Chaos Max DLC is out on Steam", "https://gg.deals/freebie/dlc/", now.AddDays(-5), "Two new characters.") +
                Item("You can play For The King II FREE on Steam, but you'll need to be quick!", "https://gg.deals/freebie/ftk/", now.AddDays(-4), "Try out the new adventure completely free on Steam.") +
                Item("Another Itch.io freebie! This time, House Call is FREE to claim and keep!", "https://gg.deals/freebie/itch/", now.AddDays(-6), "You'll finish the job.") +
                Item("A new set of FREE Path of Exile 2 Twitch Drops awaits viewers", "https://gg.deals/freebie/drops/", now.AddDays(-6), "Rewards.") +
                Item("Soulframe has ditched the invites, letting all players in for FREE!", "https://gg.deals/freebie/soulframe/", now.AddDays(-6), "Soul far, soul good.") +
                Item("Anno 117: Pax Romana kicks off its FREE weekend alongside a new DLC launch", "https://gg.deals/freebie/anno/", now.AddDays(-6), "Play the game for free until November 9.") +
                Item("Steam FREE Weekend from last month", "https://gg.deals/freebie/old/", now.AddDays(-9), "Old.") +
                Item("Epic Games is giving away a game somewhere else", "https://elsewhere.example/x", now.AddDays(-1), "Not GG.deals.") +
                Item("Previously $15, this mystery is now FREE to keep on Steam!", "https://gg.deals/freebie/noir/", now.AddDays(-4), "VR.", "https://evil.example/i.jpg") +
                "</channel></rss>";

            var news = FreeGames.ParseNews(feed, now);
            Assert.AreEqual(new[]
            {
                "https://gg.deals/freebie/roundup/", "https://gg.deals/freebie/bf6/", "https://gg.deals/freebie/autobattler/",
                "https://gg.deals/freebie/keys/", "https://gg.deals/freebie/ftk/", "https://gg.deals/freebie/noir/",
                "https://gg.deals/freebie/itch/", "https://gg.deals/freebie/anno/"
            }, news.Select(a => a.Url).ToArray());

            Assert.IsNull(news[0].Kind);
            Assert.AreEqual(FreeGameKind.Trial, news[1].Kind);
            Assert.AreEqual(FreeGameKind.Keep, news[2].Kind);
            Assert.AreEqual(FreeGameKind.Keep, news[3].Kind);
            Assert.AreEqual(FreeGameKind.Trial, news[4].Kind);
            Assert.AreEqual(FreeGameKind.Keep, news[5].Kind);
            Assert.AreEqual(FreeGameKind.Keep, news[6].Kind);
            Assert.AreEqual(FreeGameKind.Trial, news[7].Kind);
            Assert.AreEqual(now.AddDays(-1), news[0].Published);
            Assert.AreEqual("https://img.gg.deals/90/04/a_608xt.jpg?utm_source=rss", news[0].ImageUrl);
            Assert.AreEqual("Battlefield 6 FREE trial makes a comeback with four days of nonstop shooting", news[1].Title);

            // "until November 16": the end of that day.
            Assert.IsNull(news[0].Ends);
            Assert.AreEqual(new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc), news[1].Ends);
            Assert.AreEqual(new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc), news[7].Ends);

            // Shown while it lasts, and the games to try only if the person wants them; without a date, a few days.
            var all = new FreeGamesOptions();
            var noTrials = new FreeGamesOptions { Trials = false };
            Assert.IsTrue(FreeGames.IsNewsVisible(news[1], now, all));
            Assert.IsFalse(FreeGames.IsNewsVisible(news[1], now, noTrials));
            Assert.IsFalse(FreeGames.IsNewsVisible(news[1], new DateTime(2026, 11, 17, 0, 0, 1, DateTimeKind.Utc), all));
            Assert.IsFalse(FreeGames.IsNewsVisible(news[7], now, all));
            Assert.IsTrue(FreeGames.IsNewsVisible(news[2], now, noTrials));
            Assert.IsTrue(FreeGames.IsNewsVisible(news[0], now, noTrials));
            Assert.AreEqual("https://gg.deals/freebie/ftk/", news[4].Url);
            Assert.IsNull(news[4].Ends);
            Assert.IsTrue(FreeGames.IsNewsVisible(news[4], now.AddDays(-1), all));
            Assert.IsFalse(FreeGames.IsNewsVisible(news[4], now, all));
            Assert.IsFalse(FreeGames.IsNewsVisible(news[2], now.AddDays(5), all));

            // An image from somewhere else isn't loaded.
            Assert.IsNull(news[5].ImageUrl);

            // A headline about a game that a store already shows goes.
            var games = new List<FreeGame> { new FreeGame { Title = "Mail Mole" }, new FreeGame { Title = "It" } };
            Assert.IsFalse(FreeGames.RemoveRepeated(news, games).Any(a => a.Url == "https://gg.deals/freebie/keys/"));
            Assert.AreEqual(news.Count - 1, FreeGames.RemoveRepeated(news, games).Count);

            // When Epic and Steam answered, their own gifts are in the cards: those headlines go, keys given by others stay.
            var left = FreeGames.RemoveRepeated(news, new List<FreeGame>(), new[] { FreeGame.Epic, FreeGame.Steam }).Select(a => a.Url).ToArray();
            Assert.AreEqual(new[]
            {
                "https://gg.deals/freebie/roundup/", "https://gg.deals/freebie/bf6/", "https://gg.deals/freebie/keys/",
                "https://gg.deals/freebie/ftk/", "https://gg.deals/freebie/itch/", "https://gg.deals/freebie/anno/"
            }, left);

            // If only Epic answered, the gift of the store of Steam stays.
            Assert.IsTrue(FreeGames.RemoveRepeated(news, null, new[] { FreeGame.Epic }).Any(a => a.Url == "https://gg.deals/freebie/noir/"));

            // A document with a DTD isn't read.
            Assert.Catch(() => FreeGames.ParseNews("<?xml version=\"1.0\"?><!DOCTYPE rss [<!ENTITY a \"b\">]><rss><channel></channel></rss>", now));
        }

        [Test]
        public void OrderTest()
        {
            var games = new List<FreeGame>
            {
                new FreeGame { Title = "Next", Store = FreeGame.Epic, StoreId = "n", Start = now.AddDays(4), End = now.AddDays(11), IsNow = false },
                new FreeGame { Title = "Trial", Store = FreeGame.Steam, StoreId = "1", Kind = FreeGameKind.Trial, Start = now.AddDays(-1), End = now.AddDays(1), IsNow = true },
                new FreeGame { Title = "Later gift", Store = FreeGame.Epic, StoreId = "l", Start = now.AddDays(-1), End = now.AddDays(5), IsNow = true },
                new FreeGame { Title = "Sooner gift", Store = FreeGame.Steam, StoreId = "2", Start = now.AddDays(-1), End = now.AddDays(2), IsNow = true },
                new FreeGame { Title = "No end", Store = FreeGame.Gog, StoreId = "3", Start = now, End = null, IsNow = true, Seen = now },
                new FreeGame { Title = "Sooner gift again", Store = FreeGame.Steam, StoreId = "2", Start = now.AddDays(-1), End = now.AddDays(2), IsNow = true }
            };

            Assert.AreEqual(new[] { "Sooner gift", "Later gift", "No end", "Trial", "Next" }, FreeGames.Sort(games).Select(a => a.Title).ToArray());
        }

        [Test]
        public void NewsClassifyTest()
        {
            Assert.IsTrue(FreeGames.ClassifyNews("Epic Games Store will hand out Alien: Isolation and one more game for FREE next week", "", out var kind));
            Assert.AreEqual(FreeGameKind.Keep, kind);
            Assert.IsTrue(FreeGames.ClassifyNews("Medieval adventure Penance is FREE on Itch.io for a very limited time!", "", out kind));
            Assert.AreEqual(FreeGameKind.Keep, kind);
            Assert.IsTrue(FreeGames.ClassifyNews("Call of Duty: Modern Warfare 4 - DMZ Free Trial is still live this week", "", out kind));
            Assert.AreEqual(FreeGameKind.Trial, kind);
            Assert.IsFalse(FreeGames.ClassifyNews("This FREE typing shooter is being delisted from Steam, so add it to your library while you still can!", "Dreamer's Anvil will be removing the game.", out kind));
            Assert.IsFalse(FreeGames.ClassifyNews("A FREE The Legend of California Steam playtest moseys into town tomorrow", "", out kind));
            Assert.IsFalse(FreeGames.ClassifyNews(null, null, out kind));

            // "Free to play" in the summary of a free weekend is how it's told, not a game that's always free.
            var weekend = "These titles are completely free to play on Steam right now, through Monday, November 9 at 18:00 UTC.";
            Assert.IsTrue(FreeGames.ClassifyNews("Steam's FREE Weekend is serving up wondrous worlds & tactical mayhem!", weekend, out kind));
            Assert.AreEqual(FreeGameKind.Trial, kind);
            Assert.AreEqual(new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc), FreeGames.ParseNewsEnd(weekend, new DateTime(2026, 11, 5, 19, 12, 0, DateTimeKind.Utc)));
            Assert.IsFalse(FreeGames.ClassifyNews("New free-to-play shooter Arena Zero is out now", "", out kind));
            Assert.IsFalse(FreeGames.ClassifyNews("Grab Sadball FREE on Steam", "This free-to-play title will be delisted.", out kind));

            // Without a mark of a free weekend in the title, "free to play" in the summary doesn't throw it out.
            Assert.IsTrue(FreeGames.ClassifyNews("You can play Anno 117 FREE on Steam", "It is completely free to play right now.", out kind));
            Assert.AreEqual(FreeGameKind.Trial, kind);
            Assert.IsTrue(FreeGames.ClassifyNews("Anno 117 is free to play on Steam this weekend", "", out kind));
            Assert.AreEqual(FreeGameKind.Trial, kind);

            // A game to try "for a limited time" is still a game to try, not a gift.
            Assert.IsTrue(FreeGames.ClassifyNews("Try Hunt: Showdown 1896 for FREE for a limited time on Steam", "", out kind));
            Assert.AreEqual(FreeGameKind.Trial, kind);
            Assert.IsTrue(FreeGames.ClassifyNews("You can play Hunt: Showdown 1896 FREE on Steam for a limited time!", "", out kind));
            Assert.AreEqual(FreeGameKind.Trial, kind);
            Assert.IsTrue(FreeGames.ClassifyNews("Penance is FREE on Itch.io for a very limited time!", "", out kind));
            Assert.AreEqual(FreeGameKind.Keep, kind);

            // And a clear gift stays a gift, even if its summary invites to try it.
            Assert.IsTrue(FreeGames.ClassifyNews("Epic Games is giving away Control for FREE", "Want to try this shooter for free?", out kind));
            Assert.AreEqual(FreeGameKind.Keep, kind);
            Assert.IsTrue(FreeGames.ClassifyNews("This roguelike is now FREE to keep on Steam!", "Try the roguelike for free while you can.", out kind));
            Assert.AreEqual(FreeGameKind.Keep, kind);
            Assert.IsFalse(FreeGames.ClassifyNews("New free-to-play shooter Arena Zero is out now", "Try the new hero shooter for free.", out kind));

            // The dates: a headline of December can end in January; a day that doesn't exist says nothing.
            var december = new DateTime(2026, 12, 28, 10, 0, 0, DateTimeKind.Utc);
            Assert.AreEqual(new DateTime(2027, 1, 4, 0, 0, 0, DateTimeKind.Utc), FreeGames.ParseNewsEnd("Free until January 3", december));
            Assert.AreEqual(new DateTime(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc), FreeGames.ParseNewsEnd("Claim it before 16:00 UTC on November 19.", now));
            Assert.AreEqual(new DateTime(2026, 11, 22, 0, 0, 0, DateTimeKind.Utc), FreeGames.ParseNewsEnd("The offer ends Nov. 21st", now));
            Assert.AreEqual(new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc), FreeGames.ParseNewsEnd("Claim it before 10 AM PT on Nov 20", now));
            Assert.AreEqual(new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc), FreeGames.ParseNewsEnd("Free until 11:59 PM EST on November 20", now));
            Assert.IsNull(FreeGames.ParseNewsEnd("Enjoy the weekend November 20", now));
            Assert.IsNull(FreeGames.ParseNewsEnd("Free until February 30", now));
            Assert.IsNull(FreeGames.ParseNewsEnd("Grab it before it slides into the gutter", now));
        }

        [Test]
        public void KeepPreviousTest()
        {
            var previous = new List<FreeGame>
            {
                new FreeGame { Title = "Kept", Store = FreeGame.Steam, StoreId = "1", End = now.AddDays(1) },
                new FreeGame { Title = "Over", Store = FreeGame.Steam, StoreId = "2", End = now.AddHours(-1) },
                new FreeGame { Title = "Lost", Store = FreeGame.Steam, StoreId = "3", End = now.AddDays(2) },
                new FreeGame { Title = "FlatOut", Store = FreeGame.Gog, StoreId = "flatout", End = now.AddDays(2) },
                new FreeGame { Title = "Epic one", Store = FreeGame.Epic, StoreId = "e", End = now.AddDays(2) },
                new FreeGame { Title = "Seen long ago", Store = FreeGame.Gog, StoreId = "9", End = null, Seen = now.AddDays(-1) }
            };

            // Steam answered in part: what it said stays as it said it, and what it said before and still lasts comes back.
            var steam = FreeGames.KeepPrevious(new[] { new FreeGame { Title = "Kept", Store = FreeGame.Steam, StoreId = "1" } }, previous, FreeGame.Steam, now);
            Assert.AreEqual(new[] { "Lost" }, steam.Select(a => a.Title).ToArray());

            // The same id with another name is the same game: Steam renamed it.
            Assert.AreEqual(new[] { "Lost" }, FreeGames.KeepPrevious(new[] { new FreeGame { Title = "Renamed", StoreId = "1" } }, previous, FreeGame.Steam, now).Select(a => a.Title).ToArray());
            Assert.AreEqual(0, FreeGames.KeepPrevious(new[] { new FreeGame { Title = "Lost again", StoreId = "3" }, new FreeGame { Title = "Kept", StoreId = "x" } }, previous, FreeGame.Steam, now).Count);

            // The gift of GOG has the id of its page, the catalog the number of the product: the name counts too.
            Assert.AreEqual(new[] { "FlatOut" }, FreeGames.KeepPrevious(null, previous, FreeGame.Gog, now).Select(a => a.Title).ToArray());
            Assert.AreEqual(0, FreeGames.KeepPrevious(new[] { new FreeGame { Title = "flatout", StoreId = "1207658" } }, previous, FreeGame.Gog, now).Count);

            // Lists saved before only had Epic, without the store.
            Assert.AreEqual(1, FreeGames.KeepPrevious(null, new[] { new FreeGame { Title = "Old", Store = null, End = now.AddDays(1) } }, FreeGame.Epic, now).Count);
        }
    }
}
