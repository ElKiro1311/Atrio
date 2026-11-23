using NUnit.Framework;
using Playnite.Atrio;
using Playnite.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Playnite.Tests
{
    [TestFixture]
    public class PlayerActivityTests
    {
        private static PlaySession Session(DateTime local, int seconds)
        {
            return new PlaySession { GameId = Guid.NewGuid(), Start = local.ToUniversalTime(), Seconds = seconds };
        }

        private static UnlockedAchievement Achievement(string name, DateTime local)
        {
            return new UnlockedAchievement { Name = name, When = local };
        }

        [Test]
        public void DaysTest()
        {
            var day = new DateTime(2026, 11, 8, 18, 0, 0, DateTimeKind.Local);
            var days = PlayerActivity.Group(
                new[] { Session(day, 1800), Session(day.AddHours(3), 600), Session(day.AddDays(-2), 30), Session(day.AddDays(-3), 7200) },
                new[] { Achievement("First", day.AddMinutes(5)), Achievement("Second", day.AddHours(4)), Achievement("Alone", day.AddDays(-5)) });
            Assert.AreEqual(3, days.Count, "Half a minute isn't a day of playing.");
            Assert.AreEqual(day.Date, days[0].Day);
            Assert.AreEqual(2400, days[0].Seconds);
            Assert.AreEqual(day.AddHours(4), days[0].Last);
            Assert.AreEqual(new[] { "Second", "First" }, days[0].Achievements.Select(a => a.Name).ToArray());
            Assert.AreEqual(day.Date.AddDays(-3), days[1].Day);
            Assert.AreEqual(0, days[2].Seconds);
            Assert.AreEqual("Alone", days[2].Achievements.Single().Name);
            Assert.AreEqual(0, PlayerActivity.Group(null, null).Count);
        }

        [Test]
        public void PlayingUntilLateIsTheSameNightTest()
        {
            var night = new DateTime(2026, 11, 8, 23, 30, 0, DateTimeKind.Local);
            var days = PlayerActivity.Group(new[] { Session(night, 3 * 3600) }, new List<UnlockedAchievement>());
            Assert.AreEqual(1, days.Count);
            Assert.AreEqual(night.Date, days[0].Day);
        }
    }

    [TestFixture]
    public class WebImageTests
    {
        private string folder;

        [SetUp]
        public void SetUp()
        {
            folder = Path.Combine(Path.GetTempPath(), $"atrio-web-images-{Guid.NewGuid():N}");
            WebImage.TestFolder = folder;
            WebImage.ForgetMemory();
        }

        [TearDown]
        public void TearDown()
        {
            WebImage.TestFolder = null;
            WebImage.ForgetMemory();
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }
        }

        private static byte[] MakePicture(int width, int height)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, new byte[width * 3 * height], width * 3)));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        [Test]
        public void DecodeTest()
        {
            Assert.AreEqual(100, WebImage.Decode(MakePicture(400, 200), 100).PixelWidth);
            Assert.AreEqual(50, WebImage.Decode(MakePicture(400, 200), 100).PixelHeight);
            Assert.AreEqual(80, WebImage.Decode(MakePicture(80, 40), 100).PixelWidth, "A small one isn't made bigger.");
            Assert.AreEqual(80, WebImage.Decode(MakePicture(80, 40), 0).PixelWidth);
            Assert.IsTrue(WebImage.Decode(MakePicture(80, 40), 0).IsFrozen);
            Assert.IsNull(WebImage.Decode(new byte[] { 1, 2, 3 }, 100));
            Assert.IsNull(WebImage.Decode(new byte[0], 100));
        }

        [Test]
        public void TheCopyOnTheDiskIsUsedTest()
        {
            // An address that doesn't exist: only the copy can answer.
            var url = $"https://{Guid.NewGuid():N}.invalid/a.png";
            var path = WebImage.GetPath(url);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, MakePicture(400, 200));
            var first = WebImage.GetAsync(url, 100).GetAwaiter().GetResult();
            Assert.AreEqual(100, first.PixelWidth);
            Assert.AreSame(first, WebImage.GetAsync(url, 100).GetAwaiter().GetResult(), "The second time it comes from memory.");
            Assert.AreEqual(200, WebImage.GetAsync(url, 200).GetAwaiter().GetResult().PixelWidth, "Another width is another picture.");
            Assert.AreNotEqual(WebImage.GetPath(url), WebImage.GetPath(url + "?"));
        }

        [Test]
        public void TrimTest()
        {
            Directory.CreateDirectory(folder);
            var now = DateTime.UtcNow;
            for (var i = 0; i < 6; i++)
            {
                var file = Path.Combine(folder, i.ToString());
                File.WriteAllBytes(file, new byte[100]);
                File.SetLastWriteTimeUtc(file, now.AddDays(-i));
            }

            // The oldest ones go away by their age, and the ones that don't fit too.
            WebImage.Trim(folder, 1000, now.AddDays(-4.5));
            CollectionAssert.AreEquivalent(new[] { "0", "1", "2", "3", "4" }, Directory.GetFiles(folder).Select(Path.GetFileName));
            WebImage.Trim(folder, 250, now.AddDays(-4.5));
            CollectionAssert.AreEquivalent(new[] { "0", "1" }, Directory.GetFiles(folder).Select(Path.GetFileName));
        }
    }
}
