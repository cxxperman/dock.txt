// dock.txt. Model.cs — всё, что не трогает экран и Windows: закреплённые приложения и настройки (Store),
// сборка значков дока из закреплённого и открытых окон (Logic), расчёт увеличения под курсором (Magnify).
// Этот файл целиком проверяется ключом --selftest.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DevTxt.DockApp
{
    // Окно, которое Windows показала бы на панели задач
    class WinInfo
    {
        public IntPtr Hwnd;
        public string Title, Path;
        public string Key;     // не null — окно нельзя узнать по файлу программы (приложение из Магазина): у него свой ключ
        public string Label;   // не null — готовое название вместо описания из файла
    }

    // Закреплённое приложение
    class Pin
    {
        public string Name, Path;
        public string Link;   // ярлык, из которого взято: запускаем через него, чтобы не потерять его параметры
        public string Args;   // параметры ярлыка — по ним узнаём программы, которые запускаются через Update.exe
        public string Icon;   // свой значок ярлыка: "файл" или "файл,номер"
    }

    // Один значок в доке
    class DockItem
    {
        public string Key, Name, Path, Link, Icon;
        public bool Pinned, CanPin = true;
        public Pin Pin;
        public readonly List<IntPtr> Windows = new List<IntPtr>();   // сверху вниз: первое — то, с которым работали последним
        public bool Running { get { return Windows.Count > 0; } }
    }

    enum Click { Launch, Activate, Minimize }

    static class Logic
    {
        static string Lower(string s) { return (s ?? "").ToLowerInvariant(); }

        // Программа, которую на самом деле запускает ярлык вида «Update.exe --processStart Discord.exe»; null — ярлык обычный
        public static string StartedExe(Pin pin)
        {
            const string start = "--processStart";
            if (pin == null || pin.Args == null || string.IsNullOrEmpty(pin.Path) || !pin.Path.EndsWith("\\Update.exe", StringComparison.OrdinalIgnoreCase)) return null;
            int at = pin.Args.IndexOf(start, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return null;
            string exe = pin.Args.Substring(at + start.Length).Trim();
            if (exe.StartsWith("\"")) { int q = exe.IndexOf('"', 1); exe = q > 0 ? exe.Substring(1, q - 1) : exe.Trim('"'); }
            else { int sp = exe.IndexOf(' '); if (sp > 0) exe = exe.Substring(0, sp); }
            return exe.Length == 0 ? null : exe;
        }

        static string FileOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOfAny(new char[] { '\\', '/' });
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        static string Bare(string file)
        {
            file = FileOf(file);
            return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file.Substring(0, file.Length - 4) : file;
        }

        // Окно принадлежит закреплённому приложению? Обычно это тот же файл. Но Discord и ему подобные
        // закрепляются ярлыком на «Update.exe --processStart Discord.exe», а работают из соседней папки app-1.2.3.
        // А окно Steam держит помощник steamwebhelper.exe из подпапки рядом со steam.exe.
        public static bool PinMatches(Pin pin, string path)
        {
            if (pin == null || string.IsNullOrEmpty(pin.Path) || string.IsNullOrEmpty(path)) return false;
            if (string.Equals(pin.Path, path, StringComparison.OrdinalIgnoreCase)) return true;
            string dir = pin.Path.Substring(0, pin.Path.Length - FileOf(pin.Path).Length);
            if (dir.Length == 0 || !path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return false;
            string exe = StartedExe(pin);
            if (exe != null) return path.EndsWith("\\" + exe, StringComparison.OrdinalIgnoreCase);
            // Помощник: лежит в папке программы, называется её именем и кончается на «helper»
            string own = Bare(pin.Path), other = Bare(path);
            return own.Length > 0 && other.Length > own.Length && other.StartsWith(own, StringComparison.OrdinalIgnoreCase) && other.EndsWith("helper", StringComparison.OrdinalIgnoreCase);
        }

        // Имя файла программы, по которому её узнают счётчики непрочитанного: «discord.exe», «steam.exe»
        public static string ExeOf(DockItem item)
        {
            if (item == null) return "";
            return Lower(StartedExe(item.Pin) ?? FileOf(item.Path));
        }

        // ───────── Названия ─────────
        static readonly string[] helperWords = { "helper", "host", "launcher" };

        static bool HasHelperWord(string s)
        {
            foreach (string w in helperWords) if (s.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Описание файла похоже на вспомогательный процесс («Steam Client WebHelper») или его нет вовсе
        public static bool LooksLikeHelper(string description)
        {
            return description == null || description.Trim().Length == 0 || HasHelperWord(description);
        }

        static bool IsSystemName(string product)
        {
            return product.IndexOf("operating system", StringComparison.OrdinalIgnoreCase) >= 0 || product.IndexOf("операционная система", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Как назвать запущенную программу: описание её файла; если оно похоже на помощника или пустое — название продукта.
        // Продукт тоже назван по помощнику — убираем слова помощника с конца («Steam Client WebHelper» → «Steam»).
        // Продукт — сама Windows («Application Frame Host») — остаётся описание.
        public static string DisplayName(string description, string product, string file)
        {
            description = (description ?? "").Trim(); product = (product ?? "").Trim();
            if (!LooksLikeHelper(description)) return description;
            if (product.Length > 0 && !IsSystemName(product))
            {
                if (!HasHelperWord(product)) return product;
                var words = new List<string>(product.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
                while (words.Count > 0 && (HasHelperWord(words[words.Count - 1]) || string.Equals(words[words.Count - 1], "client", StringComparison.OrdinalIgnoreCase))) words.RemoveAt(words.Count - 1);
                if (words.Count > 0) return string.Join(" ", words.ToArray());
            }
            return description.Length > 0 ? description : Bare(file);
        }

        // Как назвать закреплённое: сохранённое название, а если оно похоже на помощника — имя ярлыка, из которого закрепили
        public static string PinName(Pin pin, Func<string, string> nameOf)
        {
            if (!LooksLikeHelper(pin.Name)) return pin.Name;
            if (!string.IsNullOrEmpty(pin.Link))
            {
                string link = FileOf(pin.Link);
                if (link.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) link = link.Substring(0, link.Length - 4);
                if (link.Length > 0) return link;
            }
            string name = nameOf != null ? nameOf(pin.Path) : null;
            if (!string.IsNullOrEmpty(name)) return name;
            return string.IsNullOrEmpty(pin.Name) ? Bare(pin.Path) : pin.Name;
        }

        // Значки дока: сначала закреплённые в своём порядке, за ними — запущенные, которых среди закреплённых нет.
        // Окна собираются по файлу программы. nameOf — как назвать программу по её файлу.
        public static List<DockItem> Group(List<Pin> pins, List<WinInfo> wins, bool showRunning, Func<string, string> nameOf)
        {
            var items = new List<DockItem>();
            var seen = new Dictionary<string, bool>();
            foreach (var p in pins)
            {
                string key = Lower(p.Path);
                if (key.Length == 0 || seen.ContainsKey(key)) continue;
                seen[key] = true;
                items.Add(new DockItem { Key = key, Name = PinName(p, nameOf), Path = p.Path, Link = p.Link, Icon = p.Icon, Pinned = true, Pin = p });
            }
            var running = new Dictionary<string, DockItem>();
            foreach (var w in wins)
            {
                if (string.IsNullOrEmpty(w.Path)) continue;
                DockItem found = null;
                if (w.Key == null) found = items.Find(delegate(DockItem i) { return i.Pinned && PinMatches(i.Pin, w.Path); });
                if (found == null)
                {
                    string key = w.Key ?? Lower(w.Path);
                    if (!running.TryGetValue(key, out found))
                    {
                        if (!showRunning) continue;
                        string name = w.Label ?? (nameOf != null ? nameOf(w.Path) : null);
                        if (string.IsNullOrEmpty(name)) name = System.IO.Path.GetFileNameWithoutExtension(w.Path);
                        found = new DockItem { Key = key, Name = name, Path = w.Path, CanPin = w.Key == null };
                        running[key] = found;
                        items.Add(found);
                    }
                }
                found.Windows.Add(w.Hwnd);
            }
            return items;
        }

        // Отпечаток того, что нарисовано: пока он не изменился, док не перерисовывается
        public static string Signature(List<DockItem> items)
        {
            var sb = new StringBuilder();
            foreach (var i in items) sb.Append(i.Key).Append('|').Append(i.Name).Append('|').Append(i.Pinned ? 'p' : '-').Append(i.Running ? 'r' : '-').Append('\n');
            return sb.ToString();
        }

        // Что делает щелчок: не запущено — запустить; окно уже впереди — свернуть; иначе — вывести вперёд
        public static Click Decide(DockItem item, IntPtr foreground, out IntPtr target)
        {
            target = IntPtr.Zero;
            if (!item.Running) return Click.Launch;
            if (foreground != IntPtr.Zero && item.Windows.Contains(foreground)) { target = foreground; return Click.Minimize; }
            target = item.Windows[0];
            return Click.Activate;
        }

        // Окно на весь экран (игра, видео): закрывает его целиком и не имеет заголовка.
        // Обычное развёрнутое окно с заголовком полноэкранным не считается.
        public static bool IsFullscreen(int left, int top, int right, int bottom, int screenW, int screenH, bool caption)
        {
            return !caption && left <= 0 && top <= 0 && right >= screenW && bottom >= screenH;
        }
    }

    // Увеличение под курсором. Все координаты — в «покое»: 0 — левый край первого значка, когда ничего не увеличено.
    static class Magnify
    {
        // Во сколько раз вырастает значок, центр которого в d от курсора: max под курсором, плавно до 1 на расстоянии range
        public static double Scale(double d, double range, double max)
        {
            d = Math.Abs(d);
            if (d >= range || max <= 1 || range <= 0) return 1;
            return 1 + (max - 1) * 0.5 * (1 + Math.Cos(Math.PI * d / range));
        }

        // Раскладывает значки шириной rest[i] * scale[i] с промежутком gap. Точка под курсором (u) остаётся под курсором:
        // значки раздвигаются от неё в обе стороны, а не уезжают из-под руки. from..to — края всего ряда.
        public static double[] Layout(double[] rest, double[] scale, double gap, double u, out double from, out double to)
        {
            int n = rest.Length;
            var left = new double[n];
            double total = 0;
            for (int i = 0; i < n; i++) total += rest[i] + gap;
            u = Math.Max(0, Math.Min(total, u));
            double a = 0, m = 0, anchor = u;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                double rw = rest[i] + gap, mw = rest[i] * scale[i] + gap;
                left[i] = m + gap / 2;
                if (!found && (u <= a + rw || i == n - 1)) { anchor = m + (u - a) / rw * mw; found = true; }
                a += rw; m += mw;
            }
            double shift = u - anchor;
            for (int i = 0; i < n; i++) left[i] += shift;
            from = shift; to = shift + m;
            return left;
        }

        // Пружина: шаг вперёд на dt секунд. Её можно перебить в любой момент — скорость сохраняется.
        public static void Spring(ref double x, ref double v, double target, double stiffness, double damping, double dt)
        {
            const double step = 1.0 / 240;
            while (dt > 1e-9)
            {
                double h = Math.Min(step, dt);
                v += (-stiffness * (x - target) - damping * v) * h;
                x += v * h;
                dt -= h;
            }
        }
    }

    // Закреплённые приложения и настройки: %APPDATA%\dev.txt\dock\state.json
    class Store
    {
        public const int MinSize = 36, MaxSize = 72;
        public static string DefaultDir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dev.txt", "dock"); } }

        public readonly string Dir;   // null — ничего не читаем и не пишем (--demo)
        public List<Pin> Pins = new List<Pin>();
        public bool AutoHide = false, Magnify = true, ShowRunning = true;
        // Что внизу экрана: «both» — dock.txt над панелью задач Windows (как было всегда), «dock» — только dock.txt,
        // панель Windows спрятана; «modern» — только компактная панель задач Windows, дока не видно.
        public string Bar = "both";
        public static readonly string[] Bars = { "dock", "modern", "both" };
        public bool ReplaceTaskbar { get { return Bar == "dock"; } }
        public bool WinKey = true;   // кнопка Windows открывает меню дока (только когда dock.txt вместо панели задач)
        public int Size = 48;
        // Виджеты и счётчики: ключ настройки → включено. Порядок — тот, в котором они идут на странице настроек.
        public static readonly string[] WidgetKeys = { "widgetMusic", "widgetCalendar", "widgetWeather", "widgetVolume", "widgetBattery", "widgetClock", "badges" };
        public readonly Dictionary<string, bool> Flags = new Dictionary<string, bool>();

        public Store(string dir)
        {
            Dir = dir;
            foreach (string k in WidgetKeys) Flags[k] = true;
        }

        public bool On(string key) { bool v; return Flags.TryGetValue(key, out v) && v; }

        string FilePath { get { return System.IO.Path.Combine(Dir, "state.json"); } }

        static string Str(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) ? v as string : null; }
        static bool Flag(Dictionary<string, object> d, string key, bool fallback) { object v; return d != null && d.TryGetValue(key, out v) && v is bool ? (bool)v : fallback; }

        // false — файла ещё нет (первый запуск) или он не читается
        public bool Load()
        {
            if (Dir == null) return false;
            try
            {
                if (!File.Exists(FilePath)) return false;
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath, Encoding.UTF8)) as Dictionary<string, object>;
                if (d == null) return false;
                object list;
                Pins = new List<Pin>();
                if (d.TryGetValue("pins", out list) && list is object[])
                    foreach (object o in (object[])list)
                    {
                        var p = o as Dictionary<string, object>;
                        string path = Str(p, "path");
                        if (string.IsNullOrEmpty(path)) continue;
                        Pins.Add(new Pin { Name = Str(p, "name") ?? System.IO.Path.GetFileNameWithoutExtension(path), Path = path, Link = Str(p, "link"), Args = Str(p, "args"), Icon = Str(p, "icon") });
                    }
                object set;
                d.TryGetValue("settings", out set);
                var s = set as Dictionary<string, object>;
                AutoHide = Flag(s, "autoHide", AutoHide);
                Magnify = Flag(s, "magnify", Magnify);
                ShowRunning = Flag(s, "showRunning", ShowRunning);
                string bar = Str(s, "bar");
                Bar = Array.IndexOf(Bars, bar) >= 0 ? bar : Flag(s, "replaceTaskbar", false) ? "dock" : "both";   // прежняя настройка — переключатель
                WinKey = Flag(s, "winKey", WinKey);
                foreach (string k in WidgetKeys) Flags[k] = Flag(s, k, true);
                object size;
                if (s != null && s.TryGetValue("size", out size) && size != null) try { Size = Clamp(Convert.ToDouble(size, System.Globalization.CultureInfo.InvariantCulture)); } catch { }
                return true;
            }
            catch { return false; }
        }

        public void Save()
        {
            if (Dir == null) return;
            try
            {
                var pins = new List<object>();
                foreach (var p in Pins)
                {
                    var one = HubLink.Msg("name", p.Name, "path", p.Path);
                    if (p.Link != null) one["link"] = p.Link;
                    if (p.Args != null) one["args"] = p.Args;
                    if (p.Icon != null) one["icon"] = p.Icon;
                    pins.Add(one);
                }
                var set = HubLink.Msg("autoHide", AutoHide, "magnify", Magnify, "showRunning", ShowRunning, "size", Size, "bar", Bar, "winKey", WinKey);
                foreach (string k in WidgetKeys) set[k] = Flags[k];
                var d = HubLink.Msg("pins", pins.ToArray(), "settings", set);
                Directory.CreateDirectory(Dir);
                // Сначала рядом, потом на место: оборванная запись не оставит пустой файл
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(d), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch { }
        }

        public void PinItem(DockItem item)
        {
            if (!item.CanPin || Pins.Exists(delegate(Pin p) { return string.Equals(p.Path, item.Path, StringComparison.OrdinalIgnoreCase); })) return;
            Pins.Add(new Pin { Name = item.Name, Path = item.Path });
            Save();
        }

        public void UnpinItem(DockItem item)
        {
            string path = item.Pin != null ? item.Pin.Path : item.Path;
            Pins.RemoveAll(delegate(Pin p) { return string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase); });
            Save();
        }

        static int Clamp(double v) { return (int)Math.Max(MinSize, Math.Min(MaxSize, Math.Round(v))); }

        // Описание настроек — в том виде, в каком его ждёт core.txt (kit.txt, hub/PROTOCOL.md, «Настройки приложения»)
        public Dictionary<string, object> Describe()
        {
            var taskbar = new object[]
            {
                HubLink.Msg("key", "bar", "type", "choice", "label", "Внизу экрана",
                    "hint", "dock.txt — панель задач Windows прячется, значки трея — в меню дока. Компактная панель Windows — тонкая полоса с часами, разворачивается, когда нужна; дока не видно. При переключении на неё и обратно Проводник перезапускается: открытые папки закроются",
                    "value", Bar, "options", new object[] { HubLink.Msg("value", "dock", "label", "dock.txt"), HubLink.Msg("value", "modern", "label", "Компактная панель Windows"), HubLink.Msg("value", "both", "label", "dock.txt и панель Windows") }),
                HubLink.Msg("key", "winKey", "type", "switch", "label", "Кнопка Windows открывает меню дока", "hint", "Вместо «Пуска» — меню приложений dock.txt. Сочетания вроде Win+E и Win+D работают как обычно. Действует вместе с заменой панели задач", "value", WinKey)
            };
            var items = new object[]
            {
                HubLink.Msg("key", "autoHide", "type", "switch", "label", "Автоскрытие", "hint", "Док уезжает за нижний край экрана и возвращается, когда курсор касается этого края", "value", AutoHide),
                HubLink.Msg("key", "magnify", "type", "switch", "label", "Увеличение под курсором", "hint", "Значки рядом с курсором плавно растут, как в macOS", "value", Magnify),
                HubLink.Msg("key", "showRunning", "type", "switch", "label", "Показывать запущенные", "hint", "После закреплённых приложений идут те, у которых сейчас открыто окно", "value", ShowRunning),
                HubLink.Msg("key", "size", "type", "slider", "label", "Размер значков", "value", Size, "min", MinSize, "max", MaxSize, "step", 2, "unit", "px")
            };
            string[] labels = { "Музыка", "Календарь", "Погода", "Громкость", "Батарея", "Часы", "Счётчики непрочитанного" };
            string[] hints =
            {
                "Что играет, по данным kapsula.txt; под курсором — кнопки «назад», «пауза», «вперёд»",
                "Ближайшее событие на сегодня, по данным kapsula.txt",
                "Температура и погода, по данным kapsula.txt",
                "Колесо мыши меняет громкость, щелчок выключает звук",
                "Заряд — только если у компьютера есть батарея",
                "Время; под курсором — дата",
                "Красный счётчик на значках Discord и Steam, по данным kapsula.txt"
            };
            var widgets = new object[WidgetKeys.Length];
            for (int i = 0; i < WidgetKeys.Length; i++) widgets[i] = HubLink.Msg("key", WidgetKeys[i], "type", "switch", "label", labels[i], "hint", hints[i], "value", Flags[WidgetKeys[i]]);
            return HubLink.Msg("groups", new object[] { HubLink.Msg("title", "", "items", items), HubLink.Msg("title", "Панель задач", "items", taskbar), HubLink.Msg("title", "Виджеты", "items", widgets) });
        }

        // Меняет настройку по ключу. Незнакомый ключ или не то значение — исключение: его текст уйдёт ответом в core.txt.
        public void Apply(string key, object value)
        {
            if (key == "size")
            {
                double v;
                try { v = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); }
                catch { throw new Exception("нужно число"); }
                if (value == null || value is bool || value is string) throw new Exception("нужно число");
                Size = Clamp(v);
            }
            else if (key == "bar")
            {
                string v = value as string;
                if (Array.IndexOf(Bars, v) < 0) throw new Exception("нужно одно из: dock, modern, both");
                Bar = v;
            }
            else if (key == "autoHide" || key == "magnify" || key == "showRunning" || key == "winKey" || (key != null && Flags.ContainsKey(key)))
            {
                if (!(value is bool)) throw new Exception("нужно «да» или «нет»");
                bool on = (bool)value;
                if (key == "autoHide") AutoHide = on; else if (key == "magnify") Magnify = on; else if (key == "showRunning") ShowRunning = on;
                else if (key == "winKey") WinKey = on; else Flags[key] = on;
            }
            else throw new Exception("такой настройки нет");
            Save();
        }
    }
}
