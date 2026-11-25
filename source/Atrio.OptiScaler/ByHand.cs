using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Atrio.OptiScaler
{
    /// <summary>What there is in a game of an OptiScaler that was put by hand, which nobody wrote down.</summary>
    internal static class ByHand
    {
        // Only OptiScaler and its scripts have these names.
        private static readonly Regex certain = new Regex(
            @"^(OptiScaler\.(ini|log|dll|asi)|fakenvapi\.(dll|ini|log)|dlssg_to_fsr3_amd_is_better\.dll|dlssg_to_fsr3\.log|setup_windows\.bat|setup_linux\.sh|" +
            @"remove[ _]optiscaler\.(bat|sh)|!! (README_)?EXTRACT ALL FILES TO GAME FOLDER !!(\.txt)?)$", RegexOptions.IgnoreCase);

        // The ones its packages up to the 0.9 put next to the game, which a game can have too.
        private static readonly Regex shared = new Regex(
            @"^(amd_fidelityfx_(dx12|vk|(loader|upscaler|framegeneration|denoiser|radiancecache)_(dx12|vk))\.dll|libxess(_dx11|_fg)?\.dll|libxell\.dll|nvapi64\.dll|" +
            @"Licenses\\(DirectX|FidelityFX(_v\d+)?|XeSS)_LICENSE\.[a-z]+)$", RegexOptions.IgnoreCase);

        // These come later, when the game runs or when one of its scripts does.
        private static readonly Regex later = new Regex(@"(\.log|\\remove[ _]optiscaler\.(bat|sh))$", RegexOptions.IgnoreCase);

        // A package is taken out in moments, and a game was put in its folder in some other one.
        private static readonly TimeSpan together = TimeSpan.FromMinutes(2);

        public static bool IsCertain(string relative)
        {
            return certain.IsMatch(relative);
        }

        /// <summary>
        /// The ones a game can have too that came with OptiScaler: they were made at the same time as the ones that can only be
        /// of OptiScaler, and no other program of the folder was. When the whole folder was copied from another place there's
        /// no way to know, and none is.
        /// </summary>
        /// <param name="known">The ones that are of OptiScaler for sure.</param>
        public static List<string> CameTogether(string folder, IEnumerable<string> known)
        {
            var sure = new HashSet<string>(known, StringComparer.OrdinalIgnoreCase);
            var made = sure.Where(a => File.Exists(a) && !later.IsMatch(a)).Select(File.GetCreationTimeUtc).ToList();
            var here = Directory.GetFiles(folder);
            var ofTheGame = here.Where(a => !sure.Contains(a) && !shared.IsMatch(Path.GetFileName(a)) && IsProgram(a)).Select(File.GetCreationTimeUtc).ToList();
            return here.Concat(Plan.Files(Path.Combine(folder, "Licenses"))).
                Where(a => !sure.Contains(a) && shared.IsMatch(Path.GetRelativePath(folder, a))).
                Where(a => IsNear(File.GetCreationTimeUtc(a), made) && !IsNear(File.GetCreationTimeUtc(a), ofTheGame)).ToList();
        }

        private static bool IsProgram(string path)
        {
            return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNear(DateTime time, IEnumerable<DateTime> times)
        {
            return times.Any(a => (a - time).Duration() <= together);
        }
    }
}
