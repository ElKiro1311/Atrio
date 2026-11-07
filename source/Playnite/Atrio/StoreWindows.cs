using Accessibility;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Playnite.Atrio
{
    /// <summary>A control that a store shows in its window, as the window itself describes it.</summary>
    public class StoreControl
    {
        private const int PushButton = 43;
        private const int CheckButton = 44;
        private const int Link = 30;
        private const int Dialog = 18;
        private const int CheckedState = 0x10;
        private const int UnavailableState = 0x1;

        internal IAccessible Source { get; set; }

        public string Name { get; set; }
        public int Role { get; set; }
        public int Depth { get; set; }
        public int State { get; set; }

        public bool IsButton => Role == PushButton;
        public bool IsCheckBox => Role == CheckButton;
        public bool IsLink => Role == Link;
        public bool IsDialog => Role == Dialog;
        public bool IsChecked => (State & CheckedState) != 0;
        public bool IsEnabled => (State & UnavailableState) == 0;

        /// <summary>Does what a click on the control does, without the mouse and with the window in any state.</summary>
        public virtual void Press()
        {
            Source.accDoDefaultAction(0);
        }
    }

    /// <summary>
    /// Talks to the windows of the store clients from the outside. Nothing here takes the mouse or the
    /// keyboard, the person can go on doing anything else meanwhile.
    /// </summary>
    public static class StoreWindows
    {
        private delegate bool WindowVisitor(IntPtr window, IntPtr data);

        [ComImport]
        [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            [PreserveSig]
            int QueryService(ref Guid service, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object found);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private const uint ClientObject = 0xFFFFFFFC;
        private const uint KeyDown = 0x100;
        private const uint KeyUp = 0x101;
        private const uint MouseMove = 0x200;
        private const uint LeftButtonDown = 0x201;
        private const uint LeftButtonUp = 0x202;
        private const uint WholeWindowContent = 2;
        private const int Minimized = 6;
        private const int Restored = 9;
        private const int MaxControls = 4000;
        private const int MaxDepth = 70;
        private const string ChromiumView = "Chrome_RenderWidgetHostHWND";

        public const int TabKey = 9;
        public const int EnterKey = 13;
        public const int SpaceKey = 32;

        private static readonly Dictionary<int, int> scanCodes = new Dictionary<int, int>
        {
            { TabKey, 15 },
            { EnterKey, 28 },
            { SpaceKey, 57 }
        };

        private static readonly Guid accessibleId = new Guid("618736E0-3C3D-11CF-810C-00AA00389B71");
        private static readonly Guid accessible2Id = new Guid("E89F726E-C4F4-4C19-BB19-B647D7FA8478");
        private static readonly ILogger logger = LogManager.GetLogger();

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(WindowVisitor visitor, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, WindowVisitor visitor, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder text, int size);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr window, int state);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr window, IntPtr context, uint flags);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object found);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleChildren(IAccessible parent, int start, int count, [Out] object[] children, out int obtained);

        private static string GetText(IntPtr window)
        {
            var text = new StringBuilder(300);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        private static string GetClass(IntPtr window)
        {
            var text = new StringBuilder(200);
            GetClassName(window, text, text.Capacity);
            return text.ToString();
        }

        private static List<IntPtr> GetChildren(IntPtr window)
        {
            var children = new List<IntPtr>();
            EnumChildWindows(window, (child, _) =>
            {
                children.Add(child);
                return true;
            }, IntPtr.Zero);
            return children;
        }

        /// <summary>The biggest visible window of a process that passes the filter of title and class.</summary>
        public static IntPtr Find(string process, Func<string, string, bool> filter = null)
        {
            var ids = new HashSet<uint>(Process.GetProcessesByName(process).Select(a => (uint)a.Id));
            var best = IntPtr.Zero;
            var bestArea = 0L;
            if (ids.Count == 0)
            {
                return best;
            }

            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (ids.Contains(owner) && IsWindowVisible(window) && (filter == null || filter(GetText(window), GetClass(window))))
                {
                    GetWindowRect(window, out var rect);
                    var area = IsIconic(window) ? 1L : (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        best = window;
                    }
                }

                return true;
            }, IntPtr.Zero);

            return best;
        }

        public static string GetTitle(IntPtr window) => GetText(window);

        private static IAccessible GetAccessible(IntPtr window)
        {
            var id = accessibleId;
            return AccessibleObjectFromWindow(window, ClientObject, ref id, out var found) == 0 ? found as IAccessible : null;
        }

        /// <summary>
        /// Chromium keeps its controls to itself until a screen reader shows up, and screen readers
        /// announce themselves asking for this interface.
        /// </summary>
        private static void AskForControls(IntPtr window)
        {
            if (GetAccessible(window) is IServiceProvider provider)
            {
                var service = accessibleId;
                var wanted = accessible2Id;
                provider.QueryService(ref service, ref wanted, out _);
            }
        }

        private static void Collect(IAccessible item, int depth, List<StoreControl> controls)
        {
            if (item == null || depth > MaxDepth || controls.Count >= MaxControls)
            {
                return;
            }

            var control = new StoreControl { Source = item, Depth = depth };
            var children = 0;
            try
            {
                children = item.accChildCount;
                control.Name = item.get_accName(0);
                control.Role = item.get_accRole(0) is int role ? role : -1;
                control.State = item.get_accState(0) is int state ? state : 0;
            }
            catch (COMException)
            {
                // The window changed while it was being read, what's left is read in the next round.
            }

            controls.Add(control);
            if (children <= 0)
            {
                return;
            }

            var found = new object[children];
            if (AccessibleChildren(item, 0, children, found, out var obtained) != 0)
            {
                return;
            }

            for (var i = 0; i < obtained; i++)
            {
                Collect(found[i] as IAccessible, depth + 1, controls);
            }
        }

        /// <summary>
        /// Reads the controls of a window and hands them over. The controls only work inside the
        /// function: they belong to a thread of their own, which is what the window expects to talk to.
        /// </summary>
        public static T WithControls<T>(IntPtr window, Func<List<StoreControl>, T> use)
        {
            var result = default(T);
            Exception failure = null;
            var reader = new Thread(() =>
            {
                try
                {
                    AskForControls(window);
                    var views = GetChildren(window).Where(a => GetClass(a) == ChromiumView).ToList();
                    views.ForEach(AskForControls);
                    var controls = new List<StoreControl>();
                    Collect(GetAccessible(window), 0, controls);
                    if (!controls.Any(a => a.IsButton))
                    {
                        controls.Clear();
                        views.ForEach(a => Collect(GetAccessible(a), 0, controls));
                    }

                    result = use(controls);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            });

            reader.SetApartmentState(ApartmentState.STA);
            reader.IsBackground = true;
            reader.Start();
            reader.Join();
            if (failure != null)
            {
                logger.Debug($"Couldn't read a store window: {failure.Message}");
            }

            return result;
        }

#if ATRIO_BANCO
        /// <summary>For the test bench: what a window offers, as seen from here.</summary>
        public static string Describe(string process)
        {
            var window = Find(process);
            var text = new StringBuilder($"ventana {window} '{GetText(window)}' clase {GetClass(window)}\n");
            WithControls(window, controls =>
            {
                text.AppendLine($"controles {controls.Count}, botones {controls.Count(a => a.IsButton)}, dialogos {controls.Count(a => a.IsDialog)}");
                foreach (var control in controls.Where(a => a.IsDialog || a.IsButton || a.IsCheckBox).Take(40))
                {
                    text.AppendLine($"  {new string(' ', control.Depth / 2)}{control.Role} '{control.Name}' 0x{control.State:X}");
                }

                return true;
            });

            return text.ToString();
        }
#endif

        public static void PostKey(IntPtr window, int key)
        {
            var scan = scanCodes.TryGetValue(key, out var code) ? code : 0;
            var data = 1 | (scan << 16);
            PostMessage(window, KeyDown, (IntPtr)key, (IntPtr)data);
            Thread.Sleep(50);

            // The two top bits say that the key was down and is being released.
            PostMessage(window, KeyUp, (IntPtr)key, (IntPtr)unchecked(data | (int)0xC0000000));
            Thread.Sleep(120);
        }

        /// <summary>A click in that point of the window, the pointer stays where it is.</summary>
        public static void PostClick(IntPtr window, int x, int y)
        {
            var position = (IntPtr)((y << 16) | (x & 0xFFFF));
            PostMessage(window, MouseMove, IntPtr.Zero, position);
            Thread.Sleep(60);
            PostMessage(window, LeftButtonDown, (IntPtr)1, position);
            Thread.Sleep(50);
            PostMessage(window, LeftButtonUp, IntPtr.Zero, position);
        }

        /// <summary>What the window shows, even behind others. Null if it can't be copied.</summary>
        public static Bitmap Capture(IntPtr window)
        {
            if (!GetWindowRect(window, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                return null;
            }

            var image = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
            using (var graphics = Graphics.FromImage(image))
            {
                var context = graphics.GetHdc();
                var copied = PrintWindow(window, context, WholeWindowContent);
                graphics.ReleaseHdc(context);
                if (copied)
                {
                    return image;
                }
            }

            image.Dispose();
            return null;
        }

        public static void Minimize(IntPtr window)
        {
            if (window != IntPtr.Zero)
            {
                ShowWindowAsync(window, Minimized);
            }
        }

        /// <summary>For the moments that need the person, like reading a license.</summary>
        public static void Show(IntPtr window)
        {
            if (window != IntPtr.Zero)
            {
                ShowWindowAsync(window, Restored);
                SetForegroundWindow(window);
            }
        }
    }
}
