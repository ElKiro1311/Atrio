using Playnite.Emulators;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Playnite.Atrio
{
    /// <summary>
    /// Emulators shown as if they were stores: each one has a name, a color and an icon. The icon is
    /// drawn here from the name, so that no emulator logo has to be distributed.
    /// </summary>
    public static class EmulatorStores
    {
        private static readonly string[] palette = new string[]
        {
            "#7C6CFF", "#2DD4BF", "#F59E0B", "#EC4899", "#22C55E", "#38BDF8", "#EF4444", "#A855F7"
        };

        private static readonly Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>();

        /// <summary>Empty for anything that is not an emulated game.</summary>
        public static Guid GetEmulatorId(Game game)
        {
            if (game == null || game.PluginId != Guid.Empty || game.GameActions == null)
            {
                return Guid.Empty;
            }

            return game.GameActions.FirstOrDefault(a => a.Type == GameActionType.Emulator)?.EmulatorId ?? Guid.Empty;
        }

        public static Emulator GetEmulator(Game game)
        {
            var id = GetEmulatorId(game);
            return id == Guid.Empty ? null : PlayniteApplication.Current?.Database?.Emulators?.Get(id);
        }

        public static Color GetColor(string name)
        {
            var sum = (name ?? string.Empty).Sum(a => (int)a);
            return (Color)ColorConverter.ConvertFromString(palette[sum % palette.Length]);
        }

        public static Brush GetBrush(string name)
        {
            var brush = new SolidColorBrush(GetColor(name));
            brush.Freeze();
            return brush;
        }

        public static Brush GetCardBrush(string name)
        {
            return StoreBranding.GetCardBrush(GetColor(name));
        }

        private static string GetLetters(string name)
        {
            var capitals = new string(name.Where(char.IsUpper).Take(2).ToArray());
            if (capitals.Length == 2)
            {
                return capitals;
            }

            var letters = new string(name.Where(char.IsLetterOrDigit).Take(2).ToArray());
            return letters.Length == 0 ? "?" : char.ToUpperInvariant(letters[0]) + letters.Substring(1);
        }

        public static ImageSource GetIcon(string name)
        {
            name = name ?? string.Empty;
            lock (icons)
            {
                if (icons.TryGetValue(name, out var icon))
                {
                    return icon;
                }

                const double size = 64;
                var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                var text = new FormattedText(GetLetters(name), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 30, Brushes.White, 1);
                var group = new DrawingGroup();
                group.Children.Add(new GeometryDrawing(GetBrush(name), null, new RectangleGeometry(new Rect(0, 0, size, size), 15, 15)));
                group.Children.Add(new GeometryDrawing(Brushes.White, null, text.BuildGeometry(new Point((size - text.Width) / 2, (size - text.Height) / 2))));
                var image = new DrawingImage(group);
                image.Freeze();
                icons[name] = image;
                return image;
            }
        }

        /// <summary>Turns a folder scan result into an emulator of the library.</summary>
        public static Emulator CreateEmulator(ScannedEmulator scanned)
        {
            var emulator = new Emulator(scanned.Name)
            {
                BuiltInConfigId = scanned.Id,
                BuiltinProfiles = new ObservableCollection<BuiltInEmulatorProfile>(),
                InstallDir = scanned.InstallDir
            };

            foreach (var profile in scanned.Profiles.Where(a => a.Import))
            {
                emulator.BuiltinProfiles.Add(new BuiltInEmulatorProfile
                {
                    Name = profile.Name,
                    BuiltInProfileName = profile.ProfileName
                });
            }

            return emulator;
        }
    }
}
