// dock.txt. Packaged.cs — приложения из Магазина и прочие «упакованные» (Параметры, Калькулятор, Терминал, Фотографии).
// У них нет ярлыка на файл: Windows знает их по идентификатору (AppUserModelID) и держит в папке shell:AppsFolder.
// Здесь — список этой папки, значки, запуск и «чьё это окно». Только чтение; список и значки собираются
// в фоновом потоке (AppCatalog), сюда из окна приходят лишь готовые ответы и одиночные значки.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DevTxt.DockApp
{
    static class Packaged
    {
        public const string Folder = Logic.AppsFolder;

        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int W, H; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP
        {
            public int Type, Width, Height, WidthBytes;
            public short Planes, BitsPixel;
            public IntPtr Bits;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct PROPERTYKEY { public Guid Id; public uint Pid; }
        [StructLayout(LayoutKind.Explicit, Size = 24)]
        struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort Type;
            [FieldOffset(8)] public IntPtr Pointer;
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr bitmap);
        }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out uint count);
            [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
            [PreserveSig] int Commit();
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHCreateItemFromParsingName(string path, IntPtr context, ref Guid iid, out IShellItemImageFactory item);
        [DllImport("shell32.dll")]
        static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, out IPropertyStore store);
        [DllImport("ole32.dll")]
        static extern int PropVariantClear(ref PROPVARIANT value);
        [DllImport("gdi32.dll")]
        static extern int GetObject(IntPtr handle, int size, out BITMAP bitmap);
        [DllImport("gdi32.dll")]
        static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr handle);
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("kernel32.dll")]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder id);

        const int IconOnly = 0x4;   // SIIGBF_ICONONLY: значок, а не картинка содержимого
        static readonly object gate = new object();
        static readonly Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, string> names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Всё, что лежит в папке приложений Windows: название → идентификатор. Здесь и обычные программы (их идентификатор —
        // путь или случайный набор), и упакованные; кого оставить, решает AppRules.IsPackaged. Вызывать из фонового потока STA.
        public static List<KeyValuePair<string, string>> List()
        {
            var list = new List<KeyValuePair<string, string>>();
            object shell = null, folder = null, items = null;
            try
            {
                Type t = Type.GetTypeFromProgID("Shell.Application");
                if (t == null) return list;
                shell = Activator.CreateInstance(t);
                folder = t.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, new object[] { "shell:AppsFolder" });
                if (folder == null) return list;
                items = folder.GetType().InvokeMember("Items", BindingFlags.InvokeMethod, null, folder, null);
                if (items == null) return list;
                int count = Convert.ToInt32(items.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, items, null));
                for (int i = 0; i < count; i++)
                {
                    object item = null;
                    try
                    {
                        item = items.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, items, new object[] { i });
                        if (item == null) continue;
                        string name = item.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, item, null) as string;
                        string id = item.GetType().InvokeMember("Path", BindingFlags.GetProperty, null, item, null) as string;
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id)) list.Add(new KeyValuePair<string, string>(name, id));
                    }
                    catch { }
                    finally { Free(item); }
                }
            }
            catch { }
            finally { Free(items); Free(folder); Free(shell); }
            return list;
        }

        static void Free(object com)
        {
            try { if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com); } catch { }
        }

        // Список собран: запоминаем названия — по ним подписываются значки открытых приложений из Магазина
        public static void Remember(List<KeyValuePair<string, string>> apps)
        {
            var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in apps) next[a.Value] = a.Key;
            lock (gate) names = next;
        }

        // Название приложения по идентификатору; null — список ещё не собран или такого в нём нет
        public static string Name(string app)
        {
            if (string.IsNullOrEmpty(app)) return null;
            string name;
            lock (gate) return names.TryGetValue(app, out name) ? name : null;
        }

        // Значок для дока, крупный. Достаётся один раз и запоминается.
        public static ImageSource Icon(string app)
        {
            if (string.IsNullOrEmpty(app)) return null;
            ImageSource img;
            lock (gate) if (icons.TryGetValue(app, out img)) return img;
            img = Extract(app, 128);
            lock (gate) icons[app] = img;
            return img;
        }

        // Значок нужного размера, ничего не запоминает. Картинка заморожена: её можно отдать другому потоку.
        // null — Windows значка не дала: на его месте будет плитка с буквой.
        public static ImageSource Extract(string app, int px)
        {
            if (string.IsNullOrEmpty(app)) return null;
            IShellItemImageFactory factory = null;
            IntPtr bitmap = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                if (SHCreateItemFromParsingName(Folder + app, IntPtr.Zero, ref iid, out factory) != 0 || factory == null) return null;
                if (factory.GetImage(new SIZE { W = px, H = px }, IconOnly, out bitmap) != 0 || bitmap == IntPtr.Zero) return null;
                return FromBitmap(bitmap);
            }
            catch { return null; }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                Free(factory);
            }
        }

        // Картинка Windows → картинка WPF, с прозрачностью. Лишние прозрачные поля обрезаются: у значков из Магазина
        // они бывают в треть картинки, и рядом с обычными значками такие выглядели бы мелкими.
        static ImageSource FromBitmap(IntPtr bitmap)
        {
            BITMAP bm;
            if (GetObject(bitmap, Marshal.SizeOf(typeof(BITMAP)), out bm) == 0 || bm.Width <= 0 || bm.Height <= 0) return null;
            int w = bm.Width, h = Math.Abs(bm.Height);
            var info = new BITMAPINFOHEADER { Size = Marshal.SizeOf(typeof(BITMAPINFOHEADER)), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            var bits = new byte[w * h * 4];
            IntPtr dc = GetDC(IntPtr.Zero);
            try { if (GetDIBits(dc, bitmap, 0, (uint)h, bits, ref info, 0) == 0) return null; }
            finally { ReleaseDC(IntPtr.Zero, dc); }

            int left, top, right, bottom;
            bool alpha = Bounds(bits, w, h, out left, out top, out right, out bottom);
            BitmapSource src = BitmapSource.Create(w, h, 96, 96, alpha ? PixelFormats.Pbgra32 : PixelFormats.Bgr32, null, bits, w * 4);
            if (alpha)
            {
                int[] crop = AppRules.Trim(w, h, left, top, right, bottom);
                if (crop[2] < w || crop[3] < h) src = new CroppedBitmap(src, new Int32Rect(crop[0], crop[1], crop[2], crop[3]));
            }
            src.Freeze();
            return src;
        }

        // Где на картинке что-то нарисовано. false — прозрачности в картинке нет вовсе (старый значок): она вся непрозрачная.
        static bool Bounds(byte[] bits, int w, int h, out int left, out int top, out int right, out int bottom)
        {
            left = w; top = h; right = -1; bottom = -1;
            bool any = false;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte a = bits[(y * w + x) * 4 + 3];
                    if (a != 0) any = true;
                    if (a < 16) continue;
                    if (x < left) left = x;
                    if (x > right) right = x;
                    if (y < top) top = y;
                    if (y > bottom) bottom = y;
                }
            if (!any || right < left) { left = 0; top = 0; right = w - 1; bottom = h - 1; }
            return any;
        }

        // Идентификатор приложения, которому принадлежит процесс; null — программа обычная, не упакованная
        public static string OfProcess(uint pid)
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
                if (h == IntPtr.Zero) return null;
                uint size = 256;
                var sb = new StringBuilder((int)size);
                if (GetApplicationUserModelId(h, ref size, sb) != 0) return null;
                string id = sb.ToString();
                return id.Length == 0 ? null : id;
            }
            catch { return null; }
            finally { if (h != IntPtr.Zero) CloseHandle(h); }
        }

        // Идентификатор, который окно само сообщило Windows. Нужен для свёрнутого приложения из Магазина:
        // его окно держит общая программа-рамка, а самого приложения внутри в этот момент нет.
        public static string OfWindow(IntPtr hwnd)
        {
            IPropertyStore store = null;
            try
            {
                Guid iid = typeof(IPropertyStore).GUID;
                if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return null;
                var key = new PROPERTYKEY { Id = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Pid = 5 };   // System.AppUserModel.ID
                PROPVARIANT value;
                if (store.GetValue(ref key, out value) != 0) return null;
                try { return value.Type == 31 && value.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(value.Pointer) : null; }   // 31 — строка
                finally { PropVariantClear(ref value); }
            }
            catch { return null; }
            finally { Free(store); }
        }

        // Запускает приложение так же, как «Пуск»: Проводник открывает его по идентификатору
        public static void Open(string app)
        {
            using (Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Folder + app + "\"") { UseShellExecute = true })) { }
        }
    }
}
