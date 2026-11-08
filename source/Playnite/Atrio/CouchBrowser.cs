using System;
using System.Text.RegularExpressions;
using System.Windows;

namespace Playnite.Atrio
{
    /// <summary>
    /// The arithmetic of the browser of the mode for the couch: where a typed text goes and how the sticks
    /// move the pointer and the page. The window itself lives with the mode for the couch.
    /// </summary>
    public static class CouchBrowser
    {
        /// <summary>DuckDuckGo, as the guides of the achievements: Google answers this browser with a captcha.</summary>
        public const string SearchUrl = "https://duckduckgo.com/?q=";
        public const string StartPage = "https://duckduckgo.com/";

        /// <summary>Under this the stick is at rest: no controller comes back to exactly zero.</summary>
        public const double DeadZone = 0.18;

        /// <summary>Pixels per second with the right stick all the way.</summary>
        public const double ScrollSpeed = 2200;

        /// <summary>An address when it looks like one, otherwise a search. Null for nothing.</summary>
        public static string GetTarget(string typed)
        {
            var text = typed?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            if (Regex.IsMatch(text, @"^[a-zA-Z][a-zA-Z0-9+.\-]*://"))
            {
                return text;
            }

            if (Regex.IsMatch(text, @"^(localhost|[^\s/:]+\.[a-zA-Z]{2,})(:\d+)?([/?#]\S*)?$"))
            {
                return "https://" + text;
            }

            return SearchUrl + Uri.EscapeDataString(text);
        }

        /// <summary>The address of a search for a guide of the game, as the button of the achievements.</summary>
        public static string GetGuideSearch(string gameName, string guideWord)
        {
            return SearchUrl + Uri.EscapeDataString($"{gameName} {guideWord}".Trim());
        }

        public static double ToAxis(short value)
        {
            return Math.Max(-1, Math.Min(1, value / 32767.0));
        }

        /// <summary>
        /// How fast the pointer goes for a position of the stick: nothing inside the dead zone, then growing
        /// with the square, so a little push is precise and a full one crosses the screen in a second.
        /// </summary>
        public static Vector GetPointerSpeed(double x, double y, double maxSpeed)
        {
            var magnitude = Math.Sqrt((x * x) + (y * y));
            if (magnitude < DeadZone)
            {
                return new Vector(0, 0);
            }

            var push = Math.Min(1, (magnitude - DeadZone) / (1 - DeadZone));
            var speed = maxSpeed * push * push;
            return new Vector(x / magnitude * speed, y / magnitude * speed);
        }

        /// <summary>The scroll of the page for the right stick: up on the stick goes up on the page.</summary>
        public static Vector GetScrollSpeed(double x, double y)
        {
            var speed = GetPointerSpeed(x, y, ScrollSpeed);
            return new Vector(-speed.X, -speed.Y);
        }

        /// <summary>
        /// The zoom a page starts with: on a big screen without Windows scaling, pages would read as on a
        /// screen of 1080 lines. Each step of Chromium is 20 % more.
        /// </summary>
        public static double GetStartZoom(double scale)
        {
            if (scale <= 1)
            {
                return 0;
            }

            return Math.Min(5, Math.Round(Math.Log(scale) / Math.Log(1.2), 1));
        }

        /// <summary>Keeps the pointer inside the page.</summary>
        public static Point Clamp(Point point, double width, double height)
        {
            return new Point(
                Math.Max(0, Math.Min(Math.Max(0, width - 1), point.X)),
                Math.Max(0, Math.Min(Math.Max(0, height - 1), point.Y)));
        }
    }
}
