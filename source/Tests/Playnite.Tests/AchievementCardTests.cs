using NUnit.Framework;
using Playnite.Atrio;
using Playnite.Converters;
using System;
using System.Collections.Generic;
using System.Windows;

namespace Playnite.Tests
{
    [TestFixture]
    public class AchievementCardTests
    {
        private class Item
        {
            public bool Unlocked { get; set; }
            public DateTime? UnlockTimeUtc { get; set; }
        }

        [Test]
        public void EachClickBringsMoreUntilAllAreThereTest()
        {
            Assert.AreEqual(60, AchievementCard.Next(AchievementCard.FirstCount, 31 + 100));
            Assert.AreEqual(60, AchievementCard.Next(20, 31));
            Assert.AreEqual(100, AchievementCard.Next(60, 90));
        }

        [Test]
        public void TheClickAfterTheLastOnesGoesBackToTheFirstTest()
        {
            Assert.AreEqual(AchievementCard.FirstCount, AchievementCard.Next(60, 31));
            Assert.AreEqual(AchievementCard.FirstCount, AchievementCard.Next(60, 60));
            Assert.AreEqual(AchievementCard.FirstCount, AchievementCard.Next(20, 0));
        }

        [Test]
        public void GuideSearchJoinsGameAchievementAndWordTest()
        {
            var url = AchievementCard.GetGuideUrl(" Affogato ", "A Good Beginning", "achievement");
            Assert.AreEqual("https://duckduckgo.com/?q=" + Uri.EscapeDataString("Affogato A Good Beginning achievement"), url);
            StringAssert.DoesNotContain(" ", url);
            StringAssert.DoesNotContain("\"", url);
        }

        [Test]
        public void GuideSearchSkipsWhatIsMissingTest()
        {
            Assert.AreEqual("https://duckduckgo.com/?q=" + Uri.EscapeDataString("Ricachón logro"),
                AchievementCard.GetGuideUrl(null, "Ricachón", "logro"));
            Assert.AreEqual("https://duckduckgo.com/?q=" + Uri.EscapeDataString("Game"),
                AchievementCard.GetGuideUrl("Game", " ", null));
        }

        [Test]
        public void MoreButtonTextTest()
        {
            var converter = new AchievementMoreConverter();
            Assert.AreEqual(string.Empty, converter.Convert(new object[] { 20, 20 }, typeof(object), "20", null));
            Assert.AreEqual(string.Empty, converter.Convert(new object[] { 8, 20 }, typeof(object), "20", null));
            Assert.AreEqual("<!LOCAtrioAchievementsShowMore!>", converter.Convert(new object[] { 31, 20 }, typeof(object), "20", null));
            Assert.AreEqual("<!LOCAtrioAchievementsShowFewer!>", converter.Convert(new object[] { 31, 60 }, typeof(object), "20", null));
            Assert.AreEqual(string.Empty, converter.Convert(new object[] { DependencyProperty.UnsetValue, 20 }, typeof(object), "20", null));
        }

        [Test]
        public void MoreButtonIsOnlyThereWhenThereAreMoreTest()
        {
            var converter = new AchievementMoreConverter();
            Assert.AreEqual(Visibility.Collapsed, converter.Convert(new object[] { 12, 20 }, typeof(Visibility), "20", null));
            Assert.AreEqual(Visibility.Visible, converter.Convert(new object[] { 31, 20 }, typeof(Visibility), "20", null));
            Assert.AreEqual(Visibility.Visible, converter.Convert(new object[] { 31, 60 }, typeof(Visibility), "20", null));
        }

        [Test]
        public void PreviewGrowsWithTheSecondValueTest()
        {
            var items = new List<Item>();
            for (var i = 0; i < 50; i++)
            {
                items.Add(new Item { Unlocked = i % 2 == 0, UnlockTimeUtc = i % 2 == 0 ? new DateTime(2020, 1, 1).AddDays(i) : (DateTime?)null });
            }

            var converter = new AchievementPreviewConverter();
            Assert.AreEqual(20, ((List<object>)converter.Convert(new object[] { items, 20 }, null, "20", null)).Count);
            Assert.AreEqual(50, ((List<object>)converter.Convert(new object[] { items, 60 }, null, "20", null)).Count);
            Assert.AreEqual(20, ((List<object>)converter.Convert(new object[] { items, DependencyProperty.UnsetValue }, null, "20", null)).Count);
            Assert.AreEqual(0, ((List<object>)converter.Convert(new object[] { null, 20 }, null, "20", null)).Count);

            var first = (Item)((List<object>)converter.Convert(new object[] { items, 60 }, null, "20", null))[0];
            Assert.IsTrue(first.Unlocked);
            Assert.AreEqual(new DateTime(2020, 1, 1).AddDays(48), first.UnlockTimeUtc);
        }

