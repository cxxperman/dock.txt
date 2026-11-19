// dock.txt. Widgets.cs — данные виджетов, счётчиков непрочитанного и меню приложений: всё, что не трогает экран и Windows.
// Музыку, погоду, календарь и счётчики присылает kapsula.txt через хаб (состояния kapsula.media, kapsula.weather,
// kapsula.calendar, kapsula.unread); здесь они разбираются в простые объекты. Этот файл целиком проверяется ключом --selftest.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DevTxt.DockApp
{
    // Чтение того, что пришло из хаба: объект JSON — это словарь, число — int, decimal или double
    static class J
    {
        public static Dictionary<string, object> Dict(object o) { return o as Dictionary<string, object>; }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) && v is string ? ((string)v).Trim() : "";
        }

        public static bool Num(Dictionary<string, object> d, string key, out double value)
        {
            value = 0;
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null || v is bool || v is string) return false;
            try { value = Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return false; }
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }

    // kapsula.media: { "playing": bool, "title": string, "artist": string, "app": string }
    class MediaInfo
    {
        public bool Playing;
        public string Title, Artist, App;

        public static MediaInfo From(object data)
        {
            var d = J.Dict(data);
            if (d == null) return null;
            object playing;
            var m = new MediaInfo { Title = J.Str(d, "title"), Artist = J.Str(d, "artist"), App = J.Str(d, "app"), Playing = d.TryGetValue("playing", out playing) && playing is bool && (bool)playing };
            return m.Title.Length == 0 && m.Artist.Length == 0 ? null : m;   // нечего показать — виджета нет
        }

        // «Название — исполнитель»
        public string Line { get { return Title.Length == 0 ? Artist : Artist.Length == 0 ? Title : Title + " — " + Artist; } }
        public string Tip { get { return (Playing ? "" : "Пауза · ") + Line + (App.Length > 0 ? " · " + App : ""); } }
    }

    // kapsula.weather: { "temp": число (°C), "text": string, "icon": "clear"|"cloudy"|"rain"|"snow"|"storm"|"fog", "city": string }
    class WeatherInfo
    {
        public static readonly string[] Icons = { "clear", "cloudy", "rain", "snow", "storm", "fog" };
        public double Temp;
        public string Text, Icon, City;

        public static WeatherInfo From(object data)
        {
            var d = J.Dict(data);
            double t;
            if (d == null || !J.Num(d, "temp", out t)) return null;
            var w = new WeatherInfo { Temp = t, Text = J.Str(d, "text"), City = J.Str(d, "city"), Icon = J.Str(d, "icon").ToLowerInvariant() };
            if (Array.IndexOf(Icons, w.Icon) < 0) w.Icon = "cloudy";   // незнакомый значок — просто облако
            return w;
        }

        // «+4°», «−12°», «0°»
        public string TempText
        {
            get
            {
                int t = (int)Math.Round(Temp, MidpointRounding.AwayFromZero);
                return (t > 0 ? "+" : t < 0 ? "−" : "") + Math.Abs(t) + "°";
            }
        }

        public string Tip { get { return Text.Length == 0 ? (City.Length == 0 ? TempText : City) : City.Length == 0 ? Text : Text + " · " + City; } }
    }

    class CalendarEvent
    {
        public string Title, Start, End;
        public bool AllDay;   // событие на весь день: Kapsula присылает его с "allDay": true и временем 00:00–23:59

        // Что написать вместо времени
        public string When { get { return AllDay ? "весь день" : Start; } }
    }

    // kapsula.calendar: { "events": [ { "title": string, "start": "HH:mm", "end": "HH:mm", "allDay": bool } ] } — то, что осталось на сегодня
    class CalendarInfo
    {
        public const int TipLines = 5;
        public readonly List<CalendarEvent> Events = new List<CalendarEvent>();

        public static CalendarInfo From(object data)
        {
            var d = J.Dict(data);
            object list;
            if (d == null || !d.TryGetValue("events", out list) || !(list is object[])) return null;
            var c = new CalendarInfo();
            foreach (object o in (object[])list)
            {
                var e = J.Dict(o);
                if (e == null) continue;
                object all;
                var ev = new CalendarEvent { Title = J.Str(e, "title"), Start = J.Str(e, "start"), End = J.Str(e, "end") };
                ev.AllDay = (e.TryGetValue("allDay", out all) && all is bool && (bool)all) || (ev.Start == "00:00" && ev.End == "23:59");
                if (ev.Title.Length == 0 && ev.Start.Length == 0) continue;
                c.Events.Add(ev);
            }
            return c;
        }

        // «14:30» → минуты от полуночи; −1 — не время
        public static int Minutes(string hhmm)
        {
            if (hhmm == null) return -1;
            string[] p = hhmm.Split(':');
            int h, m;
            if (p.Length != 2 || !int.TryParse(p[0], out h) || !int.TryParse(p[1], out m) || h < 0 || h > 23 || m < 0 || m > 59) return -1;
            return h * 60 + m;
        }

        // То, что ещё не кончилось к этой минуте. Kapsula присылает оставшееся, но между её сообщениями время идёт.
        public List<CalendarEvent> Remaining(int nowMinutes)
        {
            var list = new List<CalendarEvent>();
            foreach (var e in Events)
            {
                int end = Minutes(e.End), start = Minutes(e.Start);
                if (end < 0) end = start;
                if (e.AllDay || end < 0 || end > nowMinutes || (end == start && start == nowMinutes)) list.Add(e);
            }
            return list;
        }

        public static string Tip(List<CalendarEvent> left)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < left.Count && i < TipLines; i++)
            {
                if (i > 0) sb.Append('\n');
                var e = left[i];
                sb.Append(e.When);
                if (!e.AllDay && e.End.Length > 0 && e.End != e.Start) sb.Append('–').Append(e.End);
                if (e.Title.Length > 0) sb.Append(e.When.Length > 0 ? "  " : "").Append(e.Title);
            }
            return sb.ToString();
        }
    }

    // kapsula.unread: { "total": число, "apps": { "discord": число, "steam": число } }
    class UnreadInfo
    {
        public int Total;
        public readonly Dictionary<string, int> Apps = new Dictionary<string, int>();

        // Какие ещё файлы считаются тем же приложением: окно Steam держит steamwebhelper.exe
        static readonly string[][] aliases = { new string[] { "steam", "steamwebhelper" } };

        public static UnreadInfo From(object data)
        {
            var d = J.Dict(data);
            if (d == null) return null;
            var u = new UnreadInfo();
            double n;
            if (J.Num(d, "total", out n)) u.Total = (int)Math.Max(0, Math.Round(n));
            object apps;
            d.TryGetValue("apps", out apps);
            var a = J.Dict(apps);
            if (a != null)
                foreach (string key in a.Keys)
                    if (J.Num(a, key, out n)) u.Apps[key.Trim().ToLowerInvariant()] = (int)Math.Max(0, Math.Round(n));
            return u;
        }

        // Сколько непрочитанного у программы с таким файлом («Discord.exe», «steamwebhelper.exe»)
        public int For(string exe)
        {
            string bare = (exe ?? "").Trim().ToLowerInvariant();
            if (bare.EndsWith(".exe")) bare = bare.Substring(0, bare.Length - 4);
            if (bare.Length == 0) return 0;
            int n;
            if (Apps.TryGetValue(bare, out n)) return n;
            foreach (string[] a in aliases)
                if (Array.IndexOf(a, bare) > 0 && Apps.TryGetValue(a[0], out n)) return n;
            return 0;
        }

        // Надпись на счётчике: больше девяти — «9+», нет непрочитанного — счётчика нет
        public static string Text(int count) { return count <= 0 ? null : count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture); }
    }

    // Батарея: то, что отдаёт GetSystemPowerStatus
    class BatteryInfo
    {
        public bool Has, Charging, Plugged;
        public int Percent;

        public static BatteryInfo From(byte acLine, byte flag, byte percent)
        {
            var b = new BatteryInfo();
            b.Has = flag != 255 && (flag & 128) == 0 && percent <= 100;   // 128 — батареи нет, 255 — неизвестно
            b.Percent = b.Has ? percent : 0;
            b.Plugged = acLine == 1;
            b.Charging = b.Has && (flag & 8) != 0;
            return b;
        }

        public string Tip { get { return (Charging ? "Заряжается · " : Plugged ? "От сети · " : "Заряд ") + Percent + " %"; } }
        public string Signature { get { return Has ? Percent + (Charging ? "c" : "") + (Plugged ? "p" : "") : "-"; } }
    }

    // Громкость: расчёты без самого звука
    static class VolumeMath
    {
        public const double StepPerNotch = 0.02;   // один щелчок колеса — два процента, как у Windows

        public static int Percent(double level) { return (int)Math.Round(Math.Max(0, Math.Min(1, level)) * 100, MidpointRounding.AwayFromZero); }

        // Колесо над виджетом: новая громкость по целым процентам. Прибавили при выключенном звуке — звук включается.
        public static double Wheel(double level, bool muted, int delta, out bool nowMuted)
        {
            double to = Math.Max(0, Math.Min(100, Percent(level) + Math.Round(delta / 120.0 * StepPerNotch * 100)));
            nowMuted = muted && delta <= 0;
            return to / 100;
        }

        // Сколько дуг у значка динамика: 0 — тишина
        public static int Bars(double level, bool muted)
        {
            int p = Percent(level);
            return muted || p == 0 ? 0 : p <= 33 ? 1 : p <= 66 ? 2 : 3;
        }

        public static string Tip(double level, bool muted) { return muted ? "Звук выключен" : "Громкость " + Percent(level) + " %"; }
    }

    static class WidgetList
    {
        // Слева направо; часы — у самого края, как в трее
        public static readonly string[] Order = { "music", "calendar", "weather", "volume", "battery", "clock" };

        public static string Key(string id) { return "widget" + char.ToUpperInvariant(id[0]) + id.Substring(1); }

        // Какие виджеты показать: включён в настройках и есть что показывать
        public static List<string> Visible(Func<string, bool> on, bool music, bool calendar, bool weather, bool volume, bool battery)
        {
            var list = new List<string>();
            foreach (string id in Order)
            {
                bool has = id == "music" ? music : id == "calendar" ? calendar : id == "weather" ? weather : id == "volume" ? volume : id == "battery" ? battery : true;
                if (has && on(Key(id))) list.Add(id);
            }
            return list;
        }
    }

    // Приложение в меню: ярлык из меню «Пуск»
    class AppEntry
    {
        public string Name, Target, Args, Link, Icon;
        public System.Windows.Media.ImageSource Image;
    }

    static class AppRules
    {
        static readonly string[] junkWords =
        {
            "uninstall", "uninstaller", "readme", "help", "website", "documentation", "manual", "license", "faq", "changelog",
            "удалить", "удаление", "деинсталляция", "справка", "сайт", "документация", "руководство", "лицензия"
        };
        static readonly string[] junkParts = { "uninstall", "read me", "web site", "home page", "homepage", "release notes", "удалить ", "удаление " };

        public static string Lower(string s) { return (s ?? "").ToLowerInvariant().Replace('ё', 'е'); }

        static List<string> Words(string lower)
        {
            var list = new List<string>();
            int from = -1;
            for (int i = 0; i <= lower.Length; i++)
            {
                bool letter = i < lower.Length && char.IsLetterOrDigit(lower[i]);
                if (letter && from < 0) from = i;
                if (!letter && from >= 0) { list.Add(lower.Substring(from, i - from)); from = -1; }
            }
            return list;
        }

        // Не приложение, а его обвязка: удаление, справка, «прочти меня», ссылка на сайт
        public static bool IsJunk(string name, string target)
        {
            string n = Lower(name), t = Lower(target);
            int slash = t.LastIndexOf('\\');
            string file = slash >= 0 ? t.Substring(slash + 1) : t;
            if (file.StartsWith("unins") || file.Contains("uninstall")) return true;
            foreach (string p in junkParts) if (n.Contains(p)) return true;
            foreach (string w in Words(n)) if (Array.IndexOf(junkWords, w) >= 0) return true;
            return false;
        }

        // Два ярлыка на одно и то же (та же программа с теми же параметрами) — остаётся первый; обвязка убирается; по алфавиту
        public static List<AppEntry> Clean(List<AppEntry> found)
        {
            var list = new List<AppEntry>();
            var seen = new Dictionary<string, bool>();
            foreach (var e in found)
            {
                if (e == null || string.IsNullOrEmpty(e.Name) || string.IsNullOrEmpty(e.Target) || IsJunk(e.Name, e.Target)) continue;
                string key = Lower(e.Target) + "|" + Lower(e.Args).Trim();
                if (seen.ContainsKey(key)) continue;
                seen[key] = true;
                list.Add(e);
            }
            list.Sort(delegate(AppEntry a, AppEntry b) { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
            return list;
        }

        // Насколько название подходит к запросу: 0 — начинается с него, 1 — каждое слово запроса начинает какое-то слово названия,
        // 2 — каждое слово запроса просто встречается в названии, −1 — не подходит. Регистр и «ё» не важны.
        public static int Rank(string name, string query)
        {
            string n = Lower(name), q = Lower(query).Trim();
            if (q.Length == 0) return 0;
            if (n.StartsWith(q)) return 0;
            List<string> words = Words(n);
            bool starts = true;
            foreach (string part in q.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!n.Contains(part)) return -1;
                if (!words.Exists(delegate(string w) { return w.StartsWith(part); })) starts = false;
            }
            return starts ? 1 : 2;
        }

        // Что показать по запросу: лучшие совпадения — первыми, внутри — прежний порядок
        public static List<AppEntry> Filter(List<AppEntry> all, string query)
        {
            var list = new List<AppEntry>();
            if (all == null) return list;
            for (int rank = 0; rank <= 2; rank++)
                foreach (var e in all)
                    if (Rank(e.Name, query) == rank) list.Add(e);
            return list;
        }

        // Стрелки в сетке: новый выбранный номер. Вниз из последней полной строки — на последний значок.
        public static int Move(int index, int count, int columns, int dx, int dy)
        {
            if (count <= 0) return -1;
            if (index < 0) return 0;
            int to = index + dx + dy * Math.Max(1, columns);
            if (dy > 0 && to >= count && index / Math.Max(1, columns) < (count - 1) / Math.Max(1, columns)) to = count - 1;
            return to < 0 || to >= count ? index : to;
        }

        // Home, End, PageUp, PageDown: jump — «home», «end», «up», «down»; rows — сколько строк помещается на экране.
        // Страница сдвигает выбор на rows строк в том же столбце и останавливается на первой или последней строке.
        public static int Jump(int index, int count, int columns, int rows, string jump)
        {
            if (count <= 0) return -1;
            if (jump == "home") return 0;
            if (jump == "end") return count - 1;
            if (index < 0) return 0;
            columns = Math.Max(1, columns); rows = Math.Max(1, rows);
            int col = index % columns, row = index / columns, last = (count - 1) / columns;
            row = jump == "up" ? Math.Max(0, row - rows) : Math.Min(last, row + rows);
            return Math.Min(count - 1, row * columns + col);
        }
    }

    // Нижний ряд меню приложений: выключение, перезагрузка, сон, блокировка. Здесь — только что это за команды;
    // запускает их Session в Win.cs, и только в настоящем доке.
    static class SessionRules
    {
        public static readonly string[] Kinds = { "shutdown", "restart", "sleep", "lock" };

        public static string Label(string kind) { return kind == "shutdown" ? "Завершение работы" : kind == "restart" ? "Перезагрузка" : kind == "sleep" ? "Сон" : "Блокировка"; }

        // Что написать на кнопке после первого щелчка; null — действие безобидное, второй щелчок не нужен
        public static string Confirm(string kind) { return kind == "shutdown" ? "Точно выключить?" : kind == "restart" ? "Точно перезагрузить?" : null; }

        // Значок из Segoe Fluent Icons
        public static string Glyph(string kind) { return kind == "shutdown" ? "" : kind == "restart" ? "" : kind == "sleep" ? "" : ""; }

        // Программа и её параметры; у блокировки программы нет — её делает сама Windows (LockWorkStation). false — такой команды нет.
        public static bool Command(string kind, out string exe, out string args)
        {
            exe = null; args = null;
            if (kind == "shutdown") { exe = "shutdown.exe"; args = "/s /t 0"; }
            else if (kind == "restart") { exe = "shutdown.exe"; args = "/r /t 0"; }
            else if (kind == "sleep") { exe = "rundll32.exe"; args = "powrprof.dll,SetSuspendState 0,1,0"; }
            else if (kind != "lock") return false;
            return true;
        }
    }
}
