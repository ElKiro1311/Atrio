using NUnit.Framework;
using Playnite.Atrio;

namespace Playnite.Tests
{
    [TestFixture]
    public class AlmostDoneShelfTests
    {
        [Test]
        public void OnlyUnfinishedGamesWithMostAchievementsTest()
        {
            Assert.IsTrue(AlmostDoneShelf.Qualifies(9, 10));
            Assert.IsTrue(AlmostDoneShelf.Qualifies(3, 4));
            Assert.IsFalse(AlmostDoneShelf.Qualifies(10, 10), "a finished game is not almost done");
            Assert.IsFalse(AlmostDoneShelf.Qualifies(7, 10));
            Assert.IsFalse(AlmostDoneShelf.Qualifies(2, 3), "too few achievements to count");
            Assert.IsFalse(AlmostDoneShelf.Qualifies(0, 0));
        }

        [Test]
        public void PercentTest()
        {
            Assert.AreEqual(90, AlmostDoneShelf.GetPercent(9, 10), 0.001);
            Assert.AreEqual(0, AlmostDoneShelf.GetPercent(0, 0));
        }

        [Test]
        public void TextSaysHowManyAreLeftTest()
        {
            Assert.AreEqual("<!LOCAtrioHomeAchievementLeft!>", AlmostDoneShelf.GetText(9, 10));
            Assert.AreEqual("<!LOCAtrioHomeAchievementsLeft!>", AlmostDoneShelf.GetText(8, 10));
        }
    }
}
