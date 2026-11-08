using Playnite.Atrio;
using Playnite.SDK;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
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

        /// <summary>How far the player is, for the ones that count something; 0 for the rest.</summary>
        public static double ProgressPercent(object item)
        {
            return Read(item, "HasProgress") as bool? == true ? Read(item, "ProgressPercent") as double? ?? 0 : 0;
        }

        public static string ProgressText(object item)
        {
            return Read(item, "ProgressText") as string;
        }

        /// <summary>A secret one whose name the player has not asked to see, or a locked one the add-on keeps masked.</summary>
        public static bool IsTitleHidden(object item)
        {
            return Read(item, "IsTitleHidden") as bool? == true;
        }

        public static bool IsUnobtainable(object item)
        {
            return Read(item, "IsUnobtainable") as bool? == true;
        }

        /// <summary>Where the add-on read it: Steam, RetroAchievements...</summary>
        public static string ProviderKey(object item)
        {
            return Read(item, "ProviderKey") as string;
        }

        /// <summary>Its id in the store it comes from.</summary>
        public static string ApiName(object item)
        {
            return Read(item, "ApiName") as string;
        }

        private static readonly ConcurrentDictionary<Tuple<Type, string>, PropertyInfo> properties =
            new ConcurrentDictionary<Tuple<Type, string>, PropertyInfo>();

        private static object Read(object item, string property)
        {
            if (item == null)
            {
                return null;
            }

            var info = properties.GetOrAdd(Tuple.Create(item.GetType(), property), a => a.Item1.GetProperty(a.Item2));
            return info?.GetValue(item);
        }
    }

    /// <summary>
    /// The achievements a game page has room for: the unlocked ones first, the newest on top.
    /// The parameter is how many to keep. As a multi binding, the second value says how many
    /// instead, which grows each time the player asks for more, the third which ones
    /// (all, unlocked or locked) and the fourth whether the rarest go first.
    /// </summary>
    public class AchievementPreviewConverter : IValueConverter, IMultiValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Take(value, System.Convert.ToInt32(parameter, CultureInfo.InvariantCulture), AchievementCard.ShowAll, false);
        }

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var count = values?.Length > 1 && values[1] is int shown ? shown : System.Convert.ToInt32(parameter ?? 0, CultureInfo.InvariantCulture);
            var filter = values?.Length > 2 && values[2] is int which ? which : AchievementCard.ShowAll;
            var rarestFirst = values?.Length > 3 && values[3] as bool? == true;
            return Take(values?.Length > 0 ? values[0] : null, count, filter, rarestFirst);
        }

        private static List<object> Take(object list, int count, int filter, bool rarestFirst)
        {
            return AchievementCard.Arrange(list, filter, rarestFirst).Take(count).ToList();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
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

    /// <summary>The easiest achievements still missing, or, for a visibility, whether there are any.</summary>
    public class AchievementNextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var next = AchievementCard.PickNext(value);
            if (targetType == typeof(Visibility))
            {
                return next.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }

            return next;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// What is said next to one of the easiest missing achievements: how far the player is when it is almost done,
    /// otherwise how many players have it. With the parameter "tip", the longer sentence for its tooltip.
    /// </summary>
    public class AchievementNextHintConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var tip = parameter as string == "tip";
            var progress = AchievementItems.ProgressPercent(value);
            var percent = AchievementItems.Percent(value);
            if (progress >= AchievementCard.AlmostDone || (percent == null && progress > 0))
            {
                var text = AchievementItems.ProgressText(value) ?? string.Empty;
                return tip ? string.Format(ResourceProvider.GetString("LOCAtrioAchievementsNextProgress"), text) : text;
            }

            if (percent != null)
            {
                var share = AchievementPercentConverter.FormatPercent(percent.Value);
                if (!tip)
                {
                    return share;
                }

                // The sentence is made to go after the rarity, here it starts the tooltip.
                var sentence = string.Format(ResourceProvider.GetString("LOCAtrioAchievementRarityShare"), share);
                return sentence.Length == 0 ? sentence : char.ToUpper(sentence[0], Localization.ApplicationLanguageCultureInfo) + sentence.Substring(1);
            }

            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// What the card says when the chosen filter leaves nothing: none unlocked yet, or all of them unlocked.
    /// Expects the achievements and the filter. For a visibility, whether to say it.
    /// </summary>
    public class AchievementEmptyConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var filter = values?.Length > 1 && values[1] is int which ? which : AchievementCard.ShowAll;
            var list = values?.Length > 0 ? values[0] : null;
            var text = string.Empty;
            if (filter != AchievementCard.ShowAll && AchievementItems.Get(list).Count > 0 && AchievementCard.Count(list, filter) == 0)
            {
                text = ResourceProvider.GetString(filter == AchievementCard.ShowUnlocked ? "LOCAtrioAchievementsNoneUnlocked" : "LOCAtrioAchievementsAllUnlocked");
            }

            if (targetType == typeof(Visibility))
            {
                return text.IsNullOrEmpty() ? Visibility.Collapsed : Visibility.Visible;
            }

            return text;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Ties a choice of a group to a number: checked when the number is the parameter, and checking it sets the number.
    /// Unchecking does nothing, the one checked next sets it.
    /// </summary>
    public class AchievementFilterConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is int number && number == System.Convert.ToInt32(parameter, CultureInfo.InvariantCulture);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value as bool? == true ? System.Convert.ToInt32(parameter, CultureInfo.InvariantCulture) : Binding.DoNothing;
        }
    }
}
