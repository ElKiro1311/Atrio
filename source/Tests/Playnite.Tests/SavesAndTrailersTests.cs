using NUnit.Framework;
using Playnite.Atrio;
using System.Linq;
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
