using NUnit.Framework;
using Playnite.Atrio;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Playnite.Tests
{
    [TestFixture]
    public class DefaultFilterPresetsTests
    {
        private static FilterPreset Preset(string name, SortOrder? order, FilterPresetSettings settings = null)
        {
            return new FilterPreset { Name = name, SortingOrder = order, Settings = settings ?? new FilterPresetSettings() };
        }

        private static List<FilterPreset> Defaults()
        {
            return new List<FilterPreset>
            {
                Preset(DefaultFilterPresets.All, SortOrder.Name),
                Preset(DefaultFilterPresets.Favorites, SortOrder.Name, new FilterPresetSettings { Favorite = true }),
                Preset(DefaultFilterPresets.MostPlayed, SortOrder.Playtime),
                Preset(DefaultFilterPresets.RecentlyPlayed, SortOrder.LastActivity, new FilterPresetSettings { IsInstalled = true })
            };
        }

        [Test]
        public void WhatWasPlayedLastComesFirstAndTheWholeLibraryLast()
        {
            var names = DefaultFilterPresets.Sort(Defaults(), new List<Guid>()).Select(a => a.Name).ToList();
            CollectionAssert.AreEqual(new[]
            {
                DefaultFilterPresets.RecentlyPlayed,
                DefaultFilterPresets.Favorites,
                DefaultFilterPresets.MostPlayed,
                DefaultFilterPresets.All
            }, names);
        }

        [Test]
        public void PresetsOfTheUserGoAfterTheOnesOfAlways()
        {
            var presets = Defaults();
            presets.Insert(0, Preset("RPG", SortOrder.Name, new FilterPresetSettings { Favorite = true }));
            Assert.AreEqual("RPG", DefaultFilterPresets.Sort(presets, null).Last().Name);
        }

        [Test]
        public void AnOrderChosenByTheUserStaysAsItIs()
        {
            var presets = Defaults();
            var saved = presets.Select(a => a.Id).ToList();
            CollectionAssert.AreEqual(presets, DefaultFilterPresets.Sort(presets, saved));
        }

        [Test]
        public void OnlyEveryGameByNameCountsAsTheWholeLibrary()
        {
            var presets = Defaults();
            Assert.IsTrue(DefaultFilterPresets.IsWholeLibrary(presets[0]));
            Assert.IsFalse(DefaultFilterPresets.IsWholeLibrary(presets[1]), "it's filtered");
            Assert.IsFalse(DefaultFilterPresets.IsWholeLibrary(presets[2]), "the games that matter come first");
            Assert.IsFalse(DefaultFilterPresets.IsWholeLibrary(presets[3]));
            Assert.IsFalse(DefaultFilterPresets.IsWholeLibrary(null));
        }

        [Test]
        public void ANameOfTheUserIsNotTranslated()
        {
            Assert.AreEqual("Para esta noche", DefaultFilterPresets.GetName(Preset("Para esta noche", SortOrder.Name)));
            Assert.IsNull(DefaultFilterPresets.GetName(null));
        }
    }
}
