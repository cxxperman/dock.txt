// dock.txt. Glass.cs — размытие за полосой дока. Окно дока прозрачное (layered) и во всю ширину экрана, а системные
// подложки Windows (Mica, Acrylic через DWMWA_SYSTEMBACKDROP_TYPE) на таком окне не работают. Поэтому под полосой стоит
// отдельное окно-подложка ровно по ней: без рамки, почти без содержимого, не берёт фокус, не видно в Alt+Tab и на панели
// задач, щелчки пропускает. За ним Windows сама размывает то, что позади (акцент ACRYLICBLURBEHIND); углы скругляет
// сама Windows 11, как у своих окон. Подложка — владелец окна дока: Windows всегда держит док прямо над ней.
// Двигает её DockWindow (SyncGlass) — в тех же кадрах, что и саму полосу.
using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace DevTxt.DockApp
{
    class GlassBackdrop : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int State, Flags; public uint Gradient; public int Animation; }
        [StructLayout(LayoutKind.Sequential)] struct CompositionData { public int Attribute; public IntPtr Data; public int Size; }

        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionData data);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);

        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200;
        const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, WM_NCHITTEST = 0x84, HTTRANSPARENT = -1, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
        const int WCA_ACCENT_POLICY = 19, ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

        readonly HwndSource source;
        int x = int.MinValue, y, w, h;
        bool shown;

        public IntPtr Handle { get { return source.Handle; } }

        public GlassBackdrop()
        {
            var p = new HwndSourceParameters("dock.txt glass")
            {
                WindowStyle = WS_POPUP,
                ExtendedWindowStyle = WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
                PositionX = -32000, PositionY = -32000, Width = 1, Height = 1,
                // Прозрачное (layered) окно: без своего содержимого Windows закрашивает подложку чёрным, а сквозь
                // почти прозрачное содержимое видно размытое. Заодно оно по-настоящему пропускает щелчки.
                UsesPerPixelOpacity = true
            };
            source = new HwndSource(p);
            source.RootVisual = new System.Windows.Controls.Border { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
            // Щелчки сквозь подложку — тому, что под ней (над ней и так всегда полоса дока)
            source.AddHook(delegate(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
            {
                if (msg == WM_NCHITTEST) { handled = true; return new IntPtr(HTTRANSPARENT); }
                if (msg == WM_MOUSEACTIVATE) { handled = true; return new IntPtr(MA_NOACTIVATE); }
                return IntPtr.Zero;
            });
            // Акрил без флага своей тонировки: с ним (флаг 2) Windows 11 рисует подложку почти непрозрачной, и полоса
            // снова глухая. Слабый тёмный цвет всё же нужен — с нулевым акрил вырождается в ровную серую заливку.
            // Темноту для читаемости даёт полупрозрачный фон самой полосы.
            Color tint = Ui.Alpha(Tokens.Dark.Bg, 0.2);
            var accent = new AccentPolicy { State = ACCENT_ENABLE_ACRYLICBLURBEHIND, Flags = 0, Gradient = (uint)tint.A << 24 | (uint)tint.B << 16 | (uint)tint.G << 8 | tint.R };
            int size = Marshal.SizeOf(accent);
            IntPtr data = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, data, false);
                var cd = new CompositionData { Attribute = WCA_ACCENT_POLICY, Data = data, Size = size };
                SetWindowCompositionAttribute(Handle, ref cd);
            }
            finally { Marshal.FreeHGlobal(data); }
            // Форма: скругление окон Windows 11 — те же 8 точек, что у полосы (Tokens.Radius.Block). Область окна
            // (SetWindowRgn) прозрачное окно с акрилом не слушает — проверено: углы торчали из-под полосы.
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        }

        // Ставит подложку на экран, в точках экрана: видимая часть полосы (её низ может уйти за край окна дока).
        // То же место второй раз Windows не передаётся.
        public void Place(int nx, int ny, int nw, int nh)
        {
            if (nw <= 2 || nh <= 0) { Hide(); return; }
            if (nx != x || ny != y || nw != w || nh != h)
            {
                x = nx; y = ny; w = nw; h = nh;
                SetWindowPos(Handle, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
            if (!shown) { shown = true; ShowWindow(Handle, SW_SHOWNOACTIVATE); }
        }

        public void Hide()
        {
            if (!shown) return;
            shown = false;
            ShowWindow(Handle, SW_HIDE);
        }

        public void Dispose() { try { source.Dispose(); } catch { } }

        // ───────── Когда размытие нужно ─────────
        static int build = -1;

        // Windows 11: на Windows 10 акрил этого вида тормозит при движении окна — там остаётся ровный тёмный фон
        public static bool Supported
        {
            get
            {
                if (build < 0)
                {
                    try { using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) build = int.Parse((string)k.GetValue("CurrentBuildNumber")); }
                    catch { build = 0; }
                }
                return build >= 22000;
            }
        }

        // «Эффекты прозрачности» в параметрах Windows (Персонализация → Цвета). Нет значения — включены.
        public static bool TransparencyOn()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("EnableTransparency") : null;
                    return !(v is int) || (int)v != 0;
                }
            }
            catch { return true; }
        }
    }
}
