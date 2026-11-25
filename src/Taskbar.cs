// dock.txt. Taskbar.cs — док вместо панели задач Windows: своё место внизу экрана (AppBar), меню приложений по кнопке
// Windows и Win+1…9.
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

    // Своё место дока на экране: полоса внизу, которую Windows не отдаёт развёрнутым окнам (AppBar, как у панели задач).
    // Регистрируется, только пока видимый док стоит вместо панели задач (правила — AppBarRules в Habits.cs). Где она
    // сейчас — записано в файл рядом с настройками: если док упал так, что не успел её отдать, следующий запуск
    // отдаёт её за него. При выходе, при необработанной ошибке и при завершении сеанса она отдаётся сразу (Program.cs).
    static class AppBar
    {
        [StructLayout(LayoutKind.Sequential)]
        struct APPBARDATA { public int Size; public IntPtr Hwnd; public uint Callback, Edge; public int L, T, R, B; public IntPtr Param; }
        [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);

        const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_WINDOWPOSCHANGED = 9, ABE_BOTTOM = 3;
        public const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;

        // Этим сообщением Windows говорит доку, что полосы на краях экрана сдвинулись или что впереди окно на весь экран
        public static readonly uint Callback = RegisterWindowMessage("devtxt_dock_appbar");

        static readonly object gate = new object();
        static IntPtr owner;
        static bool registered, dirty = true;
        static PxRect band, monitorWas;
        static int heightWas;
        static string markerDir;

        public static bool Registered { get { return registered; } }
        public static PxRect Band { get { return band; } }

        static string Marker(string dir) { return dir == null ? null : Path.Combine(dir, "appbar.txt"); }

        static APPBARDATA Data(IntPtr hwnd)
        {
            return new APPBARDATA { Size = Marshal.SizeOf(typeof(APPBARDATA)), Hwnd = hwnd, Callback = Callback, Edge = ABE_BOTTOM };
        }

        // Полосы на краях сдвинулись (ABN_POSCHANGED), сменился экран: в следующий раз спросить Windows заново
        public static void Dirty() { dirty = true; }

        // Проводник перезапустился — он забыл все полосы: зарегистрироваться заново
        public static void Lost() { lock (gate) { registered = false; dirty = true; } }

        // Привести к нужному: want — занимать ли место, monitor — главный монитор, height — высота полосы (точки экрана).
        // true — полоса появилась, ушла или сдвинулась: док ставится заново. Одно и то же второй раз Windows не передаётся.
        public static bool Sync(IntPtr hwnd, string dir, bool want, PxRect monitor, int height)
        {
            lock (gate)
            {
                if (!want || hwnd == IntPtr.Zero || height <= 0) return RemoveLocked();
                if (registered && owner != hwnd) RemoveLocked();
                if (registered && !dirty && height == heightWas && monitor.Same(monitorWas)) return false;
                bool fresh = !registered;
                if (fresh)
                {
                    APPBARDATA n = Data(hwnd);
                    SHAppBarMessage(ABM_NEW, ref n);   // false — окно уже зарегистрировано: тоже годится
                    registered = true;
                    owner = hwnd;
                    markerDir = dir;
                    try { string f = Marker(dir); if (f != null) { Directory.CreateDirectory(dir); File.WriteAllText(f, hwnd.ToInt64().ToString()); } } catch { }
                }
                dirty = false;
                heightWas = height; monitorWas = monitor;
                PxRect ask = AppBarRules.Propose(monitor, height);
                APPBARDATA q = Data(hwnd);
                q.L = ask.L; q.T = ask.T; q.R = ask.R; q.B = ask.B;
                SHAppBarMessage(ABM_QUERYPOS, ref q);
                PxRect settled = AppBarRules.Settle(new PxRect(q.L, q.T, q.R, q.B), monitor, height);
                if (!fresh && settled.Same(band)) return false;
                APPBARDATA s = Data(hwnd);
                s.L = settled.L; s.T = settled.T; s.R = settled.R; s.B = settled.B;
                SHAppBarMessage(ABM_SETPOS, ref s);
                band = new PxRect(s.L, s.T, s.R, s.B);
                return true;
            }
        }

        // Окно дока сдвинулось — так просит Windows (для полос с автоскрытием это важно, нашей — просто вежливость)
        public static void Moved()
        {
            lock (gate)
            {
                if (!registered) return;
                APPBARDATA d = Data(owner);
                SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref d);
            }
        }

        // Отдать место. Зовётся и из чужого потока (необработанная ошибка), поэтому под замком.
        public static bool Remove() { lock (gate) return RemoveLocked(); }

        static bool RemoveLocked()
        {
            if (!registered) return false;
            APPBARDATA d = Data(owner);
            try { SHAppBarMessage(ABM_REMOVE, ref d); } catch { }
            registered = false;
            dirty = true;
            band = new PxRect();
            try { string f = Marker(markerDir); if (f != null && File.Exists(f)) File.Delete(f); } catch { }
            return true;
        }

        // Прошлый запуск не отдал место (его закрыли силой): отдаём за него. Окно с тем номером живо — это уже чужое
        // окно (номера окон Windows раздаёт заново), его полосу не трогаем.
        public static void Recover(string dir)
        {
            string f = Marker(dir);
            try
            {
                long v;
                if (f == null || !File.Exists(f)) return;
                if (long.TryParse(File.ReadAllText(f).Trim(), out v) && v != 0)
                {
                    IntPtr old = new IntPtr(v);
                    if (!IsWindow(old))
                    {
                        APPBARDATA d = Data(old);
                        SHAppBarMessage(ABM_REMOVE, ref d);
                    }
                }
                File.Delete(f);
            }
            catch { }
        }
    }

    // Кнопка Windows открывает меню приложений дока вместо «Пуска». Сочетания с ней (Win+E, Win+D, Win+L…) работают как
    // обычно: перехватывается только одиночное нажатие — и Win+1…9, которые ведут к закреплённому в доке приложению
    // (как у панели задач Windows). Перехватчик живёт в своём потоке и делает самое малое.
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

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);

        static readonly object gate = new object();
        static readonly Proc proc = Callback;
        static Thread thread;
        static uint threadId;
        static volatile IntPtr handle;
        static volatile bool enabled;   // false — перехватчик ещё стоит (его поток заканчивает работу), но уже ничего не делает
        static Dispatcher ui;
        static Action pressed;
        static Action<int> digitPressed;
        static bool winDown, other;
        static int digit;               // цифра, нажатие которой мы забрали у Windows (0 — никакой): её отпускание тоже наше
        static volatile bool menuOn, digitsOn;
        static readonly int[] mouseButtons = { 1, 2, 4, 5, 6 };   // левая, правая, средняя и две боковые кнопки мыши

        // Впереди игра или видео на весь экран (так решил док): Win+цифра остаётся игре и Windows, док её не забирает
        public static volatile bool Game;

        public static bool Installed { get { return enabled && handle != IntPtr.Zero; } }
        public static bool Digits { get { return Installed && digitsOn; } }

        // menu — одиночное нажатие открывает меню дока; digits — Win+1…9 ведут к закреплённым в доке.
        // Перехватчик стоит, пока нужно хоть одно из двух.
        public static void Sync(bool menu, bool digits, Dispatcher dispatcher, Action onPress, Action<int> onDigit)
        {
            ui = dispatcher; pressed = onPress; digitPressed = onDigit;
            menuOn = menu; digitsOn = digits;
            if (menu || digits) Start(); else Stop();
        }

        static void Start()
        {
            lock (gate)
            {
                if (thread != null) return;
                try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(typeof(WinKey).GetMethod("Callback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).MethodHandle); } catch { }
                var ready = new ManualResetEvent(false);
                uint id = 0;
                thread = new Thread(delegate()
                {
                    id = GetCurrentThreadId();
                    // Свой перехватчик поток помнит сам: пока он заканчивает работу, уже может стоять перехватчик нового потока
                    IntPtr mine = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
                    handle = mine;
                    ready.Set();
                    if (mine == IntPtr.Zero) return;
                    MSG msg;
                    while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        if (msg.Message != WM_APP) continue;
                        // Свежий перехватчик раньше, чем снят старый: Windows молча снимает медленные перехватчики
                        IntPtr fresh = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
                        if (fresh == IntPtr.Zero) continue;
                        IntPtr old = mine;
                        bool current = handle == old;
                        mine = fresh;
                        if (current) handle = fresh;
                        UnhookWindowsHookEx(old);
                    }
                    UnhookWindowsHookEx(mine);
                    if (handle == mine) handle = IntPtr.Zero;
                }) { IsBackground = true, Name = "dock winkey", Priority = ThreadPriority.Highest };
                thread.Start();
                ready.WaitOne(2000);
                threadId = id;
                winDown = false;
                enabled = true;
            }
        }

        public static void Refresh()
        {
            lock (gate) if (thread != null && handle != IntPtr.Zero && !winDown) PostThreadMessage(threadId, WM_APP, IntPtr.Zero, IntPtr.Zero);
        }

        // Не ждёт: потоку перехватчика уходит просьба закончить, и он снимает перехватчик сам, в фоне.
        // Окно дока при этом не замирает (раньше здесь ждали до секунды). С этой же строки перехватчик ничего не делает.
        public static void Stop()
        {
            lock (gate)
            {
                enabled = false;
                if (thread == null) return;
                if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                thread = null;
                threadId = 0;
                winDown = false;
                digit = 0;
            }
        }

        static INPUT Key(int vk, bool up)
        {
            var i = new INPUT { Type = 1 };
            i.Key.Vk = (ushort)vk; i.Key.Extra = Ours; i.Key.Flags = up ? 2u : 0u;
            if (vk == LWin || vk == RWin) i.Key.Flags |= 1;   // KEYEVENTF_EXTENDEDKEY
            return i;
        }

        // Нажать сочетание с кнопкой Windows от имени человека: Win+A — быстрые настройки, Win+N — уведомления и календарь.
        // Нажатия помечены как свои: перехватчик выше их пропускает.
        public static void Chord(int vk)
        {
            try { SendInput(4, new INPUT[] { Key(LWin, false), Key(vk, false), Key(vk, true), Key(LWin, true) }, Marshal.SizeOf(typeof(INPUT))); } catch { }
        }

        // Кнопки мыши, пока держат Windows: перехватчика мыши у дока нет, поэтому спрашиваем у Windows,
        // нажимали ли их с прошлого вопроса. forget — только сбросить то, что накопилось раньше.
        static bool MouseUsed(bool forget)
        {
            bool used = false;
            foreach (int b in mouseButtons)
            {
                short s = GetAsyncKeyState(b);
                if (!forget && (s & 0x8001) != 0) used = true;
            }
            return used;
        }

        static bool Held(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        static IntPtr Callback(int code, IntPtr w, IntPtr l)
        {
            try
            {
                if (enabled && code == 0 && Marshal.ReadIntPtr(l, 16) != Ours)
                {
                    int vk = Marshal.ReadInt32(l, 0) & 0xFF, m = w.ToInt32();
                    bool down = m == WM_KEYDOWN || m == WM_SYSKEYDOWN;
                    bool win = vk == LWin || vk == RWin;
                    if (win && down) { if (!winDown) { winDown = true; other = false; MouseUsed(true); } }
                    else if (down)
                    {
                        if (winDown)
                        {
                            other = true;
                            // Win+цифра: до Windows цифра не доходит — двойного действия нет (её панель задач спрятана, но
                            // сочетание всё равно слушает). Чтобы отпускание кнопки Windows не открыло «Пуск», Windows видит
                            // вместо цифры несуществующую клавишу. Повтор клавиши, пока её держат, — не новое нажатие.
                            int index = digitsOn && !Game ? WinDigit.Index(vk, Held(0x10), Held(0x11), Held(0x12)) : -1;
                            if (index >= 0)
                            {
                                if (digit != vk)
                                {
                                    digit = vk;
                                    SendInput(2, new INPUT[] { Key(Mask, false), Key(Mask, true) }, Marshal.SizeOf(typeof(INPUT)));
                                    Action<int> act = digitPressed;
                                    if (ui != null && act != null) ui.BeginInvoke(DispatcherPriority.Send, act, index);
                                }
                                return new IntPtr(1);
                            }
                        }
                    }
                    else if (!win && digit != 0 && vk == digit) { digit = 0; return new IntPtr(1); }   // отпустили забранную цифру
                    else if (win && winDown)
                    {
                        winDown = false;
                        if (!other && menuOn)
                        {
                            // Одиночное нажатие: перед отпусканием — несуществующая клавиша, тогда Windows считает,
                            // что кнопку нажимали «с чем-то», и «Пуск» не открывает. Само отпускание посылаем от себя.
                            // Win + щелчок или перетаскивание мышью — тоже не одиночное: «Пуск» не открываем, но и своё меню тоже.
                            bool mouse = MouseUsed(false);
                            SendInput(3, new INPUT[] { Key(Mask, false), Key(Mask, true), Key(vk, true) }, Marshal.SizeOf(typeof(INPUT)));
                            if (!mouse && ui != null && pressed != null) ui.BeginInvoke(DispatcherPriority.Send, pressed);
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
