// dock.txt. DockWindow.cs — сам док: стеклянная полоса внизу экрана. Слева — кнопка меню приложений, за ней значки
// (точки под запущенными, красные счётчики непрочитанного), справа за чертой — виджеты: музыка, календарь, погода,
// громкость, батарея, часы. Увеличение под курсором, подпись над значком, автоскрытие, меню по правому щелчку.
// Значки и виджеты живут, пока они есть в доке: когда что-то меняется (минута, громкость, песня, открыли или закрыли
// окно), док не собирается заново — меняется только то, что изменилось, новое проявляется, ушедшее гаснет,
// соседи плавно съезжаются. Окно прозрачное, во всю ширину экрана; прозрачные места щелчки пропускают.
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
        public string Id;                 // по нему место узнаётся между перерисовками: «item:путь», «widget:clock», «line:split»
        public DockItem Item;             // для значка приложения
        public string Widget;             // для виджета: music, calendar, weather, volume, battery, clock
        public string Tip;                // подпись виджета
        public Action<bool> Hover;        // показать или спрятать то, что видно только под курсором (подсветка, кнопки музыки)
        public Action Update;             // виджет: обновить содержимое на месте — текст, значок
        public FrameworkElement Root, Badge;
        public string BadgeText;
        public Border Dot;
        public readonly ScaleTransform Scale = new ScaleTransform(1, 1);
        public readonly TranslateTransform Move = new TranslateTransform(), DotMove = new TranslateTransform();
        public double W, Left, Centre, CellL, CellR, Now = 1;   // ширина в покое; где стоит и во сколько раз увеличен прямо сейчас
        public double Built;              // для какого размера значков собрано
        public bool Tight, Leaving;       // Leaving — уже не нужен, гаснет и освобождает место
        // Всё, что меняется плавно: присутствие (0 — нет, 1 — есть), ширина, нажатие, ширина точки под значком
        public readonly Tween Show = new Tween(1), Wide = new Tween(0), Press = new Tween(1), DotW = new Tween(0);
        public long BounceAt = -1;        // когда значок начал подпрыгивать (программа запускается); −1 — не прыгает
        public int BounceStop = -1;       // после какого прыжка остановиться: окно появилось

        public bool Icon { get { return Kind == SlotKind.Item || Kind == SlotKind.Launcher; } }
    }

    class DockWindow : Window
    {
        // Значок дока — тот же рисунок, что в sprite.json (потом переедет в kit.txt, icons/sprites.json, «dock»)
        internal static readonly string[] DockSprite = { "....####....", "....#E##....", ".##.#E##.##.", ".##.####.##.", ".##.####.##.", "............", "############", ".##########." };

        // Свои размеры дока — то, чего в токенах нет
        const double PadH = 6, PadTop = 9, DotZone = 12, EdgeGap = 8, TipGap = 9, TipH = 26, TopRoom = 12, DotSize = 4, DotWide = 14;
        const double WidgetTipGap = 5, TipMaxH = 92;   // подпись виджета бывает в несколько строк (события календаря)
        const double MusicText = 180, CalendarText = 190, MusicTextTight = 110, CalendarTextTight = 120;
        internal const double MaxScale = 1.55, RangeSlots = 2.6;
        const int HideAfterMs = 700, EdgeMs = 50, SlowPollTicks = 5;
        // Движение: появление и переезд значков, уход, нажатие (вниз быстро, обратно мягко), прыжок при запуске, виджеты
        const int AppearMs = 250, LeaveMs = 180, PressDownMs = 70, PressUpMs = 220, BounceMs = 560, BounceCycles = 3, WidgetFadeMs = 120;
        const double PressScale = 0.92, BounceHeight = 0.2;
        const int MenuTitleChars = 38;
        const string AppsTip = "Приложения";

        readonly bool demo, headless;
        readonly Store store;
        readonly Canvas stage = new Canvas();
        readonly TranslateTransform slide = new TranslateTransform();
        readonly Border panel, tip;
        readonly Rectangle strip;
        readonly TextBlock tipText;
        readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer edge = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(EdgeMs) };
        readonly DispatcherTimer minute = new DispatcherTimer();
        readonly Stopwatch clock = Stopwatch.StartNew();
        Rect work = SystemParameters.WorkArea;
        DockHub hub;

        List<DockItem> items = new List<DockItem>();
        readonly List<Slot> slots = new List<Slot>();
        readonly Dictionary<IntPtr, string> demoTitles = new Dictionary<IntPtr, string>();
        string signature;
        double size, gap, winW, winH, restW, baseline, panelTop, panelLeft, panelRight, lastHiddenY = double.NaN;
        IntPtr fg;        // окно, которое сейчас впереди: под его значком точка шире и ярче
        Slot pressed;     // значок или виджет, на котором сейчас держат кнопку мыши

        // Виджеты: что пришло из хаба от kapsula.txt и что док узнал у Windows сам
        MediaInfo media;
        WeatherInfo weather;
        CalendarInfo calendar;
        UnreadInfo unread;
        BatteryInfo battery = BatteryInfo.From(0, 255, 255);
        readonly Dictionary<string, string> hubRaw = new Dictionary<string, string>();
        Audio audio;
        bool hasVolume, volMuted, widgetsDirty, ended, fgPending;
        volatile bool volPending;
        double volLevel;
        int ticks;
        List<string> shown = new List<string>();
        AppMenuWindow appMenu;

        // Увеличение: одна пружина от 0 (покой) до 1 (курсор над доком). Сами значки идут за курсором без задержки.
        double amount, velocity, cursorU, lastU = double.NaN, lastAmount = double.NaN;
        bool looping, inside, menuOpen, hidden, fullscreen, away, moving;
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
                CornerRadius = new CornerRadius(Tokens.Radius.Block), Background = Ui.Solid(Tokens.Dark.Bg), SnapsToDevicePixels = true, IsHitTestVisible = false,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = 0.38, Color = Colors.Black },
                Child = new Border { CornerRadius = new CornerRadius(Tokens.Radius.Block), Background = Brushes.Transparent, BorderBrush = Ui.Rim, BorderThickness = new Thickness(1) }
            };
            // Одна полоса на все щелчки: от верха выросших значков до нижнего края экрана. Столбец под значком — это тот же
            // значок, поэтому курсор, упёртый в край экрана, попадает в цель. Полоса почти прозрачная, но не совсем:
            // совсем прозрачные точки Windows отдаёт окну под доком.
            strip = new Rectangle { Fill = Ui.Solid(Color.FromArgb(1, 0, 0, 0)) };
            tipText = Ui.T("", Tokens.Size.Caption, Ui.Label, FontWeights.SemiBold);
            tipText.VerticalAlignment = VerticalAlignment.Center;
            tipText.TextTrimming = TextTrimming.None;
            tip = new Border
            {
                MinHeight = TipH, CornerRadius = new CornerRadius(Tokens.Radius.Tip), Background = Ui.Solid(Tokens.Dark.Tooltip), BorderBrush = Ui.Rim, BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 4, 10, 5), Child = tipText, Opacity = 0, IsHitTestVisible = false, SnapsToDevicePixels = true
            };
            // Снизу вверх: полоса, поле для щелчков, черты и виджеты, точки, значки, подпись
            Panel.SetZIndex(strip, 1);
            Panel.SetZIndex(tip, 9);
            stage.Children.Add(panel);
            stage.Children.Add(strip);
            stage.Children.Add(tip);
            stage.RenderTransform = slide;
            Content = stage;

            stage.MouseMove += delegate { StartLoop(); };
            strip.MouseDown += StripDown;
            strip.MouseUp += StripUp;
            strip.MouseLeave += delegate { Release(); };
            strip.MouseWheel += delegate(object s, MouseWheelEventArgs e)
            {
                Slot at = SlotAt(e.GetPosition(this).X);
                if (at == null || at.Widget != "volume") return;
                e.Handled = true;
                VolumeWheel(e.Delta);
            };
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
            // Окно показали, а дока сейчас быть не должно (игра на весь экран, выбрана панель Windows) — сразу убираем
            ContentRendered += delegate { if (away && !headless) Hide(); };

            if (demo)
            {
                items = Demo();
                DemoWidgets(hubData);
            }
            else
            {
                hub = new DockHub(this, store);
                items = Collect();
                fg = Win.Foreground();
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
                // Впереди стало другое окно — узнаём сразу, а не через секунду
                if (!headless) Win.WatchForeground(ForegroundChanged);
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
            WinKey.Sync(store.ReplaceTaskbar && store.WinKey, Dispatcher, WinPressed);
            // Компактная панель Windows: включаем, только когда её выбрали; иначе возвращаем, как было до нас
            bool changed = store.Bar == "modern" ? TaskbarSwap.CompactOn(store.Dir) : TaskbarSwap.CompactBack(store.Dir);
            if (changed) TaskbarSwap.RestartExplorer(null);   // Проводник не закрылся сам — не трогаем: панель сменится при следующем входе
            // Выбрана панель Windows — дока не видно (он остаётся запущенным: настройки, счётчики, меню приложений)
            SyncAway();
        }

        // Одиночное нажатие кнопки Windows. Поверх игры или видео на весь экран меню приложений не открываем
        // (закрыть уже открытое — можно).
        void WinPressed()
        {
            if (ended) return;
            if (appMenu == null && Win.FullscreenAhead()) return;
            ToggleApps();
        }

        // Всё, что тикает и слушает, останавливается один раз
        void End()
        {
            if (ended) return;
            ended = true;
            refresh.Stop(); edge.Stop(); minute.Stop(); taskbarTimer.Stop(); hookTimer.Stop();
            StopLoop();
            Win.Unwatch();
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

        // Раз в секунду (и сразу, когда впереди стало другое окно): смотрим на окна и меняем док, только если что-то изменилось.
        // Раз в пять секунд заодно — батарея и не сменилось ли устройство звука.
        void Refresh()
        {
            bool full = Win.FullscreenAhead();
            fg = Win.Foreground();
            if (full != fullscreen)
            {
                fullscreen = full;   // игра или видео на весь экран — док уезжает за край
                SyncAway();
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
            // Те же значки — обновляем только списки окон и точки под значками
            items = now;
            foreach (DockItem item in items)
            {
                Slot s = Find("item:" + item.Key);
                if (s != null) s.Item = item;
            }
            SyncDots();
        }

        void ForegroundChanged()
        {
            if (fgPending || ended) return;
            fgPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { fgPending = false; if (!ended) Refresh(); }));
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

        // Средняя кнопка на виджете громкости: звук выключается и включается
        void VolumeMute()
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
        // Двигать ли: окно настоящее и на экране, «Меньше движения» выключено. В снимке и без окна всё встаёт на место сразу.
        bool CanAnimate { get { return !Ui.ReducedMotion && IsVisible && PresentationSource.FromVisual(this) != null; } }

        void Go(Tween t, double value, int ms)
        {
            if (CanAnimate) t.Go(value, clock.ElapsedMilliseconds, ms); else t.Set(value);
        }

        // Что-то начало меняться: пусть идут кадры. Двигать нельзя — просто расставляем заново.
        void Kick()
        {
            if (CanAnimate) StartLoop(); else Place(true);
        }

        static void Fade(UIElement e, double to, int ms)
        {
            if (!Ui.Live(e))
            {
                e.BeginAnimation(OpacityProperty, null);
                e.Opacity = to;
                return;
            }
            e.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Ui.EaseOut });
        }

        Slot Find(string id)
        {
            Slot leaving = null;
            foreach (Slot s in slots)
            {
                if (s.Id != id) continue;
                if (!s.Leaving) return s;
                leaving = s;   // уходящий годится, только если живого с таким именем нет: его можно вернуть
            }
            return leaving;
        }

        // То же место из прошлого раза, если оно собрано для того же размера значков
        Slot Reuse(string id)
        {
            Slot s = Find(id);
            return s != null && s.Built == size ? s : null;
        }

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
            var slot = new Slot { Kind = SlotKind.Item, Id = "item:" + item.Key, Item = item, W = size, Built = size };
            var root = new Grid { Width = size, Height = size, RenderTransformOrigin = new Point(0.5, 1), IsHitTestVisible = false, Children = { Face(item) } };
            root.RenderTransform = new TransformGroup { Children = { slot.Scale, slot.Move } };
            slot.Root = root;
            slot.Dot = new Border { Width = 0, Height = DotSize, CornerRadius = new CornerRadius(DotSize / 2), Background = Ui.Label, RenderTransform = slot.DotMove, IsHitTestVisible = false };
            Refit(slot, item);
            return slot;
        }

        // Значок остался тем же, а программа могла измениться: окна, счётчик непрочитанного
        void Refit(Slot slot, DockItem item)
        {
            slot.Item = item;
            string badge = UnreadInfo.Text(BadgeFor(item));
            if (badge == slot.BadgeText) return;
            var root = (Grid)slot.Root;
            if (slot.Badge != null) root.Children.Remove(slot.Badge);
            slot.Badge = badge == null ? null : Glyphs.Badge(badge, size);
            if (slot.Badge != null)
            {
                root.Children.Add(slot.Badge);
                if (slot.BadgeText == null) Motion.Pop(slot.Badge);   // счётчик появился — выскакивает; сменилась цифра — просто меняется
            }
            slot.BadgeText = badge;
        }

        // Кнопка меню приложений: пиксельный значок дока на стеклянной плитке из kit.txt
        Slot LauncherSlot()
        {
            var slot = new Slot { Kind = SlotKind.Launcher, Id = "launcher", W = size, Built = size };
            var tile = new Viewbox { Width = size * 0.92, Height = size * 0.92, Child = Ui.Tile(Ui.Sprite(DockSprite, 34)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var root = new Grid { Width = size, Height = size, RenderTransformOrigin = new Point(0.5, 1), IsHitTestVisible = false, Children = { tile } };
            root.RenderTransform = new TransformGroup { Children = { slot.Scale, slot.Move } };
            slot.Root = root;
            return slot;
        }

        Slot LineSlot(string id)
        {
            var line = new Rectangle { Width = 1, Height = Math.Round(size * 0.72), Fill = Ui.Fill2, IsHitTestVisible = false };
            var slot = new Slot { Kind = SlotKind.Line, Id = id, Root = line, W = 1, Built = size };
            line.RenderTransform = slot.Move;
            return slot;
        }

        Border MediaButton(string action, double h)
        {
            var b = new Border { Width = h + 2, Height = h - 6, CornerRadius = new CornerRadius((h - 6) / 2), Background = Brushes.Transparent };
            b.MouseEnter += delegate { b.Background = Ui.Fill2; };
            b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; b.Background = Ui.Press; };
            b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; b.Background = Ui.Fill2; WidgetAction("music", action); };
            return b;
        }

        // Виджет: стеклянная капсула в виде дока. tight — места мало, длинные надписи короче.
        // Собирается один раз; дальше slot.Update меняет в нём только то, что изменилось.
        Slot WidgetSlot(string id, bool tight)
        {
            double h = Math.Round(Math.Max(24, Math.Min(34, size * 0.64)));
            double font = h >= 28 ? Tokens.Size.Body : Tokens.Size.Caption, glyph = Math.Round(h * 0.56), pad = Math.Round(h * 0.38), space = Math.Round(h * 0.22);
            var slot = new Slot { Kind = SlotKind.Widget, Id = "widget:" + id, Widget = id, Built = size, Tight = tight };
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            // Значок меняется только вместе со своим «отпечатком»: стало больше дуг у динамика, сменилась погода
            var iconHost = new Border { Margin = new Thickness(0, 0, space, 0), VerticalAlignment = VerticalAlignment.Center };
            string iconNow = null;
            Action<string, Func<FrameworkElement>> icon = delegate(string mark, Func<FrameworkElement> make)
            {
                if (mark == iconNow) return;
                iconNow = mark;
                iconHost.Child = make();
            };
            TextBlock label = Ui.T("", font, Ui.Label, FontWeights.SemiBold);
            label.VerticalAlignment = VerticalAlignment.Center;
            label.Margin = new Thickness(0, 0, 0, 1);
            // Цифры меняются, а ширина — нет: док не дёргается под колесом и на смене минуты
            Action<string> widest = delegate(string sample)
            {
                TextBlock probe = Ui.T(sample, font, Ui.Label, FontWeights.SemiBold);
                probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                label.MinWidth = Math.Ceiling(probe.DesiredSize.Width);
            };
            if (id != "clock") row.Children.Add(iconHost);
            Panel over = null;      // то, что лежит поверх капсулы и принимает щелчки само: кнопки музыки
            Action<bool> extra = null;
            Action fill;

            if (id == "music")
            {
                double textW = tight ? MusicTextTight : MusicText;
                label.Width = textW;   // название любой длины — та же ширина: соседи не ездят от песни к песне
                row.Children.Add(label);
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                var faces = new Dictionary<string, Border>();
                foreach (string a in new string[] { "prev", "toggle", "next" })
                {
                    Border b = MediaButton(a, h);
                    b.Child = Glyphs.Media(a, false, Math.Round(glyph * 0.82));
                    faces[a] = b;
                    buttons.Children.Add(b);
                }
                over = new Grid { Width = textW, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, pad + 1, 0), Opacity = 0, IsHitTestVisible = false, Children = { buttons } };
                Panel overNow = over;
                string playNow = null;
                extra = delegate(bool on)
                {
                    // Под курсором название уступает место кнопкам; значок слева остаётся
                    Fade(label, on ? 0 : 1, WidgetFadeMs);
                    Fade(overNow, on ? 1 : 0, WidgetFadeMs);
                    overNow.IsHitTestVisible = on;
                };
                fill = delegate
                {
                    bool playing = media.Playing;
                    icon(playing ? "play" : "pause", delegate { return Glyphs.Playing(playing, Math.Round(glyph * 0.78)); });
                    if (playNow != (playing ? "1" : "0"))
                    {
                        playNow = playing ? "1" : "0";
                        faces["toggle"].Child = Glyphs.Media("toggle", playing, Math.Round(glyph * 0.82));
                    }
                    if (label.Text != media.Line) label.Text = media.Line;   // без движения: под курсором название спрятано за кнопками
                    slot.Tip = media.Tip;
                };
            }
            else if (id == "calendar")
            {
                label.MaxWidth = tight ? CalendarTextTight : CalendarText;
                row.Children.Add(label);
                icon("calendar", delegate { return Glyphs.Calendar(glyph); });
                string lineNow = null;
                fill = delegate
                {
                    DateTime now = Today;
                    List<CalendarEvent> left = calendar.Remaining(now.Hour * 60 + now.Minute);
                    string line = left[0].When + "\n" + left[0].Title;
                    if (line != lineNow)
                    {
                        lineNow = line;
                        label.Inlines.Clear();
                        if (left[0].When.Length > 0) label.Inlines.Add(new Run(left[0].When));
                        if (left[0].Title.Length > 0) label.Inlines.Add(new Run((left[0].When.Length > 0 ? "  " : "") + left[0].Title) { Foreground = Ui.Label2, FontWeight = FontWeights.Normal });
                    }
                    slot.Tip = CalendarInfo.Tip(left);
                };
            }
            else if (id == "weather")
            {
                row.Children.Add(label);
                fill = delegate
                {
                    string kind = weather.Icon;
                    icon(kind, delegate { return Glyphs.Weather(kind, glyph + 2); });
                    Motion.Swap(label, weather.TempText);
                    slot.Tip = weather.Tip;
                };
            }
            else if (id == "volume")
            {
                widest("00%");
                row.Children.Add(label);
                fill = delegate
                {
                    int bars = VolumeMath.Bars(volLevel, volMuted);
                    bool muted = volMuted;
                    icon(bars + (muted ? "m" : ""), delegate { return Glyphs.Volume(bars, muted, glyph + 1); });
                    label.Text = VolumeMath.Percent(volLevel) + "%";
                    label.Foreground = muted ? Ui.Label3 : Ui.Label;
                    slot.Tip = VolumeMath.Tip(volLevel, volMuted);
                };
            }
            else if (id == "battery")
            {
                row.Children.Add(label);
                fill = delegate
                {
                    int percent = battery.Percent;
                    bool charging = battery.Charging;
                    icon(percent + (charging ? "c" : ""), delegate { return Glyphs.Battery(percent, charging, Math.Round(h * 0.4)); });
                    label.Text = percent + "%";
                    slot.Tip = battery.Tip;
                };
            }
            else
            {
                widest("00:00");
                label.TextAlignment = TextAlignment.Center;
                row.Children.Add(label);
                fill = delegate
                {
                    DateTime now = Today;
                    label.Text = now.ToString("HH:mm", CultureInfo.InvariantCulture);
                    slot.Tip = now.ToString("dddd, d MMMM yyyy", new CultureInfo("ru-RU"));
                };
            }

            Border capsule = Glyphs.Capsule(row, h, pad);
            capsule.IsHitTestVisible = false;   // щелчки по виджету ловит общая полоса дока, как и по значкам
            // Подсветка под курсором — у каждой капсулы: видно, что она нажимается
            var glow = new Border { Height = h, CornerRadius = new CornerRadius(h / 2), Background = Ui.Solid(Ui.Alpha(Colors.White, 0.07)), Opacity = 0, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
            var root = new Grid { Height = size, RenderTransformOrigin = new Point(0.5, 0.5), Children = { capsule, glow } };
            root.RenderTransform = new TransformGroup { Children = { slot.Scale, slot.Move } };
            if (over != null)
            {
                root.Children.Add(over);
                root.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; OpenMenu(null); };
            }
            slot.Root = root;
            slot.Hover = delegate(bool on)
            {
                Fade(glow, on ? 1 : 0, WidgetFadeMs);
                if (extra != null) extra(on);
            };
            slot.Update = delegate
            {
                fill();
                capsule.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                slot.W = Math.Ceiling(capsule.DesiredSize.Width);
                root.Width = slot.W;
            };
            slot.Update();
            return slot;
        }

        // Виджеты для этого размера значков: те, что уже стоят в доке, обновляются на месте
        List<Slot> WidgetSlots(List<string> ids, bool tight)
        {
            var list = new List<Slot>();
            foreach (string id in ids)
            {
                Slot s = Reuse("widget:" + id);
                if (s != null && s.Tight == tight) s.Update(); else s = WidgetSlot(id, tight);
                list.Add(s);
            }
            return list;
        }

        void Mount(Slot s)
        {
            Panel.SetZIndex(s.Root, s.Icon ? 4 : 2);
            stage.Children.Add(s.Root);
            if (s.Dot == null) return;
            Panel.SetZIndex(s.Dot, 3);
            stage.Children.Add(s.Dot);
        }

        void Unmount(Slot s)
        {
            stage.Children.Remove(s.Root);
            if (s.Dot != null) stage.Children.Remove(s.Dot);
            if (pressed == s) pressed = null;
        }

        // Приводит док к тому, что должно быть видно сейчас. Ничего не сносит: места, которые остались, остаются
        // теми же самыми (в них меняется только содержимое), новые проявляются, лишние гаснут и освобождают место.
        void Rebuild()
        {
            // Что было под курсором — чтобы подпись и кнопки музыки не пропадали, когда док меняется (минута, громкость)
            Slot keep = hover >= 0 && hover < slots.Count && !menuOpen ? slots[hover] : null;
            long tick = clock.ElapsedMilliseconds;
            double sizeBefore = size;
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

            // Размер значков сменился — всё собирается заново и встаёт сразу; иначе перемены идут плавно
            bool animate = slots.Count > 0 && size == sizeBefore && CanAnimate;
            var next = new List<Slot>();
            next.Add(Reuse("launcher") ?? LauncherSlot());
            bool lineDone = false;
            foreach (var item in items)
            {
                if (split && !item.Pinned && !lineDone)
                {
                    lineDone = true;
                    next.Add(Reuse("line:split") ?? LineSlot("line:split"));
                }
                Slot s = Reuse("item:" + item.Key);
                if (s != null) Refit(s, item); else s = IconSlot(item);
                next.Add(s);
            }
            if (widgets.Count > 0) next.Add(Reuse("line:widgets") ?? LineSlot("line:widgets"));
            next.AddRange(widgets);

            // Уходящие остаются на своих местах, пока гаснут: каждый — сразу за прежним соседом слева
            var merged = new List<Slot>(next);
            for (int i = 0; i < slots.Count; i++)
            {
                Slot old = slots[i];
                if (next.Contains(old)) continue;
                if (!animate) { Unmount(old); continue; }
                if (!old.Leaving)
                {
                    old.Leaving = true;
                    old.Show.Go(0, tick, LeaveMs);
                    if (old.Hover != null) old.Hover(false);
                }
                int at = 0;
                for (int j = i - 1; j >= 0; j--)
                {
                    int k = merged.IndexOf(slots[j]);
                    if (k >= 0) { at = k + 1; break; }
                }
                merged.Insert(at, old);
            }
            foreach (Slot s in next)
            {
                bool fresh = !slots.Contains(s);
                if (fresh) Mount(s);
                if (!animate) { s.Leaving = false; s.Show.Set(1); s.Wide.Set(s.W); continue; }
                if (fresh) { s.Show.Set(0); s.Wide.Set(s.W); }
                if (fresh || s.Leaving) s.Show.Go(1, tick, AppearMs);
                s.Leaving = false;
                s.Wide.Go(s.W, tick, AppearMs);
            }
            slots.Clear();
            slots.AddRange(merged);

            double panelH = PadTop + size + DotZone;
            winW = work.Width;
            winH = EdgeGap + panelH + Math.Max(size * (MaxScale - 1) + TipGap + TipH, WidgetTipGap + TipMaxH) + TopRoom;
            baseline = winH - EdgeGap - DotZone;
            panelTop = baseline - size - PadTop;
            panel.Height = panelH;
            Canvas.SetTop(panel, panelTop);
            stage.Width = winW; stage.Height = winH;
            Width = winW; Height = winH;
            Left = work.Left;
            Top = work.Bottom - winH;

            SyncDots();
            double full = 0;
            foreach (Slot s in slots) if (!s.Leaving) full += s.W + gap;
            Point p;
            if (!(inside && CursorHere(out p))) cursorU = full / 2;
            int again = keep == null ? -1 : slots.IndexOf(keep);
            if (again >= 0 && slots[again].Leaving) again = -1;
            hover = again;
            if (hover < 0)
            {
                // То, что было под курсором, ушло — подпись гаснет, а не висит над чужим значком
                if (keep != null && keep.Hover != null) keep.Hover(false);
                tipOverride = null;
                tip.BeginAnimation(OpacityProperty, null);
                tip.Opacity = 0;
            }
            Place(true);
            if (hover >= 0 && !menuOpen) { if (FillTip()) { tip.BeginAnimation(OpacityProperty, null); tip.Opacity = 1; } }
            // Полоса спрятана за краем, а её высота изменилась — прячем заново, на новую глубину
            if ((hidden || away) && HiddenY != lastHiddenY) { slide.BeginAnimation(TranslateTransform.YProperty, null); slide.Y = HiddenY; }
            lastHiddenY = HiddenY;
            if (animate) StartLoop();
            if (hub != null) hub.PublishWidgets(shown, Badges());
        }

        // Точка под значком: нет окон — нет точки; окно программы впереди — точка шире и ярче
        void SyncDots()
        {
            bool any = false;
            foreach (Slot s in slots)
            {
                if (s.Kind != SlotKind.Item || s.Dot == null || s.Item == null) continue;
                double to = s.Leaving || !s.Item.Running ? 0 : fg != IntPtr.Zero && s.Item.Windows.Contains(fg) ? DotWide : DotSize;
                if (to == s.DotW.Target) continue;
                Go(s.DotW, to, AppearMs);
                any = true;
            }
            if (any) Kick();
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

        // Середина места в покое, от левого края ряда — куда «поставить курсор» для снимка
        double RestCentre(int index)
        {
            double a = 0;
            for (int i = 0; i < index; i++) a += slots[i].W + gap;
            return a + (slots[index].W + gap) / 2;
        }

        void Place() { Place(false); }

        // Расставляет значки по текущим cursorU и amount и по тому, что сейчас меняется плавно:
        // появление и уход, ширина, нажатие, прыжок, точки
        void Place(bool force)
        {
            long now = clock.ElapsedMilliseconds;
            // Кто догас — уходит совсем
            Slot under = hover >= 0 && hover < slots.Count ? slots[hover] : null;
            for (int i = slots.Count - 1; i >= 0; i--)
            {
                Slot s = slots[i];
                if (!s.Leaving || s.Show.Value(now) > 0 || s.Show.Running) continue;
                Unmount(s);
                slots.RemoveAt(i);
                force = true;
            }
            if (under != null) hover = slots.IndexOf(under);

            int n = slots.Count;
            bool busy = false;
            double[] k = new double[n], w = new double[n], press = new double[n], dot = new double[n];
            for (int i = 0; i < n; i++)
            {
                Slot s = slots[i];
                if (s.Show.Running || s.Wide.Running || s.Press.Running || s.DotW.Running || s.BounceAt >= 0) busy = true;
                k[i] = s.Show.Value(now); w[i] = s.Wide.Value(now); press[i] = s.Press.Value(now); dot[i] = s.DotW.Value(now);
            }
            if (!force && !busy && !moving && cursorU == lastU && amount == lastAmount) return;
            moving = busy;
            lastU = cursorU; lastAmount = amount;

            double[] rw = new double[n], mw = new double[n], scale = new double[n];
            double total = 0;
            for (int i = 0; i < n; i++) { rw[i] = (w[i] + gap) * k[i]; total += rw[i]; }
            restW = total;
            double a = 0, range = RangeSlots * (size + gap);
            for (int i = 0; i < n; i++)
            {
                double centre = a + rw[i] / 2;
                scale[i] = !slots[i].Icon ? 1 : 1 + amount * (Magnify.Scale(cursorU - centre, range, MaxScale) - 1);
                mw[i] = (w[i] * scale[i] + gap) * k[i];
                a += rw[i];
            }
            double from, to;
            double[] cell = Magnify.Cells(rw, mw, cursorU, out from, out to);
            double origin = Origin;
            panelLeft = origin + from - PadH; panelRight = origin + to + PadH;
            Canvas.SetLeft(panel, panelLeft);
            panel.Width = Math.Max(0, panelRight - panelLeft);
            double rise = amount * size * (MaxScale - 1) + 2;
            Canvas.SetLeft(strip, panelLeft);
            Canvas.SetTop(strip, panelTop - rise);
            strip.Width = panel.Width;
            strip.Height = Math.Max(0, winH - panelTop + rise);

            for (int i = 0; i < n; i++)
            {
                Slot s = slots[i];
                s.CellL = origin + cell[i]; s.CellR = s.CellL + mw[i];
                s.Centre = (s.CellL + s.CellR) / 2;
                s.Now = scale[i];
                s.Left = s.Centre - w[i] * scale[i] / 2;
                if (s.Root.Opacity != k[i]) s.Root.Opacity = k[i];
                if (s.Kind == SlotKind.Line)
                {
                    s.Move.X = s.Centre - 0.5;
                    s.Move.Y = baseline - size + (size - ((Rectangle)s.Root).Height) / 2;
                    continue;
                }
                double grow = 0.6 + 0.4 * k[i];   // новый значок вырастает, уходящий — сжимается
                if (s.Kind == SlotKind.Widget)
                {
                    s.Scale.ScaleX = s.Scale.ScaleY = (1 - (1 - press[i]) / 2) * grow;   // капсула широкая — сжимается вдвое слабее значка
                    s.Move.X = s.Centre - s.W / 2;
                    s.Move.Y = baseline - size;
                    continue;
                }
                s.Scale.ScaleX = s.Scale.ScaleY = scale[i] * press[i] * grow;
                s.Move.X = s.Centre - size / 2;   // значок растёт от своего низа и от середины своего места
                double y = baseline - size - size * scale[i] * grow * (1 - press[i]) / 2;   // нажатие сжимает к середине значка, а не к полу
                if (s.BounceAt >= 0)
                {
                    long t = now - s.BounceAt;
                    int hop = (int)(t / BounceMs);
                    if (s.BounceStop < 0 && s.Item != null && s.Item.Running) s.BounceStop = hop + 1;   // окно появилось — допрыгиваем и встаём
                    if (hop >= (s.BounceStop >= 0 ? Math.Min(BounceCycles, s.BounceStop) : BounceCycles) || Ui.ReducedMotion) { s.BounceAt = -1; s.BounceStop = -1; }
                    else y -= size * BounceHeight * (1 - 0.2 * hop) * Tween.Bounce(t, BounceMs);
                }
                s.Move.Y = y;
                if (s.Dot == null) continue;
                double dw = Math.Max(0, dot[i]);
                s.Dot.Width = dw;
                s.Dot.Opacity = k[i] * (0.72 + 0.28 * Math.Max(0, Math.Min(1, (dw - DotSize) / (DotWide - DotSize))));
                s.DotMove.X = s.Centre - dw / 2;
                s.DotMove.Y = baseline + (DotZone - DotSize) / 2 - 1;
            }
            PlaceTip();
        }

        // Чей это столбец: место, над которым (или под которым, до самого края экрана) стоит точка x. −1 — ничей.
        // Поля полосы слева и справа относятся к крайним местам: в угол полосы тоже можно попасть.
        int IndexAt(double x)
        {
            int first = -1, last = -1;
            for (int i = 0; i < slots.Count; i++)
            {
                Slot s = slots[i];
                if (s.Kind == SlotKind.Line || s.Leaving) continue;
                if (x >= s.CellL && x < s.CellR) return i;
                if (first < 0) first = i;
                last = i;
            }
            if (first >= 0 && x >= panelLeft && x < slots[first].CellL) return first;
            if (last >= 0 && x >= slots[last].CellR && x <= panelRight) return last;
            return -1;
        }

        Slot SlotAt(double x)
        {
            int i = IndexAt(x);
            return i < 0 ? null : slots[i];
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
            double x = s.Centre - w / 2;
            double top = s.Icon ? baseline - size * s.Now - TipGap - h : panelTop - WidgetTipGap - h;
            Canvas.SetLeft(tip, Math.Max(2, Math.Min(winW - w - 2, x)));
            Canvas.SetTop(tip, Math.Max(0, top));
        }

        // Пишет подпись для того, что под курсором, и ставит её на место. false — подписывать нечего.
        bool FillTip()
        {
            string text = hover >= 0 && hover < slots.Count ? tipOverride ?? TipFor(slots[hover]) : null;
            if (string.IsNullOrEmpty(text)) return false;
            if (tipText.Text != text) tipText.Text = text;
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
        // Кадры идут, только пока курсор над доком, пружина ещё не успокоилась или что-то меняется. В покое док не тратит ничего.
        void StartLoop()
        {
            if (looping || ended) return;
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

        // Курсор над доком: над полосой, над выросшими из неё значками или под ней, до самого края экрана
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
                inside = have && !hidden && !away && InZone(p);
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

            if (!menuOpen) SetHover(inside ? IndexAt(p.X) : -1);

            if (!inside && !menuOpen && !moving && Math.Abs(amount - target) < 0.0005 && Math.Abs(velocity) < 0.005)
            {
                amount = 0; velocity = 0;
                Place();
                StopLoop();
            }
        }

        // ───────── Автоскрытие и уход с экрана ─────────
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

        // Док стоит у самого низа экрана (панели задач Windows под ним нет)
        bool AtScreenEdge { get { return work.Bottom >= SystemParameters.PrimaryScreenHeight - 0.5; } }

        // Двадцать раз в секунду, и только при включённом автоскрытии: где курсор
        void EdgeTick()
        {
            Point p;
            if (away || !CursorHere(out p)) return;
            long now = clock.ElapsedMilliseconds;
            if (hidden)
            {
                // Нижний край экрана во всю ширину, полоска в две точки: коснулся — док выезжает сразу. Если под доком
                // панель задач Windows, ждём, пока курсор задержится: иначе док выскакивал бы по дороге к ней.
                bool onEdge = p.X >= 0 && p.X <= winW && p.Y >= winH - 2 && p.Y <= winH + 1;
                edgeHits = onEdge ? edgeHits + 1 : 0;
                if (edgeHits >= (AtScreenEdge ? 1 : 3)) { edgeHits = 0; lastInside = now; SetHidden(false); StartLoop(); }
            }
            else if (menuOpen || InZone(p)) lastInside = now;
            else if (now - lastInside > HideAfterMs) SetHidden(true);
        }

        void SetHidden(bool hide)
        {
            if (hidden == hide) return;
            hidden = hide;
            Glide();
        }

        // Дока не должно быть видно совсем: впереди игра или видео на весь экран, или выбрана панель задач Windows.
        // Он уезжает за край и только потом окно прячется; возвращается — так же, выезжая.
        void SyncAway()
        {
            bool want = fullscreen || store.Bar == "modern";
            if (want == away) return;
            away = want;
            if (headless || demo) return;
            if (!away) Show();
            Glide();
        }

        // Полоса едет туда, где ей сейчас место: за нижний край (автоскрытие, полный экран) или обратно
        void Glide()
        {
            bool down = hidden || away;
            double to = down ? HiddenY : 0;
            if (Ui.ReducedMotion || !IsVisible || PresentationSource.FromVisual(this) == null)
            {
                slide.BeginAnimation(TranslateTransform.YProperty, null);
                slide.Y = to;
                if (away && !headless && !demo && IsVisible) Hide();
                return;
            }
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(down ? Tokens.Motion.BaseMs : Tokens.Motion.SlowMs)) { EasingFunction = down ? (IEasingFunction)new CubicEase { EasingMode = EasingMode.EaseIn } : Ui.EaseOut };
            if (away) a.Completed += delegate { if (away && !ended) Hide(); };
            slide.BeginAnimation(TranslateTransform.YProperty, a);
        }

        // ───────── Щелчки ─────────
        // Все щелчки приходят на общую полосу; чей столбец — того и щелчок. Нажатие сжимает значок сразу (70 мс),
        // отпускание возвращает мягко (220 мс) — видно, что док услышал.
        void StripDown(object sender, MouseButtonEventArgs e)
        {
            Slot s = SlotAt(e.GetPosition(this).X);
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Left)
            {
                Release();
                pressed = s;
                if (s == null) return;
                Go(s.Press, PressScale, PressDownMs);
                Kick();
            }
            else if (e.ChangedButton == MouseButton.Middle && s != null) Act(s, true);
        }

        void StripUp(object sender, MouseButtonEventArgs e)
        {
            Slot s = SlotAt(e.GetPosition(this).X);
            e.Handled = true;
            if (e.ChangedButton == MouseButton.Right) { OpenMenu(s != null && s.Kind == SlotKind.Item ? s : null); return; }
            if (e.ChangedButton != MouseButton.Left) return;
            Slot was = pressed;
            Release();
            if (was != null && was == s) Act(was, false);
        }

        void Release()
        {
            if (pressed == null) return;
            Go(pressed.Press, 1, PressUpMs);
            pressed = null;
            Kick();
        }

        // Щелчок по месту в доке. middle — средняя кнопка.
        void Act(Slot s, bool middle)
        {
            if (s.Kind == SlotKind.Launcher) { if (!middle) ToggleApps(); return; }
            if (s.Kind == SlotKind.Item) { Clicked(s, middle); return; }
            if (s.Kind != SlotKind.Widget || demo) return;
            // Виджеты открывают то, что на их месте открыла бы панель задач Windows
            if (s.Widget == "volume") { if (middle) VolumeMute(); else WinKey.Chord('A'); }   // Win+A — быстрые настройки
            else if (middle) return;
            else if (s.Widget == "battery") WinKey.Chord('A');
            else if (s.Widget == "clock" || s.Widget == "calendar") WinKey.Chord('N');        // Win+N — уведомления и календарь
            else if (s.Widget == "weather") WinKey.Chord('W');                                // Win+W — виджеты Windows с погодой
            else if (s.Widget == "music")
            {
                DockItem player = media != null ? Logic.Player(items, media.App) : null;
                if (player != null && player.Running) try { Win.Activate(player.Windows[0]); } catch { }
            }
        }

        // Щелчок по значку: не запущено — запустить; впереди чужое окно — вывести своё вперёд; своё уже впереди —
        // единственное сворачивается, из нескольких выходит следующее. Средняя кнопка — ещё один экземпляр.
        void Clicked(Slot slot, bool fresh)
        {
            DockItem item = slot.Item;
            if (demo || item == null) return;
            try
            {
                IntPtr target = IntPtr.Zero;
                Click what = fresh ? Click.Launch : Logic.Decide(item, Win.Foreground(), out target);
                if (what == Click.Launch) { Shell.Launch(item); Bounce(slot); }
                else if (what == Click.Minimize) Win.Minimize(target);
                else Win.Activate(target);
            }
            catch
            {
                tipOverride = "Не удалось запустить";
                ShowTip();
            }
        }

        // Программа запускается: значок мягко подпрыгивает — до трёх раз или пока не появится её окно
        void Bounce(Slot slot)
        {
            if (!CanAnimate) return;
            slot.BounceAt = clock.ElapsedMilliseconds;
            slot.BounceStop = -1;
            StartLoop();
        }

        // Меню приложений: открыть, а если уже открыто — закрыть
        void ToggleApps()
        {
            if (appMenu != null) { appMenu.Shut(); return; }
            var menu = new AppMenuWindow(demo);
            appMenu = menu;
            menu.Closed += delegate { if (appMenu == menu) appMenu = null; };
            SetHover(-1);
            menu.Show();
            menu.Activate();
        }

        string TitleOf(IntPtr hwnd)
        {
            string t;
            if (demo) return demoTitles.TryGetValue(hwnd, out t) ? t : "";
            return Win.Title(hwnd);
        }

        PopMenu BuildMenu(Slot slot)
        {
            var menu = new PopMenu();
            DockItem item = slot != null ? slot.Item : null;
            if (item != null)
            {
                menu.Header(Ui.T(item.Name, Tokens.Size.Body, Ui.Label2, FontWeights.SemiBold));
                if (item.Windows.Count > 1)
                {
                    // Окна программы по заголовкам: щелчок выводит окно вперёд; галочка — у того, что впереди сейчас
                    foreach (IntPtr h in item.Windows)
                    {
                        IntPtr hwnd = h;
                        string title = Logic.Shorten(TitleOf(hwnd), MenuTitleChars);
                        menu.Item(title.Length > 0 ? title : item.Name, hwnd == fg, delegate { if (!demo) try { Win.Activate(hwnd); } catch { } });
                    }
                    menu.Line();
                }
                if (item.CanPin)
                {
                    menu.Item(item.Running ? "Новое окно" : "Открыть", false, delegate
                    {
                        if (demo) return;
                        try { Shell.Launch(item); Bounce(slot); } catch { }
                    });
                    menu.Item("Показать в папке", false, delegate { if (!demo) try { Shell.Reveal(item.Path); } catch { } });
                }
                if (item.Pinned) menu.Item("Открепить", false, delegate { store.UnpinItem(item); SettingsChanged(); });
                else menu.Item("Закрепить", false, item.CanPin ? new Action(delegate { store.PinItem(item); SettingsChanged(); }) : null);
                if (item.Windows.Count == 1) menu.Item("Закрыть окно", false, delegate { if (!demo && item.Windows.Count > 0) Win.Close(item.Windows[0]); });
                else if (item.Windows.Count > 1) menu.Item("Закрыть все окна", false, delegate { if (!demo) foreach (IntPtr h in item.Windows.ToArray()) Win.Close(h); });
                menu.Line();
            }
            menu.Brand(DockSprite, "dock");
            menu.Item("Автоскрытие", store.AutoHide, delegate { store.AutoHide = !store.AutoHide; store.Save(); SettingsChanged(); });
            menu.Item("Вместо панели задач Windows", store.ReplaceTaskbar, delegate { store.Bar = store.ReplaceTaskbar ? "both" : "dock"; store.Save(); SettingsChanged(); });
            // Значки трея живут в панели задач Windows: показываем её ненадолго, сама спрячется, когда курсор уйдёт
            if (TaskbarSwap.Active) menu.Item("Значки в трее", false, delegate { TaskbarSwap.Peek(); });
            bool online = hub != null && hub.Online;
            // Настройки дока живут в core.txt: открываем его, а если его нет — предлагаем скачать
            menu.Item(online || CoreApp.Installed ? "Настройки" : "Скачать core.txt — настройки и остальные приложения", false,
                delegate { CoreApp.Go(online, delegate { hub.ShowCore(); }); });
            menu.Line();
            menu.Item("Выход", false, delegate { Quit(); });
            return menu;
        }

        void OpenMenu(Slot slot)
        {
            if (menuOpen) return;
            Release();
            PopMenu menu = BuildMenu(slot);
            menuOpen = true;
            ShowTip();
            menu.Closed += delegate
            {
                menuOpen = false;
                lastInside = clock.ElapsedMilliseconds;
                StartLoop();   // курсор мог уйти, пока меню было открыто, — пусть значки вернутся на место
            };
            double cx = slot != null ? slot.Centre : (panelLeft + panelRight) / 2;
            Point p;
            if (slot == null && CursorHere(out p)) cx = p.X;
            double top = slot != null ? baseline - size * slot.Now : panelTop;
            menu.ShowAbove(Left + cx, Top + top - 6, work);
        }

        // ───────── Для снимков и проверки ─────────
        // Выдуманные значки: ни окна, ни диск, ни хаб не трогаются. У «Браузера» три окна, и одно из них впереди.
        List<DockItem> Demo()
        {
            var list = new List<DockItem>();
            string[] pinned = { "Проводник", "Браузер", "Discord", "Steam", "Музыка", "Заметки" };
            for (int i = 0; i < pinned.Length; i++)
            {
                var it = new DockItem { Key = "demo-" + i, Name = pinned[i], Path = @"C:\demo\" + pinned[i] + ".exe", Pinned = true };
                if (i == 0 || i == 1 || i == 2 || i == 4) it.Windows.Add(new IntPtr(i + 1));
                list.Add(it);
            }
            string[] tabs = { "dev.txt — приложения для Windows", "Почта — входящие", "Как устроена панель задач Windows 11 и почему её хочется заменить" };
            for (int i = 0; i < tabs.Length; i++)
            {
                var h = new IntPtr(i == 0 ? 2 : 50 + i);
                if (i > 0) list[1].Windows.Add(h);
                demoTitles[h] = tabs[i];
            }
            fg = new IntPtr(2);
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
                cursorU = RestCentre(index);
                amount = store.Magnify && !Ui.ReducedMotion ? 1 : 0;
                Place(true);
                hover = index;
                if (slots[index].Hover != null) slots[index].Hover(true);
                FillTip();
                tip.Opacity = withMenu ? 0 : 1;
            }
            // Меню — то же самое, что по правому щелчку, только нарисованное в этот же снимок
            FrameworkElement box = null;
            if (index >= 0 && withMenu)
            {
                PopMenu menu = BuildMenu(slots[index].Kind == SlotKind.Item ? slots[index] : null);
                box = (FrameworkElement)menu.Content;
                menu.Content = null;
                box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            // Сверху — ровно столько места, сколько нужно подписи или меню: окно выше из-за многострочных подписей виджетов
            double above = index >= 0 && !withMenu && !slots[index].Icon ? WidgetTipGap + tip.DesiredSize.Height : size * (MaxScale - 1) + TipGap + TipH;
            double top = box != null ? size * (MaxScale - 1) + box.DesiredSize.Height - 36 + 6 + 24 : above + TopRoom + 36;
            // Снимок — не весь экран, а док с полями
            double full = 0;
            foreach (Slot s in slots) full += s.W + gap;
            double w = Math.Min(winW, full + 2 * PadH + size * 4 + 160) + 40, h = EdgeGap + PadTop + size + DotZone + top;
            var root = new Grid { Width = w, Height = h, Background = Ui.Bg, ClipToBounds = true };
            var glow = Ui.Backdrop();
            glow.RenderTransform = new TranslateTransform(0, -260);
            root.Children.Add(glow);
            stage.HorizontalAlignment = HorizontalAlignment.Left; stage.VerticalAlignment = VerticalAlignment.Top;
            stage.Margin = new Thickness((w - winW) / 2, h - winH, 0, 0);
            root.Children.Add(stage);
            if (box != null)
            {
                box.HorizontalAlignment = HorizontalAlignment.Left; box.VerticalAlignment = VerticalAlignment.Top;
                box.Margin = new Thickness((w - winW) / 2 + slots[index].Centre - (box.DesiredSize.Width - 36) / 2, h - winH + baseline - size * slots[index].Now - 6 - (box.DesiredSize.Height - 36), 0, 0);
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
