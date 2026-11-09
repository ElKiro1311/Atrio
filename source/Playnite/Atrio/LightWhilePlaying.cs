using Playnite.Controls;
using Playnite.SDK;
using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Playnite.Atrio
{
    /// <summary>
    /// While a game is open and Atrio isn't on screen, Atrio lets go of what it keeps in memory only to be quick:
    /// the pictures it already decoded and the blurred copies of the backgrounds. Windows gets that memory back for
    /// the game. Everything loads again from the disk when Atrio is shown.
    /// </summary>
    public static class LightWhilePlaying
    {
        /// <summary>Atrio waits a bit, so a quick look at the game and back changes nothing.</summary>
        public static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

        /// <summary>While the game goes on, it lets go again from time to time: some pages keep loading in the background.</summary>
        public static readonly TimeSpan Again = TimeSpan.FromMinutes(10);

        private static readonly ILogger logger = LogManager.GetLogger();
        private static DispatcherTimer timer;
        private static Func<bool> isEnabled;
        private static Func<bool> isGameOpen;
        private static DateTime? hiddenSince;
        private static DateTime? lastLight;

        public static void Attach(Dispatcher dispatcher, Func<bool> enabled, Func<bool> gameOpen)
        {
            if (timer != null || dispatcher == null)
            {
                return;
            }

            isEnabled = enabled;
            isGameOpen = gameOpen;
            timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(10) };
            timer.Tick += (_, __) => Check(DateTime.Now);
            timer.Start();
        }

        /// <summary>Minimized, hidden in the tray or covered by nothing: the window isn't on screen.</summary>
        public static bool IsWindowHidden(Window window)
        {
            return window == null || !window.IsVisible || window.WindowState == WindowState.Minimized;
        }

        /// <summary>Whether this check is the moment to let go: the whole decision, apart from the window and the games.</summary>
        public static bool ShouldLighten(bool enabled, bool gameOpen, bool hidden, DateTime now, ref DateTime? hiddenFrom, ref DateTime? last)
        {
            if (!enabled || !gameOpen || !hidden)
            {
                hiddenFrom = null;
                return false;
            }

            if (hiddenFrom == null)
            {
                hiddenFrom = now;
                return false;
            }

            if (now - hiddenFrom.Value < Wait)
            {
                return false;
            }

            if (last != null && last.Value >= hiddenFrom.Value && now - last.Value < Again)
            {
                return false;
            }

            last = now;
            return true;
        }

        private static void Check(DateTime now)
        {
            try
            {
                var hidden = IsWindowHidden(Application.Current?.MainWindow);
                if (ShouldLighten(isEnabled?.Invoke() == true, isGameOpen?.Invoke() == true, hidden, now, ref hiddenSince, ref lastLight))
                {
                    Lighten();
                }
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to let go of memory while a game is open.");
            }
        }

        /// <summary>Lets go now, and tells how much memory Atrio had in use before and after.</summary>
        public static Tuple<long, long> Lighten()
        {
            long before, privateBefore;
            using (var process = Process.GetCurrentProcess())
            {
                before = process.WorkingSet64;
                privateBefore = process.PrivateMemorySize64;
            }

            ImageSourceManager.Cache.Clear();
            var copies = FadeImage.ClearBlurredCopies();
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // The pages Windows takes back stay in its standby list: if Atrio needs them, they come back quickly.
            using (var process = Process.GetCurrentProcess())
            {
                SetProcessWorkingSetSize(process.Handle, new IntPtr(-1), new IntPtr(-1));
            }

            long after, privateAfter;
            using (var process = Process.GetCurrentProcess())
            {
                after = process.WorkingSet64;
                privateAfter = process.PrivateMemorySize64;
            }

            logger.Info($"Let go of memory while a game is open: in use {before / 1048576} MB -> {after / 1048576} MB, " +
                $"own {privateBefore / 1048576} MB -> {privateAfter / 1048576} MB ({copies} blurred copies).");
            return Tuple.Create(before, after);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);
    }
}
