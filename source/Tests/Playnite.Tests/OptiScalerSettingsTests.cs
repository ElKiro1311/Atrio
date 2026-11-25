using Atrio.OptiScaler;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Playnite.Tests
{
    [TestFixture]
    public class OptiScalerSettingsTests
    {
        /// <summary>As the packages of the 0.9 bring them.</summary>
        private const string Stable =
            "[Upscalers]\r\n" +
            "; -------------------------------------------------------\r\n" +
            "; Select Upscaler for Dx11 games\r\n" +
            "; fsr22 (native DX11), fsr31 (native DX11), xess_12 (dx11on12), fsr31_12 (dx11on12, FSR4), dlss - Default (auto) is fsr22\r\n" +
            "Dx11Upscaler=auto\r\n" +
            "\r\n" +
            "; Select Upscaler for Dx12 games\r\n" +
            "; xess, fsr21, fsr22, fsr31 (also for FSR4), dlss - Default (auto) is xess\r\n" +
            "Dx12Upscaler=auto\r\n" +
            "\r\n" +
            "[FrameGen]\r\n" +
            "; Enables Frame Generation\r\n" +
            "; true or false - Default (auto) is false\r\n" +
            "Enabled=auto\r\n" +
            "; upscaler  - Upscaler must be enabled\r\n" +
            "; nofg, dlssg, nukems, fsrfg, upscaler, fsrfg30 - Default (auto) is nofg\r\n" +
            "FGInput=auto\r\n" +
            "; nofg, fsrfg, xefg, nukems - Default (auto) is nofg\r\n" +
            "FGOutput=auto\r\n" +
            "\r\n" +
            "[XeFG]\r\n" +
            "; 1 = 2X | 2 = 3X | 3 = 4X\r\n" +
            "; true or false - Default (auto) is 1\r\n" +
            "InterpolationCount=auto\r\n" +
            "; true or false - Default (auto) is true\r\n" +
            "DepthInverted=auto\r\n" +
            "\r\n" +
            "[OptiFG]\r\n" +
            "; true or false - Default (auto) is false\r\n" +
            "HUDFix=auto\r\n" +
            "\r\n" +
            "[FSR]\r\n" +
            "; Depending on GPU - Default (auto) is 0 - FSR4 for RDNA4, FSR3 for the rest\r\n" +
            "UpscalerIndex=auto\r\n" +
            "; true or false - Default (auto) depends on GPU - true for RDNA2, RDNA3 and RDNA4\r\n" +
            "Fsr4Update=auto\r\n" +
            "; From 0 to 5 - Default (auto) is game's default\r\n" +
            "Fsr4Preset=auto\r\n" +
            "\r\n" +
            "[Nukems]\r\n" +
            "MakeDepthCopy=auto\r\n" +
            "\r\n" +
            "[Log]\r\n" +
            "; Default (auto) is 0 = Trace\r\n" +
            "LogLevel=auto\r\n" +
            "\r\n" +
            "[UpscaleRatio]\r\n" +
            "; Set this to true to enable the internal Resolution Override\r\n" +
            "; true or false - Default (auto) is false\r\n" +
            "UpscaleRatioOverrideEnabled=auto\r\n" +
            "; Set the forced Upscale Ratio value\r\n" +
            "; Default (auto) is 1.3\r\n" +
            "UpscaleRatioOverrideValue=auto\r\n";

        /// <summary>As the ones of the 10 do.</summary>
        private const string Nightly =
            "[Upscalers]\n" +
            "; fsr22 (native DX11), fsr31 (native DX11), xess_12 (dx11on12), ffx_12 (FSR 2.3; 3.1; 4.x), dlss - Default (auto) is fsr22\n" +
            "; or the name of a custom upscaler with 'ffx' or 'dlss', run through dx11on12\n" +
            "Dx11Upscaler=auto\n" +
            "\n" +
            "; xess, fsr21, fsr22, ffx (FSR 2.3; 3.1; 4.x), dlss\n" +
            "; or the name of a custom upscaler: a folder in the 'custom' folder or one registered by an ASI plugin\n" +
            "; Default (auto) is DLSS when capable gpu, FSR4 when capable gpu, XeSS otherwise\n" +
            "Dx12Upscaler=auto\n" +
            "\n" +
            "[FrameGen]\n" +
            "; Enables Frame Generation\n" +
            "; true or false - Default (auto) is false\n" +
            "; With dlssg FG Input, auto follows the game's DLSS FG setting (on/off and frame multiplier)\n" +
            "Enabled=auto\n" +
            "\n" +
            "; nofg, dlssg, nvngxfg, fsrfg, upscaler, fsrfg30, xefg - Default (auto) is nofg\n" +
            "FGInput=auto\n" +
            "\n" +
            "; nofg, fsrfg, xefg, dlssg, flipmeter - Default (auto) is nofg\n" +
            "FGOutput=auto\n" +
            "\n" +
            "; None, Nukems, Arturs, FFX, Combo - Default (auto) is Nukems\n" +
            "FGNvngxReplacement=auto\n" +
            "\n" +
            "[XeFG]\n" +
            "; Default (auto) is 0.0 (FramerateLimit, or just under the display refresh rate: 116 at 120 Hz)\n" +
            "FramerateTargetDMFG=auto\n" +
            "[OptiFG]\n" +
            "; true or false - Default (auto) is false\n" +
            "HUDFix=auto\n" +
            "[FSR]\n" +
            "Fsr4Preset=auto\n" +
            "; From 0 to 4 - Default (auto) is 0\n" +
            "Fsr4GpuSupport=auto\n" +
            "[NvngxFG]\n" +
            "MakeDepthCopy=auto\n" +
            "[Menu]\n" +
            "; 0.5 to 2.0 - Default (auto) is 1.0, gets lower below 900p\n" +
            "Scale=auto\n" +
            "Blur=auto\n" +
            "[UpscaleRatio]\n" +
            "; true or false - Default (auto) is false\n" +
            "UpscaleRatioOverrideEnabled=auto\n" +
            "; Default (auto) is 1.3\n" +
            "UpscaleRatioOverrideValue=auto\n";

        // What OptiScaler takes by itself with each graphics card.
        private static readonly Dictionary<string, string> rdna4 = Automatic("ffx", "fsr22");
        private static readonly Dictionary<string, string> older = Automatic("xess", "fsr22");
        private static readonly Dictionary<string, string> nvidia = Automatic("dlss", "dlss");
        private static readonly Dictionary<string, string> unknown = Automatic(null, "fsr22");

        private static Dictionary<string, string> Automatic(string dx12, string others)
        {
            var result = new Dictionary<string, string> { { "upscalers|dx11upscaler", others }, { "upscalers|vulkanupscaler", others } };
            if (dx12 != null)
            {
                result.Add("upscalers|dx12upscaler", dx12);
            }

            return result;
        }

        private static IniFile Change(string original, params string[] changes)
        {
            var ini = IniFile.Parse(original);
            foreach (var change in changes)
            {
                var parts = change.Split('|', '=');
                ini.Set(parts[0], parts[1], parts[2]);
            }

            return ini;
        }

        /// <summary>
        /// What OptiScaler leaves when its settings are saved from its menu: its own spaces, the numbers with six decimals, and as
        /// left to it what is the same as its default, except the upscalers, where it writes the one it's using.
        /// </summary>
        private static IniFile AsTheGameSaves(IniFile ini, params string[] used)
        {
            var saved = IniFile.Parse(ini.ToString());
            foreach (var setting in ini.All().Where(a => a.Section != "Upscalers" && OwnSettings.IsSame(a, "auto", ini.Default(a.Section, a.Key))))
            {
                saved.Set(setting.Section, setting.Key, "auto");
            }

            foreach (var setting in saved.All().Where(a => a.Value.Contains('.')))
            {
                saved.Set(setting.Section, setting.Key, setting.Value + new string('0', 7 - setting.Value.Substring(setting.Value.IndexOf('.')).Length));
            }

            foreach (var one in used)
            {
                var parts = one.Split('|', '=');
                saved.Set(parts[0], parts[1], parts[2]);
            }

            return IniFile.Parse(string.Join("\r\n", saved.ToString().Split(new[] { "\r\n" }, StringSplitOptions.None).
                Select(a => a.StartsWith(";") || a.StartsWith("[") ? a : a.Replace("=", " = "))));
        }

        private static string[] Text(IEnumerable<Setting> settings)
        {
            return settings.Select(a => a.ToString()).ToArray();
        }

        [Test]
        public void TheSettingsAreReadHoweverTheyAreWrittenTest()
        {
            var ini = IniFile.Parse("; comment=1\r\n[ One ]\r\nKey=value\r\n  Other  =  with = equals \r\n# not=this\r\n[Two]\r\nkey = \r\n\r\nalone\r\n");
            Assert.AreEqual("value", ini.Get("one", "KEY"));
            Assert.AreEqual("with = equals", ini.Get("One", "Other"));
            Assert.AreEqual(string.Empty, ini.Get("Two", "key"));
            Assert.IsNull(ini.Get("Two", "Other"));
            Assert.IsNull(ini.Get("Three", "Key"));
            Assert.IsTrue(ini.Has("Two", "KEY"));
            Assert.AreEqual(3, ini.All().Count);
            Assert.AreEqual(0, IniFile.Parse(null).All().Count);

            var mark = Encoding.GetEncoding("iso-8859-1").GetString(Encoding.UTF8.GetPreamble());
            Assert.AreEqual("a", IniFile.Parse(mark + "[One]\r\nKey=a").Get("One", "Key"), "With the mark of UTF-8.");
        }

        [Test]
        public void TheLastOneCountsTest()
        {
            var ini = IniFile.Parse("[One]\r\nKey = auto\r\nOther=1\r\nKey=true\r\n[Two]\r\nKey=2\r\n[One]\r\nKey=false\r\n");
            Assert.AreEqual("false", ini.Get("One", "Key"));
            ini.Tidy();
            Assert.AreEqual("[One]\r\nKey=false\r\nOther=1\r\n[Two]\r\nKey=2\r\n[One]\r\n", ini.ToString());
            Assert.AreEqual("false", ini.Get("One", "Key"));
            ini.Set("One", "Key", "3");
            Assert.AreEqual("3", ini.Get("One", "Key"));
            Assert.AreEqual("1", ini.Get("One", "Other"));
        }

        [Test]
        public void OnlyWhatChangesIsWrittenTest()
        {
            var ini = IniFile.Parse(Stable);
            ini.Set("FrameGen", "FGInput", "upscaler");
            ini.Set("OptiFG", "New", "1");
            ini.Set("Another", "Key", "2");
            Assert.AreEqual(
                Stable.Replace("FGInput=auto", "FGInput=upscaler").Replace("HUDFix=auto\r\n", "HUDFix=auto\r\nNew=1\r\n") + "\r\n[Another]\r\nKey=2\r\n",
                ini.ToString());
            Assert.AreEqual("2", ini.Get("Another", "Key"));

            // A section with nothing in it, and one that is the last.
            ini = IniFile.Parse("[One]\r\n[Two]\r\nKey=1");
            ini.Set("One", "Key", "2");
            ini.Set("Two", "Other", "3");
            Assert.AreEqual("[One]\r\nKey=2\r\n[Two]\r\nKey=1\r\nOther=3", ini.ToString());
            ini.Set("Three", "Key", "4");
            Assert.AreEqual("[One]\r\nKey=2\r\n[Two]\r\nKey=1\r\nOther=3\r\n\r\n[Three]\r\nKey=4\r\n", ini.ToString());
        }

        [Test]
        public void TheFileKeepsItsBytesTest()
        {
            var folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(folder);
            try
            {
                // A path written by the client, by the game and by an editor: each one has its own way, with a mark or without it.
                var path = Path.Combine(folder, "OptiScaler.ini");
                var content = Encoding.UTF8.GetBytes("[Libraries]\r\nXeSSPath=C:\\Juegos\\Año\r\n").Concat(new byte[] { 0xF1, 0x3D, 0x31, 0x0D, 0x0A }).ToArray();
                foreach (var written in new[] { content, Encoding.UTF8.GetPreamble().Concat(content).ToArray(), content.Take(content.Length - 2).ToArray(), new byte[0] })
                {
                    File.WriteAllBytes(path, written);
                    IniFile.Read(path).Save(path);
                    Assert.AreEqual(written, File.ReadAllBytes(path));
                }

                File.WriteAllBytes(path, content);
                var ini = IniFile.Read(path);
                ini.Set("Libraries", "XeSSPath", "auto");
                ini.Save(Path.Combine(folder, "new.ini"));
                Assert.AreEqual("auto", IniFile.Read(Path.Combine(folder, "new.ini")).Get("Libraries", "XeSSPath"));
                Assert.AreEqual(2, Directory.GetFiles(folder).Length, "Nothing is left behind.");
                Assert.IsNull(IniFile.Read(Path.Combine(folder, "none.ini")));
                Assert.IsNull(IniFile.Read(null));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Test]
        public void TheDefaultTheFileTellsTest()
        {
            var stable = IniFile.Parse(Stable);
            var nightly = IniFile.Parse(Nightly);
            Assert.AreEqual("fsr22", stable.Default("Upscalers", "Dx11Upscaler"));
            Assert.AreEqual("false", stable.Default("FrameGen", "Enabled"));
            Assert.AreEqual("false", nightly.Default("FrameGen", "Enabled"), "It isn't in the last line.");
            Assert.AreEqual("1", stable.Default("XeFG", "InterpolationCount"));
            Assert.AreEqual("0", stable.Default("FSR", "UpscalerIndex"));
            Assert.AreEqual("0", stable.Default("Log", "LogLevel"));
            Assert.AreEqual("0.0", nightly.Default("XeFG", "FramerateTargetDMFG"));
            Assert.AreEqual("1.3", stable.Default("UpscaleRatio", "UpscaleRatioOverrideValue"));

            // It depends, or it isn't told.
            Assert.IsNull(nightly.Default("Upscalers", "Dx12Upscaler"));
            Assert.IsNull(nightly.Default("Menu", "Scale"));
            Assert.IsNull(nightly.Default("Menu", "Blur"));
            Assert.IsNull(stable.Default("FSR", "Fsr4Update"));
            Assert.IsNull(stable.Default("FSR", "Fsr4Preset"));
            Assert.IsNull(stable.Default("Nowhere", "Nothing"));
        }

        [Test]
        public void WhatAPersonChangedTest()
        {
            // How it was left, and then the game was opened, something was changed and it was saved.
            var left = Change(
                Stable, "FrameGen|Enabled=false", "XeFG|InterpolationCount=1", "Log|LogLevel=2", "UpscaleRatio|UpscaleRatioOverrideEnabled=true", "UpscaleRatio|UpscaleRatioOverrideValue=1.5",
                "Upscalers|Dx11Upscaler=fsr31_12");
            Assert.IsEmpty(OwnSettings.Find(AsTheGameSaves(left), left, rdna4), "Saving changes nothing.");
            var now = AsTheGameSaves(Change(left.ToString(), "FSR|Fsr4Preset=0"));
            Assert.AreEqual("1.500000", now.Get("UpscaleRatio", "UpscaleRatioOverrideValue"));
            Assert.AreEqual("auto", now.Get("FrameGen", "Enabled"));
            Assert.AreEqual(new[] { "Fsr4Preset=0" }, Text(OwnSettings.Find(now, left, rdna4)));
            Assert.IsEmpty(OwnSettings.Find(null, left));

            // What the panel put is the same as the default: it's saved as left to it.
            left = Change(Stable, "UpscaleRatio|UpscaleRatioOverrideEnabled=false", "UpscaleRatio|UpscaleRatioOverrideValue=1.3");
            now = AsTheGameSaves(left);
            Assert.AreEqual("auto", now.Get("UpscaleRatio", "UpscaleRatioOverrideValue"));
            Assert.IsEmpty(OwnSettings.Find(now, left));

            // A newer one writes that one with its new name.
            left = Change(Stable, "UpscaleRatio|UpscaleRatioOverrideValue=1.5", "Upscalers|Dx11Upscaler=fsr31_12");
            now = Change(left.ToString(), "Upscalers|Dx11Upscaler=ffx_12");
            Assert.IsEmpty(OwnSettings.Find(now, left));
            now = Change(left.ToString(), "Upscalers|Dx11Upscaler=xess_12", "UpscaleRatio|UpscaleRatioOverrideValue=auto");
            Assert.AreEqual(new[] { "Dx11Upscaler=xess_12", "UpscaleRatioOverrideValue=auto" }, Text(OwnSettings.Find(now, left)), "Leaving it to OptiScaler is a choice too.");

            // Nobody else put it: whatever isn't left to OptiScaler, or what it takes by itself.
            Assert.AreEqual(new[] { "Dx11Upscaler=fsr31_12", "UpscaleRatioOverrideValue=1.5" }, Text(OwnSettings.Find(left, null)));
            Assert.AreEqual(
                new[] { "Dx11Upscaler=fsr31_12", "Enabled=false" },
                Text(OwnSettings.Find(Change(Stable, "Upscalers|Dx11Upscaler=fsr31_12", "FrameGen|Enabled=false", "Upscalers|Dx12Upscaler=xess"), null)));
            Assert.IsEmpty(OwnSettings.Find(IniFile.Parse(Nightly), null));

            // A value that the file calls the default, where OptiScaler has another one: it wouldn't have saved it otherwise.
            left = IniFile.Parse(Nightly);
            Assert.AreEqual(new[] { "FramerateTargetDMFG=0.000000" }, Text(OwnSettings.Find(Change(Nightly, "XeFG|FramerateTargetDMFG=0.000000"), left)));
            Assert.AreEqual(new[] { "FramerateTargetDMFG=0.000000" }, Text(OwnSettings.Find(Change(Nightly, "XeFG|FramerateTargetDMFG=0.000000"), null)));
        }

        [Test]
        public void TheUpscalerInUseTest()
        {
            string[] own(string before, string used, Dictionary<string, string> automatic)
            {
                var left = IniFile.Parse(before);
                return Text(OwnSettings.Find(AsTheGameSaves(left, used), left, automatic));
            }

            // It writes the one it's using, whatever was saved.
            Assert.IsEmpty(own(Stable, "Upscalers|Dx11Upscaler=fsr22", older));
            Assert.IsEmpty(own(Stable, "Upscalers|Dx12Upscaler=xess", older));
            Assert.IsEmpty(own(Stable, "Upscalers|Dx12Upscaler=fsr31", rdna4));
            Assert.IsEmpty(own(Nightly, "Upscalers|Dx12Upscaler=ffx", rdna4));
            Assert.IsEmpty(own(Nightly, "Upscalers|Dx12Upscaler=dlss", nvidia));
            Assert.IsEmpty(own(Nightly, "Upscalers|Dx11Upscaler=dlss", nvidia));
            Assert.IsEmpty(own(Nightly, "Upscalers|Dx12Upscaler=fsr21", rdna4), "The one it falls back to.");
            Assert.IsEmpty(own(Stable, "Upscalers|Dx12Upscaler=xess", null), "Without knowing the card, what its file tells.");

            // One it wouldn't take by itself.
            Assert.AreEqual(new[] { "Dx12Upscaler=xess" }, own(Stable, "Upscalers|Dx12Upscaler=xess", rdna4));
            Assert.AreEqual(new[] { "Dx12Upscaler=fsr31" }, own(Stable, "Upscalers|Dx12Upscaler=fsr31", older));
            Assert.AreEqual(new[] { "Dx12Upscaler=ffx" }, own(Nightly, "Upscalers|Dx12Upscaler=ffx", nvidia));
            Assert.AreEqual(new[] { "Dx12Upscaler=ffx" }, own(Nightly, "Upscalers|Dx12Upscaler=ffx", unknown));
            Assert.AreEqual(new[] { "Dx12Upscaler=fsr22" }, own(Nightly, "Upscalers|Dx12Upscaler=fsr22", older));
            Assert.AreEqual(new[] { "Dx11Upscaler=ffx_12" }, own(Nightly, "Upscalers|Dx11Upscaler=ffx_12", rdna4));
            Assert.AreEqual(new[] { "Dx11Upscaler=fsr22" }, own(Nightly, "Upscalers|Dx11Upscaler=fsr22", nvidia));

            // With one chosen in the panel, another one is a choice, but not the one it falls back to.
            var chosen = Change(Stable, "Upscalers|Dx12Upscaler=fsr31").ToString();
            Assert.AreEqual(new[] { "Dx12Upscaler=xess" }, own(chosen, "Upscalers|Dx12Upscaler=xess", older));
            Assert.IsEmpty(own(chosen, "Upscalers|Dx12Upscaler=fsr21", older));
            Assert.IsEmpty(own(chosen, "Upscalers|Dx12Upscaler=ffx", rdna4));

            // Put by hand.
            Assert.IsEmpty(OwnSettings.Find(Change(Nightly, "Upscalers|Dx12Upscaler=ffx"), null, rdna4));
            Assert.AreEqual(new[] { "Dx12Upscaler=xess" }, Text(OwnSettings.Find(Change(Nightly, "Upscalers|Dx12Upscaler=xess"), null, rdna4)));
        }

        [Test]
        public void WhatOptiScalerWritesByItselfTest()
        {
            // What it finds out of the game.
            Assert.IsEmpty(OwnSettings.Find(Change(Stable, "XeFG|DepthInverted=false"), IniFile.Parse(Stable)));
            Assert.IsEmpty(OwnSettings.Find(Change(Stable, "XeFG|DepthInverted=false"), null));
            Assert.IsEmpty(OwnSettings.Find(Change(Nightly, "FrameGen|FGNvngxReplacement=None"), IniFile.Parse(Nightly)));
            Assert.AreEqual(new[] { "FGNvngxReplacement=Arturs" }, Text(OwnSettings.Find(Change(Nightly, "FrameGen|FGNvngxReplacement=Arturs"), IniFile.Parse(Nightly))));

            // The newest name of what the client wrote for the FSR 4 it put, and what an older one makes of it.
            var left = Change(Nightly, "FSR|Fsr4ForceModel=2");
            Assert.IsEmpty(OwnSettings.Find(Change(Nightly, "FSR|Fsr4GpuSupport=2"), left));
            left = Change(Stable, "FSR|Fsr4ForceEnableInt8=true");
            Assert.IsEmpty(OwnSettings.Find(Change(Stable, "FSR|Fsr4ForceEnableInt8=auto"), left));
            Assert.AreEqual(new[] { "Fsr4GpuSupport=2" }, Text(OwnSettings.Find(Change(Nightly, "FSR|Fsr4GpuSupport=2"), IniFile.Parse(Nightly))));

            Assert.IsEmpty(OwnSettings.Find(Change(Stable, "InitFlags|DisableReactiveMask=false"), IniFile.Parse(Stable)));
            Assert.AreEqual(new[] { "DisableReactiveMask=true" }, Text(OwnSettings.Find(Change(Stable, "InitFlags|DisableReactiveMask=true"), IniFile.Parse(Stable))));

            // A name of the client that version doesn't know.
            left = Change(Nightly, "FrameGen|FGInput=nvngxfg", "FrameGen|FGOutput=nvngxfg", "FrameGen|FGNvngxReplacement=Nukems", "FrameGen|Enabled=true");
            var now = AsTheGameSaves(left, "FrameGen|FGOutput=auto");
            Assert.AreEqual("auto", now.Get("FrameGen", "FGNvngxReplacement"));
            Assert.IsEmpty(OwnSettings.Find(now, left));
        }

        [Test]
        public void TheSameValueTest()
        {
            bool same(string section, string key, string one, string other, string byDefault = null) => OwnSettings.IsSame(new Setting(section, key, one), other, byDefault);
            Assert.IsTrue(same("A", "B", "auto", null));
            Assert.IsTrue(same("A", "B", "", "AUTO"));
            Assert.IsTrue(same("A", "B", "True", "true"));
            Assert.IsTrue(same("A", "B", "0x10DE", "0x10de"));
            Assert.IsTrue(same("A", "B", "0x10", "16"));
            Assert.IsTrue(same("A", "B", "1", "1.000000"));
            Assert.IsFalse(same("A", "B", "1", "1.1"));
            Assert.IsFalse(same("A", "B", "auto", "false"));
            Assert.IsTrue(same("A", "B", "auto", "false", "false"));
            Assert.IsTrue(same("A", "B", "1.300000", "auto", "1.3"));
            Assert.IsFalse(same("A", "B", "auto", "true", "false"));
            Assert.IsTrue(same("A", "B", "auto", null, "false"));
            Assert.IsTrue(same("Upscalers", "Dx12Upscaler", "fsr31", "ffx"));
            Assert.IsFalse(same("Upscalers", "Dx11Upscaler", "fsr31", "ffx_12"), "There, that name is of another one.");
            Assert.IsFalse(same("A", "Dx12Upscaler", "fsr31", "ffx"));
            Assert.IsTrue(same("FrameGen", "FGInput", "Nukems", "nvngxfg"));
        }

        [Test]
        public void ASettingInAnotherVersionTest()
        {
            var stable = IniFile.Parse(Stable);
            var nightly = IniFile.Parse(Nightly);
            string fit(string section, string key, string value, IniFile version, IniFile was = null) => OwnSettings.Fit(new Setting(section, key, value), version, was)?.ToString();

            // The ones both have, and the one that changed its name.
            Assert.AreEqual("Dx11Upscaler=fsr31_12", fit("Upscalers", "Dx11Upscaler", "ffx_12", stable, nightly));
            Assert.AreEqual("Dx11Upscaler=ffx_12", fit("Upscalers", "Dx11Upscaler", "fsr31_12", nightly, stable));
            Assert.AreEqual("Dx11Upscaler=fsr31", fit("Upscalers", "Dx11Upscaler", "fsr31", nightly, stable));
            Assert.AreEqual("Dx12Upscaler=ffx", fit("Upscalers", "Dx12Upscaler", "FSR31", nightly, stable));
            Assert.AreEqual("Dx12Upscaler=ffx", fit("Upscalers", "Dx12Upscaler", "fsr31", nightly, nightly), "The older name, which that version takes too.");
            Assert.AreEqual("Dx12Upscaler=fsr31", fit("Upscalers", "Dx12Upscaler", "ffx", stable, nightly));
            Assert.AreEqual("FGInput=upscaler", fit("FrameGen", "FGInput", "upscaler", stable, nightly));
            Assert.AreEqual("FGInput=nvngxfg", fit("FrameGen", "FGInput", "Nukems", nightly, stable));
            Assert.AreEqual("FGInput=nukems", fit("FrameGen", "FGInput", "nvngxfg", stable, nightly));
            Assert.AreEqual("Fsr4Preset=0", fit("FSR", "Fsr4Preset", "0", nightly, stable));
            Assert.AreEqual("HUDFix=auto", fit("OptiFG", "HUDFix", "auto", nightly, stable));

            // In another place.
            var moved = OwnSettings.Fit(new Setting("Nukems", "MakeDepthCopy", "true"), nightly, stable);
            Assert.AreEqual("NvngxFG", moved.Section);
            Assert.AreEqual("true", moved.Value);
            Assert.AreEqual("Nukems", OwnSettings.Fit(new Setting("NvngxFG", "MakeDepthCopy", "true"), stable, nightly).Section);

            // It doesn't know that value.
            Assert.IsNull(fit("FrameGen", "FGInput", "xefg", stable, nightly));
            Assert.IsNull(fit("FrameGen", "FGOutput", "dlssg", stable, nightly));
            Assert.IsNull(fit("FrameGen", "FGOutput", "nukems", nightly, stable));
            Assert.IsNull(fit("FrameGen", "FGInput", "xefg", stable), "And it isn't known where it comes from: it was already waiting.");

            // It isn't in its file, or that value isn't in the file of any: it may be one that no file tells.
            Assert.AreEqual("Blur=true", fit("Menu", "Blur", "true", stable, nightly));
            Assert.AreEqual("Fsr4Update=true", fit("FSR", "Fsr4Update", "true", nightly, stable));
            Assert.AreEqual("Any=1", fit("Plugin.Something", "Any", "1", stable, nightly));
            Assert.AreEqual("FGOutput=Reprojection", fit("FrameGen", "FGOutput", "Reprojection", nightly, nightly));
            Assert.IsNull(fit("Menu", "Blur", "auto", stable, nightly), "It's nothing there.");

            // Off in one and up to the game in the other.
            Assert.AreEqual("Enabled=false", fit("FrameGen", "Enabled", "auto", nightly, stable));
            Assert.AreEqual("Enabled=auto", fit("FrameGen", "Enabled", "auto", stable, nightly));
            Assert.AreEqual("Enabled=auto", fit("FrameGen", "Enabled", "auto", nightly, nightly));
            Assert.AreEqual("Enabled=true", fit("FrameGen", "Enabled", "true", nightly, stable));

            // One with a name of the person's, where they can have it.
            Assert.AreEqual("Dx11Upscaler=mine", fit("Upscalers", "Dx11Upscaler", "mine", nightly, nightly));
            Assert.AreEqual("Dx11Upscaler=mine", fit("Upscalers", "Dx11Upscaler", "mine", nightly));
            Assert.IsNull(fit("Upscalers", "Dx11Upscaler", "mine", stable));
            Assert.IsNull(fit("Upscalers", "Dx11Upscaler", "mine", stable, nightly));

            // Nothing to know it by: as it is.
            Assert.AreEqual("Anything=1", fit("A", "Anything", "1", null));
        }

        [Test]
        public void WhichOptionDecidesItTest()
        {
            string option(string section, string key) => OwnSettings.OptionOf(new Setting(section, key, "1"));
            Assert.AreEqual("output", option("upscalers", "dx11upscaler"));
            Assert.AreEqual("output", option("FSR", "UpscalerIndex"));
            Assert.AreEqual("quality", option("UpscaleRatio", "UpscaleRatioOverrideValue"));
            Assert.AreEqual("frames", option("FrameGen", "FGInput"));
            Assert.AreEqual("frames", option("OptiFG", "HUDFix"));
            Assert.AreEqual("spoofing", option("Spoofing", "Dxgi"));
            Assert.IsNull(option("FSR", "Fsr4Preset"));
            Assert.IsNull(option("OptiFG", "HUDLimit"));
            Assert.AreEqual(new[] { "output", "quality", "frames", "spoofing" }, OwnSettings.Options.ToArray());
            Assert.IsTrue(OwnSettings.OfOption("frames").All(a => a.Value == "auto" && OwnSettings.OptionOf(a) == "frames"));
            Assert.IsTrue(OwnSettings.IsOfTheClient(new Setting("log", "loglevel", "2")));
            Assert.IsFalse(OwnSettings.IsOfTheClient(new Setting("FrameGen", "Enabled", "true")));
            Assert.IsFalse(OwnSettings.IsOfTheClient(new Setting("FSR", "Fsr4Preset", "0")));
        }

        [Test]
        public void WhatIsWorthACopyTest()
        {
            var left = Change(Stable, "FrameGen|Enabled=false", "UpscaleRatio|UpscaleRatioOverrideEnabled=false", "Upscalers|Dx12Upscaler=fsr31", "Log|LogLevel=2");
            Assert.IsFalse(OwnSettings.IsWorthKeeping(left, left));
            Assert.IsFalse(OwnSettings.IsWorthKeeping(AsTheGameSaves(left), left));
            Assert.IsTrue(OwnSettings.IsWorthKeeping(AsTheGameSaves(left, "Upscalers|Dx12Upscaler=fsr21"), left), "Also what could be of nobody.");
            Assert.IsTrue(OwnSettings.IsWorthKeeping(Change(left.ToString(), "FSR|Fsr4Preset=0"), left));

            // It isn't known how they were left.
            Assert.IsFalse(OwnSettings.IsWorthKeeping(Change(Stable, "FrameGen|Enabled=false", "UpscaleRatio|UpscaleRatioOverrideEnabled=false", "Log|LogToFile=true"), null));
            Assert.IsTrue(OwnSettings.IsWorthKeeping(Change(Stable, "Upscalers|Dx12Upscaler=xess"), null));
            Assert.IsTrue(OwnSettings.IsWorthKeeping(Change(Stable, "UpscaleRatio|UpscaleRatioOverrideEnabled=true"), null));
            Assert.IsFalse(OwnSettings.IsWorthKeeping(IniFile.Parse(Stable), null));
            Assert.IsFalse(OwnSettings.IsWorthKeeping(null, null));
        }

        [Test]
        public void AnOptionGoesWholeTest()
        {
            // The panel put a ratio and the frames, and in the game only a part of each one was changed.
            var left = Change(
                Nightly, "UpscaleRatio|UpscaleRatioOverrideEnabled=true", "UpscaleRatio|UpscaleRatioOverrideValue=1.5", "FrameGen|Enabled=true", "FrameGen|FGInput=upscaler", "FrameGen|FGOutput=fsrfg",
                "OptiFG|HUDFix=true", "HUDFix|HUDFix=true", "Upscalers|Dx11Upscaler=fsr31_12", "Upscalers|Dx12Upscaler=fsr31", "Log|LogLevel=2");
            var now = Change(left.ToString(), "UpscaleRatio|UpscaleRatioOverrideValue=1.450000", "FrameGen|FGOutput=xefg", "FSR|Fsr4Preset=0");
            var own = OwnSettings.Find(now, left, rdna4);
            Assert.AreEqual(new[] { "FGOutput=xefg", "Fsr4Preset=0", "UpscaleRatioOverrideValue=1.450000" }, Text(own));
            Assert.AreEqual(
                new[] { "FGOutput=xefg", "Fsr4Preset=0", "UpscaleRatioOverrideValue=1.450000", "Enabled=true", "FGInput=upscaler", "HUDFix=true", "UpscaleRatioOverrideEnabled=true" },
                Text(OwnSettings.Whole(own, now)), "What isn't in those options stays out, and so does what is where OptiScaler doesn't look.");
            Assert.AreEqual("OptiFG", OwnSettings.Whole(own, now).Single(a => a.Key == "HUDFix").Section);
            Assert.AreEqual("upscaler>xefg", OwnSettings.Summarize("frames", own, now));
            Assert.AreEqual("1.450000", OwnSettings.Summarize("quality", own, now));
            Assert.IsEmpty(OwnSettings.Whole(new Setting[0], now));
            Assert.AreEqual(1, OwnSettings.Whole(own.Take(1), null).Count);

            // Left to OptiScaler by the person: it isn't turned on again.
            now = AsTheGameSaves(Change(left.ToString(), "UpscaleRatio|UpscaleRatioOverrideEnabled=false", "OptiFG|HUDFix=false"));
            own = OwnSettings.Find(now, left, rdna4);
            Assert.AreEqual(new[] { "HUDFix=auto", "UpscaleRatioOverrideEnabled=auto" }, Text(own));
            Assert.AreEqual(
                new[] { "HUDFix=auto", "UpscaleRatioOverrideEnabled=auto", "Enabled=true", "FGInput=upscaler", "FGOutput=fsrfg", "UpscaleRatioOverrideValue=1.500000" },
                Text(OwnSettings.Whole(own, now)));
            Assert.AreEqual("auto", OwnSettings.Summarize("quality", own, now));

            // Off is part of it too: left to OptiScaler, in another version it could be on.
            now = Change(Stable, "FrameGen|FGInput=dlssg", "FrameGen|FGOutput=fsrfg");
            own = OwnSettings.Whole(OwnSettings.Find(now, null), now);
            Assert.AreEqual(new[] { "FGInput=dlssg", "FGOutput=fsrfg", "Enabled=auto" }, Text(own));
            Assert.AreEqual("Enabled=false", OwnSettings.Fit(own.Last(), IniFile.Parse(Nightly), now).ToString());
        }

        [Test]
        public void AnOptionInAFewWordsTest()
        {
            string summary(string option, string ini, params string[] changes)
            {
                var now = Change(ini, changes);
                return OwnSettings.Summarize(option, OwnSettings.Find(now, null), now);
            }

            Assert.AreEqual("ffx_12,xess", summary("output", Nightly, "Upscalers|Dx11Upscaler=ffx_12", "Upscalers|Dx12Upscaler=xess", "FSR|UpscalerIndex=1"));
            Assert.AreEqual("fsr31,ffx", summary("output", Stable, "Upscalers|Dx11Upscaler=fsr31", "Upscalers|Dx12Upscaler=fsr31"), "With the same name, the second one is of another.");
            Assert.AreEqual(string.Empty, summary("output", Stable));
            Assert.AreEqual("upscaler>fsrfg", summary("frames", Nightly, "FrameGen|Enabled=true", "FrameGen|FGInput=upscaler", "FrameGen|FGOutput=fsrfg"));
            Assert.AreEqual("nofg", summary("frames", Nightly, "FrameGen|Enabled=false", "FrameGen|FGInput=upscaler", "FrameGen|FGOutput=fsrfg"));
            Assert.AreEqual("nofg", summary("frames", Stable, "FrameGen|FGInput=upscaler", "FrameGen|FGOutput=fsrfg"), "Off is what it does when it's left to it.");
            Assert.AreEqual("nofg", summary("frames", Stable, "FrameGen|FGInput=dlssg", "FrameGen|FGOutput=fsrfg"));
            Assert.AreEqual("dlssg>fsrfg", summary("frames", Nightly, "FrameGen|FGInput=dlssg", "FrameGen|FGOutput=fsrfg"), "There it follows the game.");
            Assert.AreEqual(string.Empty, summary("frames", Nightly, "FrameGen|Enabled=true"));
            Assert.AreEqual("1.500000", summary("quality", Nightly, "UpscaleRatio|UpscaleRatioOverrideEnabled=true", "UpscaleRatio|UpscaleRatioOverrideValue=1.500000"));
            Assert.AreEqual("1.3", summary("quality", Nightly, "UpscaleRatio|UpscaleRatioOverrideEnabled=true"), "The one it does when it's left to it.");
            Assert.AreEqual("auto", summary("quality", Nightly, "UpscaleRatio|UpscaleRatioOverrideEnabled=false", "UpscaleRatio|UpscaleRatioOverrideValue=1.5"));
            Assert.AreEqual("false", summary("spoofing", Nightly, "Spoofing|Dxgi=false"));
            Assert.AreEqual("auto", summary("spoofing", Nightly, "Spoofing|Vulkan=true"));
        }
    }
}
