using NUnit.Framework;
using Playnite.Atrio;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class SteamMarkupTests
    {
        [Test]
        public void TextAndListsTest()
        {
            Assert.AreEqual("<h2>Gameplay</h2><ul><li>One</li><li>Two <b>now</b></li></ul>", SteamMarkup.ToHtml("[h2]Gameplay[/h2]\n[list]\n[*]One\n[*]Two [b]now[/b]\n[/list]\n"));
            Assert.AreEqual("<ul><li><p>One</p></li><li><p>Two</p></li></ul>", SteamMarkup.ToHtml("[list][*][p]One[/p][/*][*][p]Two[/p][/*][/list]"));
            Assert.AreEqual("First<br><br>Second", SteamMarkup.ToHtml("First\n\nSecond"));
            Assert.AreEqual("<ul><li>A<ul><li>B</li><li>C</li></ul></li><li>D</li></ul>", SteamMarkup.ToHtml("[list][*]A[list][*]B[*]C[/list][*]D[/list]"));
            Assert.AreEqual("<ul><li>Alone</li></ul>", SteamMarkup.ToHtml("[*]Alone"));
        }

        [Test]
        public void WordsInBracketsStayTest()
        {
            Assert.AreEqual("[PC] Fixed [1-8] and [Pal Category]", SteamMarkup.ToHtml("[PC] Fixed [1-8] and [Pal Category]"));
            Assert.AreEqual("<b>Mozambique [survival pack]</b>", SteamMarkup.ToHtml("[b]Mozambique \\[survival pack][/b]"));
            Assert.AreEqual("[PC] Fixed", SteamMarkup.ToText("[b][PC][/b] Fixed"));
            Assert.AreEqual("C:\\Users\\[username]\\Documents", SteamMarkup.ToText("C:\\Users\\\\[username]\\Documents").Replace("\\\\", "\\"));
            Assert.AreEqual("An [S Rank] with [C] and [I am]", SteamMarkup.ToHtml("An [S Rank] with [C] and [I am]"));
            Assert.AreEqual("<s>old</s> <p>x</p>", SteamMarkup.ToHtml("[s]old[/s] [p align=\"left\"]x[/p]"));
        }

        [Test]
        public void NothingOfThePostRunsTest()
        {
            Assert.AreEqual("&lt;script&gt;alert(1)&lt;/script&gt;", SteamMarkup.ToHtml("<script>alert(1)</script>"));
            Assert.AreEqual("click", SteamMarkup.ToHtml("[url=javascript:alert(1)]click[/url]"));
            Assert.AreEqual(string.Empty, SteamMarkup.ToHtml("[img]javascript:alert(1)[/img]"));
            Assert.AreEqual("<a href=\"https://x.com/?a=1&amp;b=&quot;2\">x</a>".Replace("&quot;", "%22"), SteamMarkup.ToHtml("[url=https://x.com/?a=1&b=%222]x[/url]"));
            Assert.IsNull(SteamMarkup.SafeUrl("file:///c:/windows"));
            Assert.AreEqual("https://x.com/", SteamMarkup.SafeUrl("//x.com"));
            Assert.AreEqual("https://x.com/a", SteamMarkup.SafeUrl("x.com/a"));
        }

        [Test]
        public void PicturesLinksAndVideosTest()
        {
            Assert.AreEqual("<img src=\"https://clan.akamai.steamstatic.com/images/1/a.png\">", SteamMarkup.ToHtml("[img]{STEAM_CLAN_IMAGE}/1/a.png[/img]"));
            Assert.AreEqual("<img src=\"https://clan.akamai.steamstatic.com/images/1/a.png\">", SteamMarkup.ToHtml("[img src=\"{STEAM_CLAN_IMAGE}/1/a.png\" align=\"left\"][/img]"));
            Assert.AreEqual("<a href=\"https://www.expedition33.com/merch\">Merch Page</a>", SteamMarkup.ToHtml("[url=\"https://www.expedition33.com/merch \" style=\"pill\" buttoncolor=\"#ffffff\"]Merch Page[/url]"));
            Assert.AreEqual("<a href=\"https://x.com/\">https://x.com</a>", SteamMarkup.ToHtml("[url]https://x.com[/url]"));
            Assert.AreEqual(
                "<div class=\"video\"><a href=\"https://www.youtube.com/watch?v=JxYvzYGXYXI\"><img src=\"https://i.ytimg.com/vi/JxYvzYGXYXI/hqdefault.jpg\"><br>&#9654; Watch</a></div>",
                SteamMarkup.ToHtml("[previewyoutube=JxYvzYGXYXI;full][/previewyoutube]", "Watch"));
            Assert.AreEqual(
                "<a href=\"https://store.steampowered.com/app/990080/Hogwarts_Legacy\">Hogwarts Legacy</a>",
                SteamMarkup.ToHtml("[dynamiclink href=\"https://store.steampowered.com/app/990080/Hogwarts_Legacy/\"][/dynamiclink]").Replace("Legacy/\"", "Legacy\""));
            Assert.AreEqual("https://clan.akamai.steamstatic.com/images/1/b.jpg", SteamMarkup.GetFirstImage("Hello [img]{STEAM_CLAN_IMAGE}/1/b.jpg[/img] [img]{STEAM_CLAN_IMAGE}/1/c.jpg[/img]"));
            Assert.IsNull(SteamMarkup.GetFirstImage("Hello [b]you[/b]"));
        }

        [Test]
        public void BrokenMarkupIsClosedTest()
        {
            Assert.AreEqual("<b>bold <i>both</i></b>", SteamMarkup.ToHtml("[b]bold [i]both[/b]"));
            Assert.AreEqual("<ul><li>open</li></ul>", SteamMarkup.ToHtml("[list][*]open"));
            Assert.AreEqual("text", SteamMarkup.ToHtml("text[/b][/list]"));
            Assert.AreEqual("unfinished", SteamMarkup.ToHtml("[url=https://x.com]unfinished").Replace("<a href=\"https://x.com/\">", string.Empty).Replace("</a>", string.Empty));
            Assert.AreEqual(string.Empty, SteamMarkup.ToHtml(null));
        }

        [Test]
        public void ExcerptTest()
        {
            Assert.AreEqual("Title Text and more", SteamMarkup.ToText("[h1]Title[/h1]\n[img]{STEAM_CLAN_IMAGE}/1/a.png[/img]\nText [i]and[/i]\n\nmore"));
            Assert.AreEqual("One two…", SteamMarkup.GetExcerpt("One two three four", 9));
            Assert.AreEqual("The end of … is here", SteamMarkup.ToText("The end of [spoiler]someone[/spoiler] is here"));
            Assert.AreEqual("a & b", SteamMarkup.HtmlToText("<p>a &amp;<br>b</p>"));
        }
    }

    [TestFixture]
    public class StoreActivityTests
    {
        private const string Events = @"{""success"":1,""events"":[
            {""gid"":""5001"",""clan_steamid"":""103582791470526475"",""event_name"":"" Patch 1.2 "",""event_type"":12,""appid"":10,""rtime32_start_time"":1793353320,""votes_up"":55,""comment_count"":0,
             ""jsondata"":""{\""localized_summary\"":[\""English summary\"",null,null,null,null,\""Resumen\""],\""localized_capsule_image\"":[\""en.png\"",null,null,null,null,null],\""localized_title_image\"":[null]}"",
             ""announcement_body"":{""posttime"":1793353140,""commentcount"":8,""body"":""[list][*][p]Fixed [PC] crash[/p][/*][/list]""}},
            {""gid"":""5002"",""clan_steamid"":""103582791470526475"",""event_name"":""Old one"",""event_type"":999,""rtime32_start_time"":0,""jsondata"":""[]"",
             ""announcement_body"":{""posttime"":1600000000,""body"":""Hello [img]{STEAM_CLAN_IMAGE}/41005067/x.jpg[/img]""}}
        ]}";

        [Test]
        public void SteamEventsTest()
        {
            var page = StoreActivity.ParseSteam(Events, "10", 20, "spanish", "Vídeo");
            Assert.AreEqual(2, page.Posts.Count);
            Assert.AreEqual(22, page.Next);
            Assert.IsFalse(page.HasMore);
            var patch = page.Posts[0];
            Assert.AreEqual(ActivityKind.Patch, patch.Kind);
            Assert.IsTrue(patch.IsPatch);
            Assert.AreEqual("Patch 1.2", patch.Title);
            Assert.AreEqual("Resumen", patch.Summary);
            Assert.AreEqual("<ul><li><p>Fixed [PC] crash</p></li></ul>", patch.Html);
            Assert.AreEqual("https://clan.akamai.steamstatic.com/images/41005067/en.png", patch.ImageUrl, "Without a picture in the language, the English one.");
            Assert.AreEqual("https://store.steampowered.com/news/app/10/view/5001", patch.Url);
            Assert.AreEqual(55, patch.Likes);
            Assert.AreEqual(8, patch.Comments);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1793353140).LocalDateTime, patch.Date, "When it was published: an event may start weeks later.");
            Assert.AreEqual("https://clan.akamai.steamstatic.com/images/41005067/en_400x225.png", patch.ThumbnailUrl);

            var old = page.Posts[1];
            Assert.AreEqual(ActivityKind.News, old.Kind, "A kind that isn't known is news.");
            Assert.AreEqual("Hello", old.Summary);
            Assert.AreEqual("https://clan.akamai.steamstatic.com/images/41005067/x.jpg", old.ImageUrl);
            Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1600000000).LocalDateTime, old.Date);
        }

        [Test]
        public void SteamLanguagesTest()
        {
            Assert.AreEqual("spanish", StoreActivity.GetSteamLanguage("es_ES"));
            Assert.AreEqual("latam", StoreActivity.GetSteamLanguage("es_419"));
            Assert.AreEqual("brazilian", StoreActivity.GetSteamLanguage("pt_BR"));
            Assert.AreEqual("portuguese", StoreActivity.GetSteamLanguage("pt_PT"));
            Assert.AreEqual("schinese", StoreActivity.GetSteamLanguage("zh_CN"));
            Assert.AreEqual("english", StoreActivity.GetSteamLanguage("english"));
            Assert.AreEqual("english", StoreActivity.GetSteamLanguage(null));
        }

        [Test]
        public void GogChangelogIsSplitByVersionTest()
        {
            var html = "<h4>5.01 PATCH OCTOBER 8TH, 2026</h4><p>Out now.</p><h4><strong>Gameplay</strong></h4><ul><li>One</li></ul><h4>DirectX 12</h4><ul><li>Two</li></ul>" +
                "<h4>Patch 4.04 (11 July 2024)</h4><p>Older</p><script>alert(1)</script><h4>Update 2</h4><p onclick=\"x()\">Oldest</p>";
            var posts = StoreActivity.ParseChangelog(html, "The game", "https://www.gog.com/x", "gog-1");
            Assert.AreEqual(3, posts.Count);
            Assert.AreEqual("5.01 PATCH OCTOBER 8TH, 2026", posts[0].Title);
            Assert.AreEqual(new DateTime(2026, 10, 8), posts[0].Date);
            Assert.AreEqual("Out now. Gameplay One DirectX 12 Two", posts[0].Summary);
            Assert.AreEqual(new DateTime(2024, 7, 11), posts[1].Date);
            Assert.AreEqual("<p>Older</p>", posts[1].Html);
            Assert.AreEqual("Update 2", posts[2].Title);
            Assert.IsNull(posts[2].Date);
            Assert.AreEqual("<p>Oldest</p>", posts[2].Html.Replace("<p >", "<p>"));
            Assert.IsTrue(posts.All(a => a.Kind == ActivityKind.Patch));

            // What goes before the first one isn't lost.
            var intro = StoreActivity.ParseChangelog("<p>The last notes are on our web</p>" + html, "The game", null, "gog-1");
            Assert.AreEqual(4, intro.Count);
            Assert.AreEqual("The last notes are on our web", intro[0].Summary);

            // Without titles it's one change, with the name of the game.
            var single = StoreActivity.ParseChangelog("<p>Only this</p>", "The game", null, "gog-2");
            Assert.AreEqual(1, single.Count);
            Assert.AreEqual("The game", single[0].Title);
            Assert.AreEqual(0, StoreActivity.ParseChangelog("<p> </p>", "The game", null, "gog-3").Count);
            Assert.AreEqual(0, StoreActivity.ParseChangelog(null, "The game", null, "gog-4").Count);
        }

        [Test]
        public void DatesInTitlesTest()
        {
            Assert.AreEqual(new DateTime(2026, 9, 1), StoreActivity.FindDate("Hotfix Sept 1, 2026"));
            Assert.AreEqual(new DateTime(2024, 3, 15), StoreActivity.FindDate("Update 2024-03-15"));
            Assert.AreEqual(new DateTime(2019, 12, 2), StoreActivity.FindDate("2nd of December 2019"));
            Assert.IsNull(StoreActivity.FindDate("Patch 12, 2024"));
            Assert.IsNull(StoreActivity.FindDate("Update 03/04/2024"), "It could be March or April.");
            Assert.IsNull(StoreActivity.FindDate("February 31, 2024"));
        }

        [Test]
        public void MicrosoftNotesTest()
        {
            var posts = StoreActivity.ParseMicrosoft(@"{""Payload"":{""Title"":""A"",""LastUpdateDateUtc"":""2026-11-20T22:02:54Z"",""Notes"":[""Fixed <things>.\r\nAnd more.""]}}", "9X", "What's new");
            Assert.AreEqual(1, posts.Count);
            Assert.AreEqual("What's new", posts[0].Title);
            Assert.AreEqual("Fixed &lt;things&gt;.<br>And more.", posts[0].Html);
            Assert.AreEqual("Fixed <things>. And more.", posts[0].Summary);
            Assert.IsNull(posts[0].Date, "The date of the catalog is of any change in it.");
            Assert.AreEqual(0, StoreActivity.ParseMicrosoft(@"{""Payload"":{""Title"":""A""}}", "9X", "What's new").Count);
        }

        [Test]
        public void ItchDevlogTest()
        {
            var posts = StoreActivity.ParseRss(@"<?xml version=""1.0""?><rss version=""2.0""><channel><title>X</title>
                <item><guid>https://a.itch.io/g/devlog/1</guid><title>Version 1.2</title><link>https://a.itch.io/g/devlog/1</link><pubDate>Mon, 18 Aug 2025 14:58:27 GMT</pubDate>
                <description><![CDATA[<p>All <b>we</b> did<img src=""https://img.itch.zone/a.png""></p>]]></description></item>
                <item><title>Hello</title><link>javascript:x</link><description></description></item></channel></rss>");
            Assert.AreEqual(2, posts.Count);
            Assert.AreEqual(ActivityKind.Patch, posts[0].Kind);
            Assert.AreEqual("All we did", posts[0].Summary);
            Assert.IsNull(posts[0].Html, "The feed only has the start: the whole post is on its page.");
            Assert.AreEqual("https://img.itch.zone/a.png", posts[0].ImageUrl);
            Assert.AreEqual(new DateTime(2025, 8, 18, 14, 58, 27, DateTimeKind.Utc), posts[0].Date.Value.ToUniversalTime());
            Assert.AreEqual(ActivityKind.News, posts[1].Kind);
            Assert.IsNull(posts[1].Url);
            Assert.Throws<System.Xml.XmlException>(() => StoreActivity.ParseRss("<!DOCTYPE a [<!ENTITY b SYSTEM \"file:///c:/x\">]><rss><channel><item><title>&b;</title></item></channel></rss>"));
        }

        [Test]
        public void BlizzardAndRiotTest()
        {
            var page = StoreActivity.ParseBlizzard(@"{""feed"":{""contentItems"":[
                {""properties"":{""newsId"":""1"",""title"":""Notas del parche 2.1"",""category"":""Noticias"",""summary"":""Uno"",""staticAsset"":{""imageUrl"":""//x.net/a.png""},""newsUrl"":""https://news.blizzard.com/1"",""lastUpdated"":""2026-11-10T17:00:00Z""}},
                {""properties"":{""newsId"":""2"",""title"":""Evento"",""newsUrl"":""https://news.blizzard.com/2""}}],""pagination"":{""hasNextPage"":true}}}", 24);
            Assert.AreEqual(2, page.Posts.Count);
            Assert.IsTrue(page.HasMore);
            Assert.AreEqual(26, page.Next);
            Assert.AreEqual(ActivityKind.Patch, page.Posts[0].Kind);
            Assert.AreEqual("https://x.net/a.png", page.Posts[0].ImageUrl);
            Assert.IsNull(page.Posts[0].Html);
            Assert.AreEqual(ActivityKind.News, page.Posts[1].Kind);

            // The ones after the first page come without the wrapping, and a page without anything is the last one.
            var after = StoreActivity.ParseBlizzard(@"{""contentItems"":[{""properties"":{""newsId"":""3"",""title"":""C"",""staticAsset"":null}}],""pagination"":{""hasNextPage"":true}}", 26);
            Assert.AreEqual(1, after.Posts.Count);
            Assert.AreEqual(27, after.Next);
            Assert.IsTrue(after.HasMore);
            Assert.IsFalse(StoreActivity.ParseBlizzard(@"{""contentItems"":[],""pagination"":{""hasNextPage"":true}}", 27).HasMore);
            Assert.IsFalse(StoreActivity.ParseBlizzard(@"{""contentItems"":null,""pagination"":null}", 27).HasMore);

            var riot = StoreActivity.ParseRiot(@"<html><script id=""__NEXT_DATA__"" type=""application/json"">{""props"":{""items"":[
                {""title"":""Versión 13.10"",""media"":{""url"":""https://x/a.jpg""},""action"":{""payload"":{""url"":""/es-es/news/game-updates/valorant-patch-notes-13-10""}},""analytics"":{""publishDate"":""2026-11-10T14:00:00.000Z""},""category"":{""machineName"":""game-updates""}},
                {""title"":""VCT"",""action"":{""payload"":{""url"":""https://other.com/a""}},""publishedAt"":""2026-11-12T14:00:00.000Z""},
                {""title"":{""a"":1},""action"":{""payload"":{""url"":""/x""}},""publishedAt"":""2026-11-12T14:00:00.000Z""}]}}</script></html>", "https://playvalorant.com");
            Assert.AreEqual(2, riot.Count);
            Assert.AreEqual("VCT", riot[0].Title);
            Assert.AreEqual(ActivityKind.Patch, riot[1].Kind);
            Assert.AreEqual("https://playvalorant.com/es-es/news/game-updates/valorant-patch-notes-13-10", riot[1].Url);
            var kinds = StoreActivity.ParseRiot(@"<script id=""__NEXT_DATA__"">{""a"":[
                {""title"":""26.23"",""action"":{""payload"":{""url"":""/es/news/game-updates/league-of-legends-patch-26-23-notes""}},""publishedAt"":""2026-11-18T14:00:00.000Z""},
                {""title"":""Integridad"",""action"":{""payload"":{""url"":""/es/news/game-updates/proteccion""}},""publishedAt"":""2026-11-17T14:00:00.000Z"",""category"":{""machineName"":""game-updates""}}]}</script>", "https://x.com");
            CollectionAssert.AreEqual(new[] { ActivityKind.Patch, ActivityKind.News }, kinds.Select(a => a.Kind));
            Assert.AreEqual(0, StoreActivity.ParseRiot("<html></html>", "https://playvalorant.com").Count);
        }

        [Test]
        public void SteamSearchTakesOnlyTheSameGameTest()
        {
            var json = @"{""items"":[{""type"":""app"",""name"":""Hades II"",""id"":1145350},{""type"":""app"",""name"":""HADES™"",""id"":1145360},{""type"":""sub"",""name"":""Hades"",""id"":5}]}";
            Assert.AreEqual("1145360", StoreActivity.ParseSteamSearch(json, "Hades"));
            Assert.IsNull(StoreActivity.ParseSteamSearch(json, "Hades III"));
            var editions = @"{""items"":[{""type"":""app"",""name"":""Fallout 3"",""id"":22300},{""type"":""app"",""name"":""Fallout 3: Game of the Year Edition"",""id"":22370}]}";
            Assert.AreEqual("22370", StoreActivity.ParseSteamSearch(editions, "Fallout 3: Game of the Year Edition"));
            Assert.IsNull(StoreActivity.ParseSteamSearch(@"{""total"":0,""items"":[]}", "Hades"));
        }

        [Test]
        public void NewsThatAreNotesOfAPatchTest()
        {
            ActivityKind kind(string title, int type = 28) => StoreActivity.ParseSteam(
                $@"{{""events"":[{{""gid"":""1"",""event_name"":""{title}"",""event_type"":{type},""announcement_body"":{{""posttime"":1600000000,""body"":""x""}}}}]}}", "10", 0, "spanish", "v").Posts[0].Kind;
            Assert.AreEqual(ActivityKind.Patch, kind("Apex Legends: Notas del parche de Encriptación"));
            Assert.AreEqual(ActivityKind.Patch, kind("Patch Notes 1.2"));
            Assert.AreEqual(ActivityKind.Patch, kind("Hotfix for the hotfix", 999));
            Assert.AreEqual(ActivityKind.News, kind("A patch of land"));
            Assert.AreEqual(ActivityKind.News, kind("We are in 2026"));
            Assert.AreEqual(ActivityKind.Sale, kind("Patch notes of the sale", 20), "What Steam says goes first.");
        }

        [Test]
        public void ReleaseYearTest()
        {
            Assert.AreEqual(2022, StoreActivity.ParseReleaseYear(@"{""1593500"":{""success"":true,""data"":{""release_date"":{""coming_soon"":false,""date"":""Jan 14, 2022""}}}}"));
            Assert.IsNull(StoreActivity.ParseReleaseYear(@"{""1"":{""success"":true,""data"":{""release_date"":{""coming_soon"":true,""date"":""Coming soon""}}}}"));
            Assert.IsNull(StoreActivity.ParseReleaseYear(@"{""1"":{""success"":false}}"));
            Assert.IsTrue(StoreActivity.IsSameGame(2018, 2022), "God of War came to Steam four years later.");
            Assert.IsTrue(StoreActivity.IsSameGame(2019, 2024));
            Assert.IsFalse(StoreActivity.IsSameGame(2005, 2018), "The one of PlayStation 2 is another.");
            Assert.IsFalse(StoreActivity.IsSameGame(2017, 2006), "And so is an older one.");
        }

        private static Game Copy(string plugin, string id, string name = "Game")
        {
            return new Game(name) { PluginId = Guid.Parse(plugin), GameId = id };
        }

        private const string Steam = "CB91DFC9-B977-43BF-8E70-55F46E410FAB";
        private const string Epic = "00000002-DBD1-46C6-B5D0-B1BA559D10E4";
        private const string Gog = "AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E";

        [Test]
        public void SourcesFollowTheCopiesTest()
        {
            var sources = StoreActivity.GetSources(new[] { Copy(Epic, "abc"), Copy(Steam, "570"), Copy(Gog, "1495134320") }, a => a.PluginId.ToString());
            Assert.AreEqual(3, sources.Count);
            Assert.IsFalse(sources[0].HasOwn, "Epic publishes nothing for each game.");
            Assert.AreEqual("570", sources[0].SteamAppId, "The same game is on Steam: its number needs no search.");
            Assert.AreEqual("steam", sources[1].OwnKind);
            Assert.AreEqual("gog", sources[2].OwnKind);
            Assert.AreEqual("gog-1495134320", sources[2].Key);

            // A mod of Steam has a number that doesn't fit, and there's nothing to ask about it.
            Assert.IsNull(StoreActivity.GetSources(new[] { Copy(Steam, "12345678901234567890") }, a => "Steam")[0].OwnKind);
            Assert.AreEqual(0, StoreActivity.GetSources(null, a => "x").Count);

            var itch = Copy("00000001-EBB2-4EEC-ABCB-7C89937A42BB", "944");
            Assert.IsNull(StoreActivity.GetSources(new[] { itch }, a => "itch")[0].OwnKind);
            itch.Links = new ObservableCollection<Link> { new Link("Store", "https://author.itch.io/game?x=1") };
            Assert.AreEqual("itch", StoreActivity.GetSources(new[] { itch }, a => "itch")[0].OwnKind);

            Assert.AreEqual("riot", StoreActivity.GetSources(new[] { Copy("91d13c6f-63d3-42ed-a100-6f811a8387ea", "rg-valorant") }, a => "Riot")[0].OwnKind);
            Assert.AreEqual("battlenet", StoreActivity.GetSources(new[] { Copy("E3C26A3D-D695-4CB7-A769-5FF7612C7EDD", "WTCG") }, a => "Battle.net")[0].OwnKind);
            Assert.IsNull(StoreActivity.GetSources(new[] { Copy("E3C26A3D-D695-4CB7-A769-5FF7612C7EDD", "ODIN") }, a => "Battle.net")[0].OwnKind);
        }
    }
}
