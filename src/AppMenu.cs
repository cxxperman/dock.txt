// dock.txt. AppMenu.cs — меню приложений вместо «Пуска»: затемнённый экран, строка поиска и сетка всех программ
// из папок меню «Пуск». Список и значки собираются в фоновом потоке и остаются в памяти, поэтому меню
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
    // Список приложений из меню «Пуск»: общий и для этого пользователя, со всеми вложенными папками, только ярлыки
    static class AppCatalog
    {
        const int IconPx = 96, FreshSeconds = 60;
        static readonly object gate = new object();
        static readonly List<Action> waiting = new List<Action>();
        static List<AppEntry> cache;
        static bool scanning;
        static DateTime scannedAt = DateTime.MinValue;
        static int links, icons, lastMs;

        public static List<AppEntry> Cached { get { lock (gate) return cache; } }

        // Для --probe: только числа
        public static string Summary()
        {
            lock (gate)
                return cache == null ? "start menu: " + (scanning ? "still scanning" : "not scanned")
                    : "start menu: shortcuts " + links + ", apps " + cache.Count + ", with icons " + icons + ", scanned in " + lastMs + " ms";
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
            t.SetApartmentState(ApartmentState.STA);   // ярлыки читает WScript.Shell
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
            List<AppEntry> list = AppRules.Clean(found);
            var old = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            List<AppEntry> before = Cached;
            if (before != null) foreach (AppEntry e in before) old[e.Link + "|" + e.Icon + "|" + e.Target] = e;
            int have = 0;
            foreach (AppEntry e in list)
            {
                AppEntry same;
                if (old.TryGetValue(e.Link + "|" + e.Icon + "|" + e.Target, out same) && same.Image != null) e.Image = same.Image;
                else e.Image = Shell.Extract(e.Icon, IconPx) ?? Shell.Extract(e.Target, IconPx);
                if (e.Image != null) have++;
            }
            lock (gate) { links = files.Count; icons = have; lastMs = (int)watch.ElapsedMilliseconds; }
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
    }

    // Содержимое меню приложений. Отдельно от окна — чтобы его можно было нарисовать в файл, не показывая (--apps --shot).
    class AppMenuView
    {
        const double CellW = 124, CellH = 122, IconSize = 60, FieldW = 440, FieldH = 46, Dim = 0.82, Glow = 0.5;
        public readonly Grid Root;
        public readonly TextBox Box;
        public Action<AppEntry> Launch;
        public Action Dismiss;
        readonly TextBlock placeholder, note;
        readonly WrapPanel grid = new WrapPanel { Orientation = Orientation.Horizontal };
        readonly ScrollViewer scroll;
        readonly List<Border> cells = new List<Border>();
        readonly int columns;
        List<AppEntry> all, shown = new List<AppEntry>();
        int selected = -1;

        // desktop — подложить под затемнение тёмный фон со свечением: для снимка, где за окном ничего нет
        public AppMenuView(double w, double h, List<AppEntry> apps, bool desktop)
        {
            all = apps;
            columns = (int)Math.Max(3, Math.Min(9, Math.Floor((w - 160) / CellW)));
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

            TextBlock keys = Ui.T("Enter — открыть  ·  стрелки — выбрать  ·  Esc — закрыть", Tokens.Size.Caption, Ui.Label3, FontWeights.Normal);
            keys.HorizontalAlignment = HorizontalAlignment.Center;
            keys.Margin = new Thickness(0, 14, 0, 26);
            DockPanel.SetDock(keys, System.Windows.Controls.Dock.Bottom);

            note = Ui.T("", Tokens.Size.Heading, Ui.Label2, FontWeights.Normal);
            note.HorizontalAlignment = HorizontalAlignment.Center;
            note.Margin = new Thickness(0, 60, 0, 0);
            note.IsHitTestVisible = false;

            grid.Width = columns * CellW;
            grid.HorizontalAlignment = HorizontalAlignment.Center;
            scroll = new ScrollViewer
            {
                Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false, Margin = new Thickness(0, 30, 0, 0), HorizontalAlignment = HorizontalAlignment.Center
            };
            Root.Children.Add(new DockPanel { LastChildFill = true, Children = { field, keys, new Grid { Children = { scroll, note } } } });

            Root.MouseLeftButtonDown += delegate { if (Dismiss != null) Dismiss(); };   // щелчок мимо значков и поиска закрывает
            Box.TextChanged += delegate { Fill(); };
            Fill();
        }

        public int Count { get { return shown.Count; } }

        // Список собрался или обновился, пока меню открыто
        public void SetApps(List<AppEntry> apps)
        {
            all = apps;
            Fill();
        }

        static int TintOf(string name)
        {
            int sum = 0;
            foreach (char c in name) sum += c;
            return sum;
        }

        void Fill()
        {
            string query = Box.Text;
            placeholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            shown = AppRules.Filter(all, query);
            grid.Children.Clear();
            cells.Clear();
            for (int i = 0; i < shown.Count; i++)
            {
                AppEntry entry = shown[i];
                int index = i;
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
                var cell = new Border
                {
                    Width = CellW - 8, Height = CellH - 8, Margin = new Thickness(4), CornerRadius = new CornerRadius(Tokens.Radius.Row), Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
                    BorderThickness = new Thickness(1), Padding = new Thickness(6, 11, 6, 0), Child = new StackPanel { Children = { face, name } }
                };
                cell.MouseEnter += delegate { if (index != selected) cell.Background = Ui.Fill; };
                cell.MouseLeave += delegate { if (index != selected) cell.Background = Brushes.Transparent; };
                cell.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
                cell.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; if (Launch != null) Launch(entry); };
                cells.Add(cell);
                grid.Children.Add(cell);
            }
            note.Text = all == null ? "Собираю список приложений…" : shown.Count == 0 ? "Ничего не найдено" : "";
            selected = -1;
            Select(shown.Count > 0 ? 0 : -1);   // Enter открывает первое найденное
            scroll.ScrollToTop();
        }

        void Select(int index)
        {
            if (selected >= 0 && selected < cells.Count) { cells[selected].Background = Brushes.Transparent; cells[selected].BorderBrush = Brushes.Transparent; }
            selected = index;
            if (selected < 0 || selected >= cells.Count) return;
            cells[selected].Background = Ui.GlassHover;
            cells[selected].BorderBrush = Ui.Rim;
            cells[selected].BringIntoView();
        }

        public void Move(int dx, int dy) { Select(AppRules.Move(selected, shown.Count, columns, dx, dy)); }

        public void LaunchSelected()
        {
            if (selected >= 0 && selected < shown.Count && Launch != null) Launch(shown[selected]);
        }

        // Появление: значки поднимаются и проявляются короткой волной — общим движением из kit.txt.
        // При «Меньше движения» оно само ничего не делает.
        public void Enter()
        {
            if (!Ui.ReducedMotion) Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Tokens.Motion.FastMs)) { EasingFunction = Ui.EaseOut });
            Motion.Stagger(grid);
        }

        // Рисует меню в файл PNG, не показывая его на экране
        public static void Shot(string file, List<AppEntry> apps, string query)
        {
            const double w = 1280, h = 800;
            var view = new AppMenuView(w, h, apps, true);
            System.Windows.Documents.TextElement.SetFontFamily(view.Root, Ui.TextFont);
            if (!string.IsNullOrEmpty(query)) view.Box.Text = query;
            Program.Save(view.Root, w, h, 1.5, file);
        }
    }

    // Окно меню приложений: на весь главный экран, берёт фокус; закрывается по Esc, щелчку мимо и когда фокус ушёл
    class AppMenuWindow : Window
    {
        readonly AppMenuView view;
        bool closing;

        public AppMenuWindow(bool demo)
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

            view = new AppMenuView(Width, Height, demo ? AppCatalog.Demo() : AppCatalog.Cached, false);
            view.Dismiss = Shut;
            view.Launch = delegate(AppEntry e)
            {
                Shut();
                if (demo) return;
                try { Shell.Open(e.Link); } catch { }
            };
            Content = view.Root;

            PreviewKeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Escape) Shut();
                else if (e.Key == Key.Enter) view.LaunchSelected();
                else if (e.Key == Key.Left) view.Move(-1, 0);
                else if (e.Key == Key.Right) view.Move(1, 0);
                else if (e.Key == Key.Up) view.Move(0, -1);
                else if (e.Key == Key.Down) view.Move(0, 1);
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

        void Shut()
        {
            if (closing) return;
            closing = true;
            Close();
        }
    }
}
