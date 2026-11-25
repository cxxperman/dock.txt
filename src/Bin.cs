// dock.txt. Bin.cs — корзина в правом конце дока, как на Mac. Считает корзину Windows metla.txt и присылает через хаб
// состояние metla.bin = { count, bytes }; док рисует бак, показывает меню и кладёт в корзину то, что на него бросили.
// Очистку подтверждают в самой metla.txt: пункт «Очистить корзину…» просит её открыть свой лист (show, page: bin).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace DevTxt.DockApp
{
    // Что лежит в корзине Windows — по слову metla.txt
    class BinInfo
    {
        public long Count, Bytes;

        // metla.bin; null или не тот вид — состояния нет (metla.txt не на связи)
        public static BinInfo From(object data)
        {
            var d = data as Dictionary<string, object>;
            if (d == null) return null;
            long count, bytes;
            if (!Number(d, "count", out count) || !Number(d, "bytes", out bytes)) return null;
            return new BinInfo { Count = Math.Max(0, count), Bytes = Math.Max(0, bytes) };
        }

        static bool Number(Dictionary<string, object> d, string key, out long value)
        {
            value = 0;
            object v;
            if (!d.TryGetValue(key, out v) || v == null || v is bool || v is string) return false;
            try { value = (long)Math.Round(Convert.ToDouble(v, CultureInfo.InvariantCulture)); return true; }
            catch { return false; }
        }
    }

    // Строка меню бака: что написано, доступна ли и что делает (open, clean, scan, startup; null — сведение)
    class BinRow
    {
        public string Label, Act;
        public bool Enabled, Line;
    }

    static class BinRules
    {
        // Спрайты 12×12 — из kit.txt, icons/sprites.json, группа glyphs: «bin» (пустая, крышка закрыта)
        // и «bin-full» (полная: крышка приподнята, торчит бумага). Цвета — общие: «#» оранжевый, «E» глаз.
        public static readonly string[] Empty =
        {
            "....####....", ".##########.", "############", "............", ".##########.", ".##E####E##.",
            ".##E####E##.", ".##########.", ".#.#.##.#.#.", ".#.#.##.#.#.", ".##########.", "..########.."
        };
        public static readonly string[] Full =
        {
            "...##.......", ".......####.", "..###.######", "############", "..#.##.##.#.", ".##########.",
            ".##E####E##.", ".##E####E##.", ".##########.", ".#.#.##.#.#.", ".##########.", "..########.."
        };

        public const string Title = "Корзина", Offline = "metla.txt не запущена";
        public const string OpenLabel = "Открыть корзину", CleanLabel = "Очистить корзину…", ScanLabel = "Найти, что занимает место…", StartupLabel = "Автозапуск Windows…";

        // Полный бак — когда в корзине что-то есть; бросают файл — крышка тоже приподнимается
        public static string[] Sprite(long count, bool dropHot) { return count > 0 || dropHot ? Full : Empty; }

        // Бак в доке: включён в настройках и metla.txt есть — установлена или на связи
        public static bool Visible(bool setting, bool installed, bool online) { return setting && (installed || online); }

        // Куда core.txt ставит metla.txt: общий дом «Программы\dev.txt», папка приложения (как Core.exe в CoreApp.cs)
        public static string MetlaPath
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "dev.txt", "Metla", "Metla.exe"); }
        }

        public static bool MetlaInstalled
        {
            get { try { return File.Exists(MetlaPath); } catch { return false; } }
        }

        // Размер и склонения — как в metla.txt (Fmt): «512 Б», «12 КБ», «3,4 МБ», «1,25 ГБ», «12,4 ГБ»
        static readonly CultureInfo Ru = MakeRu();
        static CultureInfo MakeRu()
        {
            var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            c.NumberFormat.NumberDecimalSeparator = ",";
            c.NumberFormat.NumberGroupSeparator = " ";
            return c;
        }

        public const long KB = 1024, MB = KB * 1024, GB = MB * 1024;

        public static string Size(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes < KB) return bytes + " Б";
            if (bytes < MB) return Math.Round(bytes / (double)KB).ToString("0", Ru) + " КБ";
            if (bytes < GB)
            {
                double mb = bytes / (double)MB;
                return (mb < 100 ? mb.ToString("0.#", Ru) : Math.Round(mb).ToString("0", Ru)) + " МБ";
            }
            double gb = bytes / (double)GB;
            return (gb < 10 ? gb.ToString("0.##", Ru) : gb < 100 ? gb.ToString("0.#", Ru) : Math.Round(gb).ToString("0", Ru)) + " ГБ";
        }

        // 1 объект, 2 объекта, 5 объектов
        public static string Objects(long n)
        {
            long a = Math.Abs(n) % 100, b = a % 10;
            string word = a >= 11 && a <= 14 ? "объектов" : b == 1 ? "объект" : b >= 2 && b <= 4 ? "объекта" : "объектов";
            return n.ToString("#,0", Ru) + " " + word;
        }

        // Подпись над баком: «Корзина» и под ней сколько лежит
        public static string Tip(BinInfo info)
        {
            if (info == null) return Title + "\n" + Offline;
            if (info.Count <= 0) return Title + "\n" + "пусто";
            return Title + "\n" + Objects(info.Count) + " · " + Size(info.Bytes);
        }

        // Первая строка меню; null — сказать нечего (metla.txt не на связи)
        public static string Summary(BinInfo info)
        {
            if (info == null) return null;
            if (info.Count <= 0) return "Корзина пуста";
            return "В корзине " + Objects(info.Count) + ", " + Size(info.Bytes);
        }

        // Меню бака. canOpen — metla.txt понимает bin.open (иначе корзину открывает сам проводник), canShow — show.
        // «Очистить корзину…» только открывает лист подтверждения в metla.txt; пустая корзина или metla.txt не на связи —
        // пункт неактивен. «Найти, что занимает место…» и «Автозапуск Windows…» — только пока metla.txt на связи.
        public static List<BinRow> Menu(BinInfo info, bool canShow)
        {
            var rows = new List<BinRow>();
            string summary = Summary(info);
            if (summary != null) rows.Add(new BinRow { Label = summary });
            rows.Add(new BinRow { Label = OpenLabel, Act = "open", Enabled = true });
            rows.Add(new BinRow { Label = CleanLabel, Act = "clean", Enabled = canShow && info != null && info.Count > 0 });
            if (canShow)
            {
                rows.Add(new BinRow { Line = true });
                rows.Add(new BinRow { Label = ScanLabel, Act = "scan", Enabled = true });
                rows.Add(new BinRow { Label = StartupLabel, Act = "startup", Enabled = true });
            }
            return rows;
        }

        // Страница metla.txt для команды show
        public static string Page(string act) { return act == "clean" ? "bin" : act == "scan" ? "scan" : act == "startup" ? "startup" : null; }

        // Что бросили на бак: только полные пути к тому, что есть на диске, каждый один раз
        public static List<string> DropPaths(string[] dropped, Func<string, bool> exists)
        {
            var list = new List<string>();
            if (dropped == null) return list;
            foreach (string p in dropped)
            {
                if (string.IsNullOrEmpty(p) || p.IndexOf('\0') >= 0) continue;
                string path = p.Trim();
                bool rooted;
                try { rooted = System.IO.Path.IsPathRooted(path) && System.IO.Path.GetFullPath(path).Length > 3; } catch { rooted = false; }   // корень диска в корзину не кладём
                if (!rooted || !exists(path)) continue;
                if (list.Exists(delegate(string q) { return string.Equals(q, path, StringComparison.OrdinalIgnoreCase); })) continue;
                list.Add(path);
            }
            return list;
        }

        // Список для SHFileOperation: пути через \0; второй \0 на конце допишет маршалинг строки
        public static string FromList(List<string> paths) { return string.Join("\0", paths.ToArray()) + "\0"; }

        public const uint FO_DELETE = 3;
        public const ushort FOF_ALLOWUNDO = 0x40, FOF_WANTNUKEWARNING = 0x4000;
        // В корзину с возможностью восстановить. Без FOF_NOCONFIRMATION: если Windows спрашивает, она спросит; а если
        // что-то в корзину не влезет и удалится насовсем — предупредит (FOF_WANTNUKEWARNING).
        public const ushort RecycleFlags = FOF_ALLOWUNDO | FOF_WANTNUKEWARNING;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom, pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

        // Кладёт файлы и папки в корзину Windows. true — Windows сделала это (или человек сам отказался в её вопросе)
        public static bool Recycle(IntPtr owner, List<string> paths)
        {
            if (paths == null || paths.Count == 0) return false;
            var op = new SHFILEOPSTRUCT { hwnd = owner, wFunc = FO_DELETE, pFrom = FromList(paths), fFlags = RecycleFlags };
            return SHFileOperation(ref op) == 0;
        }
    }
}
