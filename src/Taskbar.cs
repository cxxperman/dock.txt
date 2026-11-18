// dock.txt. Taskbar.cs — док вместо панели задач Windows и меню приложений по кнопке Windows.
// Панель задач не удаляется и не ломается: ей включается автоскрытие (чтобы окна занимали весь экран) и она
// прячется. Как было — записано в файл рядом с настройками: при выходе дока всё возвращается, а если док упал,
// вернёт core.txt (он смотрит на этот же файл) или сам док при следующем запуске.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace DevTxt.DockApp
{
    static class TaskbarSwap
    {
        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA { public int Size; public IntPtr Hwnd; public uint Callback, Edge; public int L, T, R, B; public IntPtr Param; }
        [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);

        const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
        const int ABS_AUTOHIDE = 1, ABS_ALWAYSONTOP = 2;

        public static bool Active;            // панель задач сейчас спрятана доком
        static int before = -1;               // её состояние до нас
        static DateTime peekUntil = DateTime.MinValue;
        public static readonly uint TaskbarCreated = RegisterWindowMessage("TaskbarCreated");   // Проводник перезапустился

        static string Marker(string dir) { return dir == null ? null : Path.Combine(dir, "taskbar.txt"); }

        static IntPtr Bar() { return FindWindow("Shell_TrayWnd", null); }

        static void ShowAll(bool show)
        {
            IntPtr main = Bar();
            if (main != IntPtr.Zero && IsWindowVisible(main) != show) ShowWindow(main, show ? 5 : 0);   // SW_SHOW / SW_HIDE
            // Панели задач на других мониторах
            IntPtr other = IntPtr.Zero;
            while ((other = FindWindowEx(IntPtr.Zero, other, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
                if (IsWindowVisible(other) != show) ShowWindow(other, show ? 5 : 0);
        }

        static int GetState()
        {
            var d = new APPBARDATA { Size = Marshal.SizeOf(typeof(APPBARDATA)), Hwnd = Bar() };
            return SHAppBarMessage(ABM_GETSTATE, ref d).ToInt32();
        }

        static void SetState(int state)
        {
            var d = new APPBARDATA { Size = Marshal.SizeOf(typeof(APPBARDATA)), Hwnd = Bar(), Param = new IntPtr(state) };
            SHAppBarMessage(ABM_SETSTATE, ref d);
        }

        // Прошлый запуск дока не вернул панель задач (упал, его закрыли силой): что было до него
        static int Recorded(string dir)
        {
            try
            {
                string f = Marker(dir);
                int v;
                if (f != null && File.Exists(f) && int.TryParse(File.ReadAllText(f).Trim(), out v)) return v;
            }
            catch { }
            return -1;
        }

        // Спрятать панель задач Windows. Её прежнее состояние записывается до того, как что-то поменяется.
        public static void Take(string dir)
        {
            if (Bar() == IntPtr.Zero) return;   // Проводника нет — прятать нечего
            if (!Active)
            {
                int old = Recorded(dir);
                before = old >= 0 ? old : GetState();
                try { string f = Marker(dir); if (f != null) { Directory.CreateDirectory(dir); File.WriteAllText(f, before.ToString()); } } catch { }
                Active = true;
            }
            if ((GetState() & ABS_AUTOHIDE) == 0) SetState(ABS_AUTOHIDE | (before & ABS_ALWAYSONTOP));   // окна получают весь экран
            ShowAll(false);
        }

        // Вернуть панель задач такой, какой она была
        public static void Give(string dir)
        {
            int old = Active ? before : Recorded(dir);
            Active = false;
            if (old >= 0) SetState(old);
            ShowAll(true);
            try { string f = Marker(dir); if (f != null && File.Exists(f)) File.Delete(f); } catch { }
        }

        // Остался след от прошлого запуска, а замена выключена — возвращаем
        public static void Recover(string dir)
        {
            if (!Active && Recorded(dir) >= 0) Give(dir);
        }

        // Ненадолго показать панель задач — например, ради значков в трее. Спрячется сама, когда курсор уйдёт с неё.
        public static void Peek()
        {
            if (!Active) return;
            peekUntil = DateTime.UtcNow.AddSeconds(6);
            ShowAll(true);
        }

        // Курсор над панелью задач (пока она показана ради трея — не прячем)
        static bool CursorOnTaskbar()
        {
            RECT r; POINT p;
            IntPtr main = Bar();
            return main != IntPtr.Zero && IsWindowVisible(main) && GetWindowRect(main, out r) && GetCursorPos(out p) && p.X >= r.L && p.X < r.R && p.Y >= r.T - 8 && p.Y < r.B;
        }

        // ───────── Компактная панель задач Windows 11 ─────────
        // Тонкая полоса с часами и треем, которая разворачивается, когда нужна. В Windows она задумана для планшетов,
        // включается значением TabletPostureTaskbar в реестре пользователя и применяется после перезапуска Проводника.
        const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";

        public static int Compact
        {
            get
            {
                try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(ExplorerKey)) { object v = k != null ? k.GetValue("TabletPostureTaskbar") : null; return v is int ? (int)v : 0; } }
                catch { return 0; }
            }
        }

        static void WriteCompact(int v)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(ExplorerKey)) k.SetValue("TabletPostureTaskbar", v, Microsoft.Win32.RegistryValueKind.DWord);
        }

        static string CompactMarker(string dir) { return dir == null ? null : Path.Combine(dir, "compact.txt"); }

        // Включить компактную панель. Что было до нас, записывается один раз — чтобы потом вернуть именно это.
        // true — значение изменилось и Проводник нужно перезапустить.
        public static bool CompactOn(string dir)
        {
            int now = Compact;
            try
            {
                string f = CompactMarker(dir);
                if (f != null && !File.Exists(f)) { Directory.CreateDirectory(dir); File.WriteAllText(f, now.ToString()); }
            }
            catch { }
            if (now == 1) return false;
            WriteCompact(1);
            return true;
        }

        // Вернуть панель задач такой, какой она была до того, как док включил компактную. true — значение изменилось.
        public static bool CompactBack(string dir)
        {
            string f = CompactMarker(dir);
            if (f == null || !File.Exists(f)) return false;
            int was;
            try { if (!int.TryParse(File.ReadAllText(f).Trim(), out was)) was = 0; File.Delete(f); } catch { return false; }
            if (Compact == was) return false;
            WriteCompact(was);
            return true;
        }

        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        // Перезапуск Проводника — бережно: как «Выход из Проводника» в меню панели задач (Ctrl+Shift+правая кнопка),
        // без принудительного завершения. Работает в фоне. Не закрылся за 10 секунд — не трогаем, false.
        public static void RestartExplorer(Action<bool> done)
        {
            new Thread(delegate()
            {
                bool ok = false;
                try
                {
                    IntPtr bar = Bar();
                    uint pid = 0;
                    if (bar != IntPtr.Zero) GetWindowThreadProcessId(bar, out pid);
                    if (pid != 0)
                    {
                        var p = System.Diagnostics.Process.GetProcessById((int)pid);
                        PostMessage(bar, 0x5B4, IntPtr.Zero, IntPtr.Zero);   // WM_USER + 436: Проводник закрывается сам
                        ok = p.WaitForExit(10000);
                    }
                    if (ok && Bar() == IntPtr.Zero) System.Diagnostics.Process.Start(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
                }
                catch { ok = false; }
                if (done != null) done(ok);
            }) { IsBackground = true, Name = "dock explorer" }.Start();
        }

        public static bool Peeking { get { return DateTime.UtcNow < peekUntil; } }

        // Раз в секунду: Проводник любит показывать панель задач сам (после меню «Пуск», смены разрешения, перезапуска)
        public static void Keep(string dir)
        {
            if (!Active) return;
            if (Peeking) { if (CursorOnTaskbar()) peekUntil = DateTime.UtcNow.AddSeconds(2); return; }
            Take(dir);
        }
    }

    // Кнопка Windows открывает меню приложений дока вместо «Пуска». Сочетания с ней (Win+E, Win+D, Win+L…) работают как
    // обычно: перехватывается только одиночное нажатие. Перехватчик живёт в своём потоке и делает самое малое.
    static class WinKey
    {
        delegate IntPtr Proc(int code, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)]
        struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr W, L; public uint Time; public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public IntPtr Extra; }
        [StructLayout(LayoutKind.Explicit, Size = 40)]   // как в Windows для 64 бит: поле типа и самый большой вариант (мышь)
        struct INPUT { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public KEYBDINPUT Key; }

        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, Proc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

        const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_QUIT = 0x12, WM_APP = 0x8000;
        const int LWin = 0x5B, RWin = 0x5C, Mask = 0xE8;   // 0xE8 — клавиши с таким кодом не существует
        static readonly IntPtr Ours = new IntPtr(0x444F434B);   // пометка «это нажатие послали мы сами»

        static readonly object gate = new object();
        static readonly Proc proc = Callback;
        static Thread thread;
        static uint threadId;
        static volatile IntPtr handle;
        static Dispatcher ui;
        static Action pressed;
        static bool winDown, other;

        public static bool Installed { get { return handle != IntPtr.Zero; } }

        public static void Sync(bool wanted, Dispatcher dispatcher, Action onPress)
        {
            ui = dispatcher; pressed = onPress;
            if (wanted) Start(); else Stop();
        }

        static void Start()
        {
            lock (gate)
            {
                if (thread != null) return;
                try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(typeof(WinKey).GetMethod("Callback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).MethodHandle); } catch { }
                var ready = new ManualResetEvent(false);
                thread = new Thread(delegate()
                {
                    threadId = GetCurrentThreadId();
                    handle = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
                    ready.Set();
                    if (handle == IntPtr.Zero) return;
                    MSG msg;
                    while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        if (msg.Message != WM_APP) continue;
                        // Свежий перехватчик раньше, чем снят старый: Windows молча снимает медленные перехватчики
                        IntPtr fresh = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
                        if (fresh == IntPtr.Zero) continue;
                        IntPtr old = handle; handle = fresh; UnhookWindowsHookEx(old);
                    }
                    UnhookWindowsHookEx(handle);
                    handle = IntPtr.Zero;
                }) { IsBackground = true, Name = "dock winkey", Priority = ThreadPriority.Highest };
                thread.Start();
                ready.WaitOne(2000);
            }
        }

        public static void Refresh()
        {
            lock (gate) if (thread != null && handle != IntPtr.Zero && !winDown) PostThreadMessage(threadId, WM_APP, IntPtr.Zero, IntPtr.Zero);
        }

        public static void Stop()
        {
            lock (gate)
            {
                if (thread == null) return;
                if (handle != IntPtr.Zero) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                thread.Join(1000);
                thread = null;
                winDown = false;
            }
        }

        static INPUT Key(int vk, bool up)
        {
            var i = new INPUT { Type = 1 };
            i.Key.Vk = (ushort)vk; i.Key.Extra = Ours; i.Key.Flags = up ? 2u : 0u;
            if (vk == LWin || vk == RWin) i.Key.Flags |= 1;   // KEYEVENTF_EXTENDEDKEY
            return i;
        }

        static IntPtr Callback(int code, IntPtr w, IntPtr l)
        {
            try
            {
                if (code == 0 && Marshal.ReadIntPtr(l, 16) != Ours)
                {
                    int vk = Marshal.ReadInt32(l, 0) & 0xFF, m = w.ToInt32();
                    bool down = m == WM_KEYDOWN || m == WM_SYSKEYDOWN;
                    bool win = vk == LWin || vk == RWin;
                    if (win && down) { if (!winDown) { winDown = true; other = false; } }
                    else if (down) { if (winDown) other = true; }
                    else if (win && winDown)
                    {
                        winDown = false;
                        if (!other)
                        {
                            // Одиночное нажатие: перед отпусканием — несуществующая клавиша, тогда Windows считает,
                            // что кнопку нажимали «с чем-то», и «Пуск» не открывает. Само отпускание посылаем от себя.
                            SendInput(3, new INPUT[] { Key(Mask, false), Key(Mask, true), Key(vk, true) }, Marshal.SizeOf(typeof(INPUT)));
                            if (ui != null && pressed != null) ui.BeginInvoke(DispatcherPriority.Send, pressed);
                            return new IntPtr(1);
                        }
                    }
                }
            }
            catch { }
            return CallNextHookEx(IntPtr.Zero, code, w, l);
        }
    }
}