        [Test]
        public void StateTest()
        {
            var converter = new AchievementStateConverter();
            Assert.AreEqual("<!LOCAtrioAchievementStateLocked!>", converter.Convert(new object[] { false, null }, null, null, null));
            Assert.AreEqual("<!LOCAtrioAchievementStateLocked!>", converter.Convert(new object[] { DependencyProperty.UnsetValue, DependencyProperty.UnsetValue }, null, null, null));
            Assert.AreEqual("<!LOCAtrioAchievementStateUnlocked!>", converter.Convert(new object[] { true, null }, null, null, null));
            Assert.AreEqual("<!LOCAtrioAchievementStateUnlocked!>", converter.Convert(new object[] { true, DateTime.MinValue }, null, null, null));
            Assert.AreEqual("<!LOCAtrioAchievementStateUnlockedOn!>", converter.Convert(new object[] { true, new DateTime(2026, 10, 10, 19, 24, 41) }, null, null, null));
        }

        [Test]
        public void RarityTest()
        {
            var converter = new AchievementRarityConverter();
            Assert.AreEqual(string.Empty, converter.Convert(new object[] { "Rare", null }, null, null, null));
            Assert.AreEqual("<!LOCAtrioRarityRare!> · <!LOCAtrioAchievementRarityShare!>", converter.Convert(new object[] { "Rare", 6.5 }, null, null, null));
            Assert.AreEqual("<!LOCAtrioRarityUltraRare!> · <!LOCAtrioAchievementRarityShare!>", converter.Convert(new object[] { "UltraRare", 2.7 }, null, null, null));
            Assert.AreEqual("<!LOCAtrioAchievementRarityShare!>", converter.Convert(new object[] { "Legendary", 0.4 }, null, null, null));
            Assert.AreEqual("<!LOCAtrioRarityCommon!>", AchievementRarityConverter.GetTierName("Common"));
            Assert.IsNull(AchievementRarityConverter.GetTierName(null));
        }

        [Test]
        public void DatesAndPercentsTest()
        {
            Assert.AreEqual(string.Empty, new AchievementDateConverter().Convert(null, null, null, null));
            Assert.AreEqual(string.Empty, new AchievementDateConverter().Convert(DateTime.MinValue, null, null, null));
            StringAssert.Contains("2026", (string)new AchievementDateConverter().Convert(new DateTime(2026, 10, 10, 19, 24, 41), null, null, null));

            Assert.AreEqual(string.Empty, new AchievementPercentConverter().Convert(null, null, null, null));
            StringAssert.EndsWith(" %", (string)new AchievementPercentConverter().Convert(61.8, null, null, null));
            StringAssert.Contains("61", AchievementPercentConverter.FormatPercent(61.84));
        }

        [Test]
        public void PointsAndTrophiesTest()
        {
            Assert.AreEqual("<!LOCAtrioAchievementPoints!>", new AchievementPointsConverter().Convert("5", null, null, null));
            Assert.AreEqual(string.Empty, new AchievementPointsConverter().Convert(" ", null, null, null));
            Assert.AreEqual(string.Empty, new AchievementPointsConverter().Convert(null, null, null, null));

            var trophy = new AchievementTrophyConverter();
            Assert.AreEqual("<!LOCAtrioTrophyGold!>", trophy.Convert("Gold", null, null, null));
            Assert.AreEqual("<!LOCAtrioTrophyPlatinum!>", trophy.Convert(" platinum ", null, null, null));
            Assert.AreEqual("Mithril", trophy.Convert("Mithril", null, null, null));
            Assert.AreEqual(string.Empty, trophy.Convert(null, null, null, null));
        }
    }
}
