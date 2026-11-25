// dock.txt. Habits.cs — док по привычкам Windows, только правила (их целиком проверяет ключ --selftest): своё место
// внизу экрана (AppBar), появление от края при автоскрытии, Win+1…9, «просит внимания», пачки сообщений Windows
// об окнах и файлы, брошенные на значок программы. Разговор с самой Windows — в Taskbar.cs (AppBar, WinKey), Win.cs
// (сообщения об окнах) и DockWindow.cs.
using System;
using System.Collections.Generic;

namespace DevTxt.DockApp
{
    // Прямоугольник в точках экрана (настоящих пикселях), как RECT у Windows
    struct PxRect
    {
        public int L, T, R, B;
        public PxRect(int l, int t, int r, int b) { L = l; T = t; R = r; B = b; }
        public int Width { get { return R - L; } }
        public int Height { get { return B - T; } }
        public bool Same(PxRect o) { return L == o.L && T == o.T && R == o.R && B == o.B; }
        public override string ToString() { return L + "," + T + "," + R + "," + B; }
    }

    // ───────── Своё место на экране ─────────
    // Видимый док вместо панели задач занимает у Windows полосу внизу экрана — как панель задач: развёрнутые окна
    // и Snap кончаются над ним, а не уходят под него. Полоса — высотой с док в покое: увеличение значков под курсором
    // и подписи над ними места не занимают, они рисуются поверх окон, как и раньше.
    static class AppBarRules
    {
        // Занимать ли место. live — настоящий док на экране (не снимок, не --demo, не --headless).
        // С автоскрытием — нет: док уезжает за край и места не занимает. Регистрировать для этого «автоскрываемый» AppBar
        // (ABM_SETAUTOHIDEBAR) нельзя: такой на нижнем краю у Windows может быть только один, и это спрятанная доком
        // панель задач Windows (ей включено автоскрытие). Полоса нулевой высоты тоже ни к чему — её просто не регистрируем.
        // Рядом с панелью задач Windows («dock.txt и панель Windows») место не занимаем: там док стоит над панелью, как стоял.
        // taken — панель задач Windows уже спрятана доком (или её нет вовсе): раньше места не занимаем, иначе Windows
        // поставит полосу над местом, которое ещё держит за собой видимая панель, и док повиснет над пустой полосой.
        public static bool Wanted(bool live, bool replaceTaskbar, bool autoHide, bool taken) { return live && replaceTaskbar && !autoHide && taken; }

        // Повторная проверка автоскрытия спрятанной панели задач: Проводник включает его не сразу (а иногда не включает
        // вовсе) — смотрим ещё несколько раз по таймеру, не останавливая окно дока
        public const int RecheckMs = 300, RecheckTries = 8;

        // Сколько точек внизу монитора Windows всё ещё держит за спрятанной доком панелью задач. С автоскрытием — ничего
        // (такая панель места не держит). Без него — от её верха до низа монитора, если она стоит у нижнего края этого
        // монитора; у другого края или на другом мониторе — ничего.
        public static int Reserved(PxRect monitor, PxRect taskbar, bool autoHide)
        {
            if (autoHide || taskbar.Width <= 0 || taskbar.Height <= 0 || monitor.Height <= 0) return 0;
            if (taskbar.B < monitor.B || taskbar.T <= monitor.T + monitor.Height / 2) return 0;
            if (taskbar.R <= monitor.L || taskbar.L >= monitor.R) return 0;
            return Math.Max(0, Math.Min(monitor.B - taskbar.T, monitor.Height));
        }

        // То же, но не больше, чем на самом деле держат чужие полосы внизу (held, см. Held). Окно панели задач бывает
        // выше её места: компактная панель Windows 11 (TabletPostureTaskbar = 1) в свёрнутом виде держит 24 точки, а её
        // окно — все 72. Поверь доку окну — он встал бы над пустой полосой в 48 точек.
        public static int Reserved(PxRect monitor, PxRect taskbar, bool autoHide, int held)
        {
            return Math.Min(Reserved(monitor, taskbar, autoHide), Math.Max(0, held));
        }

