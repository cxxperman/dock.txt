// dock.txt. DockWindow.cs — сам док: стеклянная полоса внизу экрана. Слева — кнопка меню приложений, за ней значки
// (точки под запущенными, красные счётчики непрочитанного), справа за чертой — виджеты: музыка, календарь, погода,
// громкость, батарея, часы. Увеличение под курсором, подпись над значком, автоскрытие, меню по правому щелчку.
// Окно прозрачное и шире полосы: увеличенным значкам и подписи нужно место. Прозрачные места щелчки пропускают.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DevTxt.DockApp
{
    enum SlotKind { Item, Line, Launcher, Widget }

    // Место в ряду: значок приложения, кнопка меню приложений, черта или виджет
    class Slot
    {
        public SlotKind Kind;
        public DockItem Item;             // для значка приложения
        public string Widget;             // для виджета: music, calendar, weather, volume, battery, clock
        public string Tip;                // подпись виджета
        public Action<bool> Hover;        // показать или спрятать то, что видно только под курсором (кнопки музыки)
        public FrameworkElement Root;
        public Ellipse Dot;
        public readonly ScaleTransform Scale = new ScaleTransform(1, 1);
        public readonly TranslateTransform Move = new TranslateTransform(), DotMove = new TranslateTransform();
        public double W, Left, Now = 1;   // ширина в покое, где стоит и во сколько раз увеличен прямо сейчас

        public bool Icon { get { return Kind == SlotKind.Item || Kind == SlotKind.Launcher; } }
        public string Id { get { return Kind == SlotKind.Item ? "item:" + Item.Key : Kind == SlotKind.Widget ? "widget:" + Widget : Kind.ToString(); } }
    }

    class DockWindow : Window
    {
        // Значок дока — тот же рисунок, что в sprite.json (потом переедет в kit.txt, icons/sprites.json, «dock»)
        internal static readonly string[] DockSprite = { "....####....", "....#E##....", ".##.#E##.##.", ".##.####.##.", ".##.####.##.", "............", "############", ".##########." };

        // Свои размеры дока — то, чего в токенах нет
        const double PadH = 6, PadTop = 9, DotZone = 12, EdgeGap = 8, TipGap = 9, TipH = 26, TopRoom = 12, DotSize = 4;
        const double WidgetTipGap = 5, TipMaxH = 92;   // подпись виджета бывает в несколько строк (события календаря)
        const double MusicText = 180, CalendarText = 190, MusicTextTight = 110, CalendarTextTight = 120;
        internal const double MaxScale = 1.55, RangeSlots = 2.6;
        const int HideAfterMs = 700, EdgeMs = 100, SlowPollTicks = 5;
        const string AppsTip = "Приложения";

        readonly bool demo, headless;
        readonly Store store;
        readonly Canvas stage = new Canvas();
        readonly TranslateTransform slide = new TranslateTransform();
        readonly Border panel, tip;
        readonly TextBlock tipText;
        readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer edge = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(EdgeMs) };
        readonly DispatcherTimer minute = new DispatcherTimer();
        readonly Stopwatch clock = Stopwatch.StartNew();
        Rect work = SystemParameters.WorkArea;
        DockHub hub;

        List<DockItem> items = new List<DockItem>();
        readonly List<Slot> slots = new List<Slot>();
        string signature;
        double size, gap, winW, winH, restW, baseline, panelTop, panelLeft, panelRight;
        double[] rest = new double[0];

        // Виджеты: что пришло из хаба от kapsula.txt и что док узнал у Windows сам
        MediaInfo media;
        WeatherInfo weather;
        CalendarInfo calendar;
        UnreadInfo unread;
        BatteryInfo battery = BatteryInfo.From(0, 255, 255);
        readonly Dictionary<string, string> hubRaw = new Dictionary<string, string>();
        Audio audio;
        bool hasVolume, volMuted, widgetsDirty, ended;
        volatile bool volPending;
        double volLevel;
        int ticks;
        List<string> shown = new List<string>();
        AppMenuWindow appMenu;

        // Увеличение: одна пружина от 0 (покой) до 1 (курсор над доком). Сами значки идут за курсором без задержки.
        double amount, velocity, cursorU, lastU = double.NaN, lastAmount = double.NaN;
        bool looping, inside, menuOpen, hidden, fullscreen;
        int hover = -1, edgeHits;
        long lastFrame, lastInside;
        string tipOverride;

        // hubData — для --demo: показывать ли выдуманные музыку, погоду, календарь и счётчики
        public DockWindow(bool demo, Store store, bool headless, bool hubData)
        {
            this.demo = demo;
            this.store = store;
            this.headless = headless;
            Title = "dock.txt";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            FontFamily = Ui.TextFont;
            UseLayoutRounding = false;   // значки двигаются плавно, а не по целым точкам
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            // Полоса: тёмная основа (за окном размытия нет — стекло должно читаться на любых обоях), на ней стекло с кромкой
            panel = new Border
            {
                // Тот же чёрный, что у острова kapsula.txt наверху: верх и низ экрана — один набор, а не два разных
                CornerRadius = new CornerRadius(Tokens.Radius.Block), Background = Ui.Solid(Tokens.Dark.Bg), SnapsToDevicePixels = true,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = 0.38, Color = Colors.Black },
                Child = new Border { CornerRadius = new CornerRadius(Tokens.Radius.Block), Background = Brushes.Transparent, BorderBrush = Ui.Rim, BorderThickness = new Thickness(1) }
            };
            tipText = Ui.T("", Tokens.Size.Caption, Ui.Label, FontWeights.SemiBold);
            tipText.VerticalAlignment = VerticalAlignment.Center;
            tipText.TextTrimming = TextTrimming.None;
            tip = new Border
            {
                MinHeight = TipH, CornerRadius = new CornerRadius(Tokens.Radius.Tip), Background = Ui.Solid(Tokens.Dark.Tooltip), BorderBrush = Ui.Rim, BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 4, 10, 5), Child = tipText, Opacity = 0, IsHitTestVisible = false, SnapsToDevicePixels = true
            };
            stage.RenderTransform = slide;
            Content = stage;

            stage.MouseMove += delegate { StartLoop(); };
            panel.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; OpenMenu(null); };
            SourceInitialized += delegate
            {
                IntPtr me = new WindowInteropHelper(this).Handle;
                Win.MakeBar(me);
                // Проводник перезапустился и снова показал свою панель задач — прячем её заново
                HwndSource.FromHwnd(me).AddHook(delegate(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
                {
                    if (msg == TaskbarSwap.TaskbarCreated && msg != 0) Dispatcher.BeginInvoke(new Action(SyncTaskbar));
                    return IntPtr.Zero;
                });
            };

            if (demo)
            {
                items = Demo();
                DemoWidgets(hubData);
            }
            else
            {
                hub = new DockHub(this, store);
                items = Collect();
                // Громкость: Windows сама сообщает об изменениях (в своём потоке) — опрашивать её не нужно
                audio = Audio.Open(delegate
                {
                    if (volPending) return;
                    volPending = true;
                    Dispatcher.BeginInvoke(new Action(delegate { volPending = false; if (!ended && ReadVolume()) Rebuild(); }));
                });
                ReadVolume();
                battery = Power.Read();
                refresh.Tick += delegate { Refresh(); };
                refresh.Start();
                edge.Tick += delegate { EdgeTick(); };
                minute.Tick += delegate { MinuteTick(); };
                ArmMinute();
                Closed += delegate { End(); };
            }
            Rebuild();
            if (!demo)
            {
                hub.Start();
                hub.PublishApps(items);
                ApplyAutoHide();
                SyncTaskbar();
                taskbarTimer.Tick += delegate { TaskbarSwap.Keep(store.Dir); };
                taskbarTimer.Start();
                hookTimer.Tick += delegate { WinKey.Refresh(); };
                hookTimer.Start();
                AppCatalog.Refresh(null);   // список для меню приложений собирается заранее, в фоне
            }
        }

        internal bool Demoing { get { return demo; } }

        readonly DispatcherTimer taskbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer hookTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };

        // Панель задач Windows и кнопка Windows — по настройкам
        void SyncTaskbar()
        {
            if (demo || headless) return;
            if (store.ReplaceTaskbar) TaskbarSwap.Take(store.Dir); else TaskbarSwap.Give(store.Dir);
            WinKey.Sync(store.ReplaceTaskbar && store.WinKey, Dispatcher, delegate { if (!ended) ToggleApps(); });
            // Компактная панель Windows: включаем, только когда её выбрали; иначе возвращаем, как было до нас
            bool changed = store.Bar == "modern" ? TaskbarSwap.CompactOn(store.Dir) : TaskbarSwap.CompactBack(store.Dir);
            if (changed) TaskbarSwap.RestartExplorer(null);   // Проводник не закрылся сам — не трогаем: панель сменится при следующем входе
            // Выбрана панель Windows — дока не видно (он остаётся запущенным: настройки, счётчики, меню приложений)
            if (store.Bar == "modern") Hide(); else if (!fullscreen) Show();
        }

        // Всё, что тикает и слушает, останавливается один раз
        void End()
        {
            if (ended) return;
            ended = true;
            refresh.Stop(); edge.Stop(); minute.Stop(); taskbarTimer.Stop(); hookTimer.Stop();
            // Док уходит — панель задач и кнопка Windows возвращаются такими, какими были
            WinKey.Stop();
            if (!demo && !headless) TaskbarSwap.Give(store.Dir);
            if (audio != null) audio.Close();
            if (hub != null) hub.Stop();
        }

        // Выход: окно могло и не показываться (--headless)
        internal void Quit()
        {
            End();
            if (appMenu != null) appMenu.Close();
            if (headless) Application.Current.Shutdown(); else Close();
        }

        DateTime Today { get { return demo ? new DateTime(2026, 11, 18, 14, 5, 0) : DateTime.Now; } }

        // ───────── Что показывать ─────────
        List<DockItem> Collect()
        {
            return Logic.Group(store.Pins, Win.Taskbar(), store.ShowRunning, Shell.NameOf);
        }

        // Раз в секунду: смотрим на окна и перерисовываем док, только если что-то изменилось.
        // Раз в пять секунд заодно — батарея и не сменилось ли устройство звука.
        void Refresh()
        {
            bool full = Win.FullscreenAhead();
            if (full != fullscreen)
            {
                fullscreen = full;   // игра или видео на весь экран — дока нет совсем
                if (!headless) { if (full || store.Bar == "modern") Hide(); else Show(); }
            }
            if (++ticks % SlowPollTicks == 0)
            {
                BatteryInfo b = Power.Read();
                if (b.Signature != battery.Signature) widgetsDirty = true;
                battery = b;
                if (audio != null) audio.Rebind();
                if (ReadVolume()) widgetsDirty = true;
            }
            var now = Collect();
            bool moved = SystemParameters.WorkArea != work;   // сменили разрешение или передвинули панель задач
            if (moved) work = SystemParameters.WorkArea;
            if (moved || widgetsDirty || Logic.Signature(now) != signature)
            {
                items = now;
                Rebuild();
                hub.PublishApps(items);
                return;
            }
            // Те же значки — обновляем только списки окон
            items = now;
            int n = 0;
            foreach (var s in slots) if (s.Kind == SlotKind.Item) s.Item = items[n++];
        }

        // Часы идут раз в минуту, ровно на её границе
        void ArmMinute()
        {
            DateTime now = DateTime.Now;
            double left = 60 - now.Second - now.Millisecond / 1000.0;
            minute.Interval = TimeSpan.FromSeconds(left < 0.5 ? left + 60 : left + 0.02);
            minute.Start();
        }

        void MinuteTick()
        {
            minute.Stop();
            ArmMinute();
            if (shown.Contains("clock") || calendar != null) Rebuild();   // у календаря за минуту могло кончиться событие
        }

        // true — громкость или «без звука» изменились
        bool ReadVolume()
        {
            double level = 0;
            bool muted = false;
            bool has = audio != null && audio.Has && audio.Read(out level, out muted);
            bool changed = has != hasVolume || muted != volMuted || VolumeMath.Percent(level) != VolumeMath.Percent(volLevel);
            hasVolume = has; volLevel = level; volMuted = muted;
            return changed;
        }

        // Колесо над виджетом громкости
        void VolumeWheel(int delta)
        {
            if (demo || audio == null || !hasVolume || delta == 0) return;
            bool muted;
            double to = VolumeMath.Wheel(volLevel, volMuted, delta, out muted);
            audio.Set(to);
            if (muted != volMuted) audio.Mute(muted);
            if (ReadVolume()) Rebuild();
        }

        // Щелчок по виджету громкости: звук выключается и включается
        void VolumeClick()
        {
            if (demo || audio == null || !hasVolume) return;
            audio.Mute(!volMuted);
            if (ReadVolume()) Rebuild();
        }

        // Состояние от kapsula.txt из хаба. data == null — состояния больше нет: виджет уходит.
        internal void HubState(string topic, object data)
        {
            if (ended) return;
            string raw = data == null ? "" : new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(data), was;
            if (!hubRaw.TryGetValue(topic, out was)) was = "";
            if (was == raw) return;
            hubRaw[topic] = raw;
            if (topic == "kapsula.media") media = MediaInfo.From(data);
            else if (topic == "kapsula.weather") weather = WeatherInfo.From(data);
            else if (topic == "kapsula.calendar") calendar = CalendarInfo.From(data);
            else if (topic == "kapsula.unread") unread = UnreadInfo.From(data);
            else return;
            Rebuild();
        }

        // Хаб пропал целиком (core.txt закрыли): того, что присылала Kapsula, больше не знаем
        internal void HubGone()
        {
            if (ended || hubRaw.Count == 0) return;
            hubRaw.Clear();
            media = null; weather = null; calendar = null; unread = null;
            Rebuild();
        }

        // Нажали кнопку виджета. Сюда же приходит проверочная команда test.act (ключ --testhook).
        // Громкость отсюда не меняется: её трогают только настоящие колесо и щелчок.
        internal bool WidgetAction(string widget, string action)
        {
            if (widget != "music" || media == null || !shown.Contains("music")) return false;
            if (action != "prev" && action != "toggle" && action != "next") return false;
            if (hub != null) hub.Media(action);
            return true;
        }

        // Настройку поменяли (в core.txt или в меню)
        internal void SettingsChanged()
        {
            if (!demo) items = Collect();
            Rebuild();
            ApplyAutoHide();
            SyncTaskbar();
            if (hub != null) { hub.PublishSettings(true); hub.PublishApps(items); }
        }

        int BadgeFor(DockItem item)
        {
            return unread != null && store.On("badges") ? unread.For(Logic.ExeOf(item)) : 0;
        }

        // ───────── Разметка ─────────
        // Значок программы; если его нет — цветная плитка с первой буквой, как в core.txt
        FrameworkElement Face(DockItem item)
        {
            ImageSource img = demo ? null : Shell.Icon(item.Icon) ?? Shell.Icon(item.Path);
            if (img != null)
            {
                var image = new Image { Source = img, Width = size, Height = size };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                return image;
            }
            return Glyphs.LetterTile(item.Name, size, items.IndexOf(item));
        }

        Slot IconSlot(DockItem item)
        {
            var slot = new Slot { Kind = SlotKind.Item, Item = item, W = size };
            var root = new Grid { Width = size, Height = size, Background = Brushes.Transparent, RenderTransformOrigin = new Point(0.5, 1), Children = { Face(item) } };
            string badge = UnreadInfo.Text(BadgeFor(item));
            if (badge != null) root.Children.Add(Glyphs.Badge(badge, size));
            root.RenderTransform = new TransformGroup { Children = { slot.Scale, slot.Move } };
            slot.Root = root;
            slot.Dot = new Ellipse { Width = DotSize, Height = DotSize, Fill = Ui.Label2, RenderTransform = slot.DotMove, IsHitTestVisible = false, Visibility = item.Running ? Visibility.Visible : Visibility.Collapsed };
            Wire(slot, delegate { Clicked(slot.Item, false); }, delegate { Clicked(slot.Item, true); });
            return slot;
        }

        // Кнопка меню приложений: пиксельный значок дока на стеклянной плитке из kit.txt
        Slot LauncherSlot()
        {
            var slot = new Slot { Kind = SlotKind.Launcher, W = size };
            var tile = new Viewbox { Width = size * 0.92, Height = size * 0.92, Child = Ui.Tile(Ui.Sprite(DockSprite, 34)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var root = new Grid { Width = size, Height = size, Background = Brushes.Transparent, RenderTransformOrigin = new Point(0.5, 1), Children = { tile } };
            root.RenderTransform = new TransformGroup { Children = { slot.Scale, slot.Move } };
            slot.Root = root;
            Wire(slot, delegate { ToggleApps(); }, null);
            return slot;
        }

        Slot LineSlot()
        {
            var line = new Rectangle { Width = 1, Height = Math.Round(size * 0.72), Fill = Ui.Fill2, IsHitTestVisible = false };
            var slot = new Slot { Kind = SlotKind.Line, Root = line, W = 1 };
            line.RenderTransform = slot.Move;
            return slot;
        }

        FrameworkElement MediaButton(string action, double h, double glyph)
        {
            var b = new Border { Width = h + 2, Height = h - 6, CornerRadius = new CornerRadius((h - 6) / 2), Background = Brushes.Transparent, Child = Glyphs.Media(action, media != null && media.Playing, glyph) };
            b.MouseEnter += delegate { b.Background = Ui.Fill2; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; WidgetAction("music", action); };
            return b;
        }

        // Виджет: стеклянная капсула в виде дока. tight — места мало, длинные надписи короче.
        Slot WidgetSlot(string id, bool tight)
        {
            double h = Math.Round(Math.Max(24, Math.Min(34, size * 0.64)));
            double font = h >= 28 ? Tokens.Size.Body : Tokens.Size.Caption, glyph = Math.Round(h * 0.56), pad = Math.Round(h * 0.38), space = Math.Round(h * 0.22);
            var slot = new Slot { Kind = SlotKind.Widget, Widget = id };
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Action<FrameworkElement> icon = delegate(FrameworkElement g) { g.Margin = new Thickness(g.Margin.Left, 0, space, 0); row.Children.Add(g); };
            Func<string, Brush, TextBlock> text = delegate(string t, Brush color)
            {
                TextBlock tb = Ui.T(t, font, color, FontWeights.SemiBold);
                tb.VerticalAlignment = VerticalAlignment.Center;
                tb.Margin = new Thickness(0, 0, 0, 1);
                return tb;
            };
            UIElement content = row;
            DateTime now = Today;

            if (id == "music")
            {
                icon(Glyphs.Playing(media.Playing, Math.Round(glyph * 0.78)));
                TextBlock t = text(media.Line, Ui.Label);
                t.MaxWidth = tight ? MusicTextTight : MusicText;
                row.Children.Add(t);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
                foreach (string a in new string[] { "prev", "toggle", "next" }) buttons.Children.Add(MediaButton(a, h, Math.Round(glyph * 0.82)));
                row.HorizontalAlignment = HorizontalAlignment.Center;
                content = new Grid { MinWidth = 3 * (h + 2), Children = { row, buttons } };
                slot.Hover = delegate(bool on) { row.Opacity = on ? 0 : 1; buttons.Visibility = on ? Visibility.Visible : Visibility.Collapsed; };
                slot.Tip = media.Tip;
            }
            else if (id == "calendar")
            {
                List<CalendarEvent> left = calendar.Remaining(now.Hour * 60 + now.Minute);
                icon(Glyphs.Calendar(glyph));
                TextBlock t = text("", Ui.Label);
                if (left[0].When.Length > 0) t.Inlines.Add(new Run(left[0].When));
                if (left[0].Title.Length > 0) t.Inlines.Add(new Run((left[0].When.Length > 0 ? "  " : "") + left[0].Title) { Foreground = Ui.Label2, FontWeight = FontWeights.Normal });
                t.MaxWidth = tight ? CalendarTextTight : CalendarText;
                row.Children.Add(t);
                slot.Tip = CalendarInfo.Tip(left);
            }
            else if (id == "weather")
            {
                icon(Glyphs.Weather(weather.Icon, glyph + 2));
                row.Children.Add(text(weather.TempText, Ui.Label));
                slot.Tip = weather.Tip;
            }
            else if (id == "volume")
            {
                icon(Glyphs.Volume(VolumeMath.Bars(volLevel, volMuted), volMuted, glyph + 1));
                TextBlock t = text(VolumeMath.Percent(volLevel) + "%", volMuted ? Ui.Label3 : Ui.Label), widest = text("00%", Ui.Label);
                widest.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                t.MinWidth = Math.Ceiling(widest.DesiredSize.Width);   // от 0 до 99 капсула не меняет ширину — док не дёргается под колесом
                row.Children.Add(t);
                slot.Tip = VolumeMath.Tip(volLevel, volMuted);
            }
            else if (id == "battery")
            {
                icon(Glyphs.Battery(battery.Percent, battery.Charging, Math.Round(h * 0.4)));
                row.Children.Add(text(battery.Percent + "%", Ui.Label));
                slot.Tip = battery.Tip;
            }
            else
            {
                row.Children.Add(text(now.ToString("HH:mm", CultureInfo.InvariantCulture), Ui.Label));
                slot.Tip = now.ToString("dddd, d MMMM yyyy", new CultureInfo("ru-RU"));
            }

            Border capsule = Glyphs.Capsule(content, h, pad);
            capsule.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            slot.W = Math.Ceiling(capsule.DesiredSize.Width);
            var root = new Grid { Width = slot.W, Height = size, Background = Brushes.Transparent, RenderTransform = slot.Move, Children = { capsule } };
            root.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; OpenMenu(null); };
            if (id == "volume")
            {
                root.MouseWheel += delegate(object s, MouseWheelEventArgs e) { e.Handled = true; VolumeWheel(e.Delta); };
                root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
                root.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; VolumeClick(); };
            }
            slot.Root = root;
            return slot;
        }

        List<Slot> WidgetSlots(List<string> ids, bool tight)
        {
            var list = new List<Slot>();
            foreach (string id in ids) list.Add(WidgetSlot(id, tight));
            return list;
        }

        void Rebuild()
        {
            // Что было под курсором — чтобы подпись не моргала, когда док перерисовывается (сменилась минута, покрутили громкость)
            string keep = hover >= 0 && hover < slots.Count && !menuOpen ? slots[hover].Id : null;
            widgetsDirty = false;
            signature = Logic.Signature(items);
            int count = items.Count + 1;   // значки и кнопка меню приложений
            bool split = items.Exists(delegate(DockItem i) { return i.Pinned; }) && items.Exists(delegate(DockItem i) { return !i.Pinned; });

            DateTime now = Today;
            List<string> ids = WidgetList.Visible(store.On, media != null, calendar != null && calendar.Remaining(now.Hour * 60 + now.Minute).Count > 0, weather != null, hasVolume, battery.Has);

            // Не помещается — сначала короче надписи виджетов, потом виджеты уходят по одному слева, потом уменьшаются значки
            double room = work.Width - 2 * PadH - 2 * 24, want = store.Size;
            bool tight = false;
            List<Slot> widgets;
            for (;;)
            {
                size = want;
                gap = Math.Round(size * 0.17);
                widgets = WidgetSlots(ids, tight);
                double taken = (split ? 1 + gap : 0) + (widgets.Count > 0 ? 1 + gap : 0);
                foreach (Slot w in widgets) taken += w.W + gap;
                double fit = Math.Floor((room - taken) / (count * 1.17));
                if (fit >= Math.Min(want, 32) || ids.Count == 0)
                {
                    size = Math.Max(20, Math.Min(want, fit));
                    break;
                }
                if (!tight) tight = true; else ids.RemoveAt(0);
            }
            if (size != want)
            {
                gap = Math.Round(size * 0.17);
                widgets = WidgetSlots(ids, tight);
            }
            shown = ids;

            stage.Children.Clear();
            slots.Clear();
            stage.Children.Add(panel);
            slots.Add(LauncherSlot());
            bool lineDone = false;
            foreach (var item in items)
            {
                if (split && !item.Pinned && !lineDone)
                {
                    lineDone = true;
                    slots.Add(LineSlot());
                }
                slots.Add(IconSlot(item));
            }
            if (widgets.Count > 0) slots.Add(LineSlot());
            slots.AddRange(widgets);
            // Черты и виджеты — на полосе, точки — под значками, значки — поверх, подпись — над всем
            foreach (var s in slots) if (!s.Icon) stage.Children.Add(s.Root);
            foreach (var s in slots) if (s.Icon) { if (s.Dot != null) stage.Children.Add(s.Dot); stage.Children.Add(s.Root); }
            stage.Children.Add(tip);

            rest = new double[slots.Count];
            restW = 0;
            for (int i = 0; i < slots.Count; i++) { rest[i] = slots[i].W; restW += rest[i] + gap; }

            double panelH = PadTop + size + DotZone;
            winW = Math.Min(work.Width, restW + 2 * PadH + size * 4 + 160);
            winH = EdgeGap + panelH + Math.Max(size * (MaxScale - 1) + TipGap + TipH, WidgetTipGap + TipMaxH) + TopRoom;
            baseline = winH - EdgeGap - DotZone;
            panelTop = baseline - size - PadTop;
            panel.Height = panelH;
            Canvas.SetTop(panel, panelTop);
            stage.Width = winW; stage.Height = winH;
            Width = winW; Height = winH;
            Left = work.Left + (work.Width - winW) / 2;
            Top = work.Bottom - winH;

            hover = -1; tipOverride = null;
            tip.BeginAnimation(OpacityProperty, null);
            tip.Opacity = 0;
            Point p;
            cursorU = inside && CursorHere(out p) ? p.X - Origin : restW / 2;
            lastU = lastAmount = double.NaN;
            Place();
            int again = keep == null ? -1 : slots.FindIndex(delegate(Slot s) { return s.Id == keep; });
            if (again >= 0)
            {
                hover = again;
                if (slots[again].Hover != null) slots[again].Hover(true);
                if (FillTip()) tip.Opacity = 1;
            }
            if (hidden) { slide.BeginAnimation(TranslateTransform.YProperty, null); slide.Y = HiddenY; }
            if (hub != null) hub.PublishWidgets(shown, Badges());
        }

        // Счётчики, которые сейчас нарисованы: для состояния dock.widgets
        List<KeyValuePair<string, int>> Badges()
        {
            var list = new List<KeyValuePair<string, int>>();
            foreach (var i in items)
            {
                int n = BadgeFor(i);
                if (n > 0) list.Add(new KeyValuePair<string, int>(i.Name, n));
            }
            return list;
        }

        double HiddenY { get { return winH - panelTop + 30; } }   // полоса с тенью целиком уходит за нижний край окна
        double Origin { get { return (winW - restW) / 2; } }      // левый край ряда в покое, в координатах окна

        // Расставляет значки по текущим cursorU и amount
        void Place()
        {
            if (cursorU == lastU && amount == lastAmount) return;
            lastU = cursorU; lastAmount = amount;
            int n = slots.Count;
            var scale = new double[n];
            double a = 0, range = RangeSlots * (size + gap);
            for (int i = 0; i < n; i++)
            {
                double centre = a + (rest[i] + gap) / 2;
                scale[i] = !slots[i].Icon ? 1 : 1 + amount * (Magnify.Scale(cursorU - centre, range, MaxScale) - 1);
                a += rest[i] + gap;
            }
            double from, to;
            double[] left = Magnify.Layout(rest, scale, gap, cursorU, out from, out to);
            double origin = Origin;
            panelLeft = origin + from - PadH; panelRight = origin + to + PadH;
            Canvas.SetLeft(panel, panelLeft);
            panel.Width = Math.Max(0, panelRight - panelLeft);
            for (int i = 0; i < n; i++)
            {
                Slot s = slots[i];
                s.Left = origin + left[i]; s.Now = scale[i];
                if (s.Kind == SlotKind.Line)
                {
                    s.Move.X = s.Left;
                    s.Move.Y = baseline - size + (size - ((Rectangle)s.Root).Height) / 2;
                    continue;
                }
                if (s.Kind == SlotKind.Widget)
                {
                    s.Move.X = s.Left;
                    s.Move.Y = baseline - size;
                    continue;
                }
                s.Scale.ScaleX = s.Scale.ScaleY = scale[i];
                s.Move.X = s.Left + size * (scale[i] - 1) / 2;   // значок растёт от своего низа, поэтому сдвигаем на половину прироста
                s.Move.Y = baseline - size;
                if (s.Dot == null) continue;
                s.DotMove.X = s.Left + size * scale[i] / 2 - DotSize / 2;
                s.DotMove.Y = baseline + (DotZone - DotSize) / 2 - 1;
            }
            PlaceTip();
        }

        string TipFor(Slot s)
        {
            return s.Kind == SlotKind.Item ? s.Item.Name : s.Kind == SlotKind.Launcher ? AppsTip : s.Kind == SlotKind.Widget ? s.Tip : null;
        }

        void PlaceTip()
        {
            if (hover < 0 || hover >= slots.Count || slots[hover].Kind == SlotKind.Line) return;
            Slot s = slots[hover];
            double w = tip.DesiredSize.Width, h = tip.DesiredSize.Height;
            double x = s.Left + s.W * s.Now / 2 - w / 2;
            double top = s.Icon ? baseline - size * s.Now - TipGap - h : panelTop - WidgetTipGap - h;
            Canvas.SetLeft(tip, Math.Max(2, Math.Min(winW - w - 2, x)));
            Canvas.SetTop(tip, Math.Max(0, top));
        }

        // Пишет подпись для того, что под курсором, и ставит её на место. false — подписывать нечего.
        bool FillTip()
        {
            string text = hover >= 0 && hover < slots.Count ? tipOverride ?? TipFor(slots[hover]) : null;
            if (string.IsNullOrEmpty(text)) return false;
            tipText.Text = text;
            tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            PlaceTip();
            return true;
        }

        void SetHover(int index)
        {
            if (index == hover) return;
            if (hover >= 0 && hover < slots.Count && slots[hover].Hover != null) slots[hover].Hover(false);
            hover = index;
            tipOverride = null;
            if (hover >= 0 && slots[hover].Hover != null) slots[hover].Hover(true);
            ShowTip();
        }

        void ShowTip()
        {
            bool on = hover >= 0 && !menuOpen && FillTip();
            double to = on ? 1 : 0;
            if (Ui.ReducedMotion) { tip.BeginAnimation(OpacityProperty, null); tip.Opacity = to; return; }
            tip.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(on ? Tokens.Motion.FastMs : Tokens.Motion.PressMs)) { EasingFunction = Ui.EaseOut });
        }

        // ───────── Движение ─────────
        // Кадры идут, только пока курсор над доком или пружина ещё не успокоилась. В покое док не тратит ничего.
        void StartLoop()
        {
            if (looping) return;
            looping = true;
            lastFrame = clock.ElapsedMilliseconds;
            CompositionTarget.Rendering += Frame;
        }

        void StopLoop()
        {
            if (!looping) return;
            looping = false;
            CompositionTarget.Rendering -= Frame;
        }

        bool CursorHere(out Point p)
        {
            p = new Point();
            if (fake.HasValue) { p = fake.Value; return true; }   // репетиция (--probe): курсор выдуманный, настоящий не трогаем
            try
            {
                Point screen;
                if (!Win.Cursor(out screen) || PresentationSource.FromVisual(this) == null) return false;
                p = PointFromScreen(screen);
                return true;
            }
            catch { return false; }
        }

        // Курсор над доком: над полосой или над выросшими из неё значками
        bool InZone(Point p)
        {
            return p.X >= panelLeft && p.X <= panelRight && p.Y >= panelTop - amount * size * (MaxScale - 1) - 2 && p.Y <= winH + 1;
        }

        void Frame(object sender, EventArgs e)
        {
            long now = clock.ElapsedMilliseconds;
            double dt = Math.Min(0.05, (now - lastFrame) / 1000.0);
            lastFrame = now;

            Point p;
            bool have = CursorHere(out p);
            if (!menuOpen)
            {
                inside = have && !hidden && InZone(p);
                if (inside) cursorU = p.X - Origin;
            }
            bool reduced = Ui.ReducedMotion;
            double target = inside && store.Magnify && !reduced ? 1 : 0;
            if (reduced) { amount = 0; velocity = 0; }   // «Меньше движения»: значки не растут вовсе
            else
            {
                Magnify.Spring(ref amount, ref velocity, target, Tokens.Motion.SpringShapeStiffness, Tokens.Motion.SpringShapeDamping, dt);
                if (amount < 0) { amount = 0; velocity = 0; }
            }
            Place();

            int over = -1;
            if (inside && !menuOpen)
                for (int i = 0; i < slots.Count; i++)
                {
                    Slot s = slots[i];
                    if (s.Kind != SlotKind.Line && p.X >= s.Left - gap / 2 && p.X < s.Left + s.W * s.Now + gap / 2) { over = i; break; }
                }
            if (!menuOpen) SetHover(over);

            if (!inside && !menuOpen && Math.Abs(amount - target) < 0.0005 && Math.Abs(velocity) < 0.005)
            {
                amount = 0; velocity = 0;
                Place();
                StopLoop();
            }
        }

        // ───────── Автоскрытие ─────────
        void ApplyAutoHide()
        {
            if (demo) return;
            if (store.AutoHide)
            {
                lastInside = clock.ElapsedMilliseconds;
                if (!edge.IsEnabled) edge.Start();
            }
            else
            {
                edge.Stop();
                SetHidden(false);
            }
        }

        // Десять раз в секунду, и только при включённом автоскрытии: где курсор
        void EdgeTick()
        {
            Point p;
            if (fullscreen || !CursorHere(out p)) return;
            long now = clock.ElapsedMilliseconds;
            if (hidden)
            {
                // Курсор лёг на нижний край под доком и задержался там — возвращаемся. Проскочил мимо (к панели задач) — нет.
                bool onEdge = p.X >= panelLeft && p.X <= panelRight && p.Y >= winH - 3 && p.Y <= winH + 1;
                edgeHits = onEdge ? edgeHits + 1 : 0;
                if (edgeHits >= 2) { edgeHits = 0; lastInside = now; SetHidden(false); StartLoop(); }
            }
            else if (menuOpen || InZone(p)) lastInside = now;
            else if (now - lastInside > HideAfterMs) SetHidden(true);
        }

        void SetHidden(bool hide)
        {
            if (hidden == hide) return;
            hidden = hide;
            double to = hide ? HiddenY : 0;
            if (Ui.ReducedMotion) { slide.BeginAnimation(TranslateTransform.YProperty, null); slide.Y = to; return; }
            slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(hide ? Tokens.Motion.BaseMs : Tokens.Motion.SlowMs)) { EasingFunction = hide ? (IEasingFunction)new CubicEase { EasingMode = EasingMode.EaseIn } : Ui.EaseOut });
        }

        // ───────── Щелчки ─────────
        // click — левая кнопка, middle — средняя (null — ничего)
        void Wire(Slot slot, Action click, Action middle)
        {
            FrameworkElement root = slot.Root;
            bool pressed = false;
            Action<bool> press = delegate(bool on) { pressed = on; root.Opacity = on ? 0.6 : 1; };
            root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; press(true); };
            root.MouseLeave += delegate { press(false); };
            root.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                bool was = pressed;
                press(false);
                if (was) click();
            };
            root.MouseDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ChangedButton != MouseButton.Middle || middle == null) return;
                e.Handled = true;
                middle();
            };
            root.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; OpenMenu(slot.Kind == SlotKind.Item ? slot : null); };
        }

        // Щелчок: запущено — вывести вперёд (а если уже впереди — свернуть), нет — запустить. Средняя кнопка — ещё один экземпляр.
        void Clicked(DockItem item, bool fresh)
        {
            if (demo || item == null) return;
            try
            {
                IntPtr target = IntPtr.Zero;
                Click what = fresh ? Click.Launch : Logic.Decide(item, Win.Foreground(), out target);
                if (what == Click.Launch) Shell.Launch(item);
                else if (what == Click.Minimize) Win.Minimize(target);
                else Win.Activate(target);
            }
            catch
            {
                tipOverride = "Не удалось запустить";
                ShowTip();
            }
        }

        // Меню приложений: открыть, а если уже открыто — закрыть
        void ToggleApps()
        {
            if (appMenu != null) { appMenu.Close(); return; }
            var menu = new AppMenuWindow(demo);
            appMenu = menu;
            menu.Closed += delegate { if (appMenu == menu) appMenu = null; };
            SetHover(-1);
            menu.Show();
            menu.Activate();
        }

        MenuWindow BuildMenu(Slot slot)
        {
            var menu = new MenuWindow();
            DockItem item = slot != null ? slot.Item : null;
            if (item != null)
            {
                menu.Header(Ui.T(item.Name, Tokens.Size.Body, Ui.Label2, FontWeights.SemiBold));
                if (item.Pinned) menu.Item("Открепить", false, delegate { store.UnpinItem(item); SettingsChanged(); });
                else menu.Item("Закрепить", false, item.CanPin ? new Action(delegate { store.PinItem(item); SettingsChanged(); }) : null);
                if (item.Running) menu.Item("Закрыть окно", false, delegate { if (!demo && item.Windows.Count > 0) Win.Close(item.Windows[0]); });
                menu.Line();
            }
            var mark = Ui.Sprite(DockSprite, 18);
            var brand = Ui.Brand("dock", Tokens.Size.Heading, FontWeights.SemiBold);
            brand.Margin = new Thickness(8, 0, 0, 1); brand.VerticalAlignment = VerticalAlignment.Center;
            menu.Header(new StackPanel { Orientation = Orientation.Horizontal, Children = { mark, brand } });
            menu.Item("Автоскрытие", store.AutoHide, delegate { store.AutoHide = !store.AutoHide; store.Save(); SettingsChanged(); });
            menu.Item("Вместо панели задач Windows", store.ReplaceTaskbar, delegate { store.Bar = store.ReplaceTaskbar ? "both" : "dock"; store.Save(); SettingsChanged(); });
            menu.Item("Компактная панель Windows вместо дока", false, delegate { store.Bar = "modern"; store.Save(); SettingsChanged(); });
            // Значки трея живут в панели задач Windows: показываем её ненадолго, сама спрячется, когда курсор уйдёт
            if (TaskbarSwap.Active) menu.Item("Значки в трее", false, delegate { TaskbarSwap.Peek(); });
            bool online = hub != null && hub.Online;
            // Настройки дока живут в core.txt: открываем его, а если его нет — предлагаем скачать
            menu.Item(online ? "Настройки" : CoreApp.Installed ? "Настройки — открыть core.txt" : "Скачать core.txt — настройки и остальные приложения", false,
                delegate { CoreApp.Go(online, delegate { hub.ShowCore(); }); });
            menu.Item("Выход", false, delegate { Quit(); });
            return menu;
        }

        void OpenMenu(Slot slot)
        {
            if (menuOpen) return;
            MenuWindow menu = BuildMenu(slot);
            menuOpen = true;
            ShowTip();
            menu.Closed += delegate
            {
                menuOpen = false;
                lastInside = clock.ElapsedMilliseconds;
                StartLoop();   // курсор мог уйти, пока меню было открыто, — пусть значки вернутся на место
            };
            double cx = slot != null ? slot.Left + size * slot.Now / 2 : (panelLeft + panelRight) / 2;
            Point p;
            if (slot == null && CursorHere(out p)) cx = p.X;
            double top = slot != null ? baseline - size * slot.Now : panelTop;
            menu.ShowAbove(Left + cx, Top + top - 6, work);
        }

        // ───────── Для снимков и проверки ─────────
        // Выдуманные значки: ни окна, ни диск, ни хаб не трогаются
        static List<DockItem> Demo()
        {
            var list = new List<DockItem>();
            string[] pinned = { "Проводник", "Браузер", "Discord", "Steam", "Музыка", "Заметки" };
            for (int i = 0; i < pinned.Length; i++)
            {
                var it = new DockItem { Key = "demo-" + i, Name = pinned[i], Path = @"C:\demo\" + pinned[i] + ".exe", Pinned = true };
                if (i == 0 || i == 1 || i == 2 || i == 4) it.Windows.Add(new IntPtr(i + 1));
                list.Add(it);
            }
            string[] running = { "Калькулятор", "Фотографии" };
            for (int i = 0; i < running.Length; i++)
            {
                var it = new DockItem { Key = "demo-run-" + i, Name = running[i], Path = @"C:\demo\run-" + i + ".exe" };
                it.Windows.Add(new IntPtr(100 + i));
                list.Add(it);
            }
            return list;
        }

        // Выдуманные виджеты. Те, что приходят из хаба, собираются из таких же сообщений, какие пришлёт Kapsula.
        void DemoWidgets(bool hubData)
        {
            battery = BatteryInfo.From(0, 0, 82);
            hasVolume = true; volLevel = 0.4;
            if (!hubData) return;
            media = MediaInfo.From(HubLink.Msg("playing", true, "title", "Midnight City", "artist", "M83", "app", "Spotify"));
            weather = WeatherInfo.From(HubLink.Msg("temp", -3.4, "text", "Снег", "icon", "snow", "city", "Москва"));
            calendar = CalendarInfo.From(HubLink.Msg("events", new object[]
            {
                HubLink.Msg("title", "Созвон с командой", "start", "14:30", "end", "15:00"),
                HubLink.Msg("title", "Обед", "start", "15:00", "end", "15:45"),
                HubLink.Msg("title", "Ревью макетов", "start", "17:00", "end", "18:00")
            }));
            unread = UnreadInfo.From(HubLink.Msg("total", 15, "apps", HubLink.Msg("discord", 3, "steam", 12)));
        }

        // Рисует док в файл PNG, не показывая его на экране. over — над каким значком будто бы курсор (−1 — покой);
        // overWidget — над каким виджетом (null — ни над каким).
        public void Shot(string file, int over, string overWidget, bool withMenu)
        {
            Content = null;
            int index = -1, n = 0;
            for (int i = 0; i < slots.Count && over >= 0; i++)
                if (slots[i].Kind == SlotKind.Item && n++ == over) index = i;
            if (overWidget != null) index = slots.FindIndex(delegate(Slot s) { return s.Kind == SlotKind.Widget && s.Widget == overWidget || s.Kind == SlotKind.Launcher && overWidget == "apps"; });
            if (index >= 0)
            {
                double a = 0;
                for (int i = 0; i < index; i++) a += rest[i] + gap;
                cursorU = a + (rest[index] + gap) / 2;
                amount = store.Magnify && !Ui.ReducedMotion ? 1 : 0;
                Place();
                hover = index;
                if (slots[index].Hover != null) slots[index].Hover(true);
                FillTip();
                tip.Opacity = withMenu ? 0 : 1;
            }
            // Сверху — ровно столько места, сколько нужно подписи: окно выше из-за многострочных подписей виджетов
            double above = index >= 0 && !withMenu && !slots[index].Icon ? WidgetTipGap + tip.DesiredSize.Height : size * (MaxScale - 1) + TipGap + TipH;
            double w = winW + 40, h = EdgeGap + PadTop + size + DotZone + above + TopRoom + 36 + (withMenu ? 170 : 0);
            var root = new Grid { Width = w, Height = h, Background = Ui.Bg, ClipToBounds = true };
            var glow = Ui.Backdrop();
            glow.RenderTransform = new TranslateTransform(0, -260);
            root.Children.Add(glow);
            stage.HorizontalAlignment = HorizontalAlignment.Center; stage.VerticalAlignment = VerticalAlignment.Top;
            stage.Margin = new Thickness(0, h - winH, 0, 0);
            root.Children.Add(stage);
            if (index >= 0 && withMenu)
            {
                // Меню — то же самое, что по правому щелчку, только нарисованное в этот же снимок
                MenuWindow menu = BuildMenu(slots[index].Kind == SlotKind.Item ? slots[index] : null);
                var box = (FrameworkElement)menu.Content;
                menu.Content = null;
                box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                box.HorizontalAlignment = HorizontalAlignment.Left; box.VerticalAlignment = VerticalAlignment.Top;
                box.Margin = new Thickness((w - winW) / 2 + slots[index].Left + size * slots[index].Now / 2 - (box.DesiredSize.Width - 36) / 2, h - winH + baseline - size * slots[index].Now - 6 - (box.DesiredSize.Height - 36), 0, 0);
                root.Children.Add(box);
            }
            Program.Save(root, w, h, 2, file);
        }

        // Одна строка о том, что док увидел на этом компьютере
        public string Probe()
        {
            int pinned = 0, running = 0, windows = 0, icons = 0;
            foreach (var i in items)
            {
                if (i.Pinned) pinned++;
                if (i.Running) running++;
                windows += i.Windows.Count;
                if ((Shell.Icon(i.Icon) ?? Shell.Icon(i.Path)) != null) icons++;
            }
            return "dock.txt " + Program.VersionText + ": items " + items.Count + " (pinned " + pinned + ", running " + running + "), windows " + windows + ", icons " + icons + "/" + items.Count
                + ", icon size " + size + ", window " + Math.Round(Width) + "x" + Math.Round(Height) + " at " + Math.Round(Left) + "," + Math.Round(Top) + ", hub " + (hub != null && hub.Online ? "online" : "offline")
                + ", fullscreen " + (fullscreen ? "yes" : "no") + ", shared reduced motion " + (Ui.ForceReducedMotion ? "yes" : "no") + ", auto-hide " + (store.AutoHide ? "on" : "off")
                + "\r\nwidgets: " + string.Join(", ", shown.ToArray()) + "; volume " + (hasVolume ? VolumeMath.Percent(volLevel) + "%" + (volMuted ? " muted" : "") : "not available")
                + "; battery " + (battery.Has ? battery.Percent + "%" + (battery.Charging ? " charging" : battery.Plugged ? " plugged" : "") : "none") + "; badges " + Badges().Count
                + "\r\n" + AppCatalog.Summary();
        }

        // Репетиция для --probe: выдуманный курсор проходит по доку слева направо и уходит, затем док прячется и возвращается.
        // Настоящий курсор и чужие окна не трогаются, громкость не меняется. В конце — строка о том, что получилось.
        Point? fake;

        public void Rehearse(Action<string> done)
        {
            int step = 0, hovers = 0, lastHover = -2, menuRows = 0, widgetTips = 0;
            double maxAmount = 0, maxScale = 1, maxTip = 0, widest = 0, hiddenAt = 0;
            bool loopedWhileOver = false, settled = false, launcherTip = false;
            double restWidth = panelRight - panelLeft;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            t.Tick += delegate
            {
                step++;
                if (step <= 30)
                {
                    // 1,2 секунды над доком
                    fake = new Point(Origin + restW * (step - 1) / 29.0, baseline - size / 2);
                    StartLoop();
                    loopedWhileOver |= looping;
                    maxAmount = Math.Max(maxAmount, amount);
                    foreach (var s in slots) maxScale = Math.Max(maxScale, s.Now);
                    maxTip = Math.Max(maxTip, tip.Opacity);
                    widest = Math.Max(widest, panelRight - panelLeft);
                    if (hover != lastHover)
                    {
                        lastHover = hover;
                        if (hover >= 0 && slots[hover].Kind == SlotKind.Item) hovers++;
                        if (hover >= 0 && slots[hover].Kind == SlotKind.Widget && tipText.Text == slots[hover].Tip) widgetTips++;
                        if (hover >= 0 && slots[hover].Kind == SlotKind.Launcher && tipText.Text == AppsTip) launcherTip = true;
                    }
                }
                else if (step == 31)
                {
                    try { var m = BuildMenu(slots.Find(delegate(Slot s) { return s.Kind == SlotKind.Item; })); menuRows = ((Panel)((Border)((Border)m.Content).Child).Child).Children.Count; m.Close(); } catch { menuRows = -1; }
                    fake = new Point(-5000, -5000);
                }
                else if (step == 60) { settled = !looping && amount == 0 && hover == -1 && Math.Abs(panelRight - panelLeft - restWidth) < 0.01; SetHidden(true); }
                else if (step == 72) { hiddenAt = slide.Y; SetHidden(false); }
                else if (step == 86)
                {
                    t.Stop();
                    fake = null;
                    done("rehearsal: loop ran " + (loopedWhileOver ? "yes" : "no") + ", max amount " + maxAmount.ToString("0.00") + ", max icon scale " + maxScale.ToString("0.00") + ", labels shown for " + hovers + " icons (max opacity " + maxTip.ToString("0.00")
                        + "), apps button label " + (launcherTip ? "yes" : "no") + ", widget labels " + widgetTips + " of " + shown.Count
                        + ", bar width " + Math.Round(restWidth) + " -> " + Math.Round(widest) + ", back to rest and loop stopped " + (settled ? "yes" : "no") + ", menu rows " + menuRows
                        + ", hidden offset " + Math.Round(hiddenAt) + " of " + Math.Round(HiddenY) + ", shown again at " + Math.Round(slide.Y));
                }
            };
            t.Start();
        }

        public string ProbeNames()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var i in items) sb.Append(i.Pinned ? "[pin] " : "[run] ").Append(i.Name).Append(i.Running ? " *" + i.Windows.Count : "").Append("; ");
            return sb.ToString();
        }
    }
}
