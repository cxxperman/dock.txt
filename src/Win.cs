// dock.txt. Win.cs — разговор с Windows: какие окна открыты, чьи они, вывести вперёд, свернуть, закрыть;
// значки из файлов программ, ярлыки панели задач, запуск.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DevTxt.DockApp
{
    static class Win
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }
        delegate bool EnumProc(IntPtr hwnd, IntPtr l);

        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        const long WS_CAPTION = 0x00C00000, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
        const uint GW_OWNER = 4, WM_SYSCOMMAND = 0x0112;
        const int SC_CLOSE = 0xF060, SW_MINIMIZE = 6, SW_RESTORE = 9, DWMWA_CLOAKED = 14;

        static readonly uint self = (uint)Process.GetCurrentProcess().Id;
        static Dictionary<IntPtr, WinInfo> known = new Dictionary<IntPtr, WinInfo>();   // чьё окно — узнаём один раз, а не каждую секунду

        // Док не забирает фокус и не попадает в Alt+Tab
        public static void MakeBar(IntPtr hwnd)
        {
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
        }

        public static bool Cursor(out Point p)
        {
            POINT c;
            bool ok = GetCursorPos(out c);
            p = new Point(c.X, c.Y);
            return ok;
        }

        public static IntPtr Foreground() { return GetForegroundWindow(); }

        static string Text(IntPtr hwnd)
        {
            int n = GetWindowTextLength(hwnd);
            if (n <= 0) return "";
            var sb = new StringBuilder(n + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        static string Class(IntPtr hwnd)
        {
            var sb = new StringBuilder(128);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        static string PathOf(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION: хватает и для чужих программ
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
            }
            finally { CloseHandle(h); }
        }

        // Окно попало бы на панель задач: видно, без владельца, не служебное, не спрятано системой, с заголовком
        static bool OnTaskbar(IntPtr hwnd)
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            if ((ex & WS_EX_APPWINDOW) == 0 && (ex & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0) return false;
            if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
            int cloaked;
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            return GetWindowTextLength(hwnd) > 0;
        }

        // Окна панели задач, сверху вниз
        public static List<WinInfo> Taskbar()
        {
            var list = new List<WinInfo>();
            var next = new Dictionary<IntPtr, WinInfo>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr l)
            {
                try
                {
                    if (!OnTaskbar(hwnd)) return true;
                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    if (pid == self) return true;
                    WinInfo w;
                    bool host = false;
                    if (!known.TryGetValue(hwnd, out w) || w.Path == null || w.Key != null)
                    {
                        w = new WinInfo { Hwnd = hwnd, Path = PathOf(pid) };
                        host = Class(hwnd) == "ApplicationFrameWindow";
                    }
                    w.Title = Text(hwnd);
                    if (host)
                    {
                        // Приложение из Магазина: окно держит общая программа-рамка, а само приложение — в дочернем окне
                        string inner = null;
                        EnumChildWindows(hwnd, delegate(IntPtr child, IntPtr l2)
                        {
                            uint cp;
                            GetWindowThreadProcessId(child, out cp);
                            if (cp == pid) return true;
                            inner = PathOf(cp);
                            return inner == null;
                        }, IntPtr.Zero);
                        if (inner != null) w.Path = inner;
                        else { w.Key = "frame|" + w.Title.ToLowerInvariant(); w.Label = w.Title; }   // свёрнуто: приложения внутри нет, узнаём по заголовку
                    }
                    if (w.Path == null) return true;
                    next[hwnd] = w;
                    list.Add(w);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            known = next;
            return list;
        }

        // Впереди окно на весь экран (игра, видео)? Рабочий стол и панель задач не в счёт.
        public static bool FullscreenAhead()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            if (pid == self) return false;
            string cls = Class(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "XamlExplorerHostIslandWindow") return false;
            RECT r;
            if (!GetWindowRect(fg, out r)) return false;
            bool caption = (GetWindowLongPtr(fg, GWL_STYLE).ToInt64() & WS_CAPTION) == WS_CAPTION;
            return Logic.IsFullscreen(r.Left, r.Top, r.Right, r.Bottom, GetSystemMetrics(0), GetSystemMetrics(1), caption);
        }

        public static void Activate(IntPtr hwnd)
        {
            if (IsIconic(hwnd)) ShowWindowAsync(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
        }

        public static void Minimize(IntPtr hwnd) { ShowWindowAsync(hwnd, SW_MINIMIZE); }

        // То же, что крестик в заголовке: программа сама решает, спрашивать ли о несохранённом
        public static void Close(IntPtr hwnd) { PostMessage(hwnd, WM_SYSCOMMAND, new IntPtr(SC_CLOSE), IntPtr.Zero); }
    }

    static class Shell
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr icon);

        static readonly Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Значок программы из её файла, крупный — чтобы не расплывался при увеличении. where — "файл" или "файл,номер".
        public static ImageSource Icon(string where)
        {
            if (string.IsNullOrEmpty(where)) return null;
            ImageSource img;
            if (icons.TryGetValue(where, out img)) return img;
            img = Extract(where, 128);
            icons[where] = img;
            return img;
        }

        // Достаёт значок нужного размера и ничего не запоминает. Картинка заморожена: её можно отдать другому потоку.
        public static ImageSource Extract(string where, int px)
        {
            if (string.IsNullOrEmpty(where)) return null;
            string file = where;
            int index = 0, comma = where.LastIndexOf(',');
            if (comma > 2 && int.TryParse(where.Substring(comma + 1).Trim(), out index)) file = where.Substring(0, comma);
            else index = 0;
            try
            {
                file = Environment.ExpandEnvironmentVariables(file.Trim());
                var h = new IntPtr[1];
                if (File.Exists(file) && PrivateExtractIcons(file, index, px, px, h, new uint[1], 1, 0) != 0 && h[0] != IntPtr.Zero)
                {
                    try
                    {
                        var src = Imaging.CreateBitmapSourceFromHIcon(h[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        src.Freeze();
                        return src;
                    }
                    finally { DestroyIcon(h[0]); }
                }
            }
            catch { }
            return null;
        }

        // Как назвать программу: описание из её файла («Блокнот»); если оно похоже на вспомогательный процесс
        // или его нет — название продукта, а если нет и его — имя файла. Само правило — в Logic.DisplayName.
        public static string NameOf(string path)
        {
            string name;
            if (names.TryGetValue(path, out name)) return name;
            string description = null, product = null;
            try
            {
                FileVersionInfo v = FileVersionInfo.GetVersionInfo(path);
                description = v.FileDescription; product = v.ProductName;
            }
            catch { }
            name = Logic.DisplayName(description, product, path);
            if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(path);
            names[path] = name;
            return name;
        }

        static string Prop(object o, string name)
        {
            try { return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null) as string; } catch { return null; }
        }

        // Куда ведёт ярлык. null — ярлык не на файл (приложение из Магазина, папка) или файла уже нет.
        public static Pin Resolve(string lnk)
        {
            object shell = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                shell = Activator.CreateInstance(t);
                return Resolve(t, shell, lnk);
            }
            catch { return null; }
            finally
            {
                try { if (shell != null) Marshal.ReleaseComObject(shell); } catch { }
            }
        }

        // То же, когда ярлыков много: объект WScript.Shell создаётся один раз снаружи
        public static Pin Resolve(Type t, object shell, string lnk)
        {
            object link = null;
            try
            {
                link = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                string target = Prop(link, "TargetPath");
                if (string.IsNullOrEmpty(target) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(target)) return null;
                string args = Prop(link, "Arguments"), icon = Prop(link, "IconLocation");
                if (icon != null && (icon.Trim().StartsWith(",") || icon.Trim().Length == 0)) icon = null;   // «,0» — значок самой программы
                return new Pin { Name = Path.GetFileNameWithoutExtension(lnk), Path = target, Link = lnk, Args = string.IsNullOrEmpty(args) ? null : args, Icon = icon };
            }
            catch { return null; }
            finally
            {
                try { if (link != null) Marshal.ReleaseComObject(link); } catch { }
            }
        }

        // Запускает ярлык из меню приложений — так же, как это сделал бы «Пуск»
        public static void Open(string link)
        {
            using (Process.Start(new ProcessStartInfo(link) { UseShellExecute = true })) { }
        }

        // Первый запуск: берём то, что человек уже закрепил на панели задач Windows. Только читаем.
        public static List<Pin> TaskbarPins()
        {
            var pins = new List<Pin>();
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (!Directory.Exists(dir)) return pins;
                string[] files = Directory.GetFiles(dir, "*.lnk");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    Pin p = Resolve(f);
                    if (p != null && !pins.Exists(delegate(Pin x) { return string.Equals(x.Path, p.Path, StringComparison.OrdinalIgnoreCase); })) pins.Add(p);
                }
            }
            catch { }
            return pins;
        }

        // Запускает приложение: через ярлык, если он есть и цел (в нём параметры и рабочая папка), иначе сам файл
        public static void Launch(DockItem item)
        {
            string what = item.Link != null && File.Exists(item.Link) ? item.Link : item.Path;
            var info = new ProcessStartInfo(what) { UseShellExecute = true };
            if (what == item.Path)
            {
                if (item.Pin != null && item.Pin.Args != null) info.Arguments = item.Pin.Args;
                try { info.WorkingDirectory = Path.GetDirectoryName(item.Path); } catch { }
            }
            using (Process.Start(info)) { }
        }
    }
}
