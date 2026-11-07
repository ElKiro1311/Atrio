using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace Playnite.Atrio
{
    /// <summary>
    /// The filter presets every library starts with. Their names are saved in English
    /// whatever the language, so what the user reads has to be looked up.
    /// </summary>
    public static class DefaultFilterPresets
    {
        public const string RecentlyPlayed = "Recently Played";
        public const string Favorites = "Favorites";
        public const string MostPlayed = "Most Played";
        public const string All = "All";

        private static readonly Dictionary<string, string> names = new Dictionary<string, string>
        {
            { RecentlyPlayed, LOC.QuickFilterRecentlyPlayed },
            { Favorites, LOC.QuickFilterFavorites },
            { MostPlayed, LOC.QuickFilterMostPlayed },
            { All, LOC.QuickFilterAllGames }
        };

        private static readonly List<string> order = new List<string> { RecentlyPlayed, Favorites, MostPlayed, All };

        public static string GetName(FilterPreset preset)
        {
            if (preset?.Name != null && names.TryGetValue(preset.Name, out var key))
            {
                return ResourceProvider.GetString(key);
            }

            return preset?.Name;
        }

        /// <summary>Without an order chosen by the user, what was played last comes first and the whole library last.</summary>
        public static List<FilterPreset> Sort(List<FilterPreset> presets, List<Guid> savedOrder)
        {
            if (savedOrder?.Count > 0)
            {
                return presets;
            }

            return presets.OrderBy(a =>
            {
                var index = order.IndexOf(a.Name);
                return index < 0 ? int.MaxValue : index;
            }).ToList();
        }

        /// <summary>A list of every game by name: too long to go through it one cover at a time.</summary>
        public static bool IsWholeLibrary(FilterPreset preset)
        {
            return preset != null &&
                   (preset.SortingOrder == null || preset.SortingOrder == SortOrder.Name) &&
                   (preset.Settings == null || preset.Settings.IsEqualJson(new FilterPresetSettings()));
        }
    }

    public class FilterPresetNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return DefaultFilterPresets.GetName(value as FilterPreset);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
