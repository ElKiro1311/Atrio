using NUnit.Framework;
using Playnite.Atrio;
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Playnite.Tests
{
    [TestFixture]
    public class SavesAndTrailersTests
    {
        [Test]
        public void HashTest()
        {
            var data = Encoding.ASCII.GetBytes("abc");
            Assert.IsTrue(Ludusavi.HashMatches(data, "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
            Assert.IsTrue(Ludusavi.HashMatches(data, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
            Assert.IsFalse(Ludusavi.HashMatches(Encoding.ASCII.GetBytes("abd"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        }

        [Test]
        public void SameTitleTest()
        {
            // Only capitals, accents, spaces and signs.
            Assert.IsTrue(Ludusavi.SameTitle("Assassin's Creed® Unity", "Assassin's Creed Unity"));
            Assert.IsTrue(Ludusavi.SameTitle("THE WITCHER 3: WILD HUNT", "The Witcher 3: Wild Hunt"));
            Assert.IsTrue(Ludusavi.SameTitle("Tom Clancy’s Splinter Cell", "Tom Clancys Splinter Cell"));
            Assert.IsTrue(Ludusavi.SameTitle("Pokémon Legends", "Pokemon Legends"));
            Assert.IsTrue(Ludusavi.SameTitle("Ratchet & Clank", "Ratchet and Clank"));

            // A year or an edition could be another game.
            Assert.IsFalse(Ludusavi.SameTitle("Tomb Raider", "Tomb Raider (2013)"));
            Assert.IsFalse(Ludusavi.SameTitle("Tomb Raider", "Tomb Raider (1996)"));
            Assert.IsFalse(Ludusavi.SameTitle("Age of Empires II", "Age of Empires II: Definitive Edition"));
            Assert.IsFalse(Ludusavi.SameTitle("", ""));
            Assert.IsFalse(Ludusavi.SameTitle(null, "Doom"));
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        private static string[] SplitLikeWindows(string arguments)
        {
            var pointer = CommandLineToArgvW("ludusavi.exe " + arguments, out var count);
            try
            {
                return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size))).ToArray();
            }
            finally
            {
                LocalFree(pointer);
            }
        }

        [Test]
        public void QuoteTest()
        {
            var names = new[] { "Dead Cells", "Say \"hi\"", @"C:\Games\", @"a\\b", "a\\\"b", "--help", "Tom Clancy’s ™", "" };
            foreach (var name in names)
            {
                Assert.AreEqual(new[] { name }, SplitLikeWindows(Ludusavi.Quote(name)), name);
            }

            // The names go after "--", so one like "--help" isn't an option.
            Assert.AreEqual(new[] { "--", "Dead Cells", "--help" }, SplitLikeWindows(Ludusavi.NamesArguments(new[] { "Dead Cells", "--help", null })));
            Assert.AreEqual(string.Empty, Ludusavi.NamesArguments(new string[0]));
            Assert.AreEqual(new[] { "backup", "--force", "--path", @"D:\Atrio\Copias de partidas", "--format", "zip" },
                SplitLikeWindows("backup --force --path " + Ludusavi.Quote(@"D:\Atrio\Copias de partidas") + " --format zip"));
        }

        [Test]
        public void TrailersTest()
        {
            var json = @"{""1245620"":{""success"":true,""data"":{""movies"":[
                {""id"":1,""name"":""Gameplay"",""thumbnail"":""https://x/1.jpg"",""dash_av1"":""https://v/1/dash_av1.mpd"",""dash_h264"":""https://v/1/dash_h264.mpd"",""highlight"":false},
                {""id"":2,""name"":""Launch"",""thumbnail"":""https://x/2.jpg"",""dash_av1"":""https://v/2/dash_av1.mpd"",""dash_h264"":""https://v/2/dash_h264.mpd"",""highlight"":true},
                {""id"":3,""name"":""Old, without video"",""thumbnail"":""https://x/3.jpg"",""highlight"":true}]}}}";
            var trailers = SteamContent.ParseTrailers(json, "1245620");
            Assert.AreEqual(new[] { "Launch", "Gameplay" }, trailers.Select(a => a.Name).ToArray(), "The highlighted first; without video, out.");
            Assert.AreEqual("https://v/2/dash_h264.mpd", trailers[0].DashH264);
            Assert.AreEqual("https://v/2/dash_av1.mpd", trailers[0].DashAv1);

            Assert.IsEmpty(SteamContent.ParseTrailers(@"{""570"":{""success"":false}}", "570"));
            Assert.IsEmpty(SteamContent.ParseTrailers(@"{""570"":{""success"":true,""data"":[]}}", "570"));
            Assert.IsEmpty(SteamContent.ParseTrailers("<html>Access Denied</html>", "570"));
        }
    }
}
