using NUnit.Framework;
using Playnite.Atrio;

namespace Playnite.Tests
{
    [TestFixture]
    public class LinkBrowsersTests
    {
        [Test]
        public void OnlyTheWebGoesToABrowserTest()
        {
            Assert.IsTrue(LinkBrowsers.IsWebAddress("https://store.steampowered.com/app/1"));
            Assert.IsTrue(LinkBrowsers.IsWebAddress("http://example.com"));
            Assert.IsFalse(LinkBrowsers.IsWebAddress("steam://run/1"));
            Assert.IsFalse(LinkBrowsers.IsWebAddress(@"C:\Games\manual.pdf"));
            Assert.IsFalse(LinkBrowsers.IsWebAddress("mailto:someone@example.com"));
            Assert.IsFalse(LinkBrowsers.IsWebAddress(null));
        }

        [Test]
        public void PagesOnlyStartTheClientsOfTheStoresTest()
        {
            Assert.IsTrue(LinkBrowsers.IsStoreClientLink("steam://install/1"));
            Assert.IsTrue(LinkBrowsers.IsStoreClientLink("STEAM://open/friends"));
            Assert.IsTrue(LinkBrowsers.IsStoreClientLink("com.epicgames.launcher://store/p/game"));
            Assert.IsFalse(LinkBrowsers.IsStoreClientLink("https://store.steampowered.com"));
            Assert.IsFalse(LinkBrowsers.IsStoreClientLink("ms-msdt:/id"));
            Assert.IsFalse(LinkBrowsers.IsStoreClientLink("file:///c:/windows/system32/calc.exe"));
            Assert.IsFalse(LinkBrowsers.IsStoreClientLink(null));
        }

        [Test]
        public void ProgramComesOutOfTheCommandTest()
        {
            Assert.AreEqual(@"C:\Program Files\Browser\browser.exe", LinkBrowsers.GetProgram("\"C:\\Program Files\\Browser\\browser.exe\" --single-argument %1"));
            Assert.AreEqual(@"C:\Browser\browser.exe", LinkBrowsers.GetProgram(@"C:\Browser\browser.exe -url %1"));
            Assert.AreEqual(@"C:\Browser\browser.exe", LinkBrowsers.GetProgram("\"C:\\Browser\\browser.exe\""));
            Assert.IsNull(LinkBrowsers.GetProgram(string.Empty));
            Assert.IsNull(LinkBrowsers.GetProgram(null));
        }

        [Test]
        public void PagesAreAskedInTheLanguageOfTheApplicationTest()
        {
            Assert.AreEqual("es-ES,es,en", LinkBrowsers.GetAcceptLanguage("es_ES"));
            Assert.AreEqual("pt-BR,pt,en", LinkBrowsers.GetAcceptLanguage("pt_BR"));
            Assert.IsNull(LinkBrowsers.GetAcceptLanguage("english"));
            Assert.IsNull(LinkBrowsers.GetAcceptLanguage("en_US"));
            Assert.IsNull(LinkBrowsers.GetAcceptLanguage(null));
        }
    }
}
