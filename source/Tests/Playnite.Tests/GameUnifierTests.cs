using NUnit.Framework;
using Playnite.Atrio;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class GameUnifierTests
    {
        private static readonly Guid steam = Guid.Parse("CB91DFC9-B977-43BF-8E70-55F46E410FAB");
        private static readonly Guid epic = Guid.Parse("00000002-DBD1-46C6-B5D0-B1BA559D10E4");
        private static readonly Guid gog = Guid.Parse("AEBE8B7C-6DC3-4A66-AF31-E7375C6B5E9E");

        private static Game NewGame(string name, Guid store, bool installed = false)
        {
            return new Game(name) { PluginId = store, IsInstalled = installed };
        }

        private static GameUnifier NewUnifier()
        {
            return new GameUnifier(new AtrioSettings());
        }

        [TestCase("The Witcher® 3: Wild Hunt", "Witcher 3: Wild Hunt")]
        [TestCase("BioShock Infinite", "BioShock Infinite: Complete Edition")]
        [TestCase("Fallout 3 - Game of the Year Edition", "Fallout 3")]
        [TestCase("South Park™: The Fractured But Whole", "South Park The Fractured But Whole")]
        [TestCase("Final Fantasy VII", "FINAL FANTASY 7")]
        [TestCase("Pokémon", "Pokemon")]
        [TestCase("Half-Life", "Half Life")]
        [TestCase("Tom Clancy's The Division", "Tom Clancy’s The Division")]
        [TestCase("Dungeons & Dragons", "Dungeons and Dragons")]
        public void SameGameNormalizesTheSameTest(string first, string second)
        {
            Assert.AreEqual(GameUnifier.NormalizeName(first), GameUnifier.NormalizeName(second));
        }

        [TestCase("Doom", "Doom 3")]
        [TestCase("Age of Empires II", "Age of Empires II: Definitive Edition")]
        [TestCase("Crysis", "Crysis Remastered")]
        [TestCase("Dota 2", "Dota Underlords")]
        [TestCase("Baldur's Gate", "Baldur's Gate: Enhanced Edition")]
        public void DifferentGamesStayDifferentTest(string first, string second)
        {
            Assert.AreNotEqual(GameUnifier.NormalizeName(first), GameUnifier.NormalizeName(second));
        }

        [Test]
        public void JoinsCopiesFromDifferentStoresTest()
        {
            var one = NewGame("Hades", steam);
            var two = NewGame("HADES", epic);
            var groups = NewUnifier().BuildGroups(new List<Game> { one, two, NewGame("Celeste", gog) });

            Assert.AreEqual(2, groups.Count);
            Assert.AreSame(groups[one.Id], groups[two.Id]);
            Assert.AreEqual(2, groups[one.Id].Copies.Count);
        }

        [Test]
        public void NeverJoinsTwoEntriesOfTheSameStoreTest()
        {
            var groups = NewUnifier().BuildGroups(new List<Game> { NewGame("Prey", steam), NewGame("Prey", steam) });
            Assert.AreEqual(0, groups.Count);
        }

        [Test]
        public void HiddenGamesDontJoinTest()
        {
            var hidden = NewGame("Hades", epic);
            hidden.Hidden = true;
            var groups = NewUnifier().BuildGroups(new List<Game> { NewGame("Hades", steam), hidden });
            Assert.AreEqual(0, groups.Count);
        }

        [Test]
        public void InstalledCopyRepresentsTheGameTest()
        {
            var inSteam = NewGame("Hades", steam);
            var inEpic = NewGame("Hades", epic, true);
            var group = NewUnifier().BuildGroups(new List<Game> { inSteam, inEpic })[inEpic.Id];

            Assert.AreEqual(inEpic.Id, group.Primary.Id);
            Assert.AreEqual(inEpic.Id, group.Copies[0].Id);
        }

        [Test]
        public void StorePriorityDecidesWhenNothingIsInstalledTest()
        {
            var inEpic = NewGame("Hades", epic);
            var inGog = NewGame("Hades", gog);
            var unifier = NewUnifier();
            Assert.AreEqual(inEpic.Id, unifier.BuildGroups(new List<Game> { inGog, inEpic })[inEpic.Id].Primary.Id);

            unifier.Settings.StorePriority = new List<Guid> { gog, epic };
            Assert.AreEqual(inGog.Id, unifier.BuildGroups(new List<Game> { inGog, inEpic })[inEpic.Id].Primary.Id);
        }

        [Test]
        public void UserChoiceBeatsAnInstalledCopyTest()
        {
            var inSteam = NewGame("Hades", steam);
            var inEpic = NewGame("Hades", epic, true);
            var unifier = NewUnifier();
            var key = unifier.GetGroupKey(inSteam);
            unifier.Settings.PreferredCopies[key] = inSteam.Id;

            var group = unifier.BuildGroups(new List<Game> { inSteam, inEpic })[inEpic.Id];
            Assert.AreEqual(inSteam.Id, group.Primary.Id);
            Assert.IsTrue(group.HasUserChoice);
        }

        [Test]
        public void SplitCopyStaysOutTest()
        {
            var inSteam = NewGame("Hades", steam);
            var inEpic = NewGame("Hades", epic);
            var unifier = NewUnifier();
            unifier.Settings.SplitGames.Add(inEpic.Id);
            Assert.AreEqual(0, unifier.BuildGroups(new List<Game> { inSteam, inEpic }).Count);
        }

        [Test]
        public void ManualGroupJoinsDifferentTitlesTest()
        {
            var first = NewGame("Grand Theft Auto V", steam);
            var second = NewGame("GTA 5 Premium", epic);
            var unifier = NewUnifier();
            unifier.Settings.ManualGroups[first.Id] = "a";
            unifier.Settings.ManualGroups[second.Id] = "a";
            Assert.AreEqual(2, unifier.BuildGroups(new List<Game> { first, second }).Count);
        }

        [Test]
        public void TurningOffUnificationLeavesEverythingSeparateTest()
        {
            var unifier = NewUnifier();
            unifier.Settings.UnifyDuplicates = false;
            Assert.AreEqual(0, unifier.BuildGroups(new List<Game> { NewGame("Hades", steam), NewGame("Hades", epic) }).Count);
        }

        private static GameUnifier NewAttachedUnifier(List<Game> games)
        {
            var unifier = NewUnifier();
            unifier.Attach(() => games, null);
            unifier.EnsureBuilt();
            return unifier;
        }

        [Test]
        public void PreferredCopyBecomesPrimaryAndRaisesChangeTest()
        {
            var inSteam = NewGame("Hades", steam, true);
            var inEpic = NewGame("Hades", epic);
            var unifier = NewAttachedUnifier(new List<Game> { inSteam, inEpic });
            var changes = 0;
            unifier.GroupsChanged += (_, __) => changes++;

            Assert.IsFalse(unifier.IsSecondaryCopy(inSteam.Id));
            Assert.IsTrue(unifier.IsSecondaryCopy(inEpic.Id));

            unifier.SetPreferredCopy(inEpic);

            Assert.AreEqual(1, changes);
            Assert.IsTrue(unifier.IsSecondaryCopy(inSteam.Id));
            Assert.AreEqual(inEpic.Id, unifier.GetGroup(inSteam.Id).Primary.Id);

            unifier.ClearPreferredCopy(unifier.GetGroup(inSteam.Id));
            Assert.AreEqual(inSteam.Id, unifier.GetGroup(inSteam.Id).Primary.Id);
        }

        [Test]
        public void SplitCopyLeavesBothVisibleTest()
        {
            var inSteam = NewGame("Hades", steam);
            var inEpic = NewGame("Hades", epic);
            var unifier = NewAttachedUnifier(new List<Game> { inSteam, inEpic });

            unifier.SplitCopy(inEpic);

            Assert.IsNull(unifier.GetGroup(inSteam.Id));
            Assert.IsFalse(unifier.IsSecondaryCopy(inEpic.Id));
        }

        [Test]
        public void MergeJoinsGamesAndUndoesASplitTest()
        {
            var first = NewGame("Grand Theft Auto V", steam);
            var second = NewGame("GTA 5 Premium", epic);
            var unifier = NewAttachedUnifier(new List<Game> { first, second });
            Assert.AreEqual(0, unifier.CollapsedCopiesCount);

            unifier.MergeGames(new List<Game> { first, second });

            Assert.AreEqual(1, unifier.CollapsedCopiesCount);
            Assert.AreSame(unifier.GetGroup(first.Id), unifier.GetGroup(second.Id));

            unifier.SplitCopy(first);
            Assert.IsNull(unifier.GetGroup(second.Id));
            Assert.IsEmpty(unifier.Settings.ManualGroups);
        }

        [Test]
        public void BadgesFollowStoreOrderTest()
        {
            var unifier = NewUnifier();
            Assert.Less(unifier.GetStoreRank(steam, "Steam"), unifier.GetStoreRank(epic, "Epic"));
            Assert.Less(unifier.GetStoreRank(epic, "Epic"), unifier.GetStoreRank(gog, "GOG"));
            Assert.Less(unifier.GetStoreRank(gog, "GOG"), unifier.GetStoreRank(Guid.NewGuid(), "Rockstar Games"));
            Assert.Less(unifier.GetStoreRank(Guid.NewGuid(), "Rockstar Games"), unifier.GetStoreRank(Guid.NewGuid(), "Tienda desconocida"));

            unifier.Settings.StorePriority = new List<Guid> { gog, steam };
            Assert.Less(unifier.GetStoreRank(gog, "GOG"), unifier.GetStoreRank(steam, "Steam"));
        }

        [Test]
        public void TotalPlaytimeAddsAllCopiesTest()
        {
            var inSteam = NewGame("Hades", steam);
            var inEpic = NewGame("Hades", epic);
            inSteam.Playtime = 100;
            inEpic.Playtime = 50;
            var group = NewUnifier().BuildGroups(new List<Game> { inSteam, inEpic })[inEpic.Id];
            Assert.AreEqual(150, group.TotalPlaytime);
        }
    }
}
