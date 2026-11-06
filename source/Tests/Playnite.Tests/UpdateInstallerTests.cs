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
            Write(target, "Atrio.exe", "vieja");
            Write(target, @"library\games.db", "mis juegos");
            Write(target, "config.json", "mis ajustes");
            Write(target, @"ExtensionsData\logros\datos.json", "mis logros");
            Write(package, "Atrio.exe", "nueva");
            Write(package, @"Themes\Desktop\Nuevo\tema.xaml", "tema");

            NewInstaller().Install();

            Assert.AreEqual("nueva", Read("Atrio.exe"));
            Assert.AreEqual("tema", Read(@"Themes\Desktop\Nuevo\tema.xaml"));
            Assert.AreEqual("mis juegos", Read(@"library\games.db"));
            Assert.AreEqual("mis ajustes", Read("config.json"));
            Assert.AreEqual("mis logros", Read(@"ExtensionsData\logros\datos.json"));
        }

        [Test]
        public void OnlyFilesOfThePreviousVersionGoAwayTest()
        {
            Write(target, UpdateInstaller.FileList, "Atrio.exe\r\nThemes\\Desktop\\Viejo\\tema.xaml\r\nvieja.dll\r\n");
            Write(target, "Atrio.exe", "vieja");
            Write(target, "vieja.dll", "vieja");
            Write(target, @"Themes\Desktop\Viejo\tema.xaml", "tema viejo");
            Write(target, @"Themes\Desktop\DelUsuario\tema.xaml", "tema del usuario");
            Write(target, "nota del usuario.txt", "mia");
            Write(package, UpdateInstaller.FileList, "Atrio.exe\r\n");
            Write(package, "Atrio.exe", "nueva");

            NewInstaller().Install();

            Assert.IsFalse(Exists("vieja.dll"));
            Assert.IsFalse(Exists(@"Themes\Desktop\Viejo\tema.xaml"));
            Assert.IsFalse(Directory.Exists(Path.Combine(target, @"Themes\Desktop\Viejo")));
            Assert.AreEqual("tema del usuario", Read(@"Themes\Desktop\DelUsuario\tema.xaml"));
            Assert.AreEqual("mia", Read("nota del usuario.txt"));
        }

        [Test]
        public void ListOfFilesCantReachOutsideOfTheProgramTest()
        {
            Write(root, "ajeno.txt", "de otro");
            Write(target, UpdateInstaller.FileList, "..\\ajeno.txt\r\n" + Path.Combine(root, "ajeno.txt") + "\r\n");
            Write(package, "Atrio.exe", "nueva");

            NewInstaller().Install();

            Assert.IsTrue(File.Exists(Path.Combine(root, "ajeno.txt")));
        }

        [TestCase("9.5", "2.11", "9.5")]
        [TestCase("2.11", "2.11", "2.11-paquete")]
        [TestCase("1.0", "2.11", "2.11-paquete")]
        public void AddonTheUserUpdatedKeepsItsVersionTest(string installed, string packaged, string expected)
        {
            var addon = @"Extensions\Steam_Builtin\";
            Write(target, addon + "extension.yaml", $"Id: Steam_Builtin\r\nVersion: {installed}\r\n");
            Write(target, addon + "Steam.dll", installed);
            Write(target, addon + "solo-instalado.txt", "instalado");
            Write(target, UpdateInstaller.FileList, addon + "solo-instalado.txt\r\n");
            Write(package, addon + "extension.yaml", $"Id: Steam_Builtin\r\nVersion: {packaged}\r\n");
            Write(package, addon + "Steam.dll", packaged + "-paquete");

            NewInstaller().Install();

            Assert.AreEqual(expected, Read(addon + "Steam.dll"));
            Assert.AreEqual(expected == installed, Exists(addon + "solo-instalado.txt"));
        }

        [Test]
        public void AnyFailureLeavesEverythingAsItWasTest()
        {
            Write(target, UpdateInstaller.FileList, "Atrio.exe\r\nretirado.dll\r\nocupado.dll\r\n");
            Write(target, "Atrio.exe", "vieja");
            Write(target, "retirado.dll", "retirado");
            Write(target, "ocupado.dll", "vieja");
            Write(package, UpdateInstaller.FileList, "Atrio.exe\r\nnuevo.dll\r\nocupado.dll\r\n");
            Write(package, "Atrio.exe", "nueva");
            Write(package, "nuevo.dll", "nuevo");
            Write(package, "ocupado.dll", "nueva");

            using (new FileStream(Path.Combine(target, "ocupado.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Throws<IOException>(() => NewInstaller().Install());
            }

            Assert.AreEqual("vieja", Read("Atrio.exe"));
            Assert.AreEqual("vieja", Read("ocupado.dll"));
            Assert.AreEqual("retirado", Read("retirado.dll"));
            Assert.IsFalse(Exists("nuevo.dll"));
            Assert.AreEqual("Atrio.exe\r\nretirado.dll\r\nocupado.dll\r\n", Read(UpdateInstaller.FileList));
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
