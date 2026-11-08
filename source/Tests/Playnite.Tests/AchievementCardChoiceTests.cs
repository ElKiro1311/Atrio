using NUnit.Framework;
using Playnite.Atrio;
using Playnite.Controls;
using Playnite.Converters;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace Playnite.Tests
{
    /// <summary>Which achievements the card of a game shows, the easiest missing ones, the keys and the guides.</summary>
    [TestFixture]
    public class AchievementCardChoiceTests
    {
        private class Achievement
        {
            public string DisplayNameResolved { get; set; }
            public bool Unlocked { get; set; }
            public DateTime? UnlockTimeUtc { get; set; }
            public double? GlobalPercentUnlocked { get; set; }
            public bool HasProgress { get; set; }
            public double ProgressPercent { get; set; }
            public string ProgressText { get; set; }
            public bool IsTitleHidden { get; set; }
            public bool IsUnobtainable { get; set; }
            public string ProviderKey { get; set; }
            public string ApiName { get; set; }

            public override string ToString()
            {
                return DisplayNameResolved;
            }
        }

        private static Achievement Locked(string name, double? percent = null, double progress = 0)
        {
            return new Achievement
            {
                DisplayNameResolved = name,
                GlobalPercentUnlocked = percent,
                HasProgress = progress > 0,
                ProgressPercent = progress,
                ProgressText = progress > 0 ? $"{progress / 10}/10" : null
            };
        }

        private static Achievement Unlocked(string name, int day, double? percent = null)
        {
            return new Achievement { DisplayNameResolved = name, Unlocked = true, UnlockTimeUtc = new DateTime(2026, 10, 1).AddDays(day), GlobalPercentUnlocked = percent };
        }

        private static string Names(IEnumerable<object> items)
        {
            return string.Join(",", items.Select(a => a.ToString()));
        }

        [Test]
        public void ArrangeFiltersAndSortsTest()
        {
            var items = new List<Achievement> { Unlocked("A", 2, 5), Unlocked("B", 5), Locked("C", 10), Locked("D", 60) };
            Assert.AreEqual("B,A,C,D", Names(AchievementCard.Arrange(items, AchievementCard.ShowAll, false)));
            Assert.AreEqual("B,A", Names(AchievementCard.Arrange(items, AchievementCard.ShowUnlocked, false)));
            Assert.AreEqual("C,D", Names(AchievementCard.Arrange(items, AchievementCard.ShowLocked, false)));

            // The rarest first, and the ones without a percentage last.
            Assert.AreEqual("A,C,D,B", Names(AchievementCard.Arrange(items, AchievementCard.ShowAll, true)));
            Assert.AreEqual("C,D", Names(AchievementCard.Arrange(items, AchievementCard.ShowLocked, true)));
            Assert.AreEqual(2, AchievementCard.Count(items, AchievementCard.ShowLocked));
            Assert.AreEqual(0, AchievementCard.Count(null, AchievementCard.ShowAll));
        }

        [Test]
        public void PreviewTakesFilterAndOrderTest()
        {
            var items = new List<Achievement> { Unlocked("A", 2, 5), Unlocked("B", 5), Locked("C", 10), Locked("D", 60) };
            var converter = new AchievementPreviewConverter();
            Assert.AreEqual("C,D", Names((List<object>)converter.Convert(new object[] { items, 20, AchievementCard.ShowLocked, false }, null, "20", null)));
            Assert.AreEqual("A,C", Names((List<object>)converter.Convert(new object[] { items, 2, AchievementCard.ShowAll, true }, null, "20", null)));
            Assert.AreEqual("B,A,C,D", Names((List<object>)converter.Convert(new object[] { items, 20, DependencyProperty.UnsetValue, DependencyProperty.UnsetValue }, null, "20", null)));
        }

        [Test]
        public void EasiestMissingOnesTest()
        {
            var hidden = Locked("Hidden", 95);
            hidden.IsTitleHidden = true;
            var gone = Locked("Gone", 99);
            gone.IsUnobtainable = true;
            var items = new List<Achievement>
            {
                Unlocked("Done", 1, 90),
                Locked("Common", 70),
                Locked("Uncommon", 30),
                Locked("AlmostDone", 5, 80),
                hidden,
                gone,
                Locked("Started", null, 20),
                Locked("Unknown")
            };

            Assert.AreEqual("AlmostDone,Common,Uncommon", Names(AchievementCard.PickNext(items)));
            Assert.AreEqual("AlmostDone,Common,Uncommon,Started", Names(AchievementCard.PickNext(items, 10)));
            Assert.AreEqual(0, AchievementCard.PickNext(new List<Achievement> { Locked("Unknown"), Unlocked("Done", 1, 50) }).Count);
            Assert.AreEqual(0, AchievementCard.PickNext(null).Count);

            var converter = new AchievementNextConverter();
            Assert.AreEqual(Visibility.Visible, converter.Convert(items, typeof(Visibility), null, null));
            Assert.AreEqual(Visibility.Collapsed, converter.Convert(new List<Achievement>(), typeof(Visibility), null, null));
            Assert.AreEqual(3, ((List<object>)converter.Convert(items, typeof(object), null, null)).Count);
        }

        [Test]
        public void EasiestMissingHintTest()
        {
            var hint = new AchievementNextHintConverter();
            Assert.AreEqual("8/10", hint.Convert(Locked("AlmostDone", 5, 80), null, null, null));
            Assert.AreEqual("<!LOCAtrioAchievementsNextProgress!>", hint.Convert(Locked("AlmostDone", 5, 80), null, "tip", null));
            StringAssert.Contains("61", (string)hint.Convert(Locked("Common", 61.8), null, null, null));
            Assert.AreEqual("2/10", hint.Convert(Locked("Started", null, 20), null, null, null));
            Assert.AreEqual(string.Empty, hint.Convert(Locked("Unknown"), null, null, null));
            Assert.AreEqual(string.Empty, hint.Convert(null, null, null, null));
        }

        [Test]
        public void MoreButtonCountsWhatTheFilterLeavesTest()
        {
            var items = new List<Achievement>();
            for (var i = 0; i < 31; i++)
            {
                items.Add(i < 24 ? Unlocked("U" + i, i) : Locked("L" + i, 10));
            }

            var converter = new AchievementMoreConverter();
            Assert.AreEqual("<!LOCAtrioAchievementsShowMore!>", converter.Convert(new object[] { items, 20, AchievementCard.ShowAll }, typeof(object), "20", null));
            Assert.AreEqual(Visibility.Collapsed, converter.Convert(new object[] { items, 20, AchievementCard.ShowLocked }, typeof(Visibility), "20", null));
            Assert.AreEqual(Visibility.Visible, converter.Convert(new object[] { items, 20, AchievementCard.ShowUnlocked }, typeof(Visibility), "20", null));
            Assert.AreEqual(Visibility.Visible, converter.Convert(new object[] { items, 20, DependencyProperty.UnsetValue }, typeof(Visibility), "20", null));
            Assert.AreEqual(AchievementCard.FirstCount + AchievementCard.Step, AchievementCard.Next(AchievementCard.FirstCount, AchievementCard.Count(items, AchievementCard.ShowUnlocked)));
            Assert.AreEqual(AchievementCard.FirstCount, AchievementCard.Next(AchievementCard.FirstCount, AchievementCard.Count(items, AchievementCard.ShowLocked)));
        }

        [Test]
        public void EmptyFilterTextTest()
        {
            var empty = new AchievementEmptyConverter();
            var allUnlocked = new List<Achievement> { Unlocked("A", 1), Unlocked("B", 2) };
            var noneUnlocked = new List<Achievement> { Locked("C", 3) };
            Assert.AreEqual("<!LOCAtrioAchievementsAllUnlocked!>", empty.Convert(new object[] { allUnlocked, AchievementCard.ShowLocked }, typeof(object), null, null));
            Assert.AreEqual("<!LOCAtrioAchievementsNoneUnlocked!>", empty.Convert(new object[] { noneUnlocked, AchievementCard.ShowUnlocked }, typeof(object), null, null));
            Assert.AreEqual(string.Empty, empty.Convert(new object[] { noneUnlocked, AchievementCard.ShowLocked }, typeof(object), null, null));
            Assert.AreEqual(string.Empty, empty.Convert(new object[] { noneUnlocked, AchievementCard.ShowAll }, typeof(object), null, null));
            Assert.AreEqual(string.Empty, empty.Convert(new object[] { new List<Achievement>(), AchievementCard.ShowLocked }, typeof(object), null, null));
            Assert.AreEqual(Visibility.Visible, empty.Convert(new object[] { allUnlocked, AchievementCard.ShowLocked }, typeof(Visibility), null, null));
            Assert.AreEqual(Visibility.Collapsed, empty.Convert(new object[] { allUnlocked, AchievementCard.ShowUnlocked }, typeof(Visibility), null, null));
        }

        [Test]
        public void FilterChoiceTest()
        {
            var converter = new AchievementFilterConverter();
            Assert.AreEqual(true, converter.Convert(1, typeof(bool?), "1", null));
            Assert.AreEqual(false, converter.Convert(2, typeof(bool?), "1", null));
            Assert.AreEqual(false, converter.Convert(null, typeof(bool?), "0", null));
            Assert.AreEqual(2, converter.ConvertBack(true, typeof(int), "2", null));
            Assert.AreEqual(Binding.DoNothing, converter.ConvertBack(false, typeof(int), "2", null));
        }

        [Test]
        public void KeysTest()
        {
            Assert.AreEqual(-1, AchievementCard.GetKeyStep(Key.Left));
            Assert.AreEqual(-1, AchievementCard.GetKeyStep(Key.Up));
            Assert.AreEqual(1, AchievementCard.GetKeyStep(Key.Right));
            Assert.AreEqual(1, AchievementCard.GetKeyStep(Key.Down));
            Assert.AreEqual(0, AchievementCard.GetKeyStep(Key.Enter));
            Assert.AreEqual("Icon", AchievementKeys.ToggleName);
        }

        [Test]
        public void DirectGuidesTest()
        {
            var retro = new Achievement { ProviderKey = "RetroAchievements", ApiName = " 12345 " };
            var steam = new Achievement { ProviderKey = "Steam", ApiName = "ACH_WIN_ONE" };
            var odd = new Achievement { ProviderKey = "RetroAchievements", ApiName = "ACH" };
            Assert.AreEqual("https://retroachievements.org/achievement/12345", AchievementCard.GetRetroUrl(retro));
            Assert.IsNull(AchievementCard.GetRetroUrl(steam));
            Assert.IsNull(AchievementCard.GetRetroUrl(odd));
            Assert.IsNull(AchievementCard.GetRetroUrl(null));

            Assert.AreEqual("https://steamcommunity.com/app/620/guides/?browsefilter=toprated&requiredtags%5B%5D=Achievements", AchievementCard.GetSteamGuidesUrl("620"));
            Assert.IsNull(AchievementCard.GetSteamGuidesUrl(null));
            Assert.IsNull(AchievementCard.GetSteamGuidesUrl("abc"));

            // The page of the achievement goes before the guides of the game, and nothing without either.
            Assert.AreEqual("https://retroachievements.org/achievement/12345", AchievementCard.GetDirectGuideUrl(retro, "620"));
            Assert.AreEqual("https://steamcommunity.com/app/620/guides/?browsefilter=toprated&requiredtags%5B%5D=Achievements", AchievementCard.GetDirectGuideUrl(steam, "620"));
            Assert.IsNull(AchievementCard.GetDirectGuideUrl(steam, null));
        }

        [Test]
        public void GuideButtonsTest()
        {
            var converter = new AchievementGuideConverter();
            var retro = new Achievement { ProviderKey = "RetroAchievements", ApiName = "12345" };
            var other = new Achievement { ProviderKey = "Xbox", ApiName = "1" };
            Assert.AreEqual("<!LOCAtrioAchievementGuideRetro!>", converter.Convert(new object[] { retro, false }, typeof(string), "direct", null));
            Assert.AreEqual("<!LOCAtrioAchievementGuideSteam!>", converter.Convert(new object[] { other, true }, typeof(string), "direct", null));
            Assert.AreEqual(string.Empty, converter.Convert(new object[] { other, false }, typeof(string), "direct", null));
            Assert.AreEqual(Visibility.Collapsed, converter.Convert(new object[] { other, DependencyProperty.UnsetValue }, typeof(Visibility), "direct", null));
            Assert.AreEqual(Visibility.Visible, converter.Convert(new object[] { retro, false }, typeof(Visibility), "direct", null));
            Assert.AreEqual("<!LOCAtrioAchievementGuideWeb!>", converter.Convert(new object[] { other, true }, typeof(string), "web", null));
            Assert.AreEqual("<!LOCAtrioAchievementGuide!>", converter.Convert(new object[] { other, false }, typeof(string), "web", null));
        }
    }
}
