using Playnite.SDK;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace Playnite.Converters
{
    /// <summary>
    /// Reads the achievements of the add-on, which is not a reference of this project.
    /// </summary>
    internal static class AchievementItems
    {
        public static List<object> Get(object list)
        {
            return (list as IEnumerable)?.Cast<object>().ToList() ?? new List<object>();
        }

        public static bool IsUnlocked(object item)
        {
            return Read(item, "Unlocked") as bool? == true;
        }

        public static DateTime UnlockTime(object item)
        {
            return Read(item, "UnlockTimeUtc") as DateTime? ?? DateTime.MinValue;
        }

        /// <summary>How many players have it. Achievements without the number go last when looking for rare ones.</summary>
        public static double? Percent(object item)
        {
            return Read(item, "GlobalPercentUnlocked") as double?;
        }

        public static string Name(object item)
        {
            return Read(item, "DisplayNameResolved") as string ?? string.Empty;
        }

        private static object Read(object item, string property)
        {
            return item?.GetType().GetProperty(property)?.GetValue(item);
        }
    }

    /// <summary>
    /// The few achievements a game page has room for: the unlocked ones first, the newest on top.
    /// The parameter is how many to keep.
    /// </summary>
    public class AchievementPreviewConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return AchievementItems.Get(value).
                OrderByDescending(AchievementItems.IsUnlocked).
                ThenByDescending(AchievementItems.UnlockTime).
                Take(System.Convert.ToInt32(parameter, CultureInfo.InvariantCulture)).
                ToList();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// One unlocked achievement worth pointing out. The parameter says which: first, latest or rarest.
    /// Nothing when the player has none of them yet.
    /// </summary>
    public class AchievementMilestoneConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var unlocked = AchievementItems.Get(value).Where(AchievementItems.IsUnlocked);
            switch (parameter as string)
            {
                case "first":
                    return unlocked.Where(a => AchievementItems.UnlockTime(a) != DateTime.MinValue).OrderBy(AchievementItems.UnlockTime).FirstOrDefault();
                case "rarest":
                    return unlocked.Where(a => AchievementItems.Percent(a) != null).OrderBy(a => AchievementItems.Percent(a)).FirstOrDefault();
                default:
                    return unlocked.OrderByDescending(AchievementItems.UnlockTime).FirstOrDefault();
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>The "+16" for the ones that didn't fit. Empty when all of them are on the page.</summary>
    public class AchievementOverflowConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var hidden = (value as int? ?? 0) - System.Convert.ToInt32(parameter, CultureInfo.InvariantCulture);
            return hidden > 0 ? string.Format(ResourceProvider.GetString("LOCAtrioAchievementsMore"), hidden) : string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// The full list of a game. Expects the achievements, whether only the unlocked or only the locked
    /// ones are wanted, and the index of the sort order: game, newest, rarest, most common, name.
    /// </summary>
    public class AchievementListConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            IEnumerable<object> items = AchievementItems.Get(values[0]);
            if (values[1] as bool? == true)
            {
                items = items.Where(AchievementItems.IsUnlocked);
            }
            else if (values[2] as bool? == true)
            {
                items = items.Where(a => !AchievementItems.IsUnlocked(a));
            }

            switch (values[3] as int? ?? 0)
            {
                case 1:
                    return items.OrderByDescending(AchievementItems.IsUnlocked).ThenByDescending(AchievementItems.UnlockTime).ToList();
                case 2:
                    return items.OrderBy(a => AchievementItems.Percent(a) ?? double.MaxValue).ToList();
                case 3:
                    return items.OrderByDescending(a => AchievementItems.Percent(a) ?? -1).ToList();
                case 4:
                    return items.OrderBy(AchievementItems.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
                default:
                    return items.ToList();
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>"You've unlocked 28/30 (93 %)", from the unlocked, the total and the percentage.</summary>
    public class AchievementProgressTextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            return string.Format(
                ResourceProvider.GetString("LOCAtrioAchievementsProgress"),
                values[0] as int? ?? 0,
                values[1] as int? ?? 0,
                Math.Round(values[2] as double? ?? 0));
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
