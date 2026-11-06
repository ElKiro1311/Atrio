using NUnit.Framework;
using Playnite.Atrio;
using Playnite.Database;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class GameCollectionsTests
    {
        private static readonly Guid steam = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");
        private static readonly Guid epic = Guid.Parse("00000002-DBD1-46C6-B5D0-B1BA559D10E4");
        private static readonly Guid collectionId = Guid.NewGuid();

        private static GameUnifier NewUnifier(params Game[] games)
        {
            var unifier = new GameUnifier(new AtrioSettings());
            unifier.Attach(() => games, null);
            unifier.Rebuild();
            return unifier;
        }

        private static GameCollection NewCollection()
        {
            return GameCollection.FromCategory(new Category("RPG") { Id = collectionId });
        }

        [Test]
        public void GameInTwoStoresIsInTheCollectionThroughAnyOfThemTest()
        {
            var first = new Game("Prey") { PluginId = steam };
            var second = new Game("Prey") { PluginId = epic, CategoryIds = new List<Guid> { collectionId } };
            var collection = NewCollection();

            Assert.IsTrue(collection.Contains(new[] { first, second }));
            Assert.IsFalse(collection.Contains(new[] { first }));
        }

        [Test]
        public void UncategorizedNeedsEveryCopyOutOfTheCollectionsTest()
        {
            var inside = new Game("Prey") { CategoryIds = new List<Guid> { collectionId } };
            var outside = new Game("Prey");
            var collection = GameCollection.CreateUncategorized("Sin colección");

            Assert.IsTrue(collection.Contains(new[] { outside }));
            Assert.IsFalse(collection.Contains(new[] { outside, inside }));
            Assert.IsTrue(collection.Contains(new[] { new Game("Doom") { CategoryIds = new List<Guid>() } }));
        }

        [Test]
        public void CollectionsThatAreAlwaysThereFollowTheirFieldTest()
        {
            var favorite = new Game("Prey") { Favorite = true };
            var hidden = new Game("Doom") { Hidden = true };

            Assert.IsTrue(GameCollection.CreateFavorites("Favoritos").Contains(new[] { favorite }));
            Assert.IsFalse(GameCollection.CreateFavorites("Favoritos").Contains(new[] { hidden }));
            Assert.IsTrue(GameCollection.CreateHidden("Ocultos").Contains(new[] { hidden }));
            Assert.IsTrue(GameCollection.CreateHidden("Ocultos").ShowsHiddenGames);
            Assert.IsFalse(GameCollection.CreateFavorites("Favoritos").ShowsHiddenGames);
        }

        [Test]
        public void OnlyCollectionsFilledByHandTakeGamesTest()
        {
            Assert.IsTrue(NewCollection().AcceptsGames);
            Assert.IsTrue(GameCollection.CreateFavorites("Favoritos").AcceptsGames);
            Assert.IsFalse(GameCollection.CreateUncategorized("Sin colección").AcceptsGames);
            Assert.IsFalse(GameCollection.CreateHidden("Ocultos").AcceptsGames);
            Assert.IsFalse(new GameCollection(Guid.NewGuid(), GameCollectionKind.Dynamic, "Instalados", a => a.IsInstalled).AcceptsGames);
        }

        [Test]
        public void AddingAGameAddsEveryCopyOnceTest()
        {
            var first = new Game("Prey") { PluginId = steam };
            var second = new Game("Prey") { PluginId = epic };
            var unifier = NewUnifier(first, second);

            var changed = GameCollections.SetMembership(collectionId, new[] { first }, unifier, true);
            Assert.AreEqual(2, changed.Count);
            Assert.IsTrue(first.CategoryIds.Contains(collectionId));
            Assert.IsTrue(second.CategoryIds.Contains(collectionId));

            Assert.AreEqual(0, GameCollections.SetMembership(collectionId, new[] { second, first }, unifier, true).Count);
            Assert.AreEqual(1, first.CategoryIds.Count);
        }

        [Test]
        public void RemovingAGameRemovesEveryCopyAndNothingElseTest()
        {
            var other = Guid.NewGuid();
            var first = new Game("Prey") { PluginId = steam, CategoryIds = new List<Guid> { other, collectionId } };
            var second = new Game("Prey") { PluginId = epic, CategoryIds = new List<Guid> { collectionId } };
            var unifier = NewUnifier(first, second);

            var changed = GameCollections.SetMembership(collectionId, new[] { second }, unifier, false);
            Assert.AreEqual(2, changed.Count);
            Assert.AreEqual(new List<Guid> { other }, first.CategoryIds);
            Assert.IsFalse(second.CategoryIds.HasItems());
            Assert.IsFalse(GameCollections.IsMember(collectionId, first, unifier));
        }

        [Test]
        public void GameWithoutOtherStoresWorksWithoutGroupsTest()
        {
            var game = new Game("Prey");
            Assert.AreEqual(1, GameCollections.SetMembership(collectionId, new[] { game }, null, true).Count);
            Assert.IsTrue(GameCollections.IsMember(collectionId, game, null));
            Assert.AreEqual(1, GameCollections.GetCopies(game, null).Count);
        }

        [Test]
        public void HiddenGamesOnlyPassWhenTheirCollectionAsksForThemTest()
        {
            var database = new GameDatabase();
            var filter = new FilterSettings();
            var game = new Game("Prey") { Hidden = true };

            Assert.IsFalse(database.GetGameMatchesFilter(game, filter, false));
            Assert.IsTrue(database.GetGameMatchesFilter(game, filter, false, true));

            filter.Name = "Doom";
            Assert.IsFalse(database.GetGameMatchesFilter(game, filter, false, true));
        }
    }
}