        // Сколько внизу монитора держат чужие полосы — по рабочей области (её Windows считает сама). Пока своя полоса
        // занята и рабочая область кончается у её верха — то, что ниже своей полосы. Своя полоса, которая не дошла до
        // края рабочей области (встала над пустотой), Windows в расчёт не берёт — тогда снова по рабочей области.
        public static int Held(PxRect monitor, PxRect work, bool registered, PxRect band)
        {
            if (monitor.Height <= 0 || work.Height <= 0) return 0;
            int below = work.B;
            if (registered && band.Height > 0 && band.B <= monitor.B && work.B <= band.T + 1) below = band.B;
            return Math.Max(0, Math.Min(monitor.B - below, monitor.Height));
        }

        // Сколько занимать самому: док стоит у самого низа монитора, поверх места спрятанной панели, а своя полоса —
        // только на то, чего этому месту не хватает до высоты дока. Тогда рабочая область кончается ровно у верха дока.
        public static int Missing(int height, int reserved) { return Math.Max(0, height - Math.Max(0, reserved)); }

        // Что предложить Windows, когда снизу ещё держит место панель задач: полоса недостающей высоты прямо над этим местом
        public static PxRect Ask(PxRect monitor, int height, int reserved)
        {
            reserved = Math.Max(0, Math.Min(reserved, monitor.Height));
            return Propose(new PxRect(monitor.L, monitor.T, monitor.R, monitor.B - reserved), height);
        }

        // Низ дока в точках экрана. above — низ того, над чем он встал бы: своей полосы, а без неё — рабочей области.
        // Если ниже остаётся только место спрятанной панели задач, док встаёт к самому низу монитора, а не над пустотой.
        public static int BottomPx(PxRect monitor, int above, int reserved)
        {
            if (reserved > 0 && Math.Abs(above - (monitor.B - reserved)) <= 1) return monitor.B;
            return above;
        }

        // Высота полосы в точках экрана: высота дока в покое (в точках WPF) на масштаб экрана, с округлением вверх
        public static int Height(double restDip, double scale)
        {
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0) scale = 1;
            if (double.IsNaN(restDip) || restDip <= 0) return 0;
            return (int)Math.Ceiling(restDip * scale - 1e-6);
        }

        // Что предложить Windows (ABM_QUERYPOS): полоса этой высоты у нижнего края монитора, во всю его ширину
        public static PxRect Propose(PxRect monitor, int height)
        {
            height = Math.Max(0, Math.Min(height, monitor.Height));
            return new PxRect(monitor.L, monitor.B - height, monitor.R, monitor.B);
        }

        // Что Windows ответила → что занять (ABM_SETPOS). Windows могла поднять низ над другой полосой на том же краю
        // или сузить её у боковых — высота остаётся нашей, отсчитывается от низа, который она дала. Ниже монитора не опускаем.
        public static PxRect Settle(PxRect answer, PxRect monitor, int height)
        {
            int bottom = Math.Min(answer.B, monitor.B);
            if (bottom - height < monitor.T) bottom = Math.Min(monitor.B, monitor.T + height);
            int left = Math.Max(monitor.L, answer.L), right = Math.Min(monitor.R, answer.R);
            if (right <= left) { left = monitor.L; right = monitor.R; }
            return new PxRect(left, bottom - height, right, bottom);
        }

