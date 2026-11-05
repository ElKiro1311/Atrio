using System;
using System.Linq;
using System.Windows.Media;

namespace Playnite.Atrio
{
    /// <summary>
    /// Color and default relevance of the stores, matched by name so that it also works
    /// for community add-ons whose plugin ID isn't known in advance.
    /// </summary>
    public static class StoreBranding
    {
        private class Store
        {
            public string Key;
            public string Color;

            public Store(string key, string color)
            {
                Key = key;
                Color = color;
            }
        }

        // From the most to the least relevant one.
        private static readonly Store[] stores = new Store[]
        {
            new Store("steam", "#2E8FE6"),
            new Store("epic", "#D8DBE0"),
            new Store("gog", "#A54CD4"),
            new Store("xbox", "#2CA243"),
            new Store("battle", "#148EFF"),
            new Store("ea app", "#FF4747"),
            new Store("ubisoft", "#12C4E0"),
            new Store("rockstar", "#FCAF17"),
            new Store("riot", "#EB0029"),
            new Store("amazon", "#FF9900"),
            new Store("humble", "#D03A3A"),
            new Store("itch", "#FA5C5C"),
            new Store("legacy", "#F2A33A"),
            new Store("playstation", "#2F6FDE"),
            new Store("nintendo", "#E60012")
        };

        private static Store Find(string storeName)
        {
            return storeName == null ? null : stores.FirstOrDefault(a => storeName.IndexOf(a.Key, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static Color GetColor(string storeName)
        {
            return (Color)ColorConverter.ConvertFromString(Find(storeName)?.Color ?? "#8A93A3");
        }

        public static Brush GetBrush(string storeName)
        {
            var brush = new SolidColorBrush(GetColor(storeName));
            brush.Freeze();
            return brush;
        }

        /// <summary>Soft wash of the store color for the background of a card.</summary>
        public static Brush GetCardBrush(string storeName)
        {
            return GetCardBrush(GetColor(storeName));
        }

        public static Brush GetCardBrush(Color color)
        {
            var brush = new LinearGradientBrush(
                Color.FromArgb(0x40, color.R, color.G, color.B),
                Color.FromArgb(0x08, color.R, color.G, color.B),
                new System.Windows.Point(0, 0),
                new System.Windows.Point(1, 1));
            brush.Freeze();
            return brush;
        }

        public static int GetDefaultRank(string storeName)
        {
            var store = Find(storeName);
            return store == null ? stores.Length : Array.IndexOf(stores, store);
        }
    }
}
