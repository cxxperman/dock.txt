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
        public string Key;     // не null — окно нельзя узнать ни по файлу программы, ни по идентификатору: у него свой ключ
        public string Label;   // не null — готовое название вместо описания из файла
        public string App;     // идентификатор приложения из Магазина (AppUserModelID); null — программа обычная
        public bool Loose;     // узнано не до конца (приложение из Магазина свёрнуто) — в следующий раз смотрим заново
    }

    // Закреплённое приложение
    class Pin
    {
        public string Name, Path;
        public string Link;   // ярлык, из которого взято: запускаем через него, чтобы не потерять его параметры
        public string Args;   // параметры ярлыка — по ним узнаём программы, которые запускаются через Update.exe
        public string Icon;   // свой значок ярлыка: "файл" или "файл,номер"
        public string App;    // приложение из Магазина: его идентификатор (AppUserModelID). Path тогда — «shell:AppsFolder\идентификатор»
    }

    // Один значок в доке
    class DockItem
    {
        public string Key, Name, Path, Link, Icon;
        public string App;    // приложение из Магазина: запускается и получает значок по идентификатору, а не по файлу
        public string Exe;    // файл, из которого оно сейчас работает (по нему находят счётчики непрочитанного); null — не знаем
        public bool Pinned, CanPin = true;
        public Pin Pin;
        public readonly List<IntPtr> Windows = new List<IntPtr>();   // сверху вниз: первое — то, с которым работали последним
        public bool Running { get { return Windows.Count > 0; } }
    }

    enum Click { Launch, Activate, Minimize }

    static class Logic
    {
        public const string AppsFolder = "shell:AppsFolder\\";   // папка приложений Windows: в ней приложения из Магазина
        public const double DragSlop = 6, DragOutHeights = 1.5;

        static string Lower(string s) { return (s ?? "").ToLowerInvariant(); }
        static bool Same(string a, string b) { return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        // По чему значок узнаётся между перерисовками: приложение из Магазина — по идентификатору, обычное — по файлу
        static string KeyOf(string app, string path) { return string.IsNullOrEmpty(app) ? Lower(path) : "app|" + Lower(app); }

        // Этот значок — то же самое приложение, что и закреплённое?
        public static bool SamePin(Pin pin, DockItem item)
        {
            if (pin == null || item == null) return false;
            if (!string.IsNullOrEmpty(item.App) || !string.IsNullOrEmpty(pin.App)) return Same(pin.App, item.App);
            return Same(pin.Path, item.Path);
        }

        // ───────── Перетаскивание значков ─────────
        // Переставляет элемент списка с места from на место to. false — переставлять нечего.
        public static bool Move<T>(List<T> list, int from, int to)
        {
            if (list == null || from < 0 || from >= list.Count) return false;
            to = Math.Max(0, Math.Min(list.Count - 1, to));
            if (to == from) return false;
            T item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
            return true;
        }

        // Кнопку нажали и повели: это уже перетаскивание, а не щелчок?
        public static bool DragStarts(double dx, double dy) { return dx * dx + dy * dy > DragSlop * DragSlop; }

        // Значок унесли высоко над доком (above — на сколько курсор выше полосы): отпустить здесь — открепить
        public static bool DragOut(double above, double size) { return above > DragOutHeights * size; }

        // На какое место встанет значок, если отпустить его в точке x. left[i]..right[i] — места закреплённых значков по порядку,
        // current — где он сейчас. У границы мест значок не мечется: чтобы сменить место, её надо перейти на slack.
        public static int DropIndex(double[] left, double[] right, double x, int current, double slack)
        {
            int n = left == null || right == null ? 0 : Math.Min(left.Length, right.Length);
            if (n == 0) return -1;
            if (current >= 0 && current < n && x >= left[current] - slack && x < right[current] + slack) return current;
            for (int i = 0; i < n; i++) if (x < right[i]) return i;
            return n - 1;
        }

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
            return Lower(StartedExe(item.Pin) ?? FileOf(item.Exe ?? item.Path));
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
            if (pin.App != null && !string.IsNullOrEmpty(pin.Name)) return pin.Name;   // у приложения из Магазина файла с описанием нет
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
        // Окна собираются по файлу программы, а у приложений из Магазина — по идентификатору: файл у них меняется
        // с каждым обновлением. nameOf — как назвать программу по её файлу.
        public static List<DockItem> Group(List<Pin> pins, List<WinInfo> wins, bool showRunning, Func<string, string> nameOf)
        {
            var items = new List<DockItem>();
            var seen = new Dictionary<string, bool>();
            foreach (var p in pins)
            {
                string key = KeyOf(p.App, p.Path);
                if (key.Length == 0 || seen.ContainsKey(key)) continue;
                seen[key] = true;
                items.Add(new DockItem { Key = key, Name = PinName(p, nameOf), Path = p.Path, Link = p.Link, Icon = p.Icon, App = p.App, Pinned = true, Pin = p });
            }
            var running = new Dictionary<string, DockItem>();
            foreach (var w in wins)
            {
                if (string.IsNullOrEmpty(w.Path)) continue;
                DockItem found = null;
                if (w.App != null) found = items.Find(delegate(DockItem i) { return i.Pinned && Same(i.App, w.App); });
                if (found == null && w.Key == null) found = items.Find(delegate(DockItem i) { return i.Pinned && PinMatches(i.Pin, w.Path); });
                if (found == null)
                {
                    string key = w.Key ?? KeyOf(w.App, w.Path);
                    if (!running.TryGetValue(key, out found))
                    {
                        if (!showRunning) continue;
                        string name = w.Label ?? (nameOf != null ? nameOf(w.Path) : null);
                        if (string.IsNullOrEmpty(name)) name = System.IO.Path.GetFileNameWithoutExtension(w.Path);
                        found = new DockItem { Key = key, Name = name, Path = w.Path, App = w.Key == null ? w.App : null, CanPin = w.Key == null };
                        running[key] = found;
                        items.Add(found);
                    }
                }
                if (found.App != null && found.Exe == null && !w.Loose) found.Exe = w.Path;
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

        // Что делает щелчок: не запущено — запустить; впереди чужое окно — вывести вперёд то, с которым работали последним;
        // окно программы уже впереди: оно единственное — свернуть, окон несколько — перейти к следующему. «Следующее» —
        // то, с которым работали давнее всего: так щелчки обходят все окна по кругу, а не скачут между двумя верхними.
        public static Click Decide(DockItem item, IntPtr foreground, out IntPtr target)
        {
            target = IntPtr.Zero;
            if (!item.Running) return Click.Launch;
            if (foreground != IntPtr.Zero && item.Windows.Contains(foreground))
            {
                target = foreground;
                if (item.Windows.Count == 1) return Click.Minimize;
                for (int i = item.Windows.Count - 1; i >= 0; i--)
                    if (item.Windows[i] != foreground) { target = item.Windows[i]; break; }
                return Click.Activate;
            }
            target = item.Windows[0];
            return Click.Activate;
        }

        // Заголовок окна для строки меню: длинный обрезается с «…»
        public static string Shorten(string text, int max)
        {
            text = (text ?? "").Trim();
            if (max < 2 || text.Length <= max) return text;
            return text.Substring(0, max - 1).TrimEnd() + "…";
        }

        // Значок программы, которая играет музыку (Kapsula присылает её имя: «Spotify», «chrome.exe»); null — такой в доке нет
        public static DockItem Player(List<DockItem> items, string app)
        {
            string a = Bare(Lower(app).Trim());
            if (a.Length == 0 || items == null) return null;
            DockItem loose = null;
            foreach (DockItem i in items)
            {
                if (!i.Running) continue;
                string exe = Bare(ExeOf(i)), name = Lower(i.Name).Trim();
                if (exe == a || name == a) return i;
                if (loose == null && ((exe.Length >= 3 && a.Contains(exe)) || (name.Length >= 3 && a.Contains(name)) || (a.Length >= 3 && name.Contains(a)))) loose = i;
            }
            return loose;
        }
    }

    // Док в семье dev.txt: пункты для общего трея core.txt, горячие клавиши для его страницы «Клавиши», когда уходить
    // с экрана из-за игры и когда счётчику можно выскочить. Здесь только правила; в хаб их отдаёт DockHub.
    static class FamilyRules
    {
        public const string TrayAutoHide = "autoHide", TraySettings = "settings", KeyApps = "winKey";
        // Страница дока в core.txt: команда show через хаб и ключ --page, если core.txt приходится запускать
        public const string CorePage = "app:dock";
        // Столько миллисекунд после смены окна впереди док при core.txt уходит и по своей проверке: core.txt мог ещё не сказать
        public const int GameGraceMs = 3000;

        // Пункты дока в меню core.txt в трее. Своего окна у дока нет, поэтому «Открыть» нет; автозапуск, обновления,
        // «Не беспокоить» и выход — общие пункты core.txt. «Настройки» открывают страницу дока в core.txt.
        // onScreen — док стоит внизу экрана; при компактной панели Windows его не видно, и «Автоскрытие» недоступно.
        public static List<TrayItem> Tray(bool autoHide, bool onScreen, Action toggleAutoHide, Action settings)
        {
            return new List<TrayItem>
            {
                new TrayItem(TrayAutoHide, "Автоскрытие", autoHide, onScreen, toggleAutoHide),
                new TrayItem(TraySettings, "Настройки", null, settings)
            };
        }

        // Сочетания дока: одиночное нажатие кнопки Windows открывает меню приложений. Действует, только когда
        // dock.txt стоит вместо панели задач и в настройках это включено (так же решает WinKey.Sync).
        public static List<KeyInfo> Keys(bool replaceTaskbar, bool winKey)
        {
            return new List<KeyInfo> { new KeyInfo(KeyApps, "Меню приложений", KeyNames.Text(0, 0x5B), replaceTaskbar && winKey) };
        }

        // Уходить ли доку с экрана: впереди игра или видео на весь экран. coreOnline — core.txt на связи и прислал
        // core.family, coreGame — его ответ; local — своя проверка тем же правилом (FullScreen в Family.cs);
        // sinceSwitch — сколько миллисекунд назад впереди сменилось окно (−1 — не знаем); dockScreen — окно впереди
        // на главном мониторе, где стоит док. Игра на другом мониторе доку не мешает: он остаётся, как осталась бы там
        // панель задач Windows. core.family не говорит, на каком мониторе игра, — это док смотрит сам.
        // Без core.txt решает своя проверка. С ним — он, а своя помогает только в первые секунды после смены окна:
        // док стоит поверх игры уже сейчас, а core.txt мог ещё не успеть сказать. Вернуться док ждёт слова core.txt —
        // или окна впереди на другом мониторе.
        public static bool GameAway(bool coreOnline, bool coreGame, bool local, long sinceSwitch, bool dockScreen)
        {
            if (!dockScreen) return false;
            if (!coreOnline) return local;
            return coreGame || (local && sinceSwitch >= 0 && sinceSwitch < GameGraceMs);
        }

        // Счётчик непрочитанного появился: выскакивает, только пока не включено «Не беспокоить». Сам счётчик есть всегда.
        public static bool BadgePops(bool appeared, bool focus) { return appeared && !focus; }
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
            double[] rw = new double[n], mw = new double[n];
            for (int i = 0; i < n; i++) { rw[i] = rest[i] + gap; mw[i] = rest[i] * scale[i] + gap; }
            double[] left = Cells(rw, mw, u, out from, out to);
            for (int i = 0; i < n; i++) left[i] += gap / 2;
            return left;
        }

        // То же, но в ячейках: rw[i] — сколько места значок с промежутком занимает в покое, mw[i] — сколько сейчас.
        // Ячейка может быть и нулевой: значок только появляется или уже уходит. Возвращает левые края ячеек.
        public static double[] Cells(double[] rw, double[] mw, double u, out double from, out double to)
        {
            int n = rw.Length;
            var left = new double[n];
            double total = 0;
            for (int i = 0; i < n; i++) total += rw[i];
            u = Math.Max(0, Math.Min(total, u));
            double a = 0, m = 0, anchor = u;
            bool found = false;
            for (int i = 0; i < n; i++)
            {
                left[i] = m;
                if (!found && (u <= a + rw[i] || i == n - 1)) { anchor = m + (rw[i] > 1e-9 ? (u - a) / rw[i] * mw[i] : 0); found = true; }
                a += rw[i]; m += mw[i];
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

    // Плавный переход одного числа: от того, что есть сейчас, к новому за заданное время, с резким началом и мягким
    // торможением (та же кривая, что у Ui.EaseOut). Время приходит снаружи, в миллисекундах; переход можно перебить в любой момент.
    class Tween
    {
        double from, to;
        long start;
        int ms;
        bool running;

        public Tween(double value) { from = to = value; }

        public double Target { get { return to; } }
        public bool Running { get { return running; } }

        public void Set(double value) { from = to = value; running = false; }

        public void Go(double value, long now, int duration)
        {
            if (value == to) return;
            if (duration <= 0) { Set(value); return; }
            from = Value(now); to = value; start = now; ms = duration; running = true;
        }

        public double Value(long now)
        {
            if (!running) return to;
            double t = (now - start) / (double)ms;
            if (t >= 1) { running = false; return to; }
            return from + (to - from) * Ease(Math.Max(0, t));
        }

        // 0..1 → 0..1: показательное торможение, степень 7
        public static double Ease(double t)
        {
            if (t <= 0) return 0;
            if (t >= 1) return 1;
            return (1 - Math.Exp(-7 * t)) / (1 - Math.Exp(-7));
        }

        // Значок подпрыгивает, пока программа запускается: высота от 0 до 1 в момент t (мс), один прыжок длится cycle
        public static double Bounce(long t, int cycle)
        {
            if (t < 0 || cycle <= 0) return 0;
            return Math.Sin(Math.PI * (t % cycle) / (double)cycle);
        }
    }

    // Запуск вместе с Windows: значение «Dock» в HKCU\…\Run, как у kapsula.txt. Путь — в кавычках: по имени файла в нём
    // core.txt находит запись и переносит её, когда программа переезжает. Пока Live выключен (--demo, --selftest, --shot,
    // --headless), реестр не трогается совсем: переключатель живёт только в памяти.
    static class Autostart
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Dock";
        public static bool Live;
        static bool memory;

        public static string Command(string exe) { return "\"" + exe + "\""; }

        public static bool IsOn()
        {
            if (!Live) return memory;
            try { using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(Name) != null; }
            catch { return false; }
        }

        public static void Set(bool on)
        {
            if (!Live) { memory = on; return; }
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return;
                    if (on) k.SetValue(Name, Command(System.Reflection.Assembly.GetExecutingAssembly().Location));
                    else k.DeleteValue(Name, false);
                }
            }
            catch { }
        }
    }

    // Закреплённые приложения и настройки: %APPDATA%\dev.txt\dock\state.json
    class Store
    {
        public const int MinSize = 36, MaxSize = 72;
        public static string DefaultDir { get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dev.txt", "dock"); } }

        public readonly string Dir;   // null — ничего не читаем и не пишем (--demo)
        public List<Pin> Pins = new List<Pin>();
        public bool AutoHide = false, Magnify = true, ShowRunning = true, Bin = true;   // Bin — корзина metla.txt в правом конце дока
        // Что внизу экрана: «both» — dock.txt над панелью задач Windows (как было всегда), «dock» — только dock.txt,
        // панель Windows спрятана; «modern» — только компактная панель задач Windows, дока не видно.
        public string Bar = "both";
        public static readonly string[] Bars = { "dock", "modern", "both" };
        public bool ReplaceTaskbar { get { return Bar == "dock"; } }
        public bool WinKey = true;   // кнопка Windows открывает меню дока (только когда dock.txt вместо панели задач)
        public int Size = 48;
        // Виджеты и счётчики: ключ настройки → включено. Порядок — тот, в котором они идут на странице настроек.
        public static readonly string[] WidgetKeys = { "widgetMusic", "widgetCalendar", "widgetWeather", "widgetVolume", "widgetBattery", "widgetClock", "badges" };
        // Музыку, календарь и погоду уже показывает остров kapsula.txt наверху экрана: в доке они по умолчанию выключены
        public static readonly string[] IslandKeys = { "widgetMusic", "widgetCalendar", "widgetWeather" };
        // Отметка в state.json: у тех, кто пользовался доком раньше, эти три виджета уже один раз выключены
        public const string IslandMark = "islandWidgets";
        public readonly Dictionary<string, bool> Flags = new Dictionary<string, bool>();
        public bool IslandMigrated;   // этот Load выключил музыку, календарь и погоду (файл был от прежней версии)

        public Store(string dir)
        {
            Dir = dir;
            foreach (string k in WidgetKeys) Flags[k] = DefaultOn(k);
        }

        public static bool DefaultOn(string key) { return Array.IndexOf(IslandKeys, key) < 0; }

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
                        // Старые файлы знают только программы по файлу; у приложения из Магазина есть ещё «app» — его идентификатор
                        string path = Str(p, "path"), app = Str(p, "app");
                        if (string.IsNullOrEmpty(app)) app = null;
                        if (app == null && path != null && path.StartsWith(Logic.AppsFolder, StringComparison.OrdinalIgnoreCase) && path.Length > Logic.AppsFolder.Length) app = path.Substring(Logic.AppsFolder.Length);
                        if (string.IsNullOrEmpty(path) && app != null) path = Logic.AppsFolder + app;
                        if (string.IsNullOrEmpty(path)) continue;
                        Pins.Add(new Pin { Name = Str(p, "name") ?? (app != null ? app : System.IO.Path.GetFileNameWithoutExtension(path)), Path = path, Link = Str(p, "link"), Args = Str(p, "args"), Icon = Str(p, "icon"), App = app });
                    }
                object set;
                d.TryGetValue("settings", out set);
                var s = set as Dictionary<string, object>;
                AutoHide = Flag(s, "autoHide", AutoHide);
                Magnify = Flag(s, "magnify", Magnify);
                ShowRunning = Flag(s, "showRunning", ShowRunning);
                Bin = Flag(s, "bin", Bin);
                string bar = Str(s, "bar");
                Bar = Array.IndexOf(Bars, bar) >= 0 ? bar : Flag(s, "replaceTaskbar", false) ? "dock" : "both";   // прежняя настройка — переключатель
                WinKey = Flag(s, "winKey", WinKey);
                foreach (string k in WidgetKeys) Flags[k] = Flag(s, k, DefaultOn(k));
                if (!Flag(s, IslandMark, false))
                {
                    // Файл прежней версии: там эти виджеты были включены сами собой. Выключаем один раз — то же
                    // показывает остров kapsula.txt. Кто включит их снова, у того они так и останутся.
                    foreach (string k in IslandKeys) Flags[k] = false;
                    IslandMigrated = true;
                }
                object size;
                if (s != null && s.TryGetValue("size", out size) && size != null) try { Size = Clamp(Convert.ToDouble(size, System.Globalization.CultureInfo.InvariantCulture)); } catch { }
                if (IslandMigrated) Save();   // отметка ложится в файл сразу: второй раз не выключаем
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
                    if (p.App != null) one["app"] = p.App;
                    pins.Add(one);
                }
                var set = HubLink.Msg("autoHide", AutoHide, "magnify", Magnify, "showRunning", ShowRunning, "bin", Bin, "size", Size, "bar", Bar, "winKey", WinKey);
                foreach (string k in WidgetKeys) set[k] = Flags[k];
                set[IslandMark] = true;
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
            if (!item.CanPin || Pins.Exists(delegate(Pin p) { return Logic.SamePin(p, item); })) return;
            // Приложение из Магазина закрепляется по идентификатору: по нему оно запустится и получит значок и после обновления
            if (item.App != null) Pins.Add(new Pin { Name = item.Name, Path = Logic.AppsFolder + item.App, App = item.App });
            else Pins.Add(new Pin { Name = item.Name, Path = item.Path });
            Save();
        }

        public void UnpinItem(DockItem item)
        {
            var what = item.Pin != null ? new DockItem { Path = item.Pin.Path, App = item.Pin.App } : item;
            Pins.RemoveAll(delegate(Pin p) { return Logic.SamePin(p, what); });
            Save();
        }

        // Закреплённое переставили: с места from на место to (номера в Pins). false — порядок не изменился.
        public bool Move(int from, int to)
        {
            if (!Logic.Move(Pins, from, to)) return false;
            Save();
            return true;
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
                // «дока» — чтобы не спутать с автоскрытием острова kapsula.txt; в меню дока и в трее под его именем — просто «Автоскрытие»
                HubLink.Msg("key", "autoHide", "type", "switch", "label", "Автоскрытие дока", "hint", "Док уезжает за нижний край экрана и возвращается, когда курсор касается этого края", "value", AutoHide),
                HubLink.Msg("key", "magnify", "type", "switch", "label", "Увеличение под курсором", "hint", "Значки рядом с курсором плавно растут, как в macOS", "value", Magnify),
                HubLink.Msg("key", "showRunning", "type", "switch", "label", "Показывать запущенные", "hint", "После закреплённых приложений идут те, у которых сейчас открыто окно", "value", ShowRunning),
                HubLink.Msg("key", "bin", "type", "switch", "label", "Корзина в доке", "hint", "Бак в правом конце дока, как на Mac: сколько лежит в корзине Windows и её меню; брошенные на бак файлы уходят в корзину. Нужна metla.txt", "value", Bin),
                HubLink.Msg("key", "size", "type", "slider", "label", "Размер значков", "value", Size, "min", MinSize, "max", MaxSize, "step", 2, "unit", "px"),
                // section — core.txt показывает пункт на общей странице «Общие» → «Запуск и обновления», рядом с остальными приложениями
                HubLink.Msg("key", "autostart", "type", "switch", "label", "Запускать вместе с Windows", "hint", "Док появляется сам, как только вы вошли в Windows", "value", Autostart.IsOn(), "section", "startup")
            };
            string[] labels = { "Музыка", "Календарь", "Погода", "Громкость", "Батарея", "Часы", "Счётчики непрочитанного" };
            // Музыка, календарь и погода — то же, что на острове kapsula.txt: в доке они нужны, только если остров спрятан
            string[] hints =
            {
                "Что играет, по данным kapsula.txt; под курсором — кнопки «назад», «пауза», «вперёд». То же показывает остров kapsula.txt — включите, если остров спрятан",
                "Ближайшее событие на сегодня, по данным kapsula.txt. То же показывает остров kapsula.txt — включите, если остров спрятан",
                "Температура и погода, по данным kapsula.txt. То же показывает остров kapsula.txt — включите, если остров спрятан",
                "Колесо мыши меняет громкость, щелчок открывает быстрые настройки Windows, средняя кнопка выключает звук",
                "Заряд — только если у компьютера есть батарея",
                "Время; под курсором — дата, щелчок открывает уведомления и календарь Windows",
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
            else if (key == "autostart")
            {
                // Хранит сама Windows (раздел Run), а не state.json
                if (!(value is bool)) throw new Exception("нужно «да» или «нет»");
                Autostart.Set((bool)value);
                return;
            }
            else if (key == "autoHide" || key == "magnify" || key == "showRunning" || key == "bin" || key == "winKey" || (key != null && Flags.ContainsKey(key)))
            {
                if (!(value is bool)) throw new Exception("нужно «да» или «нет»");
                bool on = (bool)value;
                if (key == "autoHide") AutoHide = on; else if (key == "magnify") Magnify = on; else if (key == "showRunning") ShowRunning = on; else if (key == "bin") Bin = on;
                else if (key == "winKey") WinKey = on; else Flags[key] = on;
            }
            else throw new Exception("такой настройки нет");
            Save();
        }
    }
}
