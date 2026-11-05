using Playnite.Atrio;
using Playnite.Common;
using Playnite.SDK;
using System.Windows.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Playnite.DesktopApp.ViewModels
{
    /// <summary>
    /// Optional atrio-tema.yaml file next to theme.yaml. Regular Playnite themes don't have it.
    /// </summary>
    public class AtrioThemeInfo
    {
        public string Descripcion { get; set; }
        public string Familia { get; set; }
        public int Orden { get; set; } = 100;
        public string Vista { get; set; }
        public string BarraLateral { get; set; }
        public string PanelFiltros { get; set; }
        public double? AnchoPortada { get; set; }
        public string FuenteTitulos { get; set; }
        public string FuenteTexto { get; set; }
    }

    public class ThemeCard
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Author { get; set; }
        public string Description { get; set; }
        public string Family { get; set; }
        public int Order { get; set; }
        public string PreviewPath { get; set; }
        public bool IsCurrent { get; set; }
        public AtrioThemeInfo Info { get; set; }
    }

    public class ThemeFamily
    {
        public string Name { get; set; }
        public List<ThemeCard> Themes { get; set; }
    }

    public class AccentSwatch
    {
        public string Color { get; set; }
        public Brush Brush { get; set; }
    }

    public class ThemesViewModel : ObservableObject
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string ThemeInfoFile = "atrio-tema.yaml";
        private const string AccentResourceKey = "GlyphColor";
        private const string AccentSoftResourceKey = "HighlightGlyphColor";

        private static readonly string[] accentResources = new string[]
        {
            AccentResourceKey, AccentSoftResourceKey, "GlyphBrush", "HighlightGlyphBrush", "HoverBrush",
            "AtrioPlayBrush", "AtrioPlayHoverBrush", "AtrioPlayTextBrush"
        };

        private static readonly string[] accentColors = new string[]
        {
            "#4F9DFF", "#26BBFF", "#66C0F4", "#22C55E", "#A1CD44", "#F59E0B", "#F97316", "#EF4444", "#EC4899", "#A855F7", "#7C6CFF", "#14B8A6"
        };

        private readonly DesktopAppViewModel main;

        private List<ThemeFamily> families;
        public List<ThemeFamily> Families
        {
            get => families;
            set => SetValue(ref families, value);
        }

        public List<AccentSwatch> Accents { get; }

        public RelayCommand<ThemeCard> ApplyCommand { get; }
        public RelayCommand<AccentSwatch> SelectAccentCommand { get; }
        public RelayCommand<object> ResetAccentCommand { get; }
        public RelayCommand<object> BrowseThemesCommand { get; }

        public ThemesViewModel(DesktopAppViewModel main)
        {
            this.main = main;
            Accents = accentColors.Select(a => new AccentSwatch
            {
                Color = a,
                Brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(a))
            }).ToList();

            ApplyCommand = new RelayCommand<ThemeCard>(Apply);
            SelectAccentCommand = new RelayCommand<AccentSwatch>(SelectAccent);
            ResetAccentCommand = new RelayCommand<object>(_ => ResetAccent());
            BrowseThemesCommand = new RelayCommand<object>(_ => main.OpenAddonsCommand.Execute(null));
        }

        /// <summary>The themes of Atrio point to a translated text, third party ones may just say it.</summary>
        private static string Translate(string text)
        {
            return text?.StartsWith("LOC", StringComparison.Ordinal) == true ? ResourceProvider.GetString(text) : text;
        }

        public void Refresh()
        {
            var cards = new List<ThemeCard>();
            foreach (var theme in ThemeManager.GetAvailableThemes(ApplicationMode.Desktop))
            {
                try
                {
                    var infoPath = Path.Combine(theme.DirectoryPath, ThemeInfoFile);
                    var info = File.Exists(infoPath) ? Playnite.SDK.Data.Serialization.FromYamlFile<AtrioThemeInfo>(infoPath) : null;
                    var isClassic = theme.Id == ThemeManager.DefaultDesktopThemeId;
                    cards.Add(new ThemeCard
                    {
                        Id = theme.Id,
                        Name = isClassic ? ResourceProvider.GetString("LOCAtrioThemeClassic") : theme.Name,
                        Author = theme.Author,
                        Description = Translate(info?.Descripcion) ?? (isClassic ? ResourceProvider.GetString("LOCAtrioThemeClassicDescription") : ResourceProvider.GetString("LOCAtrioThemeThirdParty")),
                        Family = Translate(info?.Familia) ?? ResourceProvider.GetString("LOCAtrioThemeFamilyOther"),
                        Order = info?.Orden ?? 1000,
                        PreviewPath = GetPreview(theme.DirectoryPath),
                        IsCurrent = theme.Id == main.AppSettings.Theme,
                        Info = info
                    });
                }
                catch (Exception e)
                {
                    logger.Error(e, $"Failed to read theme {theme.Name}");
                }
            }

            Families = cards.
                OrderBy(a => a.Order).
                GroupBy(a => a.Family).
                Select(a => new ThemeFamily { Name = a.Key, Themes = a.ToList() }).
                ToList();
        }

        private static string GetPreview(string themeDir)
        {
            foreach (var file in new string[] { "preview.jpg", "preview.png" })
            {
                var path = Path.Combine(themeDir, file);
                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        }

        public void Apply(ThemeCard card)
        {
            if (card == null || card.IsCurrent)
            {
                return;
            }

            var settings = main.AppSettings;
            settings.Theme = card.Id;
            ApplyLayout(settings, card.Info);
            settings.SaveSettings();

            // Only the look changes: there is no reason to sync every store again.
            main.App.Restart(new CmdLineOptions { SkipLibUpdate = true, HideSplashScreen = Atrio.AtrioCaptura.IsActive }, true);
        }

        /// <summary>A theme can come with a preferred layout: that is what makes each variant feel different.</summary>
        public static void ApplyLayout(PlayniteSettings settings, AtrioThemeInfo info)
        {
            if (info == null)
            {
                return;
            }

            if (Enum.TryParse<DesktopView>(info.Vista, out var view))
            {
                settings.ViewSettings.GamesViewType = view;
            }

            if (Enum.TryParse<Dock>(info.BarraLateral, out var sidebar))
            {
                settings.SidebarPosition = sidebar;
            }

            if (Enum.TryParse<Dock>(info.PanelFiltros, out var filters))
            {
                settings.FilterPanelPosition = filters;
            }

            if (info.AnchoPortada != null)
            {
                settings.GridItemWidth = info.AnchoPortada.Value;
            }
        }

        private void SelectAccent(AccentSwatch swatch)
        {
            if (swatch == null)
            {
                return;
            }

            var settings = GameUnifier.Current.Settings;
            settings.AccentColor = swatch.Color;
            settings.Save();
            ApplyAccent(swatch.Color);
        }

        private void ResetAccent()
        {
            var settings = GameUnifier.Current.Settings;
            settings.AccentColor = null;
            settings.Save();
            foreach (var key in accentResources)
            {
                Application.Current.Resources.Remove(key);
            }
        }

        private static Color Mix(Color first, Color second, double amount)
        {
            return Color.FromRgb(
                (byte)(first.R + (second.R - first.R) * amount),
                (byte)(first.G + (second.G - first.G) * amount),
                (byte)(first.B + (second.B - first.B) * amount));
        }

        /// <summary>Loads the fonts a theme ships in its Fonts folder. Nothing gets installed in Windows.</summary>
        public static void ApplyFonts()
        {
            try
            {
                var theme = ThemeManager.CurrentTheme;
                var infoPath = theme == null ? null : Path.Combine(theme.DirectoryPath, ThemeInfoFile);
                if (infoPath == null || !File.Exists(infoPath))
                {
                    return;
                }

                var info = Playnite.SDK.Data.Serialization.FromYamlFile<AtrioThemeInfo>(infoPath);
                var folder = new Uri(Path.Combine(theme.DirectoryPath, "Fonts") + Path.DirectorySeparatorChar);
                if (!info.FuenteTitulos.IsNullOrEmpty())
                {
                    Application.Current.Resources["AtrioDisplayFont"] = new FontFamily(folder, "./#" + info.FuenteTitulos);
                }

                if (!info.FuenteTexto.IsNullOrEmpty())
                {
                    Application.Current.Resources["FontFamily"] = new FontFamily(folder, "./#" + info.FuenteTexto);
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to load theme fonts.");
            }
        }

        /// <summary>
        /// Every brush that uses the accent references this color as a dynamic resource,
        /// so changing it recolors the app on the spot, with no restart.
        /// </summary>
        public static void ApplyAccent(string color)
        {
            if (color.IsNullOrEmpty())
            {
                return;
            }

            try
            {
                var accent = (Color)ColorConverter.ConvertFromString(color);
                var soft = Color.FromArgb(0x33, accent.R, accent.G, accent.B);
                var luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
                var resources = Application.Current.Resources;
                resources[AccentResourceKey] = accent;
                resources[AccentSoftResourceKey] = soft;
                resources["GlyphBrush"] = new SolidColorBrush(accent);
                resources["HighlightGlyphBrush"] = new SolidColorBrush(soft);
                resources["HoverBrush"] = new SolidColorBrush(soft);
                resources["AtrioPlayBrush"] = new LinearGradientBrush(accent, Mix(accent, Colors.Black, 0.18), 0);
                resources["AtrioPlayHoverBrush"] = new SolidColorBrush(Mix(accent, Colors.White, 0.18));
                resources["AtrioPlayTextBrush"] = new SolidColorBrush(luminance > 0.62 ? Color.FromRgb(0x0B, 0x0D, 0x12) : Colors.White);
            }
            catch (Exception e)
            {
                logger.Error(e, $"Invalid accent color {color}");
            }
        }
    }
}
