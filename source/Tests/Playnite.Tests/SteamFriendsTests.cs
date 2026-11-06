using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.Collections.Generic;

namespace Playnite.Tests
{
    [TestFixture]
    public class SteamFriendsTests
    {
        private const string Token = "aaaaaaaaaaaaaaaaaaaa.bbbbbbbbbbbbbbbbbbbb.cccccccccc-_";
        private const string StoreConfig = "https://store.steampowered.com/pointssummary/ajaxgetasyncconfig";
        private const string CommunityAccount = "https://steamcommunity.com/my/edit/info";

        private static Func<string, SteamSessionPage> Pages(Dictionary<string, SteamSessionPage> pages)
        {
            return url => pages[url];
        }

        [Test]
        public void TokenIsFoundInEveryFormOfThePagesTest()
        {
            Assert.AreEqual(Token, SteamFriends.ParseAccessToken("{\"success\":1,\"data\":{\"webapi_token\":\"" + Token + "\"}}"));
            Assert.AreEqual(Token, SteamFriends.ParseAccessToken("<div data-loyalty_webapi_token=\"&quot;" + Token + "&quot;\">"));
            Assert.AreEqual(Token, SteamFriends.ParseAccessToken("&quot;webapi_token&quot;:&quot;" + Token + "&quot;"));
            Assert.AreEqual(Token, SteamFriends.ParseAccessToken("{\\\"strWebAPIToken\\\":\\\"" + Token + "\\\"}"));
            Assert.IsNull(SteamFriends.ParseAccessToken("{\"success\":1,\"data\":[]}"));
            Assert.IsNull(SteamFriends.ParseAccessToken(null));
        }

        [Test]
        public void SessionFromTheStoreIsEnoughTest()
        {
            var session = SteamFriends.ReadSession(Pages(new Dictionary<string, SteamSessionPage>
            {
                { StoreConfig, new SteamSessionPage { Address = StoreConfig, Source = "\"webapi_token\":\"" + Token + "\"" } }
            }));

            Assert.AreEqual(SteamFriendsState.Ready, session.State);
            Assert.AreEqual(Token, session.AccessToken);
        }

        [Test]
        public void SessionFromTheCommunityIsTheSecondChoiceTest()
        {
            var session = SteamFriends.ReadSession(Pages(new Dictionary<string, SteamSessionPage>
            {
                { StoreConfig, new SteamSessionPage { Address = StoreConfig, Source = "{\"success\":1,\"data\":[]}" } },
                { CommunityAccount, new SteamSessionPage { Address = CommunityAccount, Source = "data-loyalty_webapi_token=\"&quot;" + Token + "&quot;\"" } }
            }));

            Assert.AreEqual(SteamFriendsState.Ready, session.State);
        }

        [Test]
        public void NoSessionMeansSigningInTest()
        {
            var session = SteamFriends.ReadSession(Pages(new Dictionary<string, SteamSessionPage>
            {
                { StoreConfig, new SteamSessionPage { Address = StoreConfig, Source = "{\"success\":1,\"data\":[]}" } },
                { CommunityAccount, new SteamSessionPage { Address = "https://steamcommunity.com/login/home/?goto=%2Fmy", Source = "<html></html>" } }
            }));

            Assert.AreEqual(SteamFriendsState.SignedOut, session.State);
            Assert.IsNull(session.AccessToken);
        }

        [Test]
        public void SessionThatStopsAtAnotherPageIsLockedTest()
        {
            var session = SteamFriends.ReadSession(Pages(new Dictionary<string, SteamSessionPage>
            {
                { StoreConfig, new SteamSessionPage { Address = "https://store.steampowered.com/parental", Source = "<html></html>" } },
                { CommunityAccount, new SteamSessionPage { Address = "https://steamcommunity.com/parental", Source = "<html></html>" } }
            }));

            Assert.AreEqual(SteamFriendsState.Locked, session.State);
        }

        [Test]
        public void BrowserThatFailsLeavesThePageAsItIsTest()
        {
            var session = SteamFriends.ReadSession(_ => throw new InvalidOperationException());
            Assert.AreEqual(SteamFriendsState.Failed, session.State);
        }

        [Test]
        public void EachFriendGoesToTheMostRecentGroupTest()
        {
            var result = SteamFriends.ParseGameplayInfo(@"{""response"":{
                ""your_info"":{""steamid"":""1""},
                ""in_game"":[{""steamid"":""10"",""minutes_played"":30,""minutes_played_forever"":100}],
                ""played_recently"":[{""steamid"":""20"",""minutes_played"":5},{""steamid"":""21"",""minutes_played"":50},{""steamid"":""10"",""minutes_played"":30}],
                ""played_ever"":[{""steamid"":""30"",""minutes_played_forever"":700},{""steamid"":""31"",""minutes_played_forever"":900}],
                ""owns"":[{""steamid"":""40""}]}}");

            Assert.AreEqual(SteamFriendsState.Ready, result.State);
            Assert.AreEqual(1, result.InGame.Count);
            Assert.AreEqual(2, result.PlayedRecently.Count);
            Assert.AreEqual("21", result.PlayedRecently[0].SteamId);
            Assert.AreEqual("31", result.PlayedBefore[0].SteamId);
            Assert.AreEqual(1, result.Owners.Count);
            Assert.AreEqual("https://steamcommunity.com/profiles/40", result.Owners[0].ProfileUrl);
        }

        [Test]
        public void NobodyPlayingIsNotAnErrorTest()
        {
            var result = SteamFriends.ParseGameplayInfo("{\"response\":{}}");
            Assert.AreEqual(SteamFriendsState.Ready, result.State);
            Assert.AreEqual(0, result.InGame.Count + result.PlayedRecently.Count + result.PlayedBefore.Count + result.Owners.Count);
        }

        [Test]
        public void AccountNumberComesFromTheSteamIdTest()
        {
            Assert.AreEqual("22202", SteamFriends.GetAccountId("76561197960287930"));
            Assert.IsNull(SteamFriends.GetAccountId("not a number"));
            Assert.IsNull(SteamFriends.GetAccountId("5"));
        }
    }
}
