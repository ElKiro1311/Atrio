using Atrio.Updater;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class UpdateInstallerTests
    {
        private string root;
        private string target;
        private string package;
        private string backup;

        [SetUp]
        public void CreateFolders()
        {
            root = Path.Combine(Path.GetTempPath(), "atrio-test-" + Guid.NewGuid().ToString("N"));
            target = Path.Combine(root, "Atrio con espacios");
            package = Path.Combine(target, "update", "p");
            backup = Path.Combine(target, "update", "a");
            Directory.CreateDirectory(package);
        }

        [TearDown]
        public void RemoveFolders()
        {
            Directory.Delete(root, true);
        }

        private static void Write(string folder, string file, string text)
        {
            var path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, text);
        }

        private string Read(string file)
        {
            return File.ReadAllText(Path.Combine(target, file));
        }

        private bool Exists(string file)
        {
            return File.Exists(Path.Combine(target, file));
        }

        private UpdateInstaller NewInstaller()
        {
            return new UpdateInstaller(target, package, backup) { Attempts = 2, AttemptDelay = 1 };
        }

        [Test]
        public void NewVersionReplacesTheProgramAndLeavesTheDataAloneTest()
        {
            Write(target, "Atrio.exe", "old");
            Write(target, @"library\games.db", "my games");
            Write(target, "config.json", "my settings");
            Write(target, @"ExtensionsData\achievements\data.json", "my achievements");
            Write(package, "Atrio.exe", "new");
            Write(package, @"Themes\Desktop\New\theme.xaml", "theme");

            NewInstaller().Install();

            Assert.AreEqual("new", Read("Atrio.exe"));
            Assert.AreEqual("theme", Read(@"Themes\Desktop\New\theme.xaml"));
            Assert.AreEqual("my games", Read(@"library\games.db"));
            Assert.AreEqual("my settings", Read("config.json"));
            Assert.AreEqual("my achievements", Read(@"ExtensionsData\achievements\data.json"));
        }

        [Test]
        public void OnlyFilesOfThePreviousVersionGoAwayTest()
        {
            Write(target, UpdateInstaller.FileList, "Atrio.exe\r\nThemes\\Desktop\\Old\\theme.xaml\r\nold.dll\r\n");
            Write(target, "Atrio.exe", "old");
            Write(target, "old.dll", "old");
            Write(target, @"Themes\Desktop\Old\theme.xaml", "old theme");
            Write(target, @"Themes\Desktop\TheUsers\theme.xaml", "the user's theme");
            Write(target, "the user's note.txt", "mine");
            Write(package, UpdateInstaller.FileList, "Atrio.exe\r\n");
            Write(package, "Atrio.exe", "new");

            NewInstaller().Install();

            Assert.IsFalse(Exists("old.dll"));
            Assert.IsFalse(Exists(@"Themes\Desktop\Old\theme.xaml"));
            Assert.IsFalse(Directory.Exists(Path.Combine(target, @"Themes\Desktop\Old")));
            Assert.AreEqual("the user's theme", Read(@"Themes\Desktop\TheUsers\theme.xaml"));
            Assert.AreEqual("mine", Read("the user's note.txt"));
        }

        [Test]
        public void ListOfFilesCantReachOutsideOfTheProgramTest()
        {
            Write(root, "foreign.txt", "someone else's");
            Write(target, UpdateInstaller.FileList, "..\\foreign.txt\r\n" + Path.Combine(root, "foreign.txt") + "\r\n");
            Write(package, "Atrio.exe", "new");

            NewInstaller().Install();

            Assert.IsTrue(File.Exists(Path.Combine(root, "foreign.txt")));
        }

        [TestCase("9.5", "2.11", "9.5")]
        [TestCase("2.11", "2.11", "2.11-package")]
        [TestCase("1.0", "2.11", "2.11-package")]
        public void AddonTheUserUpdatedKeepsItsVersionTest(string installed, string packaged, string expected)
        {
            var addon = @"Extensions\Steam_Builtin\";
            Write(target, addon + "extension.yaml", $"Id: Steam_Builtin\r\nVersion: {installed}\r\n");
            Write(target, addon + "Steam.dll", installed);
            Write(target, addon + "only-installed.txt", "installed");
            Write(target, UpdateInstaller.FileList, addon + "only-installed.txt\r\n");
            Write(package, addon + "extension.yaml", $"Id: Steam_Builtin\r\nVersion: {packaged}\r\n");
            Write(package, addon + "Steam.dll", packaged + "-package");

            NewInstaller().Install();

            Assert.AreEqual(expected, Read(addon + "Steam.dll"));
            Assert.AreEqual(expected == installed, Exists(addon + "only-installed.txt"));
        }

        [Test]
        public void AnyFailureLeavesEverythingAsItWasTest()
        {
            Write(target, UpdateInstaller.FileList, "Atrio.exe\r\nretired.dll\r\nbusy.dll\r\n");
            Write(target, "Atrio.exe", "old");
            Write(target, "retired.dll", "retired");
            Write(target, "busy.dll", "old");
            Write(package, UpdateInstaller.FileList, "Atrio.exe\r\nnew.dll\r\nbusy.dll\r\n");
            Write(package, "Atrio.exe", "new");
            Write(package, "new.dll", "new");
            Write(package, "busy.dll", "new");

            using (new FileStream(Path.Combine(target, "busy.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<IOException>(() => NewInstaller().Install());
            }

            Assert.AreEqual("old", Read("Atrio.exe"));
            Assert.AreEqual("old", Read("busy.dll"));
            Assert.AreEqual("retired", Read("retired.dll"));
            Assert.IsFalse(Exists("new.dll"));
            Assert.AreEqual("Atrio.exe\r\nretired.dll\r\nbusy.dll\r\n", Read(UpdateInstaller.FileList));
        }

        [Test]
        public void PlanSurvivesPathsAndTextsWithAnythingInThemTest()
        {
            var path = Path.Combine(root, "plan.txt");
            new UpdatePlan
            {
                Target = @"D:\Juegos y más\Atrio",
                StartArguments = @"--userdatadir ""D:\Datos con espacios\"" --nolibupdate",
                WaitFor = 1234,
                FailedText = "No se pudo.\n\n{0}"
            }.Save(path);

            var plan = UpdatePlan.Load(path);

            Assert.AreEqual(@"D:\Juegos y más\Atrio", plan.Target);
            Assert.AreEqual(@"--userdatadir ""D:\Datos con espacios\"" --nolibupdate", plan.StartArguments);
            Assert.AreEqual(1234, plan.WaitFor);
            Assert.AreEqual("No se pudo.\n\n{0}", plan.FailedText);
        }
    }
}
