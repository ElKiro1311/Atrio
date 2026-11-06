using NUnit.Framework;
using Playnite.Converters;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class AchievementListConvertersTests
    {
        private class Item
        {
            public bool Unlocked { get; set; }
            public DateTime? UnlockTimeUtc { get; set; }
            public double? GlobalPercentUnlocked { get; set; }
            public string DisplayNameResolved { get; set; }
        }

        private static readonly Item Old = new Item { DisplayNameResolved = "Old", Unlocked = true, UnlockTimeUtc = new DateTime(2020, 1, 1), GlobalPercentUnlocked = 50 };
        private static readonly Item Recent = new Item { DisplayNameResolved = "Recent", Unlocked = true, UnlockTimeUtc = new DateTime(2024, 1, 1), GlobalPercentUnlocked = 5 };
        private static readonly Item Locked = new Item { DisplayNameResolved = "Locked", GlobalPercentUnlocked = 1 };
        private static readonly Item NoNumber = new Item { DisplayNameResolved = "A locked one without a number" };
        private static readonly List<Item> Items = new List<Item> { Locked, Old, NoNumber, Recent };

        private static List<object> Filter(bool onlyUnlocked, bool onlyLocked, int sort)
        {
            var result = new AchievementListConverter().Convert(new object[] { Items, onlyUnlocked, onlyLocked, sort }, null, null, null);
            return (List<object>)result;
        }

        [Test]
        public void PreviewKeepsTheNewestUnlockedFirstTest()
        {
            var preview = (List<object>)new AchievementPreviewConverter().Convert(Items, null, "3", null);
            CollectionAssert.AreEqual(new object[] { Recent, Old, Locked }, preview);
        }

        [Test]
        public void PreviewShowsWhatThereIsWhenThereAreFewerTest()
        {
            var preview = (List<object>)new AchievementPreviewConverter().Convert(Items, null, "20", null);
            Assert.AreEqual(4, preview.Count);
            Assert.AreEqual(0, ((List<object>)new AchievementPreviewConverter().Convert(null, null, "20", null)).Count);
        }

        [Test]
        public void ListFiltersTest()
        {
            CollectionAssert.AreEqual(Items, Filter(false, false, 0));
            CollectionAssert.AreEqual(new object[] { Old, Recent }, Filter(true, false, 0));
            CollectionAssert.AreEqual(new object[] { Locked, NoNumber }, Filter(false, true, 0));
        }

        [Test]
        public void ListSortsTest()
        {
            Assert.AreEqual(Recent, Filter(false, false, 1).First());
            CollectionAssert.AreEqual(new object[] { Locked, Recent, Old, NoNumber }, Filter(false, false, 2));
            CollectionAssert.AreEqual(new object[] { Old, Recent, Locked, NoNumber }, Filter(false, false, 3));
            Assert.AreEqual(NoNumber, Filter(false, false, 4).First());
        }

        [Test]
        public void MilestonesOnlyCountUnlockedTest()
        {
            var converter = new AchievementMilestoneConverter();
            Assert.AreEqual(Old, converter.Convert(Items, null, "first", null));
            Assert.AreEqual(Recent, converter.Convert(Items, null, "latest", null));
            Assert.AreEqual(Recent, converter.Convert(Items, null, "rarest", null));
            Assert.IsNull(converter.Convert(new List<Item> { Locked }, null, "latest", null));
        }
    }
}
