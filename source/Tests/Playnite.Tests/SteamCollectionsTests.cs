using NUnit.Framework;
using Playnite.Atrio;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class SteamCollectionsTests
    {
        // The shape of cloud-storage-namespace-1.json, with made-up collections.
        private const string Cloud = @"[
            [""user-collections.favorite"", {""key"":""user-collections.favorite"",""timestamp"":1,""value"":""{\""id\"":\""favorite\"",\""name\"":\""\"",\""added\"":[10,20,30],\""removed\"":[20]}"",""version"":""1""}],
            [""user-collections.hidden"", {""key"":""user-collections.hidden"",""timestamp"":1,""value"":""{\""id\"":\""hidden\"",\""name\"":\""\"",\""added\"":[99],\""removed\"":[]}"",""version"":""1""}],
            [""user-collections.uc-aaa"", {""key"":""user-collections.uc-aaa"",""timestamp"":1,""value"":""{\""id\"":\""uc-aaa\"",\""name\"":\""Roguelikes\"",\""added\"":[1,2,2,3],\""removed\"":[]}"",""version"":""3""}],
            [""user-collections.uc-bbb"", {""key"":""user-collections.uc-bbb"",""timestamp"":1,""is_deleted"":true,""version"":""4""}],
            [""user-collections.uc-ccc"", {""key"":""user-collections.uc-ccc"",""timestamp"":1,""value"":""{\""id\"":\""uc-ccc\"",\""name\"":\""Sin jugar\"",\""filterSpec\"":{\""nFormatVersion\"":2}}"",""version"":""5""}],
            [""user-collections.partner-ea-access"", {""key"":""user-collections.partner-ea-access"",""timestamp"":1,""value"":""{\""id\"":\""partner-ea-access\"",\""name\"":\""EA Play\""}"",""version"":""6""}],
            [""showcases.1"", {""key"":""showcases.1"",""timestamp"":1,""value"":""{}"",""version"":""7""}]
        ]";

        [Test]
        public void ParseKeepsThePlayersCollectionsAndFavoritesTest()
        {
            var collections = SteamCollections.Parse(Cloud);
            CollectionAssert.AreEqual(new[] { "favorite", "uc-aaa", "uc-ccc" }, collections.Select(a => a.Id));

            var favorites = collections[0];
            Assert.IsTrue(favorites.IsFavorites);
            CollectionAssert.AreEqual(new[] { "10", "30" }, favorites.AppIds, "the removed ones are out");

            var roguelikes = collections.Single(a => a.Id == "uc-aaa");
            Assert.AreEqual("Roguelikes", roguelikes.Name);
            Assert.IsFalse(roguelikes.IsDynamic);
            CollectionAssert.AreEqual(new[] { "1", "2", "3" }, roguelikes.AppIds, "each game once");

            var dynamic = collections.Single(a => a.Id == "uc-ccc");
            Assert.IsTrue(dynamic.IsDynamic);
            Assert.AreEqual(0, dynamic.AppIds.Count);
        }

        [Test]
        public void ParseOfAnEmptyListTest()
        {
            Assert.AreEqual(0, SteamCollections.Parse("[]").Count);
        }

        [Test]
        public void PersonaNamesByAccountNumberTest()
        {
            const string vdf = "\"users\"\n{\n\t\"76561198000000001\"\n\t{\n\t\t\"AccountName\"\t\t\"login1\"\n\t\t\"PersonaName\"\t\t\"Jugador Uno\"\n\t}\n" +
                               "\t\"76561198000000002\"\n\t{\n\t\t\"AccountName\"\t\t\"login2\"\n\t}\n}\n";
            var names = SteamCollections.ParsePersonaNames(vdf);
            Assert.AreEqual(1, names.Count);
            Assert.AreEqual("Jugador Uno", names[(76561198000000001UL - 76561197960265728UL).ToString()]);
            Assert.AreEqual(0, SteamCollections.ParsePersonaNames(null).Count);
        }

        [Test]
        public void NoSteamFolderMeansNoAccountsTest()
        {
            Assert.AreEqual(0, SteamCollections.Read(null).Count);
            Assert.AreEqual(0, SteamCollections.Read(@"C:\no-existe-steam").Count);
        }
    }
}
