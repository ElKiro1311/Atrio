using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Atrio.Updater
{
    /// <summary>
    /// Finishes an update once Atrio has closed, because a program can't replace itself while it runs. It starts from
    /// the update folder, so none of the files it replaces is in use, and it opens Atrio again at the end.
    /// </summary>
    public static class Program
    {
        private const int CloseTimeout = 60000;
        private static readonly string[] programs = { "Atrio", "Atrio.Fullscreen", "Atrio.Browser" };

        [STAThread]
        public static int Main(string[] arguments)
        {
            if (arguments.Length != 1 || !File.Exists(arguments[0]))
            {
                return 2;
            }

            var plan = UpdatePlan.Load(arguments[0]);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var label = new Label
            {
                Text = plan.WaitingText,
                Dock = DockStyle.Fill,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10.5f),
                TextAlign = ContentAlignment.MiddleCenter
            };
            var bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 6, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30 };
            var form = new Form
            {
                Text = "Atrio",
                ClientSize = new Size(420, 110),
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.None,
                BackColor = Color.FromArgb(24, 26, 33),
                ShowInTaskbar = true
            };
            form.Controls.Add(label);
            form.Controls.Add(bar);

            var exitCode = 0;
            form.Shown += async (_, __) =>
            {
                exitCode = await Task.Run(() => Run(plan, text => form.BeginInvoke(new Action(() => label.Text = text))));
                form.Close();
            };

            Application.Run(form);
            return exitCode;
        }

        private static int Run(UpdatePlan plan, Action<string> showStatus)
        {
            var exitCode = 0;
            try
            {
                WaitForAtrio(plan);
                showStatus(string.Format(plan.InstallingText, plan.Version));
                new UpdateInstaller(plan.Target, plan.Package, plan.Backup).Install();
                File.WriteAllText(plan.Marker, plan.Version);
            }
            catch (Exception e)
            {
                exitCode = 1;
                MessageBox.Show(string.Format(plan.FailedText, e.Message), "Atrio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            try
            {
                Process.Start(new ProcessStartInfo(plan.Start, plan.StartArguments) { WorkingDirectory = plan.Target, UseShellExecute = false })?.Dispose();
            }
            catch (Exception e)
            {
                exitCode = 1;
                MessageBox.Show(e.Message, "Atrio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return exitCode;
        }

        private static void WaitForAtrio(UpdatePlan plan)
        {
            var deadline = DateTime.Now.AddMilliseconds(CloseTimeout);
            try
            {
                using (var process = Process.GetProcessById(plan.WaitFor))
                {
                    process.WaitForExit(CloseTimeout);
                }
            }
            catch (ArgumentException)
            {
                // It has already closed.
            }

            while (IsAtrioRunning(plan.Target))
            {
                if (DateTime.Now > deadline)
                {
                    throw new TimeoutException(plan.StillOpenText);
                }

                Thread.Sleep(200);
            }
        }

        private static bool IsAtrioRunning(string folder)
        {
            var root = folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return programs.SelectMany(Process.GetProcessesByName).Any(process =>
            {
                using (process)
                {
                    try
                    {
                        return process.MainModule?.FileName.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true;
                    }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is InvalidOperationException)
                    {
                        // Closing right now, or not one of ours.
                        return false;
                    }
                }
            });
        }
    }
}