        // Низ дока в точках WPF: по низу занятой полосы
        public static double BottomDip(PxRect band, double scale)
        {
            if (double.IsNaN(scale) || scale <= 0) scale = 1;
            return band.B / scale;
        }
    }

    // ───────── Виден ли док ─────────
    static class Presence
    {
        // Дока не должно быть видно совсем: впереди игра или видео на весь экран, или выбрана компактная панель Windows
        public static bool Away(bool fullscreen, string bar) { return fullscreen || bar == "modern"; }

        // Окно дока спрятано (visible — как его видит сама Windows), а ему место на экране — показать снова, кто бы
        // его ни спрятал. live — настоящий док с окном (не снимок, не --demo, не --headless).
        public static bool Reshow(bool live, bool away, bool visible) { return live && !away && !visible; }
    }

    // ───────── Компактная панель задач Windows 11 ─────────
    // TabletPostureTaskbar: тонкая полоса с часами, которая разворачивается, когда нужна. Нужна она только в режиме
    // «modern». При «dock» она мешает: спрятанная, она всё равно держит внизу место (24 точки свёрнутая, 72 развёрнутая)
    // и не принимает автоскрытие, — поэтому выключается. При «both» — как было до дока (original; −1 — не записано,
    // тогда не трогаем). Ответ — нужное значение, −1 — оставить как есть.
    static class CompactRules
    {
        public static int Want(string bar, int original)
        {
            if (bar == "modern") return 1;
            if (bar == "dock") return 0;
            return original >= 0 ? original : -1;
        }
    }

    // ───────── Появление от края при автоскрытии ─────────
    // Док выезжает, только если курсор задержался у нижнего края. Задержка, а не «упор»: курсор у края экрана дальше
    // не едет, и Windows сообщает одну и ту же точку — отличить «упёрся» от «просто стоит» можно только сырым вводом
    // мыши, а это ещё один перехватчик. По дороге к прокрутке, к кнопкам внизу окна и обратно курсор проходит край
    // за десятки миллисекунд — 350 мс он там не стоит. А тому, кто хочет док, 350 мс — почти сразу (столько же
    // ждёт подсказка в Windows). Уход — по-прежнему через 0,7 с после того, как курсор ушёл с дока (HideAfterMs).
    // Пока впереди игра или видео на весь экран (blocked), от края не выезжает совсем — даже если курсор стоит там
    // долго (в стратегиях курсор у нижнего края — обычное дело).
    class EdgeReveal
    {
        public const int DwellMs = 350;
        long since = -1;

        // Очередной взгляд на курсор (двадцать раз в секунду). true — пора выезжать.
        public bool Step(long now, bool onEdge, bool blocked)
        {
            if (blocked || !onEdge) { since = -1; return false; }
            if (since < 0) since = now;
            if (now - since < DwellMs) return false;
            since = -1;
            return true;
        }

        public void Reset() { since = -1; }
    }

    // ───────── Win+1…9 ─────────
    // Как у панели задач Windows: Win+цифра — закреплённое приложение с этим номером. Не запущено — запустить; запущено —
    // вывести вперёд; уже впереди — следующее окно или свернуть. Ровно то же, что щелчок по значку (Logic.Decide).
    static class WinDigit
    {
        public const int First = 0x31, Last = 0x39;   // верхний ряд, 1…9

        // Номер закреплённого (0…8) по клавише, нажатой при зажатой кнопке Windows; −1 — не наше: другая клавиша
        // или к цифре добавили Shift, Ctrl, Alt — эти сочетания остаются у Windows
        public static int Index(int vk, bool shift, bool ctrl, bool alt)
        {
            if (vk < First || vk > Last || shift || ctrl || alt) return -1;
            return vk - First;
        }

        // Закреплённое с этим номером, по порядку в доке; null — столько закреплённых нет
        public static DockItem Pinned(List<DockItem> items, int index)
        {
            if (items == null || index < 0) return null;
            int n = 0;
            foreach (DockItem i in items)
            {
                if (!i.Pinned) continue;
                if (n++ == index) return i;
            }
            return null;
        }

        // Что сделать по Win+цифре; false — делать нечего (столько закреплённых нет)
        public static bool Decide(List<DockItem> items, int index, IntPtr foreground, out DockItem item, out Click what, out IntPtr target)
        {
            item = Pinned(items, index);
            target = IntPtr.Zero;
            what = Click.Launch;
            if (item == null) return false;
            what = Logic.Decide(item, foreground, out target);
            return true;
        }
    }

    // ───────── Сообщения Windows об окнах ─────────
    // Вместо того чтобы раз в секунду перебирать все окна, док слушает Windows (RegisterShellHookWindow): окно создано,
    // закрыто, вышло вперёд, сменило заголовок, просит внимания, переехало на другой монитор. Сообщения приходят пачками
    // (открылась программа — десяток подряд), поэтому они копятся и разбираются разом, когда пачка утихла.
    static class ShellCodes
    {
        public const int Created = 1, Destroyed = 2, Activated = 4, Redraw = 6, Replaced = 13, Replacing = 14, MonitorChanged = 16;
        public const int HighBit = 0x8000, RudeActivated = Activated | HighBit, Flash = Redraw | HighBit;
    }

    // Что накопилось в пачке: перечитать ли окна, обновить ли заголовки, и по порядку — кто попросил внимания
    // (true) и кто вышел вперёд или закрылся (false: просьба снята)
    class ShellSummary
    {
        public bool Windows, Titles;
        public readonly List<KeyValuePair<IntPtr, bool>> Attention = new List<KeyValuePair<IntPtr, bool>>();
        public readonly List<IntPtr> Gone = new List<IntPtr>();
    }

    class ShellBatch
    {
        // Окна появились, закрылись, вышли вперёд — разбираем через 120 мс тишины; сменился только заголовок — не чаще
        // раза в секунду (некоторые программы меняют его постоянно: таймер, проценты загрузки)
        public const int QuickMs = 120, TitleMs = 1000;
        long due = -1;
        ShellSummary now = new ShellSummary();

        public bool Pending { get { return due >= 0; } }
        public long Due { get { return due; } }

        // Пришло сообщение code об окне hwnd. Возвращает, к какому времени разобрать пачку (её срок только приближается:
        // за быстрым сообщением медленное срок не отодвигает); −1 — сообщение доку не нужно.
        public long Add(int code, IntPtr hwnd, long at)
        {
            int wait;
            switch (code)
            {
                case ShellCodes.Created:
                case ShellCodes.Replaced:
                case ShellCodes.Replacing:
                case ShellCodes.MonitorChanged:
                    now.Windows = true; wait = QuickMs; break;
                case ShellCodes.Destroyed:
                    now.Windows = true; now.Gone.Add(hwnd); now.Attention.Add(new KeyValuePair<IntPtr, bool>(hwnd, false)); wait = QuickMs; break;
                case ShellCodes.Activated:
                case ShellCodes.RudeActivated:
                    // Вперёд вышло другое окно: порядок окон сменился (по нему щелчок выбирает «следующее»), просьба окна снята
                    now.Windows = true; now.Attention.Add(new KeyValuePair<IntPtr, bool>(hwnd, false)); wait = QuickMs; break;
                case ShellCodes.Flash:
                    now.Attention.Add(new KeyValuePair<IntPtr, bool>(hwnd, true)); wait = QuickMs; break;
                case ShellCodes.Redraw:
                    now.Titles = true; wait = TitleMs; break;
                default:
                    return -1;
            }
            long d = at + wait;
            if (due < 0 || d < due) due = d;
            return due;
        }

        // Забрать накопленное; пачка начинается заново
        public ShellSummary Take()
        {
            ShellSummary s = now;
            now = new ShellSummary();
            due = -1;
            return s;
        }
    }

    // ───────── Окно просит внимания ─────────
    // Программа мигает своей кнопкой на панели задач (FlashWindowEx) — значок в доке подпрыгивает, как на Mac: три прыжка,
    // потом под ним остаётся оранжевая точка, пока окно не вышло вперёд (или не закрылось). При «Меньше движения»
    // и «Не беспокоить» — без прыжков, сразу точка.
    enum AttentionPhase { None, Bounce, Mark }

    class Attention
    {
        public const int Hops = 3;
        readonly Dictionary<IntPtr, long> asking = new Dictionary<IntPtr, long>();

        // Окно попросило внимания в момент at. true — впервые (значку пора подпрыгнуть). Окно и так впереди — не просит:
        // Windows тоже не мигает кнопкой окна, с которым сейчас работают.
        public bool Flash(IntPtr hwnd, IntPtr foreground, long at)
        {
            if (hwnd == IntPtr.Zero || hwnd == foreground) return false;
            if (asking.ContainsKey(hwnd)) return false;
            asking[hwnd] = at;
            return true;
        }

        // Окно вышло вперёд или закрылось — просьба снята. true — она была.
        public bool Clear(IntPtr hwnd) { return asking.Remove(hwnd); }

        // Окон, которых больше нет, не помним
        public void Keep(Predicate<IntPtr> alive)
        {
            var dead = new List<IntPtr>();
            foreach (IntPtr h in asking.Keys) if (!alive(h)) dead.Add(h);
            foreach (IntPtr h in dead) asking.Remove(h);
        }

        public int Count { get { return asking.Count; } }

        // Когда окно этой программы попросило внимания (самое раннее из её окон); −1 — не просит
        public long Since(DockItem item)
        {
            long first = -1, at;
            if (item == null) return -1;
            foreach (IntPtr h in item.Windows)
                if (asking.TryGetValue(h, out at) && (first < 0 || at < first)) first = at;
            return first;
        }

        // Что видно у значка спустя elapsed мс после просьбы: прыжки, потом отметка. still — без движения
        // («Меньше движения», «Не беспокоить», окна на экране нет): сразу отметка. hopMs — один прыжок.
        public static AttentionPhase Phase(long since, long now, bool still, int hopMs)
        {
            if (since < 0) return AttentionPhase.None;
            if (still) return AttentionPhase.Mark;
            return now - since < (long)Hops * hopMs ? AttentionPhase.Bounce : AttentionPhase.Mark;
        }
    }

    // ───────── Файл на значке программы ─────────
    // Файлы из проводника, брошенные на значок, открываются этой программой: она запускается с их путями (как если бы
    // их бросили на её файл в проводнике). Принимают только значки обычных программ: у приложения из Магазина и у
    // программы, которая запускается через Update.exe (Discord и ему подобные), передать файлы так нельзя.
    static class OpenWith
    {
        public const int MaxArgs = 30000;   // в командную строку Windows влезает 32 767 знаков — с запасом

        public static string Tip(string name) { return "Открыть в «" + name + "»"; }
        public const string Failed = "Не удалось открыть";

        public static bool Accepts(DockItem item, Func<string, bool> fileExists)
        {
            if (item == null || !string.IsNullOrEmpty(item.App) || string.IsNullOrEmpty(item.Path)) return false;
            if (Logic.StartedExe(item.Pin) != null) return false;
            if (!item.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            return fileExists == null || fileExists(item.Path);
        }

        // Строка параметров: свои параметры ярлыка (если есть), за ними пути — каждый в кавычках. Путь с кавычкой
        // пропускается (в именах файлов Windows кавычек не бывает). Всё, что не влезает в командную строку, отбрасывается.
        // null — передавать нечего.
        public static string Arguments(string own, List<string> paths)
        {
            if (paths == null) return null;
            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(own) && own.Trim().Length > 0) sb.Append(own.Trim());
            int added = 0;
            foreach (string p in paths)
            {
                if (string.IsNullOrEmpty(p) || p.IndexOf('"') >= 0 || p.IndexOf('\0') >= 0) continue;
                string one = "\"" + p + "\"";
                if (sb.Length + 1 + one.Length > MaxArgs) break;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(one);
                added++;
            }
            return added == 0 ? null : sb.ToString();
        }
    }
}
