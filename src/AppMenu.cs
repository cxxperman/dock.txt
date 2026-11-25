// dock.txt. AppMenu.cs — меню приложений вместо «Пуска»: затемнённый экран, строка поиска и сетка всех программ
// из папок меню «Пуск» и приложения из Магазина. Список и значки собираются в фоновом потоке и остаются в памяти, поэтому меню
// открывается сразу. Какие ярлыки считать приложениями и как искать — в Widgets.cs (AppRules).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DevTxt.DockApp
{
    // Список приложений: ярлыки из меню «Пуск» (общего и этого пользователя, со всеми вложенными папками)
    // и приложения из Магазина, у которых ярлыков нет
    static class AppCatalog
    {
        const int IconPx = 96, FreshSeconds = 60;
        static readonly object gate = new object();
        static readonly List<Action> waiting = new List<Action>();
        static List<AppEntry> cache;
        static bool scanning;
        static DateTime scannedAt = DateTime.MinValue;
        static int links, icons, lastMs, store;

        public static List<AppEntry> Cached { get { lock (gate) return cache; } }

        // Для --probe: только числа
        public static string Summary()
        {
            lock (gate)
                return cache == null ? "start menu: " + (scanning ? "still scanning" : "not scanned")
                    : "start menu: shortcuts " + links + ", apps " + cache.Count + " (from the Store " + store + "), with icons " + icons + ", scanned in " + lastMs + " ms";
        }

        // Собирает список в фоновом потоке, если его ещё нет или он старше минуты. done зовётся в том же фоновом потоке,
        // когда новый список готов; если список свежий, ничего не происходит.
        public static void Refresh(Action done)
        {
            lock (gate)
            {
                if (cache != null && (DateTime.UtcNow - scannedAt).TotalSeconds < FreshSeconds) return;
                if (done != null) waiting.Add(done);
                if (scanning) return;
                scanning = true;
            }
            var t = new Thread(delegate()
            {
                List<AppEntry> list = null;
                try { list = Scan(); } catch { }
                Action[] call;
                lock (gate)
                {
                    if (list != null) { cache = list; scannedAt = DateTime.UtcNow; }
                    scanning = false;
                    call = waiting.ToArray();
                    waiting.Clear();
                }
                foreach (Action a in call) try { a(); } catch { }
            }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "dock.txt apps" };
            t.SetApartmentState(ApartmentState.STA);   // ярлыки читает WScript.Shell, папку приложений — Shell.Application
            t.Start();
        }

        static void Walk(string dir, List<string> files)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.lnk")) files.Add(f);
                foreach (string d in Directory.GetDirectories(dir)) Walk(d, files);
            }
            catch { }   // папка закрыта или исчезла — пропускаем
        }

        // Читает папки меню «Пуск». Только читает. Значки тех же ярлыков берутся из прошлого раза.
        public static List<AppEntry> Scan()
        {
            var watch = Stopwatch.StartNew();
            var files = new List<string>();
            Walk(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), files);
            Walk(Environment.GetFolderPath(Environment.SpecialFolder.Programs), files);
            var found = new List<AppEntry>();
            object shell = null;
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t != null) shell = Activator.CreateInstance(t);
                if (shell != null)
                    foreach (string f in files)
                    {
                        Pin p = Shell.Resolve(t, shell, f);
                        if (p != null) found.Add(new AppEntry { Name = p.Name, Target = p.Path, Args = p.Args, Link = p.Link, Icon = p.Icon });
                    }
            }
            finally
            {
                try { if (shell != null) Marshal.ReleaseComObject(shell); } catch { }
            }
            // Приложения из Магазина (Параметры, Калькулятор, Терминал…): ярлыков у них нет, они в папке приложений Windows
            var packaged = new List<AppEntry>();
            List<KeyValuePair<string, string>> folder = Packaged.List();
            Packaged.Remember(folder);
            foreach (var a in folder)
                if (AppRules.IsPackaged(a.Value)) packaged.Add(new AppEntry { Name = a.Key, App = a.Value, Target = Packaged.Folder + a.Value });
            List<AppEntry> list = AppRules.Merge(AppRules.Clean(found), packaged);
            var old = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            List<AppEntry> before = Cached;
            if (before != null) foreach (AppEntry e in before) old[e.Link + "|" + e.Icon + "|" + e.Target] = e;
            int have = 0;
            foreach (AppEntry e in list)
            {
                AppEntry same;
                if (old.TryGetValue(e.Link + "|" + e.Icon + "|" + e.Target, out same) && same.Image != null) e.Image = same.Image;
                else e.Image = e.App != null ? Packaged.Extract(e.App, IconPx) : Shell.Extract(e.Icon, IconPx) ?? Shell.Extract(e.Target, IconPx);
                if (e.Image != null) have++;
            }
            lock (gate) { links = files.Count; icons = have; store = list.FindAll(delegate(AppEntry e) { return e.App != null; }).Count; lastMs = (int)watch.ElapsedMilliseconds; }
            return list;
        }

        // Выдуманный список для снимков (--demo --apps)
        public static List<AppEntry> Demo()
        {
            string[] names =
            {
                "Aura", "Blender", "Discord", "Figma", "Kapsula", "Notion", "OBS Studio", "Paint", "Spotify", "Steam", "Telegram", "Visual Studio Code",
                "Браузер", "Диспетчер задач", "Заметки", "Калькулятор", "Календарь", "Камера", "Карты", "Музыка", "Ножницы", "Параметры", "Погода", "Почта",
                "Проводник", "Терминал", "Фотографии", "Часы"
            };
            var list = new List<AppEntry>();
            foreach (string n in names) list.Add(new AppEntry { Name = n, Target = @"C:\demo\" + n + ".exe", Link = @"C:\demo\" + n + ".lnk" });
            return AppRules.Clean(list);
        }

        // Выдуманные закреплённые — те же, что в выдуманном доке (--demo)
        public static List<AppEntry> DemoPinned()
        {
            var list = new List<AppEntry>();
            foreach (string n in DockWindow.DemoPins) list.Add(new AppEntry { Name = n, Target = @"C:\demo\" + n + ".exe" });
            return list;
        }

        // Раздел «Закреплённые»: закреплённые значки дока в том же порядке. icons — брать настоящие значки программ.
        public static List<AppEntry> FromDock(List<DockItem> items, bool icons)
        {
            var list = new List<AppEntry>();
            if (items == null) return list;
            foreach (DockItem i in items)
            {
                if (!i.Pinned) continue;
                list.Add(new AppEntry { Name = i.Name, Target = i.Path, Link = i.Link, App = i.App, Args = i.Pin != null ? i.Pin.Args : null, Pinned = i, Image = icons ? Shell.IconOf(i) : null });
            }
            return list;
        }
    }

    // Содержимое меню приложений. Отдельно от окна — чтобы его можно было нарисовать в файл, не показывая (--apps --shot).
    // Ячейки значков делаются один раз: поиск только переставляет их и прячет лишние, а не собирает сетку заново.
    // Пока поиск пуст, сверху — «Закреплённые» (как в доке), под ними — «Все приложения»; стрелки ходят по обоим разделам
    // как по одному списку. При наборе — только найденное, без разделов.
    class AppMenuView
    {
        const double CellW = 148, CellH = 128, IconSize = 64, FieldW = 440, FieldH = 46, Dim = 0.82, Glow = 0.5, PowerH = 34, HeadH = 34, SectionGap = 14;
        const int ConfirmSeconds = 4, MaxColumns = 7;
        public readonly Grid Root;
        public readonly TextBox Box;
        public Action<AppEntry> Launch;
        public Action Dismiss;
        public Action<string> OnSession;   // выключение, перезагрузка, сон, блокировка; null — кнопки ничего не делают (снимок, --demo)
        readonly TextBlock placeholder, note, pinHead, allHead;
        readonly Canvas grid = new Canvas { VerticalAlignment = VerticalAlignment.Top };
        readonly ScrollViewer scroll;
        readonly Dictionary<string, Cell> made = new Dictionary<string, Cell>(StringComparer.OrdinalIgnoreCase);
        readonly List<Cell> cells = new List<Cell>();   // то, что сейчас показано, по порядку: сначала закреплённые
        readonly List<UIElement> order = new List<UIElement>();   // заголовки и ячейки сверху вниз — для волны появления
        readonly int columns;
        readonly List<AppEntry> pinned;
        int[] sections = new int[0];   // сколько значков в каждом показанном разделе
        List<AppEntry> all;
        Cell marked;
        int selected = -1;
        Point lastMouse = new Point(-1, -1);

        class Cell
        {
            public AppEntry Entry;
            public Border Root;
            public int Index = -1;   // место в показанном; −1 — спрятана
        }

        // desktop — подложить под затемнение тёмный фон со свечением: для снимка, где за окном ничего нет
        // pinned — закреплённые в доке, по порядку (может быть пустым)
        public AppMenuView(double w, double h, List<AppEntry> apps, List<AppEntry> pinned, bool desktop)
        {
            all = apps;
            this.pinned = pinned ?? new List<AppEntry>();
            // Не шире семи колонок: на широком экране сетка не расползается и не выглядит редкой
            columns = (int)Math.Max(3, Math.Min(MaxColumns, Math.Floor((w - 160) / CellW)));
            Root = new Grid { Width = w, Height = h, Background = Brushes.Transparent, ClipToBounds = true };
            if (desktop)
            {
                Root.Children.Add(new Rectangle { Fill = Ui.Bg });
                Root.Children.Add(Ui.Backdrop(w, h));
            }
            Root.Children.Add(new Rectangle { Fill = Ui.Solid(Ui.Alpha(Tokens.Dark.Bg, Dim)) });
            Canvas glow = Ui.Backdrop(w, h);
            glow.Opacity = Glow;
            Root.Children.Add(glow);

            Box = new TextBox
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Ui.Label, CaretBrush = Ui.Label, SelectionBrush = Ui.Solid(Tokens.Palette.Accent),
                FontSize = Tokens.Size.Title, FontFamily = Ui.DisplayFont, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0), FocusVisualStyle = null
            };
            placeholder = Ui.T("Поиск", Tokens.Size.Title, Ui.Label3, FontWeights.Normal);
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            placeholder.IsHitTestVisible = false;
            placeholder.Margin = new Thickness(2, 0, 0, 1);
            FrameworkElement lens = Glyphs.Search(20);
            lens.Margin = new Thickness(0, 0, 10, 0);
            DockPanel.SetDock(lens, System.Windows.Controls.Dock.Left);
            var line = new DockPanel { LastChildFill = true, Children = { lens, new Grid { Children = { placeholder, Box } } } };
            Border field = Glyphs.Capsule(line, FieldH, 18);
            field.Width = FieldW;
            field.HorizontalAlignment = HorizontalAlignment.Center;
            field.Margin = new Thickness(0, Math.Max(36, Math.Round(h * 0.085)), 0, 0);
            field.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; Box.Focus(); };
            DockPanel.SetDock(field, System.Windows.Controls.Dock.Top);

            // Внизу — питание и подсказка о клавишах
            var power = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 16, 0, 0) };
            foreach (string kind in SessionRules.Kinds) power.Children.Add(PowerButton(kind));
            TextBlock keys = Ui.T("Enter — открыть  ·  стрелки — выбрать  ·  Esc — закрыть", Tokens.Size.Caption, Ui.Label3, FontWeights.Normal);
            keys.HorizontalAlignment = HorizontalAlignment.Center;
            keys.Margin = new Thickness(0, 12, 0, 22);
            var bottom = new StackPanel { Children = { power, keys } };
            DockPanel.SetDock(bottom, System.Windows.Controls.Dock.Bottom);

            note = Ui.T("", Tokens.Size.Heading, Ui.Label2, FontWeights.Normal);
            note.HorizontalAlignment = HorizontalAlignment.Center;
            note.Margin = new Thickness(0, 60, 0, 0);
            note.IsHitTestVisible = false;

            // Подписи разделов — мелким вторичным текстом, как подписи групп в остальных приложениях семьи
            pinHead = Head("Закреплённые");
            allHead = Head("Все приложения");
            grid.Children.Add(pinHead);
            grid.Children.Add(allHead);
            grid.Width = columns * CellW;
            grid.HorizontalAlignment = HorizontalAlignment.Center;
            // Полоса прокрутки — тонкая, из общих стилей kit.txt (Ui.Styles), и только когда есть что листать
            scroll = new ScrollViewer
            {
                Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false, Margin = new Thickness(0, 30, 0, 0), HorizontalAlignment = HorizontalAlignment.Center
            };
            // Колесо прокрутило список под неподвижным курсором — выбранным становится то, что теперь под ним
            scroll.PreviewMouseWheel += delegate { lastMouse = new Point(-1, -1); };
            Root.Children.Add(new DockPanel { LastChildFill = true, Children = { field, bottom, new Grid { Children = { scroll, note } } } });

            Root.MouseLeftButtonDown += delegate { if (Dismiss != null) Dismiss(); };   // щелчок мимо значков и поиска закрывает
            Box.TextChanged += delegate { Fill(); };
            Fill();
        }

        public int Count { get { return cells.Count; } }

        // Список собрался или обновился, пока меню открыто
        public void SetApps(List<AppEntry> apps)
        {
            all = apps;
            // Ячейки программ, которых больше нет, убираются; остальные остаются теми же
            var alive = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (apps != null) foreach (AppEntry e in apps) alive[KeyOf(e)] = true;
            foreach (AppEntry e in pinned) alive[PinKey + KeyOf(e)] = true;
            foreach (string key in new List<string>(made.Keys))
            {
                if (alive.ContainsKey(key)) continue;
                grid.Children.Remove(made[key].Root);
                made.Remove(key);
            }
            Fill();
        }

        static string KeyOf(AppEntry e) { return (e.Link ?? "") + "|" + (e.Target ?? "") + "|" + (e.Args ?? "") + "|" + (e.App ?? ""); }
        // Закреплённое есть и среди всех приложений: у него в разделе «Закреплённые» своя ячейка
        const string PinKey = "pin|";

        static TextBlock Head(string text)
        {
            TextBlock t = Ui.T(text, Tokens.Size.Caption, Ui.Label2, FontWeights.SemiBold);
            t.IsHitTestVisible = false;
            t.Visibility = Visibility.Collapsed;
            return t;
        }

        static int TintOf(string name)
        {
            int sum = 0;
            foreach (char c in name) sum += c;
            return sum;
        }

        // Ячейка программы: та же, что в прошлый раз, или новая
        Cell CellFor(AppEntry entry, bool pin)
        {
            string key = (pin ? PinKey : "") + KeyOf(entry);
            Cell cell;
            if (made.TryGetValue(key, out cell))
            {
                if (ReferenceEquals(cell.Entry.Image, entry.Image)) { cell.Entry = entry; return cell; }
                grid.Children.Remove(cell.Root);   // значок появился или сменился — ячейка делается заново
            }
            cell = new Cell { Entry = entry };
            FrameworkElement face;
            if (entry.Image != null)
            {
                var image = new Image { Source = entry.Image, Width = IconSize, Height = IconSize, HorizontalAlignment = HorizontalAlignment.Center };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                face = image;
            }
            else
            {
                face = Glyphs.LetterTile(entry.Name, IconSize / 0.92, TintOf(entry.Name));
                face.Margin = new Thickness(0, -2, 0, -3);
            }
            TextBlock name = Ui.T(entry.Name, Tokens.Size.Caption, Ui.Label, FontWeights.Normal);
            name.TextWrapping = TextWrapping.Wrap;
            name.TextAlignment = TextAlignment.Center;
            name.MaxHeight = 32;
            name.Margin = new Thickness(0, 8, 0, 0);
            Border root = new Border
            {
                Width = CellW - 8, Height = CellH - 8, Margin = new Thickness(4), CornerRadius = new CornerRadius(Tokens.Radius.Row), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1), Padding = new Thickness(6, 11, 6, 0), Child = new StackPanel { Children = { face, name } }
            };
            cell.Root = root;
            Cell me = cell;
            // Курсор двигает ту же подсветку, что и стрелки: выбранное всегда одно. Считается только настоящее движение мыши —
            // когда список прокрутился под неподвижным курсором от клавиш, выбор остаётся за клавишами.
            root.MouseMove += delegate(object s, MouseEventArgs e)
            {
                Point p = e.GetPosition(Root);
                if (p == lastMouse) return;
                lastMouse = p;
                if (me.Index >= 0 && me.Index != selected) Select(me.Index, false);
            };
            root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; if (me.Index >= 0) Select(me.Index, false); root.Background = Ui.Press; };
            root.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; if (Launch != null) Launch(me.Entry); };
            made[key] = cell;
            grid.Children.Add(root);
            return cell;
        }

        // Показывает то, что подходит к запросу: ячейки встают по порядку, остальные прячутся
        void Fill()
        {
            string query = Box.Text;
            placeholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            List<AppEntry> list = AppRules.Filter(all, query);
            // Разделы — только пока ничего не набрано и список уже собран
            bool split = query.Length == 0 && all != null && pinned.Count > 0;
            Select(-1, false);
            foreach (Cell c in made.Values) c.Index = -1;
            cells.Clear();
            order.Clear();
            double y = 0;
            if (split)
            {
                y = Lay(Heading(pinHead, y), pinned, true);
                y = Heading(allHead, y + SectionGap);
            }
            pinHead.Visibility = allHead.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            y = Lay(y, list, false);
            sections = split ? new int[] { pinned.Count, list.Count } : new int[] { list.Count };
            foreach (Cell c in made.Values)
                if (c.Index < 0 && c.Root.Visibility != Visibility.Collapsed) c.Root.Visibility = Visibility.Collapsed;
            grid.Height = y;
            note.Text = all == null ? "Собираю список приложений…" : list.Count == 0 ? "Ничего не найдено" : "";
            Select(cells.Count > 0 ? 0 : -1, false);   // Enter открывает первое: закреплённое или найденное
            scroll.ScrollToTop();
        }

        // Подпись раздела на высоте y; возвращает, где начинаются его значки
        double Heading(TextBlock head, double y)
        {
            Canvas.SetLeft(head, 12);
            Canvas.SetTop(head, y + 10);
            order.Add(head);
            return y + HeadH;
        }

        // Ячейки раздела рядами от высоты y; возвращает, где раздел кончается
        double Lay(double y, List<AppEntry> list, bool pin)
        {
            for (int i = 0; i < list.Count; i++)
            {
                Cell c = CellFor(list[i], pin);
                c.Index = cells.Count;
                cells.Add(c);
                order.Add(c.Root);
                Canvas.SetLeft(c.Root, (i % columns) * CellW);
                Canvas.SetTop(c.Root, y + (i / columns) * CellH);
                if (c.Root.Visibility != Visibility.Visible) c.Root.Visibility = Visibility.Visible;
            }
            return y + Math.Ceiling(list.Count / (double)columns) * CellH;
        }

        // reveal — прокрутить список так, чтобы выбранное было видно (для клавиш; под курсором оно и так на виду)
        void Select(int index, bool reveal)
        {
            if (marked != null) { marked.Root.Background = Brushes.Transparent; marked.Root.BorderBrush = Brushes.Transparent; }
            selected = index >= 0 && index < cells.Count ? index : -1;
            marked = selected < 0 ? null : cells[selected];
            if (marked == null) return;
            marked.Root.Background = Ui.GlassHover;
            marked.Root.BorderBrush = Ui.Rim;
            if (reveal) marked.Root.BringIntoView();
        }

        public void Move(int dx, int dy) { Select(AppRules.Move(selected, sections, columns, dx, dy), true); }

        // Home, End, PageUp, PageDown
        public void Jump(string jump)
        {
            double view = scroll.ViewportHeight > 0 ? scroll.ViewportHeight : scroll.ActualHeight;
            Select(AppRules.Jump(selected, sections, columns, (int)Math.Max(1, Math.Floor(view / CellH)), jump), true);
        }

        public void LaunchSelected()
        {
            if (selected >= 0 && selected < cells.Count && Launch != null) Launch(cells[selected].Entry);
        }

        // Кнопка питания. Сон и блокировка срабатывают сразу. Выключение и перезагрузка — со второго щелчка:
        // после первого кнопка спрашивает «Точно выключить?» и через несколько секунд сама возвращается назад.
        Border PowerButton(string kind)
        {
            string label = SessionRules.Label(kind), confirm = SessionRules.Confirm(kind);
            TextBlock icon = Ui.Glyph(SessionRules.Glyph(kind));
            icon.FontSize = Tokens.Size.Body; icon.Foreground = Ui.Label2; icon.VerticalAlignment = VerticalAlignment.Center; icon.Margin = new Thickness(0, 0, 8, 0);
            TextBlock text = Ui.T(label, Tokens.Size.Body, Ui.Label, FontWeights.Normal);
            text.VerticalAlignment = VerticalAlignment.Center;
            text.Margin = new Thickness(0, 0, 0, 1);
            if (confirm != null)
            {
                // Вопрос не меняет ширину кнопки: соседние кнопки не уезжают из-под курсора
                double widest = 0;
                foreach (string t in new string[] { label, confirm })
                {
                    TextBlock probe = Ui.T(t, Tokens.Size.Body, Ui.Label, FontWeights.Normal);
                    probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    widest = Math.Max(widest, probe.DesiredSize.Width);
                }
                text.MinWidth = Math.Ceiling(widest);
            }
            Border b = Glyphs.Capsule(new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, text } }, PowerH, 14);
            b.Margin = new Thickness(4, 0, 4, 0);
            Brush danger = Ui.Solid(Ui.Alpha(Tokens.Palette.Red, 0.6)), rest = b.Background;
            bool armed = false, over = false;
            var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(ConfirmSeconds) };
            Action paint = delegate { b.Background = armed ? danger : over ? Ui.GlassHover : rest; };
            Action disarm = delegate
            {
                back.Stop();
                if (!armed) return;
                armed = false;
                text.Text = label;
                paint();
            };
            back.Tick += delegate { disarm(); };
            Action press = delegate
            {
                if (confirm != null && !armed)
                {
                    armed = true;
                    text.Text = confirm;
                    paint();
                    back.Stop(); back.Start();
                    return;
                }
                disarm();
                if (OnSession != null) OnSession(kind);
            };
            b.MouseEnter += delegate { over = true; paint(); };
            b.MouseLeave += delegate { over = false; paint(); };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; press(); };
            Ui.Keyboard(b, press);
            return b;
        }

        // Появление: значки поднимаются и проявляются короткой волной — общим движением из kit.txt.
        // При «Меньше движения» оно само ничего не делает.
        public void Enter()
        {
            if (!Ui.ReducedMotion) Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Tokens.Motion.FastMs)) { EasingFunction = Ui.EaseOut });
            // Волна идёт в порядке детей сетки: ставим их так, как они стоят на экране, — сверху вниз, первым закреплённое
            var rest = new List<UIElement>();
            foreach (UIElement e in grid.Children) if (!order.Contains(e)) rest.Add(e);
            grid.Children.Clear();
            foreach (UIElement e in order) grid.Children.Add(e);
            foreach (UIElement e in rest) grid.Children.Add(e);
            Motion.Stagger(grid);
        }

        // Рисует меню в файл PNG, не показывая его на экране
        public static void Shot(string file, List<AppEntry> apps, List<AppEntry> pinned, string query)
        {
            const double w = 1280, h = 800;
            var view = new AppMenuView(w, h, apps, pinned, true);
            System.Windows.Documents.TextElement.SetFontFamily(view.Root, Ui.TextFont);
            if (!string.IsNullOrEmpty(query)) view.Box.Text = query;
            Program.Save(view.Root, w, h, 1.5, file);
        }
    }

    // Окно меню приложений: на весь главный экран, берёт фокус; закрывается по Esc, щелчку мимо и когда фокус ушёл
    class AppMenuWindow : Window
    {
        const int FadeOutMs = 120;
        readonly AppMenuView view;
        bool closing;

        // pinned — закреплённые в доке, в его порядке: раздел «Закреплённые» сверху
        public AppMenuWindow(bool demo, List<AppEntry> pinned)
        {
            Title = "dock.txt";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0; Top = 0;
            Width = SystemParameters.PrimaryScreenWidth; Height = SystemParameters.PrimaryScreenHeight;
            FontFamily = Ui.TextFont;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            view = new AppMenuView(Width, Height, demo ? AppCatalog.Demo() : AppCatalog.Cached, pinned, false);
            view.Dismiss = Shut;
            view.Launch = delegate(AppEntry e)
            {
                Shut();
                if (demo) return;
                // Закреплённое запускается так же, как щелчком в доке: через свой ярлык или файл с параметрами
                try { if (e.Pinned != null) Shell.Launch(e.Pinned); else if (e.App != null) Packaged.Open(e.App); else Shell.Open(e.Link); } catch { }
            };
            // Питание — только в настоящем доке; в --demo кнопки нарисованы, но ничего не делают
            if (!demo) view.OnSession = delegate(string kind) { Shut(); Session.Run(kind); };
            Content = view.Root;

            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (closing) { e.Handled = true; return; }
                if (e.Key == Key.Escape) Shut();
                else if (e.Key == Key.Enter)
                {
                    if (!view.Box.IsKeyboardFocusWithin) return;   // Enter на кнопке питания нажимает её, а не открывает программу
                    view.LaunchSelected();
                }
                else if (e.Key == Key.Left) view.Move(-1, 0);
                else if (e.Key == Key.Right) view.Move(1, 0);
                else if (e.Key == Key.Up) view.Move(0, -1);
                else if (e.Key == Key.Down) view.Move(0, 1);
                else if (e.Key == Key.Home) view.Jump("home");
                else if (e.Key == Key.End) view.Jump("end");
                else if (e.Key == Key.PageUp) view.Jump("up");
                else if (e.Key == Key.PageDown) view.Jump("down");
                else return;
                e.Handled = true;
            };
            Deactivated += delegate { Shut(); };
            Closing += delegate { closing = true; };
            Loaded += delegate
            {
                view.Box.Focus();
                Keyboard.Focus(view.Box);
                view.Enter();
            };
            // Список мог устареть (поставили программу): пересобираем в фоне и подменяем, если меню ещё открыто
            if (!demo) AppCatalog.Refresh(delegate { Dispatcher.BeginInvoke(new Action(delegate { if (!closing) view.SetApps(AppCatalog.Cached); })); });
        }

        // Закрытие: меню быстро гаснет и только потом исчезает
        internal void Shut()
        {
            if (closing) return;
            closing = true;
            view.Root.IsHitTestVisible = false;
            if (!Ui.Live(view.Root)) { Close(); return; }
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(FadeOutMs)) { EasingFunction = Ui.EaseOut };
            fade.Completed += delegate { try { Close(); } catch { } };
            view.Root.BeginAnimation(OpacityProperty, fade);
        }
    }
}
