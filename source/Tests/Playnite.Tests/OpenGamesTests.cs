using NUnit.Framework;
using Playnite.Atrio;
using Playnite.SDK.Events;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class OpenGamesTests
    {
        private static readonly List<string> shared = new List<string> { @"C:\Program Files", @"C:\Users" };

        private static KeyValuePair<Guid, string> Game(Guid id, string folder)
        {
            return new KeyValuePair<Guid, string>(id, folder);
        }

        private static KeyValuePair<int, string> Process(int id, string path)
        {
            return new KeyValuePair<int, string>(id, path);
        }

        [Test]
        public void FolderKeyTest()
        {
            Assert.IsNull(OpenGames.GetFolderKey(null, shared));
            Assert.IsNull(OpenGames.GetFolderKey("  ", shared));
            Assert.IsNull(OpenGames.GetFolderKey(@"Juegos\Undertale", shared));
            Assert.IsNull(OpenGames.GetFolderKey(@"D:\", shared));
            Assert.IsNull(OpenGames.GetFolderKey(@"C:\Program Files\", shared));
            Assert.IsNull(OpenGames.GetFolderKey(@"c:\users", shared));
            Assert.IsNull(OpenGames.GetFolderKey(@"D:\SteamLibrary", shared));
            Assert.IsNull(OpenGames.GetFolderKey(@"D:\SteamLibrary\steamapps", shared));
            Assert.IsNull(OpenGames.GetFolderKey(@"D:\SteamLibrary\steamapps\common", shared));
            Assert.IsNull(OpenGames.GetFolderKey(@"C:\XboxGames", shared));
            Assert.AreEqual(@"D:\SteamLibrary\steamapps\common\Undertale\", OpenGames.GetFolderKey(@"D:\SteamLibrary\steamapps\common\Undertale", shared));
            Assert.AreEqual(@"C:\Program Files\EA Games\Juego\", OpenGames.GetFolderKey(@"C:\Program Files\EA Games\Juego\", shared));
            Assert.AreEqual(@"D:\Witcher3\", OpenGames.GetFolderKey(@" D:\Witcher3 ", shared));
        }

        [Test]
        public void LibraryFoldersAreLeftOutTest()
        {
            var library = Guid.NewGuid();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            var folders = OpenGames.GetFolders(new[]
            {
                Game(library, @"D:\Juegos"),
                Game(first, @"D:\Juegos\Uno"),
                Game(second, @"D:\Juegos\Dos")
            }, shared);

            CollectionAssert.AreEquivalent(new[] { @"D:\Juegos\Uno\", @"D:\Juegos\Dos\" }, folders.Select(a => a.Folder));
        }

        [Test]
        public void GameWithOneInsideStaysTest()
        {
            var game = Guid.NewGuid();
            var extra = Guid.NewGuid();
            var folders = OpenGames.GetFolders(new[]
            {
                Game(game, @"D:\Juego"),
                Game(extra, @"D:\Juego\Extra")
            }, shared);

            Assert.AreEqual(new[] { @"D:\Juego\Extra\", @"D:\Juego\" }, folders.Select(a => a.Folder).ToArray(), "deepest first");
        }

        [Test]
        public void SharedRomFoldersAreLeftOutTest()
        {
            var roms = Enumerable.Range(0, OpenGames.MaxGamesPerFolder + 1).Select(_ => Game(Guid.NewGuid(), @"D:\Roms\SNES")).ToList();
            var twins = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var folders = OpenGames.GetFolders(roms.Concat(twins.Select(a => Game(a, @"D:\Juego"))), shared);

            Assert.AreEqual(1, folders.Count);
            CollectionAssert.AreEquivalent(twins, folders[0].Games);
        }

        [Test]
        public void MatchTest()
        {
            var game = Guid.NewGuid();
            var extra = Guid.NewGuid();
            var twins = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var folders = OpenGames.GetFolders(new[]
            {
                Game(game, @"D:\X"),
                Game(extra, @"D:\X\DLC"),
                Game(twins[0], @"E:\Doble"),
                Game(twins[1], @"E:\Doble")
            }, shared);

            var open = OpenGames.Match(folders, new[]
            {
                Process(10, @"D:\X\DLC\run.exe"),
                Process(11, @"d:\x\bin\game.exe"),
                Process(12, @"D:\Xtra\game.exe"),
                Process(13, @"E:\Doble\juego.exe"),
                Process(14, null)
            });

            CollectionAssert.AreEquivalent(new[] { 10 }, open[extra]);
            CollectionAssert.AreEquivalent(new[] { 11 }, open[game]);
            CollectionAssert.AreEquivalent(new[] { 13 }, open[twins[0]]);
            CollectionAssert.AreEquivalent(new[] { 13 }, open[twins[1]]);
            Assert.AreEqual(4, open.Count, @"D:\Xtra is not D:\X");
        }

        [Test]
        public void HelpersAreNotTheGameTest()
        {
            Assert.IsTrue(OpenGames.IsHelper(@"C:\GOG Games\Samorost 1\unins000.exe"));
            Assert.IsTrue(OpenGames.IsHelper(@"D:\Steam\steamapps\common\Juego\_CommonRedist\vcredist\2019\VC_redist.x64.exe"));
            Assert.IsTrue(OpenGames.IsHelper(@"C:\Program Files\EA Games\Juego\__Installer\Cleanup.exe"));
            Assert.IsTrue(OpenGames.IsHelper(@"D:\Juego\Redist\DXSETUP.exe"));
            Assert.IsTrue(OpenGames.IsHelper(@"D:\Juego\Uninstall Juego.exe"));
            Assert.IsTrue(OpenGames.IsHelper(null));
            Assert.IsFalse(OpenGames.IsHelper(@"D:\Juego\Juego.exe"));
            Assert.IsFalse(OpenGames.IsHelper(@"D:\Juego\Binaries\Win64\Juego-Win64-Shipping.exe"));
            Assert.IsFalse(OpenGames.IsHelper(@"D:\Juego\UnityCrashHandler64.exe"));
        }

        [Test]
        public void CompareTest()
        {
            var stays = Guid.NewGuid();
            var closed = Guid.NewGuid();
            var opened = Guid.NewGuid();
            var change = OpenGames.Compare(
                new Dictionary<Guid, List<int>> { { stays, new List<int> { 1 } }, { closed, new List<int> { 2 } } },
                new Dictionary<Guid, List<int>> { { stays, new List<int> { 3 } }, { opened, new List<int> { 4 } } });

            CollectionAssert.AreEqual(new[] { opened }, change.Opened);
            CollectionAssert.AreEqual(new[] { closed }, change.Closed);
            Assert.IsFalse(change.IsEmpty);
            Assert.IsTrue(OpenGames.Compare(new Dictionary<Guid, List<int>>(), new Dictionary<Guid, List<int>>()).IsEmpty);
        }

        [Test]
        public void GuideComboTest()
        {
            var start = new DateTime(2026, 11, 8, 4, 0, 0);
            var combo = new GuideCombo();
            combo.Update(ControllerInput.Back, true, start);
            Assert.IsFalse(combo.IsHolding, "one button alone");
            combo.Update(ControllerInput.A, true, start);
            combo.Update(ControllerInput.Start, true, start.AddMilliseconds(200));
            Assert.IsTrue(combo.IsHolding);
            Assert.IsFalse(combo.ShouldFire(start.AddMilliseconds(900)));
            Assert.IsTrue(combo.ShouldFire(start.AddMilliseconds(1200)));
            Assert.IsFalse(combo.ShouldFire(start.AddMilliseconds(2500)), "once per hold");
            Assert.IsFalse(combo.IsHolding);

            combo.Update(ControllerInput.Start, false, start.AddSeconds(3));
            combo.Update(ControllerInput.Start, true, start.AddSeconds(4));
            Assert.IsFalse(combo.ShouldFire(start.AddSeconds(4.5)));
            combo.Update(ControllerInput.Back, false, start.AddSeconds(4.6));
            Assert.IsFalse(combo.ShouldFire(start.AddSeconds(6)), "let go before the second");

            combo.Update(ControllerInput.Back, true, start.AddSeconds(7));
            Assert.IsTrue(combo.ShouldFire(start.AddSeconds(8)), "a new hold fires again");
        }
    }
}
