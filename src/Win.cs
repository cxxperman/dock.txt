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
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);

        const int GWL_EXSTYLE = -20;
        const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
        const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
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

        // Окно на главном мониторе — там, где стоит док (точка 0,0 всегда на главном). Окна нет или монитор не узнать —
        // считаем, что на главном: тогда решает одно правило игры на весь экран, как и раньше.
        public static bool OnDockScreen(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return true;
            try
            {
                IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                return mon == IntPtr.Zero || mon == MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
            }
            catch { return true; }
        }

        public static bool Minimized(IntPtr hwnd) { try { return IsIconic(hwnd); } catch { return false; } }

        // Клавиша сейчас нажата? Только вопрос к Windows: ввод не перехватывается. Док не берёт фокус,
        // поэтому Esc во время перетаскивания значка до него иначе не дошёл бы.
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        public const int KeyEscape = 0x1B;
        public static bool KeyDown(int key) { try { return (GetAsyncKeyState(key) & 0x8000) != 0; } catch { return false; } }

        // Заголовок окна — для списка окон в меню значка
        public static string Title(IntPtr hwnd)
        {
            try { return Text(hwnd); } catch { return ""; }
        }

        // Windows сама сообщает, что впереди стало другое окно: точка под значком и полноэкранный режим не ждут секунду.
        // Это не перехватчик ввода: сообщение приходит в поток окна дока, в чужие программы ничего не внедряется.
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        const uint EVENT_SYSTEM_FOREGROUND = 3;
        static WinEventProc watchProc;   // держим ссылку: иначе сборщик мусора уберёт то, что зовёт Windows
        static IntPtr watch;

        public static void WatchForeground(Action changed)
        {
            if (watch != IntPtr.Zero || changed == null) return;
            watchProc = delegate { try { changed(); } catch { } };
            try { watch = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, watchProc, 0, 0, 0); } catch { watch = IntPtr.Zero; }
        }

        public static void Unwatch()
        {
            if (watch == IntPtr.Zero) return;
            try { UnhookWinEvent(watch); } catch { }
            watch = IntPtr.Zero;
        }

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
                    if (!known.TryGetValue(hwnd, out w) || w.Path == null || w.Key != null || w.Loose)
                    {
                        w = new WinInfo { Hwnd = hwnd, Path = PathOf(pid) };
                        host = Class(hwnd) == "ApplicationFrameWindow";
                        if (!host) w.App = Packaged.OfProcess(pid);   // упакованная программа со своим окном (Терминал, новый Блокнот)
                    }
                    w.Title = Text(hwnd);
                    if (host)
                    {
                        // Приложение из Магазина: окно держит общая программа-рамка, а само приложение — в дочернем окне
                        string inner = null;
                        uint innerPid = 0;
                        EnumChildWindows(hwnd, delegate(IntPtr child, IntPtr l2)
                        {
                            uint cp;
                            GetWindowThreadProcessId(child, out cp);
                            if (cp == pid) return true;
                            inner = PathOf(cp);
                            innerPid = cp;
                            return inner == null;
                        }, IntPtr.Zero);
                        if (inner != null) { w.Path = inner; w.App = Packaged.OfProcess(innerPid) ?? Packaged.OfWindow(hwnd); }
                        else
                        {
                            // Свёрнуто: приложения внутри нет. Окно само знает, чьё оно; не знает — узнаём по заголовку.
                            w.App = Packaged.OfWindow(hwnd);
                            w.Loose = true;
                            w.Label = w.Title;
                            if (w.App == null) w.Key = "frame|" + w.Title.ToLowerInvariant();
                        }
                    }
                    if (w.App != null)
                    {
                        // Название — то же, что в «Пуске», как только список приложений собран
                        string app = Packaged.Name(w.App);
                        if (app != null) w.Label = app;
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

        // Значок для дока: свой значок ярлыка, значок приложения из Магазина или значок из файла программы
        public static ImageSource IconOf(DockItem item)
        {
            return Icon(item.Icon) ?? (item.App != null ? Packaged.Icon(item.App) : null) ?? Icon(item.Exe ?? item.Path);
        }

        // Запускает приложение: через ярлык, если он есть и цел (в нём параметры и рабочая папка), иначе сам файл.
        // Приложение из Магазина — по идентификатору.
        public static void Launch(DockItem item)
        {
            if (item.App != null) { Packaged.Open(item.App); return; }
            string what = item.Link != null && File.Exists(item.Link) ? item.Link : item.Path;
            var info = new ProcessStartInfo(what) { UseShellExecute = true };
            if (what == item.Path)
            {
                if (item.Pin != null && item.Pin.Args != null) info.Arguments = item.Pin.Args;
                try { info.WorkingDirectory = Path.GetDirectoryName(item.Path); } catch { }
            }
            using (Process.Start(info)) { }
        }

        // «Показать в папке»: Проводник открывает папку программы и выделяет её файл
        public static void Reveal(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            using (Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true })) { }
        }
    }

    // Выключение, перезагрузка, сон и блокировка из меню приложений. Работает только в настоящем доке:
    // пока Armed не включён (снимки, --demo, --selftest, --probe, --headless), Run ничего не делает.
    static class Session
    {
        [DllImport("user32.dll")] static extern bool LockWorkStation();

        public static bool Armed;

        public static bool Run(string kind)
        {
            string exe, args;
            if (!Armed || !SessionRules.Command(kind, out exe, out args)) return false;
            try
            {
                if (exe == null) return LockWorkStation();
                using (Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe), args) { UseShellExecute = false, CreateNoWindow = true })) { }
                return true;
            }
            catch { return false; }
        }
    }
}
