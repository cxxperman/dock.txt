// dock.txt — док в духе macOS для Windows, часть dev.txt: закреплённые и запущенные приложения на стеклянной полосе внизу экрана.
// Ключи для проверки:
//   --demo              выдуманные значки; окна, диск, реестр и хаб не трогаются
//   --nohub             вместе с --demo: без выдуманных данных из хаба — только часы, громкость и батарея
//   --shot файл.png     нарисовать док в файл и выйти (обычно вместе с --demo); --hover N — будто курсор над значком N,
//                       --hover music|calendar|weather|volume|battery|clock|apps — над виджетом или кнопкой меню; --menu — с меню этого значка
//   --apps              вместе с --shot: нарисовать меню приложений; --query текст — с этим текстом в поиске
//   --selftest          проверить сборку значков из окон, закрепление, увеличение, настройки, виджеты, счётчики, названия и поиск в меню; код выхода 0 — всё в порядке
//   --probe [файл]      запуститься по-настоящему, провести по доку выдуманный курсор, записать, что док увидел, и выйти
//   --headless          работать, не показывая окна (для проверок с хабом); --seconds N — выйти через N секунд
//   --testhook          принимать из хаба проверочные команды test.act и test.quit
//   --state папка       хранить state.json в этой папке, а не в %APPDATA%\dev.txt\dock (для проверок)
// Переменная DOCK_HUB_PORT — хаб на другом порту, рядом с настоящим core.txt (тоже для проверок).
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace DevTxt.DockApp
{
    static class Program
    {
        public static string VersionText
        {
            get { Version v = typeof(Program).Assembly.GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        static string Arg(string[] args, string name)
        {
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name && !args[i + 1].StartsWith("--")) return args[i + 1];
            return null;
        }

        [STAThread]
        static int Main(string[] args)
        {
            bool demo = Array.IndexOf(args, "--demo") >= 0, probe = Array.IndexOf(args, "--probe") >= 0, headless = Array.IndexOf(args, "--headless") >= 0;
            string shot = Arg(args, "--shot"), probeFile = Arg(args, "--probe"), stateDir = Arg(args, "--state"), over = Arg(args, "--hover"), overWidget = null;
            int hover = -1, seconds = 0;
            if (over != null && !int.TryParse(over, out hover)) { hover = -1; overWidget = over; }
            if (Arg(args, "--seconds") != null) int.TryParse(Arg(args, "--seconds"), out seconds);
            if (Array.IndexOf(args, "--selftest") >= 0) return SelfTest.Run();
            DockHub.TestHook = Array.IndexOf(args, "--testhook") >= 0;

            bool created = true;
            Mutex mutex = null;
            if (shot == null && !headless) mutex = new Mutex(true, "devtxt_dock_single_instance", out created);   // без окна второй док первому не мешает
            if (!created) return 0;   // док уже на экране

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            if (shot != null && Array.IndexOf(args, "--apps") >= 0)
            {
                // Меню приложений: выдуманный список или настоящий, собранный прямо сейчас
                AppMenuView.Shot(shot, demo ? AppCatalog.Demo() : AppCatalog.Scan(), Arg(args, "--query"));
                return 0;
            }
            var store = new Store(demo ? null : stateDir ?? Store.DefaultDir);
            if (!demo && !store.Load())
            {
                // Первый запуск: начинаем с того, что уже закреплено на панели задач Windows
                store.Pins = Shell.TaskbarPins();
                store.Save();
            }
            // Док живёт весь день: случайная ошибка в одном щелчке не должна его ронять — записываем её и работаем дальше
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                e.Handled = true;
                try { if (store.Dir != null) File.AppendAllText(Path.Combine(store.Dir, "dock.log"), DateTime.Now.ToString("s") + " " + e.Exception + "\r\n"); } catch { }
            };

            // Прошлый запуск не вернул панель задач (упал или его закрыли силой), а замена выключена — возвращаем сразу
            if (!demo && !headless && shot == null && !store.ReplaceTaskbar) TaskbarSwap.Recover(store.Dir);
            if (!demo && !headless && shot == null)
            {
                AppDomain.CurrentDomain.UnhandledException += delegate { WinKey.Stop(); TaskbarSwap.Give(store.Dir); };
                app.SessionEnding += delegate { WinKey.Stop(); TaskbarSwap.Give(store.Dir); };
            }
            var w = new DockWindow(demo, store, headless, Array.IndexOf(args, "--nohub") < 0);
            if (shot != null) { w.Shot(shot, hover, overWidget, Array.IndexOf(args, "--menu") >= 0); return 0; }

            w.Closed += delegate { app.Shutdown(); };
            if (!headless) w.Show();
            if (seconds > 0)
            {
                var stop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                stop.Tick += delegate { stop.Stop(); w.Quit(); };
                stop.Start();
            }
            if (probe)
            {
                // Секунду осматриваемся (окна, хаб), потом репетиция с выдуманным курсором — всего около пяти секунд на экране
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
                timer.Tick += delegate
                {
                    timer.Stop();
                    w.Rehearse(delegate(string rehearsal)
                    {
                        string line = w.Probe() + "\r\n" + rehearsal + "\r\n" + w.ProbeNames();
                        try { if (probeFile != null) File.WriteAllText(probeFile, line + "\r\n"); else Console.WriteLine(line); } catch { }
                        w.Quit();
                    });
                };
                timer.Start();
            }
            app.Run();
            GC.KeepAlive(mutex);
            return 0;
        }

        // Рисует то, что на экран не выводилось, в файл PNG. scale — во сколько раз крупнее обычного.
        public static void Save(FrameworkElement root, double w, double h, double scale, string file)
        {
            root.Measure(new Size(w, h));
            root.Arrange(new Rect(0, 0, w, h));
            root.UpdateLayout();
            var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, System.Windows.Media.PixelFormats.Pbgra32);
            bmp.Render(root);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var f = File.Create(file)) enc.Save(f);
        }
    }

    static class SelfTest
    {
        static int failed;
        static void Check(bool ok, string what) { if (!ok) { failed++; Console.Error.WriteLine("FAIL  " + what); } }
        static bool Near(double a, double b) { return Math.Abs(a - b) < 1e-6; }

        static WinInfo W(int hwnd, string path) { return new WinInfo { Hwnd = new IntPtr(hwnd), Title = "t" + hwnd, Path = path }; }

        public static int Run()
        {
            // ── Окна собираются в значки ──
            var pins = new List<Pin>
            {
                new Pin { Name = "Notes", Path = @"C:\Apps\Notes\notes.exe" },
                new Pin { Name = "Chat", Path = @"C:\Users\u\AppData\Local\Chat\Update.exe", Args = "--processStart Chat.exe" },
                new Pin { Name = "Idle", Path = @"C:\Apps\Idle\idle.exe" },
                new Pin { Name = "Notes again", Path = @"c:\apps\notes\NOTES.EXE" }
            };
            var wins = new List<WinInfo>
            {
                W(1, @"C:\Tools\term.exe"),
                W(2, @"C:\Apps\Notes\Notes.exe"),
                W(3, @"C:\Users\u\AppData\Local\Chat\app-1.0.9\Chat.exe"),
                W(4, @"C:\Tools\TERM.exe"),
                W(5, @"C:\Apps\Notes\notes.exe"),
                new WinInfo { Hwnd = new IntPtr(6), Title = "Calc", Path = @"C:\Windows\System32\ApplicationFrameHost.exe", Key = "frame|calc", Label = "Calc" },
                new WinInfo { Hwnd = new IntPtr(7), Title = "x", Path = null }
            };
            Func<string, string> nameOf = delegate(string p) { return "name:" + Path.GetFileName(p).ToLowerInvariant(); };
            var items = Logic.Group(pins, wins, true, nameOf);
            Check(items.Count == 5, "three pins (duplicate dropped) and two running groups, got " + items.Count);
            Check(items.Count == 5 && items[0].Name == "Notes" && items[0].Pinned && items[0].Windows.Count == 2 && items[0].Windows[0] == new IntPtr(2), "pinned app collects its windows in z-order, path compared without case");
            Check(items.Count == 5 && items[1].Name == "Chat" && items[1].Running, "a pin made through Update.exe --processStart recognises the real program");
            Check(items.Count == 5 && items[2].Name == "Idle" && !items[2].Running, "a pin with no windows is not running");
            Check(items.Count == 5 && !items[3].Pinned && items[3].Name == "name:term.exe" && items[3].Windows.Count == 2 && items[3].CanPin, "two windows of one unpinned program make one item");
            Check(items.Count == 5 && items[4].Name == "Calc" && !items[4].CanPin, "a Store app frame is named by its title and cannot be pinned by file");
            var onlyPins = Logic.Group(pins, wins, false, nameOf);
            Check(onlyPins.Count == 3 && onlyPins[0].Running && onlyPins[1].Running, "with running apps hidden only pins stay, and they still know they run");
            Check(!Logic.PinMatches(pins[1], @"C:\Users\u\AppData\Local\Other\app-1\Chat.exe") && !Logic.PinMatches(pins[1], @"C:\Users\u\AppData\Local\Chat\app-1\Other.exe"), "Update.exe pin does not claim other programs");
            Check(Logic.Signature(items) == Logic.Signature(Logic.Group(pins, wins, true, nameOf)), "same windows — same signature, nothing to redraw");
            wins.RemoveAt(2);
            Check(Logic.Signature(items) != Logic.Signature(Logic.Group(pins, wins, true, nameOf)), "a program closed — signature changes");

            // ── Щелчок ──
            IntPtr target;
            Check(Logic.Decide(items[2], new IntPtr(2), out target) == Click.Launch, "not running — launch");
            Check(Logic.Decide(items[0], new IntPtr(1), out target) == Click.Activate && target == new IntPtr(2), "running, another window ahead — bring the most recent one forward");
            Check(Logic.Decide(items[0], new IntPtr(5), out target) == Click.Minimize && target == new IntPtr(5), "already ahead — minimise that very window");
            Check(Logic.IsFullscreen(0, 0, 1920, 1080, 1920, 1080, false), "borderless window over the whole screen is fullscreen");
            Check(!Logic.IsFullscreen(-8, -8, 1928, 1088, 1920, 1080, true), "a maximised window with a caption is not");
            Check(!Logic.IsFullscreen(0, 0, 1920, 1040, 1920, 1080, false), "a window that leaves the taskbar visible is not");

            // ── Закрепление переживает перезапуск ──
            string dir = Path.Combine(Path.GetTempPath(), "dock.txt-test-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            try
            {
                var a = new Store(dir);
                Check(!a.Load(), "no file yet — first run");
                a.Pins.Add(new Pin { Name = "Чат", Path = @"C:\Chat\Update.exe", Link = @"C:\l\Chat.lnk", Args = "--processStart Chat.exe", Icon = @"C:\Chat\app.ico,0" });
                a.PinItem(items[3]);
                a.PinItem(items[3]);
                a.PinItem(items[4]);
                a.Apply("autoHide", true);
                a.Apply("size", 61.4);
                a.Apply("bar", "dock");
                a.Apply("winKey", false);
                var b = new Store(dir);
                Check(b.Load(), "state file is read back");
                Check(b.Pins.Count == 2 && b.Pins[0].Name == "Чат" && b.Pins[0].Link == @"C:\l\Chat.lnk" && b.Pins[0].Args == "--processStart Chat.exe" && b.Pins[0].Icon == @"C:\Chat\app.ico,0", "a pin keeps its name, link, arguments and icon");
                Check(b.Pins.Count == 2 && b.Pins[1].Path == @"C:\Tools\term.exe" && b.Pins[1].Name == "name:term.exe", "pinning a running app once adds it once; a Store frame is not pinned");
                Check(b.AutoHide && b.Magnify && b.ShowRunning && b.Size == 61, "settings are read back");
                Check(b.ReplaceTaskbar && b.Bar == "dock" && !b.WinKey, "the bottom bar choice and the Windows key setting are read back");
                bool badBar = false;
                try { b.Apply("bar", "left"); } catch { badBar = true; }
                Check(badBar && b.Bar == "dock", "an unknown bottom bar is refused");
                File.WriteAllText(Path.Combine(dir, "state.json"), "{\"settings\":{\"replaceTaskbar\":true}}");
                var old = new Store(dir);
                old.Load();
                Check(old.Bar == "dock", "the old on/off setting becomes the choice «dock.txt»");
                Check(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(b.Describe()).Contains("\"bar\""), "core.txt sees the taskbar settings");
                Check(!TaskbarSwap.Active && !WinKey.Installed, "a self-test never hides the taskbar or hooks the Windows key");
                b.UnpinItem(new DockItem { Path = @"c:\tools\TERM.exe" });
                var c = new Store(dir);
                c.Load();
                Check(c.Pins.Count == 1 && c.Pins[0].Name == "Чат", "unpinning removes exactly that app");
                File.WriteAllText(Path.Combine(dir, "state.json"), "{ not json");
                Check(!new Store(dir).Load(), "a broken file reads as a first run instead of crashing");
                var none = new Store(null);
                none.PinItem(items[3]);
                none.Save();
                Check(none.Pins.Count == 1 && !none.Load(), "demo store keeps pins in memory and touches no disk");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            // ── Увеличение под курсором ──
            Check(Near(Magnify.Scale(0, 100, 1.5), 1.5), "the icon under the cursor is at full scale");
            Check(Near(Magnify.Scale(100, 100, 1.5), 1) && Near(Magnify.Scale(250, 100, 1.5), 1), "far icons are not scaled");
            Check(Near(Magnify.Scale(-40, 100, 1.5), Magnify.Scale(40, 100, 1.5)), "scale is symmetric");
            Check(Magnify.Scale(20, 100, 1.5) > Magnify.Scale(60, 100, 1.5) && Magnify.Scale(60, 100, 1.5) > 1, "scale falls off with distance");
            double[] rest = { 48, 48, 1, 48, 48 }, ones = { 1, 1, 1, 1, 1 };
            double from, to;
            double[] left = Magnify.Layout(rest, ones, 8, 77, out from, out to);
            Check(Near(from, 0) && Near(to, 4 * 56 + 9) && Near(left[0], 4) && Near(left[1], 60) && Near(left[2], 116) && Near(left[3], 125), "at rest icons stand in a row with even gaps");
            double[] big = { 1.2, 1.5, 1, 1.3, 1 };
            foreach (double u in new double[] { 0, 30, 84, 115, 150, 233 })
            {
                // Точка ряда, которая в покое была под курсором, остаётся под ним
                left = Magnify.Layout(rest, big, 8, u, out from, out to);
                double a = 0, at = double.NaN;
                for (int i = 0; i < rest.Length; i++)
                {
                    double rw = rest[i] + 8;
                    if (u <= a + rw || i == rest.Length - 1) { at = left[i] - 4 + (u - a) / rw * (rest[i] * big[i] + 8); break; }
                    a += rw;
                }
                Check(Near(at, u), "the point under the cursor stays under it (u=" + u + ", got " + at + ")");
                Check(Near(to - from, 48 * 1.2 + 48 * 1.5 + 1 + 48 * 1.3 + 48 + 5 * 8), "the row grows by exactly what the icons gained");
                bool ordered = true;
                for (int i = 1; i < left.Length; i++) if (left[i] < left[i - 1] + rest[i - 1] * big[i - 1] + 8 - 1e-6) ordered = false;
                Check(ordered, "magnified icons never overlap");
            }
            left = Magnify.Layout(new double[0], new double[0], 8, 10, out from, out to);
            Check(left.Length == 0 && Near(to - from, 0), "an empty dock lays out to nothing");
            double x = 0, v = 0;
            for (int i = 0; i < 20; i++) Magnify.Spring(ref x, ref v, 1, Tokens.Motion.SpringShapeStiffness, Tokens.Motion.SpringShapeDamping, 1.0 / 60);
            Check(x > 0.6 && x < 1.2, "the spring is most of the way in a third of a second, got " + x);
            double tx = x, tv = v;
            Magnify.Spring(ref tx, ref tv, 0, Tokens.Motion.SpringShapeStiffness, Tokens.Motion.SpringShapeDamping, 1.0 / 60);
            Check(Math.Abs(tx - x) < 0.1, "turning the spring back does not jump");
            for (int i = 0; i < 180; i++) Magnify.Spring(ref x, ref v, 1, Tokens.Motion.SpringShapeStiffness, Tokens.Motion.SpringShapeDamping, 1.0 / 60);
            Check(Math.Abs(x - 1) < 0.001 && Math.Abs(v) < 0.01, "the spring settles");

            // ── Настройки для core.txt ──
            var s = new Store(null);
            var d = s.Describe();
            var groups = d["groups"] as object[];
            var list = groups != null && groups.Length == 3 ? ((Dictionary<string, object>)groups[0])["items"] as object[] : null;
            Check(list != null && list.Length == 4, "the first group has four settings");
            var tgroup = groups != null && groups.Length == 3 ? (Dictionary<string, object>)groups[1] : null;
            var tlist = tgroup != null ? tgroup["items"] as object[] : null;
            Check(tgroup != null && (string)tgroup["title"] == "Панель задач" && tlist != null && tlist.Length == 2
                && (string)((Dictionary<string, object>)tlist[0])["key"] == "bar" && (string)((Dictionary<string, object>)tlist[0])["value"] == "both" && (string)((Dictionary<string, object>)tlist[0])["type"] == "choice"
                && (string)((Dictionary<string, object>)tlist[1])["key"] == "winKey", "the second group is «Панель задач»: the replacement is off until asked for, then the Windows key");
            var wgroup = groups != null && groups.Length == 3 ? (Dictionary<string, object>)groups[2] : null;
            var wlist = wgroup != null ? wgroup["items"] as object[] : null;
            Check(wgroup != null && (string)wgroup["title"] == "Виджеты" && wlist != null && wlist.Length == 7, "the third group is «Виджеты» with six widgets and the unread counters");
            if (wlist != null && wlist.Length == 7)
            {
                string wkeys = "";
                foreach (Dictionary<string, object> it in wlist) { wkeys += it["key"] + " "; Check((string)it["type"] == "switch" && it["value"] is bool && (bool)it["value"] && ((string)it["label"]).Length > 0, "every widget setting is a switch, on by default, with a label"); }
                Check(wkeys == "widgetMusic widgetCalendar widgetWeather widgetVolume widgetBattery widgetClock badges ", "widget keys, got " + wkeys);
                Check((string)((Dictionary<string, object>)wlist[6])["label"] == "Счётчики непрочитанного", "the counters switch is named «Счётчики непрочитанного»");
            }
            if (list != null && list.Length == 4)
            {
                string keys = "", types = "";
                foreach (Dictionary<string, object> it in list) { keys += it["key"] + " "; types += it["type"] + " "; Check(it.ContainsKey("label") && it.ContainsKey("value"), "every setting has a label and a value"); }
                Check(keys == "autoHide magnify showRunning size " && types == "switch switch switch slider ", "three switches and the size slider, got " + keys + "/ " + types);
                var size = (Dictionary<string, object>)list[3];
                Check((int)size["min"] == 36 && (int)size["max"] == 72 && (int)size["value"] == 48, "icon size runs from 36 to 72 and starts at 48");
            }
            string json = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(d);
            Check(json.StartsWith("{\"groups\":[{\"title\":\"\",\"items\":[{\"key\":\"autoHide\",\"type\":\"switch\""), "the description serialises the way the protocol shows it");
            s.Apply("magnify", false);
            s.Apply("size", 100);
            Check(!s.Magnify && s.Size == 72, "settings.set applies, size is kept within limits");
            s.Apply("size", 40m);
            Check(s.Size == 40, "a number arrives in whatever numeric type the parser picked");
            bool refused = false;
            try { s.Apply("nope", true); } catch { refused = true; }
            Check(refused, "an unknown key is refused");
            refused = false;
            try { s.Apply("autoHide", "yes"); } catch { refused = true; }
            Check(refused && !s.AutoHide, "a switch does not take text");
            refused = false;
            try { s.Apply("size", "big"); } catch { refused = true; }
            Check(refused && s.Size == 40, "a slider does not take text");
            s.Apply("widgetWeather", false);
            s.Apply("badges", false);
            Check(!s.On("widgetWeather") && !s.On("badges") && s.On("widgetClock"), "settings.set switches a widget and the counters off");
            refused = false;
            try { s.Apply("widgetClock", 1); } catch { refused = true; }
            Check(refused && s.On("widgetClock"), "a widget switch does not take a number");
            var after = (object[])((Dictionary<string, object>)((object[])s.Describe()["groups"])[2])["items"];
            Check(!(bool)((Dictionary<string, object>)after[2])["value"] && !(bool)((Dictionary<string, object>)after[6])["value"] && (bool)((Dictionary<string, object>)after[0])["value"], "the description shows the new widget values");
            More();
            var apps = DockHub.Apps(items);
            Check(apps.Length == 5 && (string)((Dictionary<string, object>)apps[0])["name"] == "Notes" && (bool)((Dictionary<string, object>)apps[0])["running"] && !(bool)((Dictionary<string, object>)apps[2])["running"], "dock.apps lists name, path and running");

            Console.WriteLine(failed == 0 ? "all passed" : failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        static object Parse(string json) { return new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(json); }
        static AppEntry App(string name, string target, string args) { return new AppEntry { Name = name, Target = target, Args = args, Link = @"C:\l\" + name + ".lnk" }; }

        // Виджеты, счётчики, названия и меню приложений
        static void More()
        {
            // ── Настройки виджетов переживают перезапуск ──
            string dir = Path.Combine(Path.GetTempPath(), "dock.txt-test-w-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            try
            {
                var a = new Store(dir);
                a.Apply("widgetMusic", false);
                a.Apply("badges", false);
                var b = new Store(dir);
                Check(b.Load() && !b.On("widgetMusic") && !b.On("badges") && b.On("widgetClock") && b.On("widgetVolume"), "widget switches are saved and read back; the rest stay on");
                File.WriteAllText(Path.Combine(dir, "state.json"), "{\"pins\":[],\"settings\":{\"autoHide\":true,\"size\":40}}");
                var old = new Store(dir);
                Check(old.Load() && old.AutoHide && old.Size == 40 && old.On("widgetClock") && old.On("badges"), "a state file from the previous version reads with all widgets on");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            // ── Названия ──
            Check(Logic.DisplayName("Notepad", "Windows", @"C:\n.exe") == "Notepad", "an ordinary description is the name");
            Check(Logic.DisplayName("Acme Crash Helper", "Acme Studio", @"C:\a\helper.exe") == "Acme Studio", "a helper description gives way to the product name");
            Check(Logic.DisplayName("Game LAUNCHER", "Game", @"C:\g.exe") == "Game" && Logic.DisplayName("Runtime Host", "Tool", @"C:\t.exe") == "Tool", "launcher and host too, in any case");
            Check(Logic.DisplayName("", "Zed", @"C:\z\zed.exe") == "Zed" && Logic.DisplayName(null, null, @"C:\z\zed.exe") == "zed", "an empty description: product name, then file name");
            Check(Logic.DisplayName("Steam Client WebHelper", "Steam Client WebHelper", @"C:\Steam\bin\steamwebhelper.exe") == "Steam", "a product named after its helper loses the helper words");
            Check(Logic.DisplayName("Application Frame Host", "Microsoft® Windows® Operating System", @"C:\Windows\System32\ApplicationFrameHost.exe") == "Application Frame Host", "Windows itself is not a product name worth showing");
            Check(Logic.DisplayName("", "Операционная система Microsoft® Windows®", @"C:\Windows\TextInputHost.exe") == "TextInputHost", "no description and a system product: file name");
            Func<string, string> nameOf = delegate(string p) { return "name:" + Path.GetFileName(p).ToLowerInvariant(); };
            Check(Logic.PinName(new Pin { Name = "Steam Client WebHelper", Path = @"C:\Steam\steam.exe", Link = @"C:\l\Steam.lnk" }, nameOf) == "Steam", "a pin saved under a helper name is shown by its shortcut's file name");
            Check(Logic.PinName(new Pin { Name = "Steam Client WebHelper", Path = @"C:\Steam\steam.exe" }, nameOf) == "name:steam.exe", "with no shortcut the name comes from the file");
            Check(Logic.PinName(new Pin { Name = "Мой Steam", Path = @"C:\Steam\steam.exe", Link = @"C:\l\Steam.lnk" }, nameOf) == "Мой Steam", "an ordinary pin name is kept");

            // ── Чьё окно ──
            var steam = new Pin { Name = "Steam", Path = @"C:\Program Files (x86)\Steam\steam.exe", Link = @"C:\l\Steam.lnk" };
            Check(Logic.PinMatches(steam, @"C:\Program Files (x86)\Steam\bin\cef\cef.win64\steamwebhelper.exe"), "Steam's window, held by steamwebhelper.exe, belongs to the Steam pin");
            Check(!Logic.PinMatches(steam, @"C:\Program Files (x86)\Steam\steamapps\common\Game\game.exe") && !Logic.PinMatches(steam, @"C:\Other\steamwebhelper.exe") && !Logic.PinMatches(steam, @"C:\Program Files (x86)\Steam\bin\crashhelper.exe"), "a game in Steam's folder, a helper elsewhere and another program's helper do not");
            var discord = new Pin { Name = "Discord", Path = @"C:\Users\u\AppData\Local\Discord\Update.exe", Args = "--processStart Discord.exe" };
            var group = Logic.Group(new List<Pin> { steam, discord }, new List<WinInfo> { W(1, @"C:\Program Files (x86)\Steam\bin\cef\cef.win64\steamwebhelper.exe"), W(2, @"C:\Tools\term.exe") }, true, nameOf);
            Check(group.Count == 3 && group[0].Running && group[0].Name == "Steam" && !group[1].Running, "the Steam pin shows as running; no separate «WebHelper» icon appears");

            // ── Счётчики непрочитанного ──
            var unread = UnreadInfo.From(Parse("{\"total\":15,\"apps\":{\"discord\":3,\"steam\":12,\"Mail\":1}}"));
            Check(unread != null && unread.Total == 15 && unread.Apps.Count == 3, "kapsula.unread is read");
            Check(Logic.ExeOf(group[1]) == "discord.exe" && unread.For(Logic.ExeOf(group[1])) == 3, "a Discord pin made through Update.exe gets the discord counter");
            Check(Logic.ExeOf(group[0]) == "steam.exe" && unread.For("steam.exe") == 12 && unread.For("steamwebhelper.exe") == 12 && unread.For("Steam.EXE") == 12, "steam.exe and steamwebhelper.exe both get the steam counter");
            Check(unread.For("mail.exe") == 1 && unread.For("term.exe") == 0 && unread.For("") == 0 && unread.For(Logic.ExeOf(group[2])) == 0, "other programs: by their own name or nothing");
            Check(UnreadInfo.Text(0) == null && UnreadInfo.Text(-2) == null && UnreadInfo.Text(1) == "1" && UnreadInfo.Text(9) == "9" && UnreadInfo.Text(10) == "9+" && UnreadInfo.Text(250) == "9+", "badge text: nothing for zero, the number up to nine, «9+» above");
            var nothing = UnreadInfo.From(Parse("{\"total\":0,\"apps\":{\"discord\":0,\"steam\":0}}"));
            Check(nothing != null && UnreadInfo.Text(nothing.For("discord.exe")) == null && UnreadInfo.Text(nothing.For("steam.exe")) == null, "zeros from Kapsula draw no badge");
            Check(UnreadInfo.From(null) == null && UnreadInfo.From("x") == null, "no state — no counters");

            // ── Музыка ──
            var media = MediaInfo.From(Parse("{\"playing\":true,\"title\":\"Midnight City\",\"artist\":\"M83\",\"app\":\"Spotify\"}"));
            Check(media != null && media.Playing && media.Line == "Midnight City — M83" && media.Tip == "Midnight City — M83 · Spotify", "kapsula.media: title — artist, the app in the label");
            var paused = MediaInfo.From(Parse("{\"playing\":false,\"title\":\"Song\",\"artist\":\"\"}"));
            Check(paused != null && !paused.Playing && paused.Line == "Song" && paused.Tip == "Пауза · Song", "paused, no artist, no app");
            Check(MediaInfo.From(null) == null && MediaInfo.From(Parse("{\"playing\":false,\"title\":\"\",\"artist\":\"\"}")) == null && MediaInfo.From(Parse("[1]")) == null, "null, an empty track or a wrong shape — no music widget");

            // ── Погода ──
            var weather = WeatherInfo.From(Parse("{\"temp\":8,\"text\":\"Переменная облачность\",\"icon\":\"cloudy\",\"city\":\"Нью-Йорк\"}"));
            Check(weather != null && weather.TempText == "+8°" && weather.Icon == "cloudy" && weather.Tip == "Переменная облачность · Нью-Йорк", "kapsula.weather as Kapsula sends it");
            Check(WeatherInfo.From(Parse("{\"temp\":-3.4,\"icon\":\"SNOW\"}")).TempText == "−3°" && WeatherInfo.From(Parse("{\"temp\":-3.4,\"icon\":\"SNOW\"}")).Icon == "snow" && WeatherInfo.From(Parse("{\"temp\":0.2}")).TempText == "0°", "temperature is rounded and signed");
            Check(WeatherInfo.From(Parse("{\"temp\":5,\"icon\":\"tornado\"}")).Icon == "cloudy" && WeatherInfo.From(Parse("{\"text\":\"x\"}")) == null && WeatherInfo.From(Parse("{\"temp\":\"8\"}")) == null && WeatherInfo.From(null) == null, "an unknown icon is a cloud; no number — no widget");
            Check(WeatherInfo.Icons.Length == 6, "six weather icons");

            // ── Календарь ──
            var cal = CalendarInfo.From(Parse("{\"events\":[{\"title\":\"Праздник\",\"start\":\"00:00\",\"end\":\"23:59\",\"allDay\":true},{\"title\":\"Созвон\",\"start\":\"14:30\",\"end\":\"15:00\"},{\"title\":\"Напоминание\",\"start\":\"16:00\",\"end\":\"16:00\"},{\"title\":\"A\",\"start\":\"17:00\",\"end\":\"18:00\"},{\"title\":\"B\",\"start\":\"18:00\",\"end\":\"19:00\"},{\"title\":\"C\",\"start\":\"19:00\",\"end\":\"20:00\"},{\"title\":\"D\",\"start\":\"20:00\",\"end\":\"21:00\"}]}"));
            Check(cal != null && cal.Events.Count == 7 && cal.Events[0].AllDay && cal.Events[0].When == "весь день" && !cal.Events[1].AllDay && cal.Events[1].When == "14:30", "kapsula.calendar: an all-day event says «весь день» instead of a time");
            Check(CalendarInfo.Minutes("14:30") == 870 && CalendarInfo.Minutes("7:05") == 425 && CalendarInfo.Minutes("24:00") == -1 && CalendarInfo.Minutes("soon") == -1 && CalendarInfo.Minutes(null) == -1, "HH:mm is read, nonsense is not");
            Check(cal.Remaining(14 * 60 + 5).Count == 7 && cal.Remaining(14 * 60 + 59)[1].Title == "Созвон" && cal.Remaining(15 * 60)[1].Title == "Напоминание", "an event stays until its end");
            Check(cal.Remaining(16 * 60)[1].Title == "Напоминание" && cal.Remaining(16 * 60 + 1)[1].Title == "A", "an event whose start equals its end stays through its own minute");
            Check(cal.Remaining(23 * 60 + 30).Count == 1 && cal.Remaining(23 * 60 + 30)[0].AllDay, "an all-day event stays all day");
            string tip = CalendarInfo.Tip(cal.Remaining(14 * 60 + 5));
            Check(tip.Split('\n').Length == 5 && tip.StartsWith("весь день  Праздник\n14:30–15:00  Созвон\n16:00  Напоминание\n17:00–18:00  A\n"), "the label lists up to five events, got " + tip.Replace("\n", " / "));
            var mirror = CalendarInfo.From(Parse("{\"events\":[{\"title\":\"X\",\"start\":\"00:00\",\"end\":\"23:59\"}]}"));
            Check(mirror.Events[0].AllDay, "00:00–23:59 without the flag is an all-day event too");
            var empty = CalendarInfo.From(Parse("{\"events\":[]}"));
            Check(empty != null && empty.Remaining(0).Count == 0 && CalendarInfo.From(null) == null && CalendarInfo.From(Parse("{\"events\":5}")) == null, "an empty day and no state both leave nothing to show");

            // ── Громкость и батарея ──
            bool muted;
            Check(VolumeMath.Percent(0.404) == 40 && VolumeMath.Percent(1.2) == 100 && VolumeMath.Percent(-1) == 0, "volume in percent");
            Check(Near(VolumeMath.Wheel(0.40, false, 120, out muted), 0.42) && !muted && Near(VolumeMath.Wheel(0.40, false, -240, out muted), 0.36) && !muted, "one wheel notch is two percent");
            Check(Near(VolumeMath.Wheel(0.99, false, 120, out muted), 1) && Near(VolumeMath.Wheel(0.01, false, -120, out muted), 0), "volume stays between 0 and 100");
            Check(Near(VolumeMath.Wheel(0.30, true, 120, out muted), 0.32) && !muted, "wheel up while muted unmutes");
            VolumeMath.Wheel(0.30, true, -120, out muted);
            Check(muted, "wheel down while muted stays muted");
            Check(VolumeMath.Bars(0.5, true) == 0 && VolumeMath.Bars(0, false) == 0 && VolumeMath.Bars(0.2, false) == 1 && VolumeMath.Bars(0.5, false) == 2 && VolumeMath.Bars(0.9, false) == 3, "speaker arcs follow the level");
            Check(VolumeMath.Tip(0.4, false) == "Громкость 40\u00A0%" && VolumeMath.Tip(0.4, true) == "Звук выключен", "volume label");
            var bat = BatteryInfo.From(1, 8, 64);
            Check(bat.Has && bat.Charging && bat.Percent == 64 && bat.Tip == "Заряжается · 64\u00A0%", "a charging battery");
            Check(!BatteryInfo.From(1, 128, 255).Has && !BatteryInfo.From(0, 255, 255).Has && !BatteryInfo.From(1, 128, 100).Has, "a desktop has no battery");
            Check(BatteryInfo.From(0, 0, 17).Tip == "Заряд 17\u00A0%" && BatteryInfo.From(1, 0, 100).Tip == "От сети · 100\u00A0%" && BatteryInfo.From(0, 0, 17).Signature != BatteryInfo.From(0, 0, 18).Signature, "battery label and change detection");

            // ── Какие виджеты в доке ──
            var st = new Store(null);
            Check(string.Join(",", WidgetList.Visible(st.On, true, true, true, true, true).ToArray()) == "music,calendar,weather,volume,battery,clock", "all widgets, the clock last");
            Check(string.Join(",", WidgetList.Visible(st.On, false, false, false, true, false).ToArray()) == "volume,clock", "no hub data and no battery: volume and clock");
            Check(string.Join(",", WidgetList.Visible(st.On, false, false, false, false, false).ToArray()) == "clock", "the clock is always available");
            st.Apply("widgetClock", false);
            st.Apply("widgetMusic", false);
            Check(string.Join(",", WidgetList.Visible(st.On, true, false, true, true, false).ToArray()) == "weather,volume", "a widget switched off in settings is gone");
            var state = DockHub.Widgets(new List<string> { "volume", "clock" }, new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>("Steam", 12) });
            string wjson = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state);
            Check(wjson == "{\"shown\":[\"volume\",\"clock\"],\"badges\":[{\"name\":\"Steam\",\"count\":12,\"text\":\"9+\"}]}", "dock.widgets lists shown widgets and badges, got " + wjson);

            // ── Меню приложений ──
            var found = new List<AppEntry>
            {
                App("Zed", @"C:\Apps\Zed\zed.exe", null),
                App("Блокнот", @"C:\Windows\notepad.exe", null),
                App("Notepad", @"c:\windows\NOTEPAD.EXE", ""),
                App("Uninstall Zed", @"C:\Apps\Zed\unins000.exe", null),
                App("Zed Remover", @"C:\Apps\Zed\Uninstall.exe", null),
                App("Удалить Игру", @"C:\Games\g\setup.exe", "/remove"),
                App("Zed Help", @"C:\Apps\Zed\helpviewer.exe", null),
                App("ReadMe", @"C:\Apps\Zed\notes.exe", null),
                App("Zed Website", @"C:\Apps\Zed\open.exe", "https://zed"),
                App("Справка", @"C:\Apps\q\q.exe", null),
                App("Chrome", @"C:\Chrome\chrome.exe", null),
                App("Почта (веб)", @"C:\Chrome\chrome_proxy.exe", "--app-id=mail"),
                App("Карты (веб)", @"C:\Chrome\chrome_proxy.exe", "--app-id=maps"),
                App("Карты ещё раз", @"C:\Chrome\chrome_proxy.exe", " --app-id=maps "),
                App("Visual Studio Code", @"C:\VS\Code.exe", null),
                App("Helper Tool", @"C:\H\helpertool.exe", null),
                App("Ёлка", @"C:\e\e.exe", null),
                App("", @"C:\x\x.exe", null),
                null
            };
            var clean = AppRules.Clean(found);
            string names = "";
            foreach (var e in clean) names += e.Name + "|";
            Check(names == "Chrome|Helper Tool|Visual Studio Code|Zed|Блокнот|Ёлка|Карты (веб)|Почта (веб)|", "uninstallers, help, readme and website links go; duplicates by target and arguments collapse; sorted, got " + names);
            Check(AppRules.IsJunk("Uninstall", @"C:\a\a.exe") && AppRules.IsJunk("App", @"C:\a\unins001.exe") && AppRules.IsJunk("Read Me First", @"C:\a\a.exe") && AppRules.IsJunk("Сайт программы", @"C:\a\a.exe") && !AppRules.IsJunk("Helper Tool", @"C:\a\a.exe") && !AppRules.IsJunk("Удалённый рабочий стол", @"C:\a\mstsc.exe"), "junk rules look at whole words");
            Func<string, string> search = delegate(string q) { string r = ""; foreach (var e in AppRules.Filter(clean, q)) r += e.Name + "|"; return r; };
            Check(search("") == names && search("   ") == names, "an empty query shows everything");
            Check(search("vis") == "Visual Studio Code|" && search("VIS") == "Visual Studio Code|", "typing filters, case does not matter");
            Check(search("stu") == "Visual Studio Code|" && search("code") == "Visual Studio Code|" && search("st co") == "Visual Studio Code|", "any word start matches, several words too");
            Check(search("hrom") == "Chrome|" && search("локн") == "Блокнот|", "a plain substring matches, in Latin and in Cyrillic");
            Check(search("БЛО") == "Блокнот|" && search("елка") == "Ёлка|" && search("ёлка") == "Ёлка|" && search("веб") == "Карты (веб)|Почта (веб)|", "Russian: case and ё/е do not matter; a word in brackets counts as a word");
            Check(search("c") == "Chrome|Visual Studio Code|" && search("h") == "Helper Tool|Chrome|", "names and words that start with the query come before plain matches");
            Check(search("zzz") == "" && AppRules.Filter(null, "a").Count == 0, "nothing found is an empty list");
            Check(AppRules.Rank("Visual Studio Code", "vis") == 0 && AppRules.Rank("Visual Studio Code", "code") == 1 && AppRules.Rank("Visual Studio Code", "isual") == 2 && AppRules.Rank("Visual Studio Code", "xx") == -1, "ranking: start of name, start of a word, anywhere, nowhere");
            Check(AppRules.Move(0, 10, 4, 1, 0) == 1 && AppRules.Move(0, 10, 4, -1, 0) == 0 && AppRules.Move(1, 10, 4, 0, 1) == 5 && AppRules.Move(5, 10, 4, 0, -1) == 1 && AppRules.Move(1, 10, 4, 0, -1) == 1, "arrows move the selection and stop at the edges");
            Check(AppRules.Move(6, 10, 4, 0, 1) == 9 && AppRules.Move(9, 10, 4, 0, 1) == 9 && AppRules.Move(9, 10, 4, 1, 0) == 9 && AppRules.Move(-1, 10, 4, 0, 1) == 0 && AppRules.Move(0, 0, 4, 1, 0) == -1, "down from above a short last row lands on the last app");
            Check(AppCatalog.Demo().Count == 28, "the demo menu has 28 apps");
        }
    }
}
