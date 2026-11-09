using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class StorageAndCapturesTests
    {
        private const long Gb = 1L << 30;

        [Test]
        public void RootTest()
        {
            Assert.AreEqual(@"D:\", Storage.GetRoot(@"d:\steam\steamapps\common\Dead Cells"));
            Assert.AreEqual(@"C:\", Storage.GetRoot(@"C:\Games"));
            Assert.IsNull(Storage.GetRoot(@"\\servidor\juegos\Undertale"));
            Assert.IsNull(Storage.GetRoot(@"juegos\Undertale"));
            Assert.IsNull(Storage.GetRoot(null));
            Assert.IsNull(Storage.GetRoot("  "));
        }

        [Test]
        public void DisksTest()
        {
            var disks = new List<StorageDiskInfo>
            {
                new StorageDiskInfo { Root = @"C:\", Label = "Windows", Total = 500 * Gb, Free = 200 * Gb },
                new StorageDiskInfo { Root = @"D:\", Label = "Juegos", Total = 2000 * Gb, Free = 300 * Gb },
                new StorageDiskInfo { Root = @"E:\", Total = 1000 * Gb, Free = 900 * Gb }
            };

            var games = new List<StorageGame>
            {
                new StorageGame { GameId = Guid.NewGuid(), Name = "Pequeño", Folder = @"d:\steam\Pequeño", Size = 2 * Gb },
                new StorageGame { GameId = Guid.NewGuid(), Name = "Grande", Folder = @"D:\steam\Grande", Size = 150 * Gb },
                new StorageGame { GameId = Guid.NewGuid(), Name = "Sin medir", Folder = @"D:\Otros\Sin medir", Size = 0 },
                new StorageGame { GameId = Guid.NewGuid(), Name = "En C", Folder = @"C:\Games\En C", Size = 10 * Gb },
                new StorageGame { GameId = Guid.NewGuid(), Name = "Disco quitado", Folder = @"G:\Juegos\Fuera", Size = 50 * Gb }
            };

            var result = Storage.Build(games, disks);
            Assert.AreEqual(new[] { @"D:\", @"C:\", @"E:\" }, result.Select(a => a.Root).ToArray(), "Disks with games first, the fullest of games first.");
            var d = result[0];
            Assert.AreEqual(new[] { "Grande", "Pequeño", "Sin medir" }, d.Games.Select(a => a.Name).ToArray());
            Assert.AreEqual(152 * Gb, d.GamesSize);
            Assert.AreEqual(2000 * Gb - 300 * Gb - 152 * Gb, d.OtherSize);
            Assert.IsEmpty(result[2].Games);
            Assert.IsFalse(result.Any(a => a.Games.Any(b => b.Name == "Disco quitado")), "A game on a disk that isn't there is left out.");
        }

        [Test]
        public void OldSizesNeverPassTheUsedRoomTest()
        {
            var disks = new List<StorageDiskInfo> { new StorageDiskInfo { Root = @"D:\", Total = 100 * Gb, Free = 90 * Gb } };
            var games = new List<StorageGame> { new StorageGame { Name = "Ya no ocupa tanto", Folder = @"D:\Juego", Size = 40 * Gb } };
            var disk = Storage.Build(games, disks)[0];
            Assert.AreEqual(10 * Gb, disk.GamesSize);
            Assert.AreEqual(0, disk.OtherSize);
        }

        [Test]
        public void SizeTextTest()
        {
            var size = DirectUninstall.FormatSize(2000 * Gb);
            StringAssert.EndsWith(" TB", size);
            StringAssert.StartsWith("1", size);
            StringAssert.EndsWith(" GB", DirectUninstall.FormatSize(152 * Gb));
            StringAssert.EndsWith(" MB", DirectUninstall.FormatSize(42L << 20));
        }

        [Test]
        public void NamesTest()
        {
            Assert.AreEqual("clair obscur expedition 33", GameCaptures.Normalize("Clair Obscur: Expedition 33™"));
            Assert.AreEqual("pokemon", GameCaptures.Normalize("Pokémon"));
            Assert.AreEqual(string.Empty, GameCaptures.Normalize("  "));
            CollectionAssert.AreEquivalent(
                new[] { "clair obscur expedition 33", "clair obscur", "expedition 33" },
                GameCaptures.GetNameVariants(new[] { "Clair Obscur: Expedition 33" }));
            Assert.AreEqual("Dota 2", GameCaptures.GetGameBarTitle("Dota 2 2026-11-08 12-34-56"));
            Assert.AreEqual("Run", GameCaptures.GetGameBarTitle("Run 2026-09-20 17-40-39"));
            Assert.IsNull(GameCaptures.GetGameBarTitle("Recording 2026-10-13 191154"));
            Assert.AreEqual("Clair Obscur Expedi", GameCaptures.GetDiscordTitle("Clair_Obscur_Expedi_9965277e-16a8-433f-9c1f-a97c807d9cd2"));
            Assert.IsNull(GameCaptures.GetDiscordTitle("Dota_2_sin-numero"));
            var variants = GameCaptures.GetNameVariants(new[] { "Clair Obscur: Expedition 33" });
            Assert.IsTrue(GameCaptures.StartsOneOf("Clair_Obscur_Expedi", variants));
            Assert.IsFalse(GameCaptures.StartsOneOf("Cla", variants), "Too short to tell.");
            Assert.IsFalse(GameCaptures.StartsOneOf("Pantalla_1", variants));

            // "Portal" isn't a name Discord cut: it's another game.
            var portal2 = GameCaptures.GetNameVariants(new[] { "Portal 2" });
            Assert.IsFalse(GameCaptures.StartsOneOf("Portal", portal2));
            Assert.IsTrue(GameCaptures.StartsOneOf("Portal_2", portal2));
        }

        [Test]
        public void SequelKeepsItsOwnCapturesTest()
        {
            var root = Path.Combine(Path.GetTempPath(), "atrio-secuela-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, @"Videos\Captures"));
                File.WriteAllBytes(Path.Combine(root, @"Videos\Captures\Hollow Knight 2026-11-01 10-00-00.png"), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(root, @"Videos\Captures\Hollow Knight Silksong 2026-11-02 10-00-00.png"), new byte[] { 1 });
                var places = new CapturePlaces { GameBar = Path.Combine(root, @"Videos\Captures"), Videos = Path.Combine(root, "Videos") };

                var library = new[] { "Hollow Knight", "Hollow Knight: Silksong" };
                var silksong = GameCaptures.Find(new[] { "Hollow Knight: Silksong" }, null, places, library.Where(a => a != "Hollow Knight: Silksong"));
                Assert.AreEqual(1, silksong.Count, "Only its own: the name without subtitle is another game of the library.");
                StringAssert.Contains("Silksong", silksong[0].Path);

                var original = GameCaptures.Find(new[] { "Hollow Knight" }, null, places, library.Where(a => a != "Hollow Knight"));
                Assert.AreEqual(1, original.Count);
                StringAssert.DoesNotContain("Silksong", original[0].Path);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void FindTest()
        {
            var root = Path.Combine(Path.GetTempPath(), "atrio-capturas-" + Guid.NewGuid().ToString("N"));
            try
            {
                string make(string path, DateTime date)
                {
                    var full = Path.Combine(root, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    File.WriteAllBytes(full, new byte[] { 1, 2, 3 });
                    File.SetLastWriteTime(full, date);
                    return full;
                }

                var day = new DateTime(2026, 11, 8, 12, 0, 0);
                var steam = make(@"steam\userdata\111\760\remote\570\screenshots\20260315_1.jpg", day.AddDays(-3));
                make(@"steam\userdata\111\760\remote\570\screenshots\thumbnails\20260315_1.jpg", day);
                make(@"steam\userdata\111\760\remote\999\screenshots\otro.jpg", day);
                var gameBar = make(@"Videos\Captures\Dota 2 2026-11-08 12-34-56.mp4", day.AddHours(1));
                make(@"Videos\Captures\Dota 2 notas.txt", day);
                make(@"Videos\Captures\shopping (900×495) - Google Chrome 2026-09-20 17-39-31.mp4", day);
                var discord = make(@"Videos\Discord Clips\Dota_2_a000bfe5-dafd-4dbc-8f65-cb46d7a3b2a1.mp4", day.AddHours(2));
                make(@"Videos\Discord Clips\Pantalla_1_fdb96eef-2e7c-42db-a438-36de5c314e17.mp4", day);
                var medal = make(@"Videos\Medal\Clips\Dota 2\clip.mp4", day.AddHours(3));
                var amd = make(@"Videos\Radeon ReLive\Dota 2\captura.png", day.AddHours(4));
                var nvidia = make(@"Videos\Dota 2\nvidia.mp4", day.AddHours(5));
                make(@"Videos\Valorant\otro.mp4", day);

                var places = new CapturePlaces
                {
                    SteamUserData = new List<string> { Path.Combine(root, @"steam\userdata") },
                    GameBar = Path.Combine(root, @"Videos\Captures"),
                    Videos = Path.Combine(root, "Videos")
                };

                var found = GameCaptures.Find(new[] { "Dota 2" }, new[] { "570" }, places);
                Assert.AreEqual(new[] { nvidia, amd, medal, discord, gameBar, steam }, found.Select(a => a.Path).ToArray(), "Newest first, only this game.");
                Assert.AreEqual(new[] { "NVIDIA", "AMD", "Medal", "Discord", "Xbox Game Bar", "Steam" }, found.Select(a => a.Source).ToArray());
                Assert.IsFalse(found.Single(a => a.Source == "Steam").IsVideo);
                Assert.IsTrue(found.Single(a => a.Source == "Discord").IsVideo);

                Assert.IsEmpty(GameCaptures.Find(new[] { "Hollow Knight" }, new string[0], places));
                Assert.IsEmpty(GameCaptures.Find(null, null, null));
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        /// <summary>The small window of Ubisoft Connect, drawn with the colors measured in real copies of it.</summary>
        private static Bitmap DrawUbisoft(int width, int height, bool yes, bool tick)
        {
            var page = new Bitmap(width, height);
            var scale = width / UbisoftDirectUninstallController.BaseWidth;
            using (var g = Graphics.FromImage(page))
            {
                g.Clear(Color.FromArgb(28, 33, 39));
                if (yes)
                {
                    using (var blue = new SolidBrush(Color.FromArgb(0, 110, 245)))
                    {
                        g.FillRectangle(blue, (float)(385 * scale), (float)(249 * scale), (float)(117 * scale), (float)(30 * scale));
                    }
                }

                if (tick)
                {
                    using (var white = new SolidBrush(Color.White))
                    {
                        g.FillRectangle(white, (float)(40 * scale), (float)(135 * scale), (float)(8 * scale), (float)(8 * scale));
                    }
                }
            }

            return page;
        }

        [Test]
        public void UbisoftPagesTest()
        {
            using (var question = DrawUbisoft(534, 314, true, false))
            using (var done = DrawUbisoft(534, 314, false, true))
            using (var bigger = DrawUbisoft(801, 471, true, false))
            using (var nothing = DrawUbisoft(534, 314, false, false))
            using (var otherShape = DrawUbisoft(714, 454, true, true))
            {
                Assert.AreEqual(UbisoftUninstallPage.Question, UbisoftDirectUninstallController.Classify(question));
                Assert.AreEqual(UbisoftUninstallPage.Done, UbisoftDirectUninstallController.Classify(done));
                Assert.AreEqual(UbisoftUninstallPage.Question, UbisoftDirectUninstallController.Classify(bigger), "Same window at 150 %.");
                Assert.AreEqual(UbisoftUninstallPage.Unknown, UbisoftDirectUninstallController.Classify(nothing));
                Assert.AreEqual(UbisoftUninstallPage.Unknown, UbisoftDirectUninstallController.Classify(otherShape), "The window of the installs has another shape.");
                Assert.AreEqual(UbisoftUninstallPage.Unknown, UbisoftDirectUninstallController.Classify(null));
            }
        }
    }
}
