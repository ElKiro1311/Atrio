using Playnite.SDK;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Playnite.Converters
{
    /// <summary>
    /// "Unlocked on 28 October 2026 at 13:57", or that it's still to be done.
    /// Expects whether it's unlocked and the local time of the unlock, which some stores don't give.
    /// </summary>
    public class AchievementStateConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2 || values[0] as bool? != true)
            {
                return ResourceProvider.GetString("LOCAtrioAchievementStateLocked");
            }

            if (values[1] is DateTime time && time.Year > 1970)
            {
                var language = Localization.ApplicationLanguageCultureInfo;
                return string.Format(
                    ResourceProvider.GetString("LOCAtrioAchievementStateUnlockedOn"),
                    time.ToString(GetFormat("LOCAtrioAchievementDateFormat", "d MMMM yyyy"), language),
                    time.ToString("t", language));
            }

            return ResourceProvider.GetString("LOCAtrioAchievementStateUnlocked");
        }

        /// <summary>How the language writes dates, which is up to each translation.</summary>
        internal static string GetFormat(string key, string fallback)
        {
            var format = ResourceProvider.GetString(key);
            return format.IsNullOrWhiteSpace() || format.StartsWith("<!") ? fallback : format;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>"10 Oct 2026, 19:24" in the way the language of the app writes it. Empty without a date.</summary>
    public class AchievementDateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is DateTime time ? Format(time, true) : string.Empty;
        }

        /// <summary>"10 Oct 2026, 19:24", or only "10 Oct 2026" without the time. Empty for no date.</summary>
        public static string Format(DateTime time, bool withTime)
        {
            if (time.Year <= 1970)
            {
                return string.Empty;
            }

            var language = Localization.ApplicationLanguageCultureInfo;
            var date = time.ToString(AchievementStateConverter.GetFormat("LOCAtrioAchievementShortDateFormat", "d MMM yyyy"), language);
            return withTime ? date + ", " + time.ToString("t", language) : date;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>"61.8 %" in the way the language of the app writes it. Empty without a number.</summary>
    public class AchievementPercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is double percent ? FormatPercent(percent) : string.Empty;
        }

        public static string FormatPercent(double percent)
        {
            var number = percent.ToString("0.#", Localization.ApplicationLanguageCultureInfo);
            var format = ResourceProvider.GetString("LOCAtrioAchievementPercent");
            return format.StartsWith("<!") ? number + " %" : string.Format(format, number);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// "Rare · 4.6 % of players have it", written the way the language of the app writes numbers.
    /// Expects the rarity tier of the add-on and the percentage. Empty when the store gives no percentage.
    /// </summary>
    public class AchievementRarityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2 || !(values[1] is double percent))
            {
                return string.Empty;
            }

            var language = Localization.ApplicationLanguageCultureInfo;
            var share = string.Format(
                ResourceProvider.GetString("LOCAtrioAchievementRarityShare"),
                AchievementPercentConverter.FormatPercent(percent));
            var tier = GetTierName(values[0]?.ToString());
            if (!tier.IsNullOrEmpty())
            {
                return tier + " · " + share;
            }

            // Without the tier the sentence starts the line.
            return share.Length == 0 ? share : char.ToUpper(share[0], language) + share.Substring(1);
        }

        public static string GetTierName(string tier)
        {
            switch (tier)
            {
                case "Common":
                    return ResourceProvider.GetString("LOCAtrioRarityCommon");
                case "Uncommon":
                    return ResourceProvider.GetString("LOCAtrioRarityUncommon");
                case "Rare":
                    return ResourceProvider.GetString("LOCAtrioRarityRare");
                case "UltraRare":
                    return ResourceProvider.GetString("LOCAtrioRarityUltraRare");
                default:
                    return null;
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>"5 points", from the points as the add-on writes them.</summary>
    public class AchievementPointsConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var points = value?.ToString();
            return points.IsNullOrWhiteSpace() ? string.Empty : string.Format(ResourceProvider.GetString("LOCAtrioAchievementPoints"), points.Trim());
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>The kind of trophy in the language of the app. Kinds it doesn't know stay as they come.</summary>
    public class AchievementTrophyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var trophy = value?.ToString() ?? string.Empty;
            switch (trophy.Trim().ToLowerInvariant())
            {
                case "platinum":
                    return ResourceProvider.GetString("LOCAtrioTrophyPlatinum");
                case "gold":
                    return ResourceProvider.GetString("LOCAtrioTrophyGold");
                case "silver":
                    return ResourceProvider.GetString("LOCAtrioTrophySilver");
                case "bronze":
                    return ResourceProvider.GetString("LOCAtrioTrophyBronze");
                default:
                    return trophy;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// The button under the icons of a game page: "Show more (11)" while some are left,
    /// "Show fewer" once all of them are there. Expects the total and how many are on the page;
    /// the parameter is how many the page shows first. Empty when those are all there is.
    /// Instead of the total it takes the achievements and, third, which ones the page shows.
    /// Bound to a visibility, it says whether the button is there at all.
    /// </summary>
    public class AchievementMoreConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var total = 0;
            if (values?.Length > 0 && values[0] is int count)
            {
                total = count;
            }
            else if (values?.Length > 0 && values[0] is System.Collections.IEnumerable list && !(values[0] is string))
            {
                total = Atrio.AchievementCard.Count(list, values.Length > 2 && values[2] is int filter ? filter : Atrio.AchievementCard.ShowAll);
            }

            var text = GetText(
                total,
                values?.Length > 1 && values[1] is int number ? number : 0,
                System.Convert.ToInt32(parameter ?? 0, CultureInfo.InvariantCulture));
            if (targetType == typeof(Visibility))
            {
                return text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }

            return text;
        }

        private static string GetText(int total, int shown, int first)
        {
            if (total <= first)
            {
                return string.Empty;
            }

            return total > shown ?
                string.Format(ResourceProvider.GetString("LOCAtrioAchievementsShowMore"), total - shown) :
                ResourceProvider.GetString("LOCAtrioAchievementsShowFewer");
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>
    /// The guide buttons of an achievement. Expects the achievement and whether the game is on Steam.
    /// With the parameter "direct", the button that goes straight to a guide: "See it on RetroAchievements"
    /// or "Achievement guides on Steam", or nothing for the other stores (as a visibility, whether it is there).
    /// With "web", the search: "Search the web" next to the other one, "Search for a guide" alone.
    /// </summary>
    public class AchievementGuideConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var achievement = values?.Length > 0 ? values[0] : null;
            var onSteam = values?.Length > 1 && values[1] as bool? == true;
            var direct = GetDirectKey(achievement, onSteam);
            if (parameter as string == "web")
            {
                return ResourceProvider.GetString(direct == null ? "LOCAtrioAchievementGuide" : "LOCAtrioAchievementGuideWeb");
            }

            if (targetType == typeof(Visibility))
            {
                return direct == null ? Visibility.Collapsed : Visibility.Visible;
            }

            return direct == null ? string.Empty : ResourceProvider.GetString(direct);
        }

        public static string GetDirectKey(object achievement, bool onSteam)
        {
            if (Atrio.AchievementCard.GetRetroUrl(achievement) != null)
            {
                return "LOCAtrioAchievementGuideRetro";
            }

            return onSteam ? "LOCAtrioAchievementGuideSteam" : null;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
