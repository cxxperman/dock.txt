// dock.txt — док в духе macOS для Windows, часть dev.txt: закреплённые и запущенные приложения на стеклянной полосе внизу экрана.
// Ключи для проверки:
//   --demo              выдуманные значки; окна, диск, реестр и хаб не трогаются
//   --nohub             вместе с --demo: без выдуманных данных из хаба — только часы, громкость и батарея
//   --shot файл.png     нарисовать док в файл и выйти (обычно вместе с --demo); --hover N — будто курсор над значком N,
//                       --hover music|calendar|weather|volume|battery|clock|apps — над виджетом или кнопкой меню; --menu — с меню этого значка.
//                       Музыка, календарь и погода по умолчанию выключены (их показывает остров kapsula.txt): с --demo
//                       и --hover над одним из них он в снимке включён — каким его видит тот, кто его включил
//                       --previews — вместе с --demo и --hover N: над значком панель с его окнами (окна нарисованные);
//                       --drag N — вместе с --demo: закреплённый значок N будто бы несут на два места правее; --out — унесли из дока
//   --bin full|empty|offline  вместе с --demo: что в выдуманной корзине; --hover bin — курсор над баком
//   --asking N          вместе с --demo: окно значка N просит внимания (под значком оранжевая точка)
//   --apps              вместе с --shot: нарисовать меню приложений; --query текст — с этим текстом в поиске
//   --selftest          проверить сборку значков из окон, закрепление, увеличение, настройки, виджеты, счётчики, названия и поиск в меню,
//                       пункты для трея и клавиши core.txt и уход из-под игры на весь экран; код выхода 0 — всё в порядке
//   --probe [файл]      запуститься по-настоящему, провести по доку выдуманный курсор, записать, что док увидел, и выйти
//   --headless          работать, не показывая окна (для проверок с хабом); --seconds N — выйти через N секунд
//   --testhook          принимать из хаба проверочные команды test.act, test.state и test.quit
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
            // Запуск вместе с Windows пишется в реестр только настоящим доком; в снимках и проверках — только в памяти
            Autostart.Live = !demo && !headless && shot == null;

            bool created = true;
            Mutex mutex = null;
            if (shot == null && !headless) mutex = new Mutex(true, "devtxt_dock_single_instance", out created);   // без окна второй док первому не мешает
            if (!created) return 0;   // док уже на экране

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Ui.Styles(app.Resources);   // общие подсказки и тонкие полосы прокрутки из kit.txt
            // Выключение, перезагрузка, сон и блокировка из меню приложений работают только в настоящем доке
            Session.Armed = !demo && !headless && !probe && shot == null;
            if (shot != null && Array.IndexOf(args, "--apps") >= 0)
            {
                // Меню приложений: выдуманный список или настоящий, собранный прямо сейчас; закреплённые — выдуманные
                // или те, что в state.json (файл только читается)
                List<AppEntry> pinned;
                if (demo) pinned = AppCatalog.DemoPinned();
                else
                {
                    var saved = new Store(stateDir ?? Store.DefaultDir);
                    saved.Load();
                    pinned = AppCatalog.FromDock(Logic.Group(saved.Pins, new List<WinInfo>(), false, Shell.NameOf), true);
                }
                AppMenuView.Shot(shot, demo ? AppCatalog.Demo() : AppCatalog.Scan(), pinned, Arg(args, "--query"));
                return 0;
            }
            var store = new Store(demo ? null : stateDir ?? Store.DefaultDir);
            if (!demo && !store.Load())
            {
                // Первый запуск: начинаем с того, что уже закреплено на панели задач Windows
                store.Pins = Shell.TaskbarPins();
                store.Save();
            }
            // Снимок над музыкой, календарём или погодой: в выдуманном доке этот виджет включён (по умолчанию его нет)
            if (demo && !string.IsNullOrEmpty(overWidget) && Array.IndexOf(Store.IslandKeys, WidgetList.Key(overWidget)) >= 0) store.Flags[WidgetList.Key(overWidget)] = true;
            // Док живёт весь день: случайная ошибка в одном щелчке не должна его ронять — записываем её и работаем дальше
            app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                e.Handled = true;
                try { if (store.Dir != null) File.AppendAllText(Path.Combine(store.Dir, "dock.log"), DateTime.Now.ToString("s") + " " + e.Exception + "\r\n"); } catch { }
            };

            // Прошлый запуск не вернул панель задач (упал или его закрыли силой), а замена выключена — возвращаем сразу.
            // И не отдал своё место внизу экрана — отдаём за него (своё займём заново, когда окно появится).
            if (!demo && !headless && shot == null && !store.ReplaceTaskbar) TaskbarSwap.Recover(store.Dir);
            if (!demo && !headless && shot == null) AppBar.Recover(store.Dir);
            if (!demo && !headless && shot == null)
            {
                // Док падает или сеанс кончается: место внизу экрана освобождается раньше, чем возвращается панель задач
                AppDomain.CurrentDomain.UnhandledException += delegate { WinKey.Stop(); AppBar.Remove(); TaskbarSwap.Give(store.Dir); };
                app.SessionEnding += delegate { WinKey.Stop(); AppBar.Remove(); TaskbarSwap.Give(store.Dir); };
            }
            var w = new DockWindow(demo, store, headless, Array.IndexOf(args, "--nohub") < 0);
            if (demo && Arg(args, "--bin") != null) w.DemoBin(Arg(args, "--bin"));
            int asking;
            if (demo && Arg(args, "--asking") != null && int.TryParse(Arg(args, "--asking"), out asking)) w.DemoAsk(asking);
            if (shot != null)
            {
                // Панель с окнами и перетаскивание рисуются только выдуманными: настоящие окна и закреплённое не трогаются
                int dragged = -1;
                if (!demo || Arg(args, "--drag") == null || !int.TryParse(Arg(args, "--drag"), out dragged)) dragged = -1;
                w.Shot(shot, hover, overWidget, Array.IndexOf(args, "--menu") >= 0, demo && Array.IndexOf(args, "--previews") >= 0, dragged, Array.IndexOf(args, "--out") >= 0);
                return 0;
            }

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
            Check(Logic.Decide(items[1], new IntPtr(3), out target) == Click.Minimize && target == new IntPtr(3), "its only window is already ahead — minimise that very window");
            Check(Logic.Decide(items[0], new IntPtr(2), out target) == Click.Activate && target == new IntPtr(5), "two windows, the top one ahead — go to the other one instead of minimising");
            Check(Logic.Decide(items[0], new IntPtr(5), out target) == Click.Activate && target == new IntPtr(2), "two windows, the lower one ahead — go to the other one");
            var many = new DockItem { Key = "m", Name = "Many" };
            foreach (int h in new int[] { 11, 12, 13 }) many.Windows.Add(new IntPtr(h));
            Check(Logic.Decide(many, new IntPtr(99), out target) == Click.Activate && target == new IntPtr(11), "three windows, another app ahead — the most recent one comes forward");
            string round = "";
            for (int i = 0; i < 4; i++)
            {
                // Каждый щелчок выводит окно вперёд: оно встаёт первым в списке, как сделала бы Windows
                Click c = Logic.Decide(many, many.Windows[0], out target);
                round += (c == Click.Activate ? "" : "?") + target + " ";
                many.Windows.Remove(target);
                many.Windows.Insert(0, target);
            }
            Check(round == "13 12 11 13 ", "clicks walk through all three windows in a circle, got " + round);
            Check(Logic.Shorten("  Короткий  ", 20) == "Короткий" && Logic.Shorten("Очень длинный заголовок окна", 12) == "Очень длинн…" && Logic.Shorten(null, 5) == "" && Logic.Shorten("abc def ghi", 5) == "abc…", "a long window title is cut with «…»");
            var players = new List<DockItem>
            {
                new DockItem { Key = "a", Name = "Браузер", Path = @"C:\b\chrome.exe" },
                new DockItem { Key = "b", Name = "Spotify", Path = @"C:\s\Spotify.exe" },
                new DockItem { Key = "c", Name = "Музыка Яндекс", Path = @"C:\y\ym.exe" }
            };
            Check(Logic.Player(players, "Spotify") == null, "a player with no window cannot be brought forward");
            foreach (DockItem p in players) p.Windows.Add(new IntPtr(1));
            Check(Logic.Player(players, "Spotify") == players[1] && Logic.Player(players, "spotify.exe") == players[1] && Logic.Player(players, "CHROME.EXE") == players[0], "the player is found by its name or its file");
            Check(Logic.Player(players, "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify") == players[1] && Logic.Player(players, "музыка") == players[2], "…and by a longer id that contains it, or a part of its name");
            Check(Logic.Player(players, "") == null && Logic.Player(players, null) == null && Logic.Player(players, "Winamp") == null && Logic.Player(null, "x") == null, "no player in the dock — nothing to bring forward");
            // ── Игра или видео на весь экран: общее правило семьи (FullScreen в Family.cs) и когда док уходит ──
            Check(FullScreen.Is(0, 0, 1920, 1080, 0, 0, 1920, 1080, false, false, false), "a borderless window exactly over its monitor is fullscreen");
            Check(!FullScreen.Is(-8, -8, 1928, 1088, 0, 0, 1920, 1080, true, true, true) && !FullScreen.Is(0, 0, 1920, 1080, 0, 0, 1920, 1080, false, true, false), "a maximised window is not, with a caption or without (Discord, Steam when the taskbar is hidden)");
            Check(!FullScreen.Is(0, 0, 1920, 1040, 0, 0, 1920, 1080, false, false, false) && !FullScreen.Is(0, 0, 1920, 1080, 0, 0, 1920, 1080, false, false, true), "a window that leaves the taskbar visible or has a sizing frame is not");
            Check(FullScreen.Is(1920, 0, 4480, 1440, 1920, 0, 4480, 1440, false, false, false), "a game on the second monitor counts by its own monitor");
            Check(FamilyRules.GameAway(false, false, true, -1, true) && !FamilyRules.GameAway(false, true, false, 100, true), "without core.txt the dock follows its own check and ignores a stale word from core.txt");
            Check(FamilyRules.GameAway(true, true, false, -1, true) && FamilyRules.GameAway(true, true, false, 99999, true) && !FamilyRules.GameAway(true, false, false, 10, true), "with core.txt its core.family.game decides");
            Check(FamilyRules.GameAway(true, false, true, 0, true) && FamilyRules.GameAway(true, false, true, FamilyRules.GameGraceMs - 1, true), "right after the foreground window changed the dock steps aside on its own check, before core.txt has spoken");
            Check(!FamilyRules.GameAway(true, false, true, FamilyRules.GameGraceMs, true) && !FamilyRules.GameAway(true, false, true, -1, true) && FamilyRules.GameGraceMs <= 5000, "after a few seconds only core.txt keeps it away; with no switch seen, its word alone");
            Check(!FamilyRules.GameAway(true, true, true, 0, false) && !FamilyRules.GameAway(true, true, false, -1, false) && !FamilyRules.GameAway(false, false, true, -1, false), "a game on another monitor does not move the dock off the main one — with core.txt or without");
            Check(Win.OnDockScreen(IntPtr.Zero), "no window ahead: the monitor check does not stand in the way, the full-screen rule alone decides");

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

                // ── Перетаскивание: порядок закреплённого ──
                var order = new List<string> { "a", "b", "c", "d" };
                Check(Logic.Move(order, 0, 2) && string.Join("", order.ToArray()) == "bcad", "moving right puts the item at the new place, got " + string.Join("", order.ToArray()));
                Check(Logic.Move(order, 3, 0) && string.Join("", order.ToArray()) == "dbca", "moving left too");
                Check(!Logic.Move(order, 1, 1) && !Logic.Move(order, 9, 0) && !Logic.Move(order, -1, 0) && !Logic.Move<string>(null, 0, 1) && string.Join("", order.ToArray()) == "dbca", "moving nowhere or from nowhere changes nothing");
                Check(Logic.Move(order, 0, 99) && string.Join("", order.ToArray()) == "bcad", "a place past the end is the last place");
                Check(!Logic.DragStarts(0, 0) && !Logic.DragStarts(4, 4) && !Logic.DragStarts(6, 0) && Logic.DragStarts(7, 0) && Logic.DragStarts(-5, 5), "a press becomes a drag only after about six points of movement");
                Check(!Logic.DragOut(72, 48) && Logic.DragOut(73, 48) && !Logic.DragOut(-20, 48), "an icon is dragged out when the cursor is more than 1.5 icon heights above the dock");
                double[] cellL = { 0, 56, 112, 168 }, cellR = { 56, 112, 168, 224 };
                Check(Logic.DropIndex(cellL, cellR, 30, 0, 6) == 0 && Logic.DropIndex(cellL, cellR, 130, 0, 6) == 2 && Logic.DropIndex(cellL, cellR, -50, 2, 6) == 0 && Logic.DropIndex(cellL, cellR, 999, 0, 6) == 3, "the drop place is the cell under the icon, clamped to the pinned ones");
                Check(Logic.DropIndex(cellL, cellR, 115, 1, 6) == 1 && Logic.DropIndex(cellL, cellR, 119, 1, 6) == 2 && Logic.DropIndex(cellL, cellR, 52, 1, 6) == 1 && Logic.DropIndex(cellL, cellR, 49, 1, 6) == 0, "at a cell border the icon keeps its place until the border is clearly crossed");
                Check(Logic.DropIndex(new double[0], new double[0], 5, 0, 6) == -1 && Logic.DropIndex(null, null, 5, 0, 6) == -1, "nothing pinned — nowhere to drop");
                var mv = new Store(dir);
                mv.Pins.Add(new Pin { Name = "A", Path = @"C:\a\a.exe" });
                mv.Pins.Add(new Pin { Name = "B", Path = @"C:\b\b.exe" });
                mv.Pins.Add(new Pin { Name = "C", Path = @"C:\c\c.exe" });
                Check(mv.Move(2, 0) && !mv.Move(1, 1) && !mv.Move(7, 0), "the store moves a pin and refuses a move that changes nothing");
                var mv2 = new Store(dir);
                mv2.Load();
                Check(mv2.Pins.Count == 3 && mv2.Pins[0].Name == "C" && mv2.Pins[1].Name == "A" && mv2.Pins[2].Name == "B", "the new order of pins survives a restart");
                Check(mv2.AutoHide == mv.AutoHide && mv2.Size == mv.Size, "moving a pin keeps the settings");

                // ── Приложения из Магазина: закрепление по идентификатору ──
                const string calc = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
                var storeWins = new List<WinInfo>
                {
                    new WinInfo { Hwnd = new IntPtr(21), Title = "Калькулятор", Path = @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2405.2.0_x64__8wekyb3d8bbwe\CalculatorApp.exe", App = calc, Label = "Калькулятор" },
                    new WinInfo { Hwnd = new IntPtr(22), Title = "Калькулятор", Path = @"C:\Windows\System32\ApplicationFrameHost.exe", App = calc.ToUpperInvariant(), Label = "Калькулятор", Loose = true },
                    new WinInfo { Hwnd = new IntPtr(23), Title = "Unknown", Path = @"C:\Windows\System32\ApplicationFrameHost.exe", Key = "frame|unknown", Label = "Unknown", Loose = true },
                    W(24, @"C:\Tools\term.exe")
                };
                var storeItems = Logic.Group(new List<Pin>(), storeWins, true, nameOf);
                Check(storeItems.Count == 3 && storeItems[0].App == calc && storeItems[0].Windows.Count == 2 && storeItems[0].Name == "Калькулятор" && storeItems[0].CanPin, "windows of a Store app are grouped by its id — a minimised frame included — and it can be pinned");
                Check(storeItems.Count == 3 && storeItems[0].Exe != null && storeItems[0].Exe.EndsWith("CalculatorApp.exe") && Logic.ExeOf(storeItems[0]) == "calculatorapp.exe", "a Store app is known to the unread counters by its real file");
                Check(storeItems.Count == 3 && !storeItems[1].CanPin && storeItems[1].App == null && storeItems[2].App == null && storeItems[2].CanPin, "a frame that names no app still cannot be pinned; ordinary programs are untouched");
                var sp = new Store(dir);
                sp.Load();
                sp.PinItem(storeItems[0]);
                sp.PinItem(storeItems[0]);
                var sp2 = new Store(dir);
                sp2.Load();
                Pin calcPin = sp2.Pins.Find(delegate(Pin p) { return p.App != null; });
                Check(sp2.Pins.Count == 4 && calcPin != null && calcPin.App == calc && calcPin.Path == Logic.AppsFolder + calc && calcPin.Name == "Калькулятор", "a pinned Store app keeps its id and name after a restart, and is pinned once");
                var afterUpdate = new List<WinInfo> { new WinInfo { Hwnd = new IntPtr(31), Title = "Калькулятор", Path = @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_12.0.0.0_x64__8wekyb3d8bbwe\CalculatorApp.exe", App = calc } };
                var pinnedStore = Logic.Group(sp2.Pins, afterUpdate, true, nameOf);
                DockItem calcItem = pinnedStore.Find(delegate(DockItem i) { return i.App == calc; });
                Check(pinnedStore.Count == 4 && calcItem != null && calcItem.Pinned && calcItem.Running && calcItem.Key == storeItems[0].Key && calcItem.Name == "Калькулятор", "the pin finds its window after the app was updated (another folder), and keeps the same place in the dock");
                var idle = Logic.Group(sp2.Pins, new List<WinInfo>(), true, nameOf).Find(delegate(DockItem i) { return i.App == calc; });
                Check(idle != null && !idle.Running && idle.Path == Logic.AppsFolder + calc, "a pinned Store app that is not running is launched by its id");
                sp2.UnpinItem(calcItem);
                var sp3 = new Store(dir);
                sp3.Load();
                Check(sp3.Pins.Count == 3 && !sp3.Pins.Exists(delegate(Pin p) { return p.App != null; }), "unpinning a Store app removes exactly it");
                // Файл прежней версии: о приложениях из Магазина он не знает
                File.WriteAllText(Path.Combine(dir, "state.json"), "{\"pins\":[{\"name\":\"Notes\",\"path\":\"C:\\\\Apps\\\\Notes\\\\notes.exe\"},{\"name\":\"Чат\",\"path\":\"C:\\\\Chat\\\\Update.exe\",\"link\":\"C:\\\\l\\\\Chat.lnk\",\"args\":\"--processStart Chat.exe\",\"icon\":\"C:\\\\Chat\\\\app.ico,0\"}],\"settings\":{\"autoHide\":true,\"size\":40,\"bar\":\"both\"}}");
                var legacy = new Store(dir);
                Check(legacy.Load() && legacy.Pins.Count == 2 && legacy.Pins[0].Path == @"C:\Apps\Notes\notes.exe" && legacy.Pins[0].App == null && legacy.Pins[1].Link == @"C:\l\Chat.lnk" && legacy.Pins[1].Args == "--processStart Chat.exe" && legacy.Pins[1].App == null && legacy.AutoHide && legacy.Size == 40, "a state file written before Store apps reads as before");
                legacy.Save();
                Check(!File.ReadAllText(Path.Combine(dir, "state.json")).Contains("\"app\""), "…and ordinary pins are written back without the new field");
                File.WriteAllText(Path.Combine(dir, "state.json"), "{\"pins\":[{\"name\":\"Calc\",\"app\":\"" + calc + "\"},{\"name\":\"Term\",\"path\":\"shell:AppsFolder\\\\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App\"},{\"name\":\"Empty\"}]}");
                var loose = new Store(dir);
                Check(loose.Load() && loose.Pins.Count == 2 && loose.Pins[0].Path == Logic.AppsFolder + calc && loose.Pins[1].App == "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "a Store pin is understood from its id alone or from its path alone; a pin with neither is dropped");
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
            // ── Значок появляется и уходит: его ячейка растёт от нуля, соседи раздвигаются ──
            double[] cells = Magnify.Cells(new double[] { 56, 0, 56 }, new double[] { 56, 0, 56 }, 56, out from, out to);
            Check(Near(cells[0], 0) && Near(cells[1], 56) && Near(cells[2], 56) && Near(to - from, 112) && !double.IsNaN(from), "a slot that has not grown yet takes no room and breaks nothing");
            cells = Magnify.Cells(new double[] { 56, 28, 56 }, new double[] { 56, 28, 56 }, 0, out from, out to);
            Check(Near(cells[2], 84) && Near(to - from, 140), "a half-grown slot takes half its room");
            cells = Magnify.Cells(new double[] { 0, 0 }, new double[] { 0, 0 }, 5, out from, out to);
            Check(Near(to - from, 0) && !double.IsNaN(cells[0]) && !double.IsNaN(cells[1]), "all slots at zero width lay out without dividing by zero");
            var tw = new Tween(1);
            Check(Near(tw.Value(0), 1) && !tw.Running, "a tween at rest holds its value");
            tw.Go(0.92, 1000, 70);
            Check(tw.Running && Near(tw.Value(1000), 1) && tw.Value(1035) < 0.94 && tw.Value(1035) > 0.92 && Near(tw.Value(1070), 0.92) && !tw.Running, "press: most of the way down in half the time, exactly there at the end");
            tw.Go(1, 2000, 220);
            double mid = tw.Value(2050);
            tw.Go(0.92, 2050, 70);
            Check(Near(tw.Value(2050), mid) && mid > 0.92 && mid < 1, "a tween interrupted half-way continues from where it was, without a jump");
            tw.Set(0.5);
            tw.Go(0.5, 3000, 100);
            Check(!tw.Running && Near(tw.Value(3050), 0.5) && Near(tw.Target, 0.5), "going to where it already is starts nothing");
            Check(Near(Tween.Ease(0), 0) && Near(Tween.Ease(1), 1) && Tween.Ease(0.3) > 0.8 && Tween.Ease(0.2) < Tween.Ease(0.4), "the ease starts fast and brakes softly");
            Check(Near(Tween.Bounce(0, 560), 0) && Near(Tween.Bounce(280, 560), 1) && Tween.Bounce(560, 560) < 1e-9 && Near(Tween.Bounce(840, 560), 1) && Near(Tween.Bounce(-5, 560), 0), "a launch bounce goes up and comes back once per cycle");

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
            Check(list != null && list.Length == 6, "the first group has six settings");
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
                foreach (Dictionary<string, object> it in wlist) { wkeys += it["key"] + " "; Check((string)it["type"] == "switch" && it["value"] is bool && (bool)it["value"] == Store.DefaultOn((string)it["key"]) && ((string)it["label"]).Length > 0 && !it.ContainsKey("section"), "every widget setting is a switch with a label and stays on the dock's own page"); }
                Check(wkeys == "widgetMusic widgetCalendar widgetWeather widgetVolume widgetBattery widgetClock badges ", "widget keys, got " + wkeys);
                Check((string)((Dictionary<string, object>)wlist[6])["label"] == "Счётчики непрочитанного", "the counters switch is named «Счётчики непрочитанного»");
                string offByDefault = "", islandHints = "", fromKapsula = "";
                foreach (Dictionary<string, object> it in wlist)
                {
                    string hint = (string)it["hint"];
                    if (!(bool)it["value"]) offByDefault += it["key"] + " ";
                    if (hint.Contains("То же показывает остров kapsula.txt — включите, если остров спрятан")) islandHints += it["key"] + " ";
                    if (hint.Contains("по данным kapsula.txt")) fromKapsula += it["key"] + " ";
                }
                Check(offByDefault == "widgetMusic widgetCalendar widgetWeather " && islandHints == offByDefault, "music, calendar and weather are off by default and their hints say the island kapsula.txt shows the same; volume, battery, clock and badges stay on, got " + offByDefault + "/ " + islandHints);
                Check(fromKapsula == "widgetMusic widgetCalendar widgetWeather badges ", "the hints still say these widgets need data from kapsula.txt (without it they stay empty), got " + fromKapsula);
            }
            if (list != null && list.Length == 6)
            {
                string keys = "", types = "";
                foreach (Dictionary<string, object> it in list) { keys += it["key"] + " "; types += it["type"] + " "; Check(it.ContainsKey("label") && it.ContainsKey("value"), "every setting has a label and a value"); }
                Check(keys == "autoHide magnify showRunning bin size autostart " && types == "switch switch switch switch slider switch ", "four switches (the bin among them), the size slider and autostart, got " + keys + "/ " + types);
                var size = (Dictionary<string, object>)list[4];
                Check((int)size["min"] == 36 && (int)size["max"] == 72 && (int)size["value"] == 48, "icon size runs from 36 to 72 and starts at 48");
                var auto = (Dictionary<string, object>)list[5];
                Check((string)auto["label"] == "Запускать вместе с Windows" && (string)auto["section"] == "startup" && !(bool)auto["value"], "«Запускать вместе с Windows» goes to «Общие» → «Запуск и обновления» in core.txt and is off by default");
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
            s.Apply("widgetVolume", false);
            s.Apply("badges", false);
            s.Apply("widgetMusic", true);
            Check(!s.On("widgetVolume") && !s.On("badges") && s.On("widgetClock") && s.On("widgetMusic") && !s.On("widgetWeather"), "settings.set switches widgets and the counters off, and an island widget back on");
            refused = false;
            try { s.Apply("widgetClock", 1); } catch { refused = true; }
            Check(refused && s.On("widgetClock"), "a widget switch does not take a number");
            var after = (object[])((Dictionary<string, object>)((object[])s.Describe()["groups"])[2])["items"];
            Check((bool)((Dictionary<string, object>)after[0])["value"] && !(bool)((Dictionary<string, object>)after[2])["value"] && !(bool)((Dictionary<string, object>)after[3])["value"] && !(bool)((Dictionary<string, object>)after[6])["value"] && (bool)((Dictionary<string, object>)after[5])["value"], "the description shows the new widget values");
            string sections = "";
            foreach (object g in (object[])s.Describe()["groups"])
                foreach (Dictionary<string, object> it in (object[])((Dictionary<string, object>)g)["items"]) if (it.ContainsKey("section")) sections += it["key"] + ":" + it["section"] + " ";
            Check(sections == "autostart:startup ", "only autostart moves to a shared page of core.txt, got " + sections);
            // ── Запуск вместе с Windows: в проверке реестр не трогается ──
            Check(!Autostart.Live, "the self-test never writes the Windows registry");
            Check(Autostart.Command(@"C:\Users\u\dev.txt\dock\Dock.exe") == "\"C:\\Users\\u\\dev.txt\\dock\\Dock.exe\"" && Autostart.Name == "Dock" && Autostart.RunKey == @"Software\Microsoft\Windows\CurrentVersion\Run",
                "the Run entry is «Dock» with the full path in quotes — the form core.txt looks for when it moves the program");
            s.Apply("autostart", true);
            Check(Autostart.IsOn() && FirstItem(s, "autostart"), "settings.set autostart=true switches it on and the description shows it");
            refused = false;
            try { s.Apply("autostart", "yes"); } catch { refused = true; }
            Check(refused && Autostart.IsOn(), "the autostart switch does not take text");
            s.Apply("autostart", false);
            Check(!Autostart.IsOn() && !FirstItem(s, "autostart"), "…and off again");
            More();
            var apps = DockHub.Apps(items);
            Check(apps.Length == 5 && (string)((Dictionary<string, object>)apps[0])["name"] == "Notes" && (bool)((Dictionary<string, object>)apps[0])["running"] && !(bool)((Dictionary<string, object>)apps[2])["running"], "dock.apps lists name, path and running");

            Console.WriteLine(failed == 0 ? "all passed" : failed + " failed");
            return failed == 0 ? 0 : 1;
        }

        // Значение переключателя из первой группы описания
        static bool FirstItem(Store s, string key)
        {
            foreach (Dictionary<string, object> it in (object[])((Dictionary<string, object>)((object[])s.Describe()["groups"])[0])["items"])
                if ((string)it["key"] == key) return it["value"] is bool && (bool)it["value"];
            return false;
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
                var fresh = new Store(null);
                Check(!fresh.On("widgetMusic") && !fresh.On("widgetCalendar") && !fresh.On("widgetWeather") && fresh.On("widgetVolume") && fresh.On("widgetBattery") && fresh.On("widgetClock") && fresh.On("badges"), "a new dock starts without music, calendar and weather (the island shows them); volume, battery, clock and badges are on");
                var a = new Store(dir);
                a.Apply("widgetMusic", false);
                a.Apply("badges", false);
                a.Apply("widgetWeather", true);
                var b = new Store(dir);
                Check(b.Load() && !b.On("widgetMusic") && !b.On("badges") && b.On("widgetClock") && b.On("widgetVolume") && b.On("widgetWeather") && !b.IslandMigrated, "widget switches are saved and read back; an island widget switched on stays on");
                File.WriteAllText(Path.Combine(dir, "state.json"), "{\"pins\":[],\"settings\":{\"autoHide\":true,\"size\":40,\"widgetMusic\":true,\"widgetClock\":false}}");
                var old = new Store(dir);
                Check(old.Load() && old.IslandMigrated && old.AutoHide && old.Size == 40 && !old.On("widgetMusic") && !old.On("widgetCalendar") && !old.On("widgetWeather") && !old.On("widgetClock") && old.On("badges") && old.On("widgetVolume"), "a state file from the previous version: music, calendar and weather are switched off once, the rest are kept as they were");
                Check(File.ReadAllText(Path.Combine(dir, "state.json")).Contains("\"" + Store.IslandMark + "\":true"), "the one-time switch-off is marked in state.json right away");
                old.Apply("widgetMusic", true);
                var again = new Store(dir);
                Check(again.Load() && !again.IslandMigrated && again.On("widgetMusic") && !again.On("widgetCalendar") && !again.On("widgetClock"), "after that the person can switch them back on: the next start does not switch them off again");
                File.WriteAllText(Path.Combine(dir, "state.json"), "{\"pins\":[]}");
                var bare = new Store(dir);
                Check(bare.Load() && bare.IslandMigrated && !bare.On("widgetMusic") && bare.On("widgetClock"), "an old file without settings is migrated too");
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
            Check(string.Join(",", WidgetList.Visible(st.On, true, true, true, true, true).ToArray()) == "volume,battery,clock", "by default the dock does not repeat the island: no music, calendar or weather even with kapsula.txt data");
            foreach (string k in Store.IslandKeys) st.Apply(k, true);
            Check(string.Join(",", WidgetList.Visible(st.On, true, true, true, true, true).ToArray()) == "music,calendar,weather,volume,battery,clock", "all widgets switched on, the clock last");
            Check(string.Join(",", WidgetList.Visible(st.On, false, false, false, true, false).ToArray()) == "volume,clock", "no hub data and no battery: volume and clock");
            Check(string.Join(",", WidgetList.Visible(st.On, false, false, false, false, false).ToArray()) == "clock", "the clock is always available");
            st.Apply("widgetClock", false);
            st.Apply("widgetMusic", false);
            Check(string.Join(",", WidgetList.Visible(st.On, true, false, true, true, false).ToArray()) == "weather,volume", "a widget switched off in settings is gone");
            var state = DockHub.Widgets(new List<string> { "volume", "clock" }, new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>("Steam", 12) });
            string wjson = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(state);
            Check(wjson == "{\"shown\":[\"volume\",\"clock\"],\"badges\":[{\"name\":\"Steam\",\"count\":12,\"text\":\"9+\"}]}", "dock.widgets lists shown widgets and badges, got " + wjson);

            // ── Семья: пункты в трее core.txt, горячие клавиши, «Не беспокоить» ──
            int toggled = 0, opened = 0;
            List<TrayItem> tray = FamilyRules.Tray(false, true, delegate { toggled++; }, delegate { opened++; });
            string trayJson = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(tray.ConvertAll<object>(delegate(TrayItem t) { return t.ToData(); }));
            Check(trayJson == "[{\"key\":\"autoHide\",\"label\":\"Автоскрытие\",\"check\":false},{\"key\":\"settings\",\"label\":\"Настройки\"}]", "dock.tray: «Автоскрытие» with a check mark and «Настройки», got " + trayJson);
            Check(tray.Count <= Family.MaxTray && !tray.Exists(delegate(TrayItem t) { return t.Key == "show"; }), "no more than four items and no «Открыть»: the dock has no window of its own");
            bool shared = false;
            foreach (TrayItem t in tray)
            {
                string l = t.Label.ToLowerInvariant();
                if (l.Contains("windows") || l.Contains("обновл") || l.Contains("беспокоить") || l.Contains("core.txt") || l.Contains("выход") || l.Contains("dock")) shared = true;
            }
            Check(!shared, "autostart, updates, do-not-disturb, «Открыть core.txt», exit and the app name are left to core.txt's own menu");
            tray[0].Run();
            tray[1].Run();
            Check(toggled == 1 && opened == 1, "tray.run autoHide toggles auto-hide, tray.run settings opens core.txt");
            Check(FamilyRules.Tray(true, true, null, null)[0].Check == true && FamilyRules.Tray(false, true, null, null)[0].Check == false && FamilyRules.Tray(true, true, null, null)[1].Check == null, "the check mark follows the auto-hide setting; «Настройки» is a plain action");
            List<TrayItem> compact = FamilyRules.Tray(true, false, null, null);
            string compactJson = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(compact.ConvertAll<object>(delegate(TrayItem t) { return t.ToData(); }));
            Check(!compact[0].Enabled && compact[1].Enabled && tray[0].Enabled && compactJson == "[{\"key\":\"autoHide\",\"label\":\"Автоскрытие\",\"check\":true,\"enabled\":false},{\"key\":\"settings\",\"label\":\"Настройки\"}]",
                "with the compact Windows bar the dock is not on screen: «Автоскрытие» is shown but greyed out, «Настройки» still work, got " + compactJson);
            Check(FamilyRules.CorePage == "app:dock", "«Настройки» open the dock's own page in core.txt");
            List<KeyInfo> keys = FamilyRules.Keys(true, true);
            Check(keys.Count == 2 && keys[0].Id == "winKey" && keys[0].Label == "Меню приложений" && keys[0].Keys == "Win" && KeyNames.Normal("win") == keys[0].Keys && keys[0].On, "dock.keys: the Windows key opens «Меню приложений»");
            Check(keys[1].Id == "winDigits" && keys[1].Label == "Закреплённое приложение по номеру" && keys[1].Keys == "Win+1…9" && keys[1].On, "dock.keys: Win+1…9 open the pinned apps by number");
            Check(FamilyRules.Keys(true, false)[1].On && !FamilyRules.Keys(false, true)[1].On, "Win+1…9 work whenever dock.txt replaces the taskbar, even with the Windows key setting off");
            Check(!FamilyRules.Keys(false, true)[0].On && !FamilyRules.Keys(true, false)[0].On && !FamilyRules.Keys(false, false)[0].On, "the Windows key is listed as off unless dock.txt replaces the taskbar and the setting is on");
            string keysJson = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(keys[0].ToData());
            Check(keysJson == "{\"id\":\"winKey\",\"label\":\"Меню приложений\",\"keys\":\"Win\",\"on\":true}", "a key serialises as the protocol shows it, got " + keysJson);
            Check(FamilyRules.BadgePops(true, false) && !FamilyRules.BadgePops(true, true) && !FamilyRules.BadgePops(false, false), "a new badge pops out, but not while «Не беспокоить» is on; a changed number never pops");

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
            // Приложения из Магазина в меню
            Check(AppRules.IsPackaged("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App") && AppRules.IsPackaged("windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel") && AppRules.IsPackaged("Claude_pzs8sxrjxfjjc!Claude"), "a packaged app is known by its id: family_publisher!app");
            Check(!AppRules.IsPackaged(@"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\notepad.exe") && !AppRules.IsPackaged("Microsoft.AutoGenerated.{923DD477-5846-686B-A659-0FCCD73851A8}") && !AppRules.IsPackaged("MSEdge") && !AppRules.IsPackaged(@"C:\Apps\Odd!Name_1\app.exe") && !AppRules.IsPackaged("NoFamily!App") && !AppRules.IsPackaged("Family_pub!") && !AppRules.IsPackaged(null) && !AppRules.IsPackaged(""), "ordinary programs in the apps folder are not packaged apps");
            var packaged = new List<AppEntry>
            {
                new AppEntry { Name = "Параметры", App = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel" },
                new AppEntry { Name = "chrome", App = "Google.Chrome_8wekyb3d8bbwe!App" },
                new AppEntry { Name = "Readme", App = "Some.App_abc!App" },
                new AppEntry { Name = "Not packaged", App = "MSEdge" },
                new AppEntry { Name = "Калькулятор", App = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" },
                new AppEntry { Name = "Калькулятор", App = "Other.Calc_abc!App" },
                null
            };
            var merged = AppRules.Merge(clean, packaged);
            Check(merged.Count == clean.Count + 2 && merged.Exists(delegate(AppEntry e) { return e.Name == "Параметры" && e.App != null; }) && merged.FindAll(delegate(AppEntry e) { return e.Name == "Калькулятор"; }).Count == 1, "Store apps join the menu once each; junk and ordinary programs do not, got " + merged.Count + " from " + clean.Count);
            Check(merged.FindAll(delegate(AppEntry e) { return AppRules.Lower(e.Name) == "chrome"; }).Count == 1 && merged.Find(delegate(AppEntry e) { return AppRules.Lower(e.Name) == "chrome"; }).App == null, "an app that already has a shortcut under the same name is not listed twice — the shortcut wins");
            bool sorted = true;
            for (int i = 1; i < merged.Count; i++) if (string.Compare(merged[i - 1].Name, merged[i].Name, StringComparison.CurrentCultureIgnoreCase) > 0) sorted = false;
            Check(sorted && AppRules.Merge(null, null).Count == 0 && AppRules.Merge(clean, null).Count == clean.Count, "the merged list stays in alphabetical order; nothing to add is fine");
            int[] trim = AppRules.Trim(96, 96, 28, 28, 67, 67), whole = AppRules.Trim(96, 96, 6, 8, 90, 88), edge = AppRules.Trim(96, 96, 0, 0, 39, 20);
            Check(trim[2] == trim[3] && trim[2] >= 40 && trim[2] < 60 && trim[0] <= 28 && trim[0] + trim[2] >= 68 && trim[1] <= 28 && trim[1] + trim[3] >= 68, "an icon with wide empty margins is cropped to a square around the drawing");
            Check(whole[0] == 0 && whole[1] == 0 && whole[2] == 96 && whole[3] == 96, "an icon that fills its picture is left whole");
            Check(edge[0] == 0 && edge[1] == 0 && edge[2] == edge[3] && edge[0] + edge[2] <= 96 && AppRules.Trim(0, 0, 0, 0, 0, 0)[2] == 0 && AppRules.Trim(96, 96, 50, 50, 10, 10)[2] == 96, "the crop never leaves the picture; an empty picture is left alone");

            // ── Панель с окнами программы ──
            Size wide = PreviewRules.Thumb(16 / 9.0), tall = PreviewRules.Thumb(0.5), unknown = PreviewRules.Thumb(0), strip = PreviewRules.Thumb(8);
            Check(wide.Width == 200 && Math.Abs(wide.Height - 112.5) <= 0.5 && tall.Height == PreviewRules.ThumbMaxH && tall.Width == PreviewRules.ThumbMinW && unknown.Width == 200 && Near(unknown.Height, 125) && strip.Height == PreviewRules.ThumbMinH, "a window preview is about 200 wide in the window's proportions; tall and very wide windows stay within limits");
            Rect fitWide = PreviewRules.Fit(2, 200, 132), fitTall = PreviewRules.Fit(0.5, 104, 132);
            Check(Near(fitWide.Width, 200) && Near(fitWide.Height, 100) && Near(fitWide.Y, 16) && Near(fitWide.X, 0) && Near(fitTall.Height, 132) && Near(fitTall.Width, 66) && Near(fitTall.X, 19), "the picture keeps the window's proportions and sits in the middle of its place");
            var cardsA = new List<PreviewCard> { new PreviewCard { Hwnd = new IntPtr(1), Title = "a" }, new PreviewCard { Hwnd = new IntPtr(2), Title = "b" } };
            var cardsB = new List<PreviewCard> { new PreviewCard { Hwnd = new IntPtr(1), Title = "other title" }, new PreviewCard { Hwnd = new IntPtr(2), Title = "b" } };
            var cardsC = new List<PreviewCard> { new PreviewCard { Hwnd = new IntPtr(1), Title = "a" }, new PreviewCard { Hwnd = new IntPtr(2), Title = "b", Minimized = true } };
            Check(PreviewRules.SameWindows(cardsA, cardsB) && !PreviewRules.SameWindows(cardsA, cardsC) && !PreviewRules.SameWindows(cardsA, new List<PreviewCard>()) && !PreviewRules.SameWindows(null, cardsA), "a new title does not rebuild the previews; a closed or minimised window does");
            Check(PreviewRules.MaxWindows == 5 && PreviewRules.OpenMs >= 300 && PreviewRules.OpenMs <= 500 && PreviewRules.CloseMs >= 150 && PreviewRules.CloseMs <= 250, "up to five windows; opens after a rest of about 0.4 s and closes about 0.2 s after the cursor left");
            Check(AppRules.Rank("Visual Studio Code", "vis") == 0 && AppRules.Rank("Visual Studio Code", "code") == 1 && AppRules.Rank("Visual Studio Code", "isual") == 2 && AppRules.Rank("Visual Studio Code", "xx") == -1, "ranking: start of name, start of a word, anywhere, nowhere");
            Check(AppRules.Move(0, 10, 4, 1, 0) == 1 && AppRules.Move(0, 10, 4, -1, 0) == 0 && AppRules.Move(1, 10, 4, 0, 1) == 5 && AppRules.Move(5, 10, 4, 0, -1) == 1 && AppRules.Move(1, 10, 4, 0, -1) == 1, "arrows move the selection and stop at the edges");
            Check(AppRules.Move(6, 10, 4, 0, 1) == 9 && AppRules.Move(9, 10, 4, 0, 1) == 9 && AppRules.Move(9, 10, 4, 1, 0) == 9 && AppRules.Move(-1, 10, 4, 0, 1) == 0 && AppRules.Move(0, 0, 4, 1, 0) == -1, "down from above a short last row lands on the last app");
            Check(AppCatalog.Demo().Count == 28, "the demo menu has 28 apps");
            Check(AppRules.Jump(5, 30, 4, 3, "home") == 0 && AppRules.Jump(5, 30, 4, 3, "end") == 29 && AppRules.Jump(-1, 30, 4, 3, "down") == 0 && AppRules.Jump(3, 0, 4, 3, "end") == -1, "Home and End go to the first and the last app");
            // Два раздела — «Закреплённые» (6) и «Все приложения» (10) — в четыре колонки: ряды 4, 2 | 4, 4, 2
            int[] two = { 6, 10 };
            Check(AppRules.Move(5, two, 4, 1, 0) == 6 && AppRules.Move(6, two, 4, -1, 0) == 5 && AppRules.Move(15, two, 4, 1, 0) == 15, "left and right walk across the section border as one list");
            Check(AppRules.Move(1, two, 4, 0, 1) == 5 && AppRules.Move(3, two, 4, 0, 1) == 5 && AppRules.Move(5, two, 4, 0, 1) == 7 && AppRules.Move(4, two, 4, 0, 1) == 6, "down from pinned keeps the column, a short row takes its last app, then into «Все приложения»");
            Check(AppRules.Move(7, two, 4, 0, -1) == 5 && AppRules.Move(9, two, 4, 0, -1) == 5 && AppRules.Move(2, two, 4, 0, -1) == 2 && AppRules.Move(13, two, 4, 0, 1) == 15 && AppRules.Move(15, two, 4, 0, 1) == 15, "up back into pinned, and the edges stop");
            Check(AppRules.Jump(1, two, 4, 2, "down") == 7 && AppRules.Jump(7, two, 4, 2, "up") == 1 && AppRules.Jump(3, two, 4, 2, "end") == 15 && AppRules.Jump(9, two, 4, 2, "home") == 0, "PageDown, PageUp, Home and End go across sections");
            Check(AppRules.Move(-1, two, 4, 0, 1) == 0 && AppRules.Move(0, new int[] { 0, 0 }, 4, 1, 0) == -1 && AppRules.Jump(0, new int[0], 4, 2, "end") == -1, "nothing selected — the first pinned app; nothing shown — nothing to select");
            bool same = true;
            for (int i = -1; i < 10; i++) for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) if (dx * dy == 0 && AppRules.Move(i, new int[] { 10 }, 4, dx, dy) != AppRules.Move(i, 10, 4, dx, dy)) same = false;
            Check(same, "with one section (a search) the arrows work exactly as before");
            var dockItems = new List<DockItem>
            {
                new DockItem { Key = "a", Name = "Alpha", Path = @"C:\a.exe", Pinned = true },
                new DockItem { Key = "r", Name = "Running", Path = @"C:\r.exe" },
                new DockItem { Key = "b", Name = "Beta", Path = @"C:\b.exe", Pinned = true, Pin = new Pin { Name = "Beta", Path = @"C:\b.exe", Args = "--x" } }
            };
            List<AppEntry> pinnedApps = AppCatalog.FromDock(dockItems, false);
            Check(pinnedApps.Count == 2 && pinnedApps[0].Name == "Alpha" && pinnedApps[1].Name == "Beta" && pinnedApps[1].Args == "--x" && pinnedApps[1].Pinned == dockItems[2], "«Закреплённые» in the apps menu are the dock's pins in the dock's order and launch the way the dock does");
            Check(AppCatalog.DemoPinned().Count == DockWindow.DemoPins.Length, "the demo menu shows the demo dock's pins");
            Check(AppRules.Jump(5, 30, 4, 3, "down") == 17 && AppRules.Jump(17, 30, 4, 3, "up") == 5 && AppRules.Jump(5, 30, 4, 3, "up") == 1 && AppRules.Jump(22, 30, 4, 3, "down") == 29 && AppRules.Jump(27, 30, 4, 3, "down") == 29, "PageUp and PageDown move by a screen of rows, keep the column and stop at the ends");

            // ── Питание в меню приложений ──
            string exe, args;
            Check(string.Join(",", SessionRules.Kinds) == "shutdown,restart,sleep,lock", "four power buttons");
            Check(SessionRules.Command("shutdown", out exe, out args) && exe == "shutdown.exe" && args == "/s /t 0", "shutdown is shutdown.exe /s /t 0");
            Check(SessionRules.Command("restart", out exe, out args) && exe == "shutdown.exe" && args == "/r /t 0", "restart is shutdown.exe /r /t 0");
            Check(SessionRules.Command("sleep", out exe, out args) && exe == "rundll32.exe" && args == "powrprof.dll,SetSuspendState 0,1,0", "sleep goes through powrprof");
            Check(SessionRules.Command("lock", out exe, out args) && exe == null && !SessionRules.Command("format", out exe, out args), "lock needs no program; an unknown command is refused");
            Check(SessionRules.Confirm("shutdown") == "Точно выключить?" && SessionRules.Confirm("restart") == "Точно перезагрузить?" && SessionRules.Confirm("sleep") == null && SessionRules.Confirm("lock") == null, "only shutdown and restart ask twice");
            Check(!Session.Armed && !Session.Run("lock") && !Session.Run("shutdown"), "a self-test never locks, sleeps or shuts down the computer");
            Bin();
            Habits();
        }

        // Док по привычкам Windows: своё место внизу экрана, появление от края, Win+1…9, сообщения об окнах,
        // «просит внимания», файлы на значке программы
        static void Habits()
        {
            // ── Своё место (AppBar) ──
            Check(AppBarRules.Wanted(true, true, false, true), "a visible dock that replaces the taskbar takes its place at the bottom");
            Check(!AppBarRules.Wanted(true, true, true, true), "with auto-hide it takes no place at all");
            Check(!AppBarRules.Wanted(true, false, false, true), "next to the Windows taskbar (or with the compact one) it takes none either");
            Check(!AppBarRules.Wanted(false, true, false, true), "a snapshot, --demo or --headless never registers anything");
            Check(!AppBarRules.Wanted(true, true, false, false), "not before the Windows taskbar is hidden (Take first, then the band): otherwise it lands above the place the taskbar still holds");
            Check(AppBarRules.RecheckMs >= 200 && AppBarRules.RecheckMs <= 500 && AppBarRules.RecheckTries * AppBarRules.RecheckMs >= 2000 && AppBarRules.RecheckTries * AppBarRules.RecheckMs <= 4000, "the taskbar's auto-hide is asked again and rechecked every " + AppBarRules.RecheckMs + " ms for 2–4 s");
            // Числа с живой машины: экран 3440×1440, 100 %, панель задач 0,1368–3440,1440 спрятана, а автоскрытие Windows не включила
            PxRect wide = new PxRect(0, 0, 3440, 1440), held = new PxRect(0, 1368, 3440, 1440);
            int reserved = AppBarRules.Reserved(wide, held, false), missing = AppBarRules.Missing(77, reserved);
            PxRect small = AppBarRules.Settle(AppBarRules.Ask(wide, missing, reserved), wide, missing);
            int bottom = AppBarRules.BottomPx(wide, small.B, reserved);
            Check(reserved == 72 && missing == 5, "the hidden taskbar without auto-hide still holds 72 px: the dock takes only the missing 5 px, got " + reserved + " and " + missing);
            Check(small.Same(new PxRect(0, 1363, 3440, 1368)), "the 5 px band sits right above the taskbar's place, so the work area ends at 1440 − 77 = 1363, got " + small);
            Check(bottom == 1440 && bottom - 77 == small.T, "the dock stands at the very bottom, 1363–1440, over the taskbar's place, not above an empty strip; got bottom " + bottom);
            Check(AppBarRules.BottomPx(wide, 1368, 72) == 1440 && AppBarRules.BottomPx(wide, 1367, 72) == 1440, "with no band of its own (auto-hide, or the taskbar holds enough) the dock still goes down over the taskbar's place");
            int on = AppBarRules.Reserved(wide, held, true);
            PxRect fullBand = AppBarRules.Settle(AppBarRules.Ask(wide, AppBarRules.Missing(77, on), on), wide, AppBarRules.Missing(77, on));
            Check(on == 0 && fullBand.Same(new PxRect(0, 1363, 3440, 1440)) && AppBarRules.BottomPx(wide, fullBand.B, on) == 1440, "once auto-hide is on: the full 77 px at the very bottom, got " + fullBand);
            Check(AppBarRules.Missing(77, 100) == 0 && AppBarRules.Missing(77, -5) == 77, "a taskbar that holds more than the dock: no band at all; nonsense counts as nothing");
            Check(AppBarRules.Reserved(wide, new PxRect(0, 0, 3440, 72), false) == 0 && AppBarRules.Reserved(wide, new PxRect(0, 0, 72, 1440), false) == 0 && AppBarRules.Reserved(wide, new PxRect(-1920, 1008, 0, 1080), false) == 0 && AppBarRules.Reserved(wide, new PxRect(), false) == 0,
                "a taskbar at the top, at a side, on another monitor or not found holds nothing at our bottom");
            PxRect hi = new PxRect(0, 0, 2880, 1620);   // 150 %: панель 72 × 1,5 = 108, док 77 × 1,5 → 116
            int r150 = AppBarRules.Reserved(hi, new PxRect(0, 1512, 2880, 1620), false), m150 = AppBarRules.Missing(AppBarRules.Height(77, 1.5), r150);
            PxRect b150 = AppBarRules.Settle(AppBarRules.Ask(hi, m150, r150), hi, m150);
            Check(r150 == 108 && m150 == 8 && b150.Same(new PxRect(0, 1504, 2880, 1512)) && AppBarRules.BottomPx(hi, b150.B, r150) == 1620, "at 150 %: 108 held, 8 px band at 1504–1512, the dock at 1504–1620; got " + b150);
            // Числа с живой машины 0.7.1: компактная панель Windows (TabletPostureTaskbar = 1) свёрнута и держит 24 точки
            // (рабочая область 0,0–3440,1416), хотя её окно — 0,1368–3440,1440, автоскрытия нет. Док по окну панели считал
            // 72, просил полосу 1363–1368 посреди рабочей области (Windows её не учитывает) и вставал над полосой в 24 точки.
            PxRect work24 = new PxRect(0, 0, 3440, 1416);
            int old72 = AppBarRules.Reserved(wide, held, false);
            int bad = AppBarRules.BottomPx(wide, work24.B, old72);
            Check(old72 == 72 && bad == 1416, "the old count: 72 from the taskbar's window, so the dock stood at 1416 above an empty 24 px strip; got " + old72 + ", " + bad);
            int h24 = AppBarRules.Held(wide, work24, false, new PxRect()), r24 = AppBarRules.Reserved(wide, held, false, h24), m24 = AppBarRules.Missing(77, r24);
            PxRect b24 = AppBarRules.Settle(AppBarRules.Ask(wide, m24, r24), wide, m24);
            int bottom24 = AppBarRules.BottomPx(wide, b24.B, r24);
            Check(h24 == 24 && r24 == 24 && m24 == 53, "the compact taskbar holds 24 px by the work area: the dock takes the missing 53, got " + h24 + ", " + r24 + ", " + m24);
            Check(b24.Same(new PxRect(0, 1363, 3440, 1416)) && bottom24 == 1440 && bottom24 - 77 == b24.T, "the 53 px band is 1363–1416, the dock stands at 1363–1440 at the very bottom, the work area ends at its top; got " + b24 + ", bottom " + bottom24);
            Check(AppBarRules.BottomPx(wide, work24.B, r24) == 1440, "before the band is taken (or with auto-hide) the dock already goes down to 1440 over the compact taskbar");
            PxRect work1363 = new PxRect(0, 0, 3440, 1363);
            Check(AppBarRules.Held(wide, work1363, true, b24) == 24 && AppBarRules.Reserved(wide, held, false, AppBarRules.Held(wide, work1363, true, b24)) == 24, "with the band taken (work area 1363) it still counts 24 below the band: nothing moves");
            Check(AppBarRules.Held(wide, work24, true, new PxRect(0, 1363, 3440, 1368)) == 24, "a stale band in the middle of the work area (Windows ignores it) counts nothing: 24 by the work area, the band is asked again");
            Check(AppBarRules.Held(wide, new PxRect(0, 0, 3440, 1368), false, new PxRect()) == 72 && AppBarRules.Reserved(wide, held, false, 72) == 72, "the full taskbar that holds its whole 72 px still counts 72");
            Check(AppBarRules.Held(wide, new PxRect(0, 0, 3440, 1363), true, new PxRect(0, 1363, 3440, 1440)) == 0 && AppBarRules.Reserved(wide, held, true, 24) == 0, "the full band at the bottom or a taskbar with auto-hide: nothing held");
            Check(AppBarRules.Held(wide, new PxRect(), false, new PxRect()) == 0 && AppBarRules.Reserved(wide, held, false, -3) == 0, "no work area known: nothing held; nonsense counts as nothing");
            // Виден ли док: при «dock» без игры на весь экран окно спрятанным не остаётся
            Check(!Presence.Away(false, "dock") && !Presence.Away(false, "both") && Presence.Away(false, "modern") && Presence.Away(true, "dock"), "the dock goes away only for a full-screen game or the compact Windows taskbar");
            Check(Presence.Reshow(true, Presence.Away(false, "dock"), false), "bar=dock, auto-hide off, no game, the window hidden: shown again");
            Check(!Presence.Reshow(true, false, true) && !Presence.Reshow(true, true, false) && !Presence.Reshow(false, false, false), "already visible, away, or no real window (--demo, --headless): left alone");
            // Компактная панель Windows: нужна только в «modern»; при «dock» выключается, при «both» — как было до дока
            Check(CompactRules.Want("modern", -1) == 1 && CompactRules.Want("modern", 0) == 1, "modern turns the compact taskbar on");
            Check(CompactRules.Want("dock", 1) == 0 && CompactRules.Want("dock", -1) == 0, "dock turns it off even when Windows had it on before (it holds 24 px and refuses auto-hide)");
            Check(CompactRules.Want("both", 1) == 1 && CompactRules.Want("both", 0) == 0 && CompactRules.Want("both", -1) == -1, "both gives back what was there before the dock; nothing recorded — left alone");
            Check(AppBarRules.BottomPx(wide, 1300, 72) == 1300 && AppBarRules.BottomPx(wide, 1368, 0) == 1368, "another bar between the dock and the taskbar's place, or nothing held: the dock stays where it was");
            Check(AppBarRules.Height(77, 1) == 77 && AppBarRules.Height(77, 1.25) == 97 && AppBarRules.Height(77, 1.5) == 116 && AppBarRules.Height(77, 2) == 154, "the band is the dock at rest times the screen scale, rounded up: 77 → 77, 97, 116, 154");
            Check(AppBarRules.Height(76.8, 1.25) == 96 && AppBarRules.Height(0, 1) == 0 && AppBarRules.Height(77, 0) == 77 && AppBarRules.Height(77, double.NaN) == 77, "a whole number of pixels is not rounded up once more; no scale is 100 %");
            PxRect mon = new PxRect(0, 0, 1920, 1080), second = new PxRect(-2560, -200, 0, 1240);
            PxRect ask = AppBarRules.Propose(mon, 77);
            Check(ask.Same(new PxRect(0, 1003, 1920, 1080)), "proposed: the full width of the monitor at its bottom, got " + ask);
            Check(AppBarRules.Propose(second, 116).Same(new PxRect(-2560, 1124, 0, 1240)), "a monitor left of and above the main one keeps its own coordinates");
            Check(AppBarRules.Propose(mon, 5000).Same(new PxRect(0, 0, 1920, 1080)), "never taller than the monitor");
            Check(AppBarRules.Settle(new PxRect(0, 955, 1920, 1032), mon, 77).Same(new PxRect(0, 955, 1920, 1032)), "Windows lifted it over another bar on the same edge: our height from the bottom it gave");
            Check(AppBarRules.Settle(new PxRect(0, 1003, 1920, 1080), mon, 77).Same(ask) && AppBarRules.Settle(new PxRect(60, 1003, 1920, 1100), mon, 77).Same(new PxRect(60, 1003, 1920, 1080)), "nothing in the way — the place asked for; a side bar narrows it; never below the monitor");
            Check(AppBarRules.Settle(new PxRect(0, -50, 1920, 10), mon, 77).Same(new PxRect(0, 0, 1920, 77)) && AppBarRules.Settle(new PxRect(500, 1003, 400, 1080), mon, 77).Same(ask), "an answer that makes no sense is put back on the monitor");
            Check(Near(AppBarRules.BottomDip(new PxRect(0, 1003, 1920, 1080), 1), 1080) && Near(AppBarRules.BottomDip(new PxRect(0, 1044, 2880, 1160), 1.5), 773.3333333333334) && Near(AppBarRules.BottomDip(new PxRect(0, 1, 2, 1080), 0), 1080), "the dock stands on the bottom of its band, in WPF points");

            // ── Появление от края ──
            var edge = new EdgeReveal();
            Check(EdgeReveal.DwellMs >= 300 && EdgeReveal.DwellMs <= 400, "the cursor has to rest at the edge 0.3–0.4 s");
            bool shown = false;
            for (long t = 0; t <= 300; t += 50) shown |= edge.Step(t, true, false);
            Check(!shown && edge.Step(350, true, false), "resting at the edge: nothing for 0.3 s, then at 0.35 s the dock comes out");
            edge = new EdgeReveal();
            shown = false;
            long[] path = { 0, 50, 100, 150, 200, 250, 300, 350, 400, 450, 500 };
            bool[] near = { true, true, true, false, true, true, true, true, false, true, true };   // курсор ездит к прокрутке и обратно
            for (int i = 0; i < path.Length; i++) shown |= edge.Step(path[i], near[i], false);
            Check(!shown, "passing the edge on the way to a scroll bar or a button at the bottom of a window never brings the dock out");
            edge = new EdgeReveal();
            shown = false;
            for (long t = 0; t <= 5000; t += 50) shown |= edge.Step(t, true, true);
            Check(!shown, "a game or a video on the full screen: the cursor may rest at the bottom for seconds, the dock stays away");
            edge = new EdgeReveal();
            edge.Step(0, true, false); edge.Step(300, true, false);
            Check(!edge.Step(320, true, true) && !edge.Step(400, true, false) && edge.Step(750, true, false), "a game that came in the middle starts the wait over");
            Check(edge.Step(0, true, false) == false && edge.Step(400, true, false), "after coming out the next rest is counted afresh");

            // ── Win+1…9 ──
            Check(WinDigit.Index(0x31, false, false, false) == 0 && WinDigit.Index(0x39, false, false, false) == 8 && WinDigit.Index(0x35, false, false, false) == 4, "Win+1 is the first pin, Win+9 the ninth");
            Check(WinDigit.Index(0x30, false, false, false) == -1 && WinDigit.Index(0x61, false, false, false) == -1 && WinDigit.Index(0x45, false, false, false) == -1, "Win+0, the number pad and Win+E stay with Windows");
            Check(WinDigit.Index(0x31, true, false, false) == -1 && WinDigit.Index(0x31, false, true, false) == -1 && WinDigit.Index(0x31, false, false, true) == -1, "with Shift, Ctrl or Alt the combination stays with Windows");
            var dock = new List<DockItem>
            {
                new DockItem { Key = "a", Name = "A", Path = @"C:\a.exe", Pinned = true },
                new DockItem { Key = "b", Name = "B", Path = @"C:\b.exe", Pinned = true },
                new DockItem { Key = "c", Name = "C", Path = @"C:\c.exe", Pinned = true },
                new DockItem { Key = "r", Name = "R", Path = @"C:\r.exe" }
            };
            dock[1].Windows.Add(new IntPtr(21));
            dock[2].Windows.Add(new IntPtr(31)); dock[2].Windows.Add(new IntPtr(32));
            dock[3].Windows.Add(new IntPtr(41));
            DockItem got;
            Click what;
            IntPtr target;
            Check(WinDigit.Decide(dock, 0, new IntPtr(41), out got, out what, out target) && got == dock[0] && what == Click.Launch, "Win+1, not running: launch");
            Check(WinDigit.Decide(dock, 1, new IntPtr(41), out got, out what, out target) && got == dock[1] && what == Click.Activate && target == new IntPtr(21), "Win+2, running behind another window: bring it forward");
            Check(WinDigit.Decide(dock, 1, new IntPtr(21), out got, out what, out target) && what == Click.Minimize && target == new IntPtr(21), "Win+2 again, its only window ahead: minimise it");
            Check(WinDigit.Decide(dock, 2, new IntPtr(31), out got, out what, out target) && what == Click.Activate && target == new IntPtr(32), "Win+3 with two windows, one ahead: the next window");
            Check(!WinDigit.Decide(dock, 3, IntPtr.Zero, out got, out what, out target) && got == null && !WinDigit.Decide(dock, 8, IntPtr.Zero, out got, out what, out target) && !WinDigit.Decide(dock, -1, IntPtr.Zero, out got, out what, out target) && !WinDigit.Decide(null, 0, IntPtr.Zero, out got, out what, out target),
                "only pinned apps count: Win+4 with three pins does nothing (the running app is not numbered)");

            // ── Сообщения Windows об окнах ──
            var batch = new ShellBatch();
            Check(!batch.Pending && batch.Add(ShellCodes.Created, new IntPtr(1), 1000) == 1000 + ShellBatch.QuickMs && batch.Pending, "a new window: the batch is read 120 ms later");
            Check(batch.Add(ShellCodes.Redraw, new IntPtr(1), 1010) == 1120 && batch.Add(ShellCodes.Activated, new IntPtr(1), 1050) == 1120, "later messages never push the moment away");
            Check(batch.Add(7, new IntPtr(1), 1060) == -1 && batch.Add(ShellCodes.Activated | 0x100, IntPtr.Zero, 1060) == -1 && batch.Due == 1120, "messages the dock does not need are ignored");
            ShellSummary sum = batch.Take();
            Check(sum.Windows && sum.Titles && sum.Attention.Count == 1 && !sum.Attention[0].Value && !batch.Pending, "one batch: windows re-read once, titles too, the activated window loses its request");
            Check(batch.Add(ShellCodes.Redraw, new IntPtr(5), 2000) == 2000 + ShellBatch.TitleMs && batch.Add(ShellCodes.Redraw, new IntPtr(6), 2500) == 3000, "only titles changing — at most once a second");
            Check(batch.Add(ShellCodes.Destroyed, new IntPtr(5), 2600) == 2720, "…but a closed window brings the moment closer");
            sum = batch.Take();
            Check(sum.Windows && sum.Gone.Count == 1 && sum.Gone[0] == new IntPtr(5), "a closed window is in the batch");
            for (int i = 0; i < 40; i++) batch.Add(i % 2 == 0 ? ShellCodes.Created : ShellCodes.RudeActivated, new IntPtr(100 + i), 3000 + i);
            batch.Add(ShellCodes.Flash, new IntPtr(7), 3050);
            batch.Add(ShellCodes.MonitorChanged, new IntPtr(7), 3060);
            sum = batch.Take();
            Check(sum.Windows && !sum.Titles && sum.Attention.Count == 21 && sum.Attention[20].Key == new IntPtr(7) && sum.Attention[20].Value, "forty messages from a starting program make one re-read; the request for attention keeps its order");
            Check(ShellCodes.Flash == 0x8006 && ShellCodes.RudeActivated == 0x8004 && ShellCodes.Replaced == 13 && ShellCodes.MonitorChanged == 16, "the codes are those of HSHELL_FLASH, HSHELL_RUDEAPPACTIVATED, HSHELL_WINDOWREPLACED, HSHELL_MONITORCHANGED");

            // ── Просит внимания ──
            var att = new Attention();
            var chat = new DockItem { Key = "chat", Name = "Chat", Path = @"C:\chat.exe" };
            chat.Windows.Add(new IntPtr(70)); chat.Windows.Add(new IntPtr(71));
            Check(att.Since(chat) == -1 && Attention.Phase(-1, 0, false, 560) == AttentionPhase.None, "nobody asks: nothing to show");
            Check(!att.Flash(new IntPtr(70), new IntPtr(70), 1000) && att.Count == 0, "the window ahead does not ask (Windows does not flash it either)");
            Check(att.Flash(new IntPtr(71), new IntPtr(1), 1000) && !att.Flash(new IntPtr(71), new IntPtr(1), 1300) && att.Since(chat) == 1000, "a window asks once; asking again does not start the hops over");
            Check(Attention.Hops >= 2 && Attention.Hops <= 3, "two or three hops");
            Check(Attention.Phase(att.Since(chat), 1000, false, 560) == AttentionPhase.Bounce && Attention.Phase(att.Since(chat), 1000 + 3 * 560 - 1, false, 560) == AttentionPhase.Bounce, "first the icon hops");
            Check(Attention.Phase(att.Since(chat), 1000 + 3 * 560, false, 560) == AttentionPhase.Mark && Attention.Phase(att.Since(chat), 99999, false, 560) == AttentionPhase.Mark, "then the orange dot stays");
            Check(Attention.Phase(att.Since(chat), 1000, true, 560) == AttentionPhase.Mark, "with «Меньше движения» (or «Не беспокоить») — no hops, the dot at once");
            Check(att.Clear(new IntPtr(71)) && att.Since(chat) == -1 && !att.Clear(new IntPtr(71)), "the window came forward: the request is gone");
            att.Flash(new IntPtr(70), IntPtr.Zero, 5); att.Flash(new IntPtr(99), IntPtr.Zero, 6);
            att.Keep(delegate(IntPtr h) { return h != new IntPtr(99); });
            Check(att.Count == 1 && att.Since(chat) == 5, "a closed window no longer asks");
            Check(att.Since(null) == -1 && !att.Flash(IntPtr.Zero, IntPtr.Zero, 0), "no window — no request");

            // ── Файлы на значке программы ──
            Func<string, bool> real = delegate(string p) { return p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !p.Contains("gone"); };
            var paint = new DockItem { Key = "p", Name = "Paint", Path = @"C:\Windows\paint.exe", Pinned = true, Pin = new Pin { Name = "Paint", Path = @"C:\Windows\paint.exe" } };
            var pinArgs = new DockItem { Key = "e", Name = "Edge", Path = @"C:\e\msedge.exe", Pinned = true, Pin = new Pin { Name = "Edge", Path = @"C:\e\msedge.exe", Args = "--profile-directory=Default" } };
            var squirrel = new DockItem { Key = "d", Name = "Discord", Path = @"C:\Users\u\AppData\Local\Discord\Update.exe", Pinned = true, Pin = new Pin { Name = "Discord", Path = @"C:\Users\u\AppData\Local\Discord\Update.exe", Args = "--processStart Discord.exe" } };
            var store = new DockItem { Key = "app|calc", Name = "Калькулятор", Path = Logic.AppsFolder + "Calc!App", App = "Calc!App" };
            Check(OpenWith.Accepts(paint, real) && OpenWith.Accepts(pinArgs, real), "an ordinary program takes files dropped on its icon");
            Check(!OpenWith.Accepts(store, real) && !OpenWith.Accepts(squirrel, real) && !OpenWith.Accepts(new DockItem { Path = @"C:\gone\x.exe" }, real) && !OpenWith.Accepts(new DockItem { Path = @"C:\x.lnk" }, real) && !OpenWith.Accepts(null, real),
                "a Store app, a program started through Update.exe, a missing file or not a program — no");
            var files = new List<string> { @"C:\Фото\кот.png", @"D:\a b\c.txt" };
            Check(OpenWith.Arguments(null, files) == "\"C:\\Фото\\кот.png\" \"D:\\a b\\c.txt\"", "the paths go after the program, each in quotes, got " + OpenWith.Arguments(null, files));
            Check(OpenWith.Arguments(" --profile-directory=Default ", files) == "--profile-directory=Default \"C:\\Фото\\кот.png\" \"D:\\a b\\c.txt\"", "the shortcut's own parameters come first");
            Check(OpenWith.Arguments(null, new List<string> { "C:\\bad\"name.txt", "" }) == null && OpenWith.Arguments(null, null) == null && OpenWith.Arguments("--x", new List<string>()) == null, "nothing usable — nothing is started");
            var many = new List<string>();
            for (int i = 0; i < 2000; i++) many.Add(@"C:\very\long\folder\name\for\testing\file-" + i + ".txt");
            string longArgs = OpenWith.Arguments(null, many);
            Check(longArgs != null && longArgs.Length <= OpenWith.MaxArgs && longArgs.EndsWith(".txt\""), "too many files: as many as fit into a Windows command line, none cut in half");
            Check(OpenWith.Tip("Paint") == "Открыть в «Paint»" && OpenWith.Failed == "Не удалось открыть", "the label over the icon: «Открыть в «Paint»»");
            List<string> picked = BinRules.DropPaths(new string[] { @"C:\keep\a.txt", @"C:\" }, delegate(string p) { return true; });
            Check(picked.Count == 1 && OpenWith.Arguments(null, picked) == "\"C:\\keep\\a.txt\"", "the same check as for the bin: full paths that exist, never a drive root");
        }

        static string Menu(List<BinRow> rows)
        {
            string r = "";
            foreach (BinRow b in rows) r += b.Line ? "— | " : b.Label + (b.Act == null ? "" : b.Enabled ? "" : " (off)") + " | ";
            return r;
        }

        // Корзина metla.txt в доке
        static void Bin()
        {
            // ── Спрайт: те же строки, что в kit.txt (glyphs), полный — когда что-то лежит ──
            Check(BinRules.Empty.Length == 12 && BinRules.Full.Length == 12 && Array.TrueForAll(BinRules.Empty, delegate(string r) { return r.Length == 12; }) && Array.TrueForAll(BinRules.Full, delegate(string r) { return r.Length == 12; }), "bin sprites are 12×12");
            Check(BinRules.Empty[0] == "....####...." && BinRules.Empty[5] == ".##E####E##." && BinRules.Full[0] == "...##......." && BinRules.Full[11] == "..########..", "the sprites are the kit.txt glyphs «bin» and «bin-full»");
            Check(BinRules.Sprite(0, false) == BinRules.Empty && BinRules.Sprite(1, false) == BinRules.Full && BinRules.Sprite(500, false) == BinRules.Full && BinRules.Sprite(-1, false) == BinRules.Empty, "an empty bin has its lid shut, anything inside lifts it");
            Check(BinRules.Sprite(0, true) == BinRules.Full, "files carried over the empty bin lift the lid too");
            // ── Размер и склонения ──
            Check(BinRules.Size(0) == "0\u00A0Б" && BinRules.Size(512) == "512\u00A0Б" && BinRules.Size(12 * 1024) == "12\u00A0КБ" && BinRules.Size(3565158) == "3,4\u00A0МБ" && BinRules.Size(500L * 1024 * 1024) == "500\u00A0МБ", "bytes, kilobytes, megabytes, got " + BinRules.Size(3565158));
            Check(BinRules.Size(5 * BinRules.GB) == "5\u00A0ГБ" && BinRules.Size(BinRules.GB * 5 / 4) == "1,25\u00A0ГБ" && BinRules.Size(BinRules.GB * 124 / 10) == "12,4\u00A0ГБ" && BinRules.Size(BinRules.GB * 250) == "250\u00A0ГБ", "gigabytes the way metla.txt writes them, got " + BinRules.Size(BinRules.GB * 124 / 10));
            Check(BinRules.Objects(0) == "0\u00A0объектов" && BinRules.Objects(1) == "1\u00A0объект" && BinRules.Objects(2) == "2\u00A0объекта" && BinRules.Objects(5) == "5\u00A0объектов", "0, 1, 2, 5 objects");
            Check(BinRules.Objects(11) == "11\u00A0объектов" && BinRules.Objects(21) == "21\u00A0объект" && BinRules.Objects(104) == "104\u00A0объекта" && BinRules.Objects(1234) == "1\u00A0234\u00A0объекта", "11, 21, 104 and a thousand-something");
            // ── Сведение и подпись ──
            BinInfo none = BinInfo.From(Parse("{\"count\":0,\"bytes\":0}")), one = BinInfo.From(Parse("{\"count\":1,\"bytes\":12288}")), two = BinInfo.From(Parse("{\"count\":2,\"bytes\":3565158}")), five = BinInfo.From(Parse("{\"count\":5,\"bytes\":1342177280}"));
            Check(none != null && none.Count == 0 && five != null && five.Count == 5 && five.Bytes == 1342177280, "metla.bin is read");
            Check(BinInfo.From(null) == null && BinInfo.From(Parse("{\"count\":\"3\",\"bytes\":1}")) == null && BinInfo.From(Parse("{\"count\":3}")) == null && BinInfo.From(Parse("[1]")) == null, "no state or a wrong shape — metla.txt is not there");
            Check(BinInfo.From(Parse("{\"count\":3,\"bytes\":1.5E10}")).Bytes == 15000000000L, "big byte counts arrive as whatever number the parser picked");
            Check(BinRules.Summary(none) == "Корзина пуста" && BinRules.Summary(one) == "В корзине 1\u00A0объект, 12\u00A0КБ" && BinRules.Summary(two) == "В корзине 2\u00A0объекта, 3,4\u00A0МБ" && BinRules.Summary(five) == "В корзине 5\u00A0объектов, 1,25\u00A0ГБ" && BinRules.Summary(null) == null,
                "menu summary for 0, 1, 2 and 5 objects, got " + BinRules.Summary(two));
            Check(BinRules.Tip(none) == "Корзина\nпусто" && BinRules.Tip(five) == "Корзина\n5\u00A0объектов · 1,25\u00A0ГБ" && BinRules.Tip(null) == "Корзина\nmetla.txt не запущена", "the label: «Корзина» and under it what is inside, «пусто» or that metla.txt is not running");
            // ── Меню ──
            Check(Menu(BinRules.Menu(five, true)) == "В корзине 5\u00A0объектов, 1,25\u00A0ГБ | Открыть корзину | Очистить корзину… | — | Найти, что занимает место… | Автозапуск Windows… | ", "a full bin with metla.txt online, got " + Menu(BinRules.Menu(five, true)));
            Check(Menu(BinRules.Menu(none, true)) == "Корзина пуста | Открыть корзину | Очистить корзину… (off) | — | Найти, что занимает место… | Автозапуск Windows… | ", "an empty bin: nothing to clean");
            Check(Menu(BinRules.Menu(null, false)) == "Открыть корзину | Очистить корзину… (off) | ", "metla.txt not running: only «Открыть корзину» works, cleaning is greyed out, got " + Menu(BinRules.Menu(null, false)));
            Check(BinRules.Menu(five, true)[0].Act == null && !BinRules.Menu(five, true)[0].Enabled, "the summary row is not clickable");
            Check(BinRules.Page("clean") == "bin" && BinRules.Page("scan") == "scan" && BinRules.Page("startup") == "startup" && BinRules.Page("open") == null, "«Очистить корзину…» opens metla.txt's confirmation (show page bin); the dock never empties the bin itself");
            // ── Когда бак в доке ──
            Check(BinRules.Visible(true, true, false) && BinRules.Visible(true, false, true) && BinRules.Visible(true, true, true), "the bin shows when metla.txt is installed or online and the setting is on");
            Check(!BinRules.Visible(false, true, true) && !BinRules.Visible(true, false, false) && !BinRules.Visible(false, false, false), "no bin with the setting off or without metla.txt");
            var st = new Store(null);
            Check(st.Bin && FirstItem(st, "bin"), "«Корзина в доке» is on by default");
            st.Apply("bin", false);
            Check(!st.Bin && !FirstItem(st, "bin"), "settings.set bin=false switches it off");
            bool refused = false;
            try { st.Apply("bin", "yes"); } catch { refused = true; }
            Check(refused && !st.Bin, "the bin switch does not take text");
            string dir = Path.Combine(Path.GetTempPath(), "dock.txt-test-bin-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            try
            {
                var a = new Store(dir);
                a.Apply("bin", false);
                var b = new Store(dir);
                Check(b.Load() && !b.Bin, "the bin switch survives a restart");
                File.WriteAllText(Path.Combine(dir, "state.json"), "{\"pins\":[],\"settings\":{\"islandWidgets\":true}}");
                var old = new Store(dir);
                Check(old.Load() && old.Bin, "a state file from before the bin: the bin is on");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
            Check(BinRules.MetlaPath.EndsWith(@"\Programs\dev.txt\Metla\Metla.exe"), "metla.txt is looked for where core.txt installs it");
            // ── Перетаскивание: что уйдёт в корзину ──
            Func<string, bool> exists = delegate(string p) { return p.StartsWith(@"C:\keep", StringComparison.OrdinalIgnoreCase); };
            List<string> dropped = BinRules.DropPaths(new string[] { @"C:\keep\a.txt", @"c:\KEEP\A.TXT", @"C:\keep\folder", @"C:\gone\b.txt", "relative.txt", "", null, @"C:\", @"C:\keep\x" + "\0" + "y" }, exists);
            Check(dropped.Count == 2 && dropped[0] == @"C:\keep\a.txt" && dropped[1] == @"C:\keep\folder", "only full paths that exist go to the bin, each once; never a drive root, got " + string.Join(";", dropped.ToArray()));
            Check(BinRules.DropPaths(null, exists).Count == 0 && BinRules.DropPaths(new string[0], exists).Count == 0, "nothing dropped — nothing to do");
            Check(BinRules.FromList(dropped) == "C:\\keep\\a.txt\0C:\\keep\\folder\0", "the list for Windows: paths split by \\0 (the closing second \\0 comes with the string)");
            Check(BinRules.FO_DELETE == 3 && BinRules.RecycleFlags == 0x4040 && (BinRules.RecycleFlags & 0x10) == 0, "delete with undo (to the Recycle Bin) and a warning if something would be deleted for good; Windows is not told to skip its questions");
            Check(!BinRules.Recycle(IntPtr.Zero, new List<string>()) && !BinRules.Recycle(IntPtr.Zero, null), "an empty list never reaches Windows");
        }
    }
}
