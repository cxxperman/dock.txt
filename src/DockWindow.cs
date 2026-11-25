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
    enum SlotKind { Item, Line, Launcher, Widget, Bin }

    // Место в ряду: значок приложения, кнопка меню приложений, черта, виджет или корзина (самая правая, за чертой)
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
        // Перетаскивание. Float: 0 — значок на своём месте, 1 — в руке, у курсора. Shift — на сколько место ещё не доехало
        // туда, где должно быть (соседи расступаются плавно). Gap: 1 — место в ряду занято, 0 — закрыто (значок унесли из дока).
        public readonly Tween Float = new Tween(0), Shift = new Tween(0), Gap = new Tween(1);
        public double FloatX, FloatY;     // где середина значка, пока он в руке
        public System.Windows.Media.Effects.DropShadowEffect Shadow;
        public long BounceAt = -1;        // когда значок начал подпрыгивать (программа запускается); −1 — не прыгает
        public int BounceStop = -1;       // после какого прыжка остановиться: окно появилось

        public bool Icon { get { return Kind == SlotKind.Item || Kind == SlotKind.Launcher || Kind == SlotKind.Bin; } }
    }

    class DockWindow : Window
    {
        // Значок дока — тот же рисунок, что в sprite.json (потом переедет в kit.txt, icons/sprites.json, «dock»)
        internal static readonly string[] DockSprite = { "....####....", "....#E##....", ".##.#E##.##.", ".##.####.##.", ".##.####.##.", "............", "############", ".##########." };

        // Свои размеры дока — то, чего в токенах нет
        const double PadH = 6, PadTop = 9, DotZone = 12, EdgeGap = 8, TipGap = 9, TipH = 26, TopRoom = 12, DotSize = 4, DotWide = 14, DotAsking = 8;
        const double WidgetTipGap = 5, TipMaxH = 92;   // подпись виджета бывает в несколько строк (события календаря)
        const double MusicText = 180, CalendarText = 190, MusicTextTight = 110, CalendarTextTight = 120;
        internal const double MaxScale = 1.55, RangeSlots = 2.6;
        const int HideAfterMs = 700, EdgeMs = 50, SlowPollTicks = 5;
        // Окна док перечитывает по сообщениям Windows (ShellBatch); на случай, если какое-то потерялось, — ещё и раз в 12 с
        const int FallbackMs = 12000;
        // Движение: появление и переезд значков, уход, нажатие (вниз быстро, обратно мягко), прыжок при запуске, виджеты
        const int AppearMs = 250, LeaveMs = 180, PressDownMs = 70, PressUpMs = 220, BounceMs = 560, BounceCycles = 3, WidgetFadeMs = 120;
        const double PressScale = 0.92, BounceHeight = 0.2;
        const int MenuTitleChars = 38;
        const string AppsTip = "Приложения", UnpinTip = "Открепить";
        // Перетаскивание: поднятый значок чуть крупнее и с тенью; соседи расступаются, отпущенный садится на место
        const double LiftScale = 1.12, LiftShadow = 0.5, DropSlack = 0.12;
        const int LiftMs = 140, SettleMs = 220, PartMs = 200;

        // Стекло: системный акрил в подложке и полупрозрачный тёмный фон самой полосы поверх него — белые подписи
        // читаются и на белых обоях, а сквозь полосу видно, что позади
        const double PanelTint = 0.45;
        const int WM_SETTINGCHANGE = 0x1A, WM_DISPLAYCHANGE = 0x7E, WM_DPICHANGED = 0x2E0, WM_WINDOWPOSCHANGED = 0x47, SPI_SETWORKAREA = 0x2F;
        static readonly Brush solidFill = Ui.Solid(Tokens.Dark.Bg), glassFill = Ui.Solid(Ui.Alpha(Tokens.Dark.Bg, PanelTint));
        // Точка «просит внимания» — оранжевая, как кнопка такого окна на панели задач Windows
        static readonly Brush askingDot = Ui.Solid(Tokens.Palette.Orange);
        GlassBackdrop glass;   // null — размытия нет: Windows 10, снимок, без окна
        bool glassOn;          // размытие сейчас включено («Эффекты прозрачности» Windows не выключены)
        readonly Grid shadowBox;
        readonly RectangleGeometry shadowOuter, shadowInner;
        long glideUntil = -1;  // до какого времени полоса едет за край или обратно: подложка едет вместе с ней
        bool gliding;

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
        Slot armed, drag; // armed — закреплённый значок нажат и его можно поднять; drag — он уже в руке
        Point armAt;      // где нажали
        double grabX, grabY;   // от середины значка до курсора в момент, когда его подняли
        int dragTo = -1;  // на каком месте среди закреплённых значок окажется, если отпустить
        bool dragOut;     // унесён высоко над доком: отпустить — открепить

        // Окна программы над её значком
        PreviewWindow preview;
        Slot previewFor;
        int previewPolls;
        readonly DispatcherTimer previewOpen = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewRules.OpenMs) };
        readonly DispatcherTimer previewShut = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PreviewRules.CloseMs) };
        readonly DispatcherTimer previewPoll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };

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

        // Корзина: что прислала metla.txt (null — её нет на связи), стоит ли она, несут ли сейчас файлы над баком
        BinInfo bin;
        bool binInstalled, binDropHot, binShown;
        string binKey = "";

        // Увеличение: одна пружина от 0 (покой) до 1 (курсор над доком). Сами значки идут за курсором без задержки.
        double amount, velocity, cursorU, lastU = double.NaN, lastAmount = double.NaN;
        bool looping, inside, menuOpen, hidden, fullscreen, away, moving;
        int hover = -1;
        long lastFrame, lastInside, lastSwitch = -1;   // lastSwitch — когда впереди сменилось окно (−1 — ещё не менялось)
        string tipOverride;

        // По привычкам Windows (Habits.cs): появление от края, сообщения об окнах, «просит внимания», файлы на значке
        readonly EdgeReveal edgeRule = new EdgeReveal();
        readonly ShellBatch shellBatch = new ShellBatch();
        readonly Attention attention = new Attention();
        readonly DispatcherTimer shellTimer = new DispatcherTimer();
        IntPtr me;            // окно дока у Windows (нет — пока окно не создано, и без окна)
        bool shellOn;         // Windows присылает сообщения об окнах
        long lastCollect;     // когда окна перечитывались в последний раз
        bool dotsLater;       // прыжки «просит внимания» кончились — точке пора стать оранжевой
        Slot dropFor;         // над каким значком несут файлы, чтобы открыть их этой программой

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

            // Полоса: настоящее стекло — под ней окно-подложка, за которым Windows размывает обои и окна (Glass.cs),
            // а сама полоса — полупрозрачная тёмная тонировка с кромкой: значки и подписи читаются и на белых обоях.
            // Где размытия нет (Windows 10, выключены «Эффекты прозрачности», снимки) — прежний ровный тёмный фон,
            // тот же, что у острова kapsula.txt наверху.
            panel = new Border
            {
                CornerRadius = new CornerRadius(Tokens.Radius.Block), Background = solidFill, SnapsToDevicePixels = true, IsHitTestVisible = false,
                Child = new Border { CornerRadius = new CornerRadius(Tokens.Radius.Block), Background = Brushes.Transparent, BorderBrush = Ui.Rim, BorderThickness = new Thickness(1) }
            };
            // Тень — отдельно и только снаружи: под полупрозрачной полосой она просвечивала бы внутрь. Внутренность
            // вырезана по форме полосы (на точку меньше — у кромки тень плотная и прикрывает край размытия).
            shadowInner = new RectangleGeometry(new Rect(), Tokens.Radius.Block, Tokens.Radius.Block);
            shadowOuter = new RectangleGeometry();
            shadowBox = new Grid
            {
                IsHitTestVisible = false, Clip = new CombinedGeometry(GeometryCombineMode.Exclude, shadowOuter, shadowInner),
                Children = { new Border { CornerRadius = new CornerRadius(Tokens.Radius.Block), Background = solidFill, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 22, ShadowDepth = 5, Direction = 270, Opacity = 0.38, Color = Colors.Black } } }
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
            stage.Children.Add(shadowBox);
            stage.Children.Add(panel);
            stage.Children.Add(strip);
            stage.Children.Add(tip);
            stage.RenderTransform = slide;
            Content = stage;

            stage.MouseMove += delegate { StartLoop(); };
            strip.MouseDown += StripDown;
            strip.MouseUp += StripUp;
            strip.MouseLeave += delegate { Release(); };
            strip.MouseMove += StripMove;
            strip.LostMouseCapture += delegate { armed = null; if (drag != null) CancelDrag(); };
            // Файлы и папки из проводника: на бак — в корзину, на значок программы — открыть ею; остальное их не принимает
            strip.AllowDrop = true;
            strip.DragEnter += FilesOver;
            strip.DragOver += FilesOver;
            strip.DragLeave += delegate(object s, DragEventArgs e) { e.Handled = true; SetBinDropHot(false); SetDropFor(null); };
            strip.Drop += FilesDropped;
            shellTimer.Tick += delegate { shellTimer.Stop(); ShellFlush(); };
            previewOpen.Tick += delegate { previewOpen.Stop(); PreviewDue(); };
            previewShut.Tick += delegate { previewShut.Stop(); PreviewLeft(); };
            previewPoll.Tick += delegate
            {
                // Окно закрывали крестиком в панели: смотрим чаще обычного, пока оно не исчезнет
                if (--previewPolls <= 0 || ended || preview == null || !preview.Shown) previewPoll.Stop();
                if (!ended && !demo) Refresh();
            };
            strip.MouseWheel += delegate(object s, MouseWheelEventArgs e)
            {
                Slot at = SlotAt(e.GetPosition(this).X);
                if (at == null || at.Widget != "volume") return;
                e.Handled = true;
                VolumeWheel(e.Delta);
            };
            SourceInitialized += delegate
            {
                me = new WindowInteropHelper(this).Handle;
                Win.MakeBar(me);
                // Окно появляется на экране — только у настоящего окна (не в снимке и не без окна): тогда и подложка
                // для размытия. Она владелец окна дока, поэтому док всегда стоит прямо над ней.
                if (GlassBackdrop.Supported)
                {
                    try
                    {
                        glass = new GlassBackdrop();
                        new WindowInteropHelper(this).Owner = glass.Handle;
                    }
                    catch { glass = null; }
                }
                ApplyGlass();
                HwndSource.FromHwnd(me).AddHook(delegate(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
                {
                    uint m = (uint)msg;
                    // Сообщения Windows об окнах: копятся в пачку и разбираются разом
                    if (shellOn && m == Win.ShellMessage) { OnShell(w.ToInt32(), l); return IntPtr.Zero; }
                    // Полосы на краях экрана сдвинулись — встаём заново; впереди окно на весь экран — смотрим, не игра ли
                    if (m == AppBar.Callback && m != 0)
                    {
                        int code = w.ToInt32();
                        if (code == AppBar.ABN_POSCHANGED) { AppBar.Dirty(); Dispatcher.BeginInvoke(new Action(delegate { Rebuild(); KickAutoHide(); })); }
                        else if (code == AppBar.ABN_FULLSCREENAPP) Dispatcher.BeginInvoke(new Action(SyncGame));
                        return IntPtr.Zero;
                    }
                    // Проводник перезапустился и снова показал свою панель задач — прячем её заново; полосу дока он забыл
                    if (m == TaskbarSwap.TaskbarCreated && msg != 0) { AppBar.Lost(); Dispatcher.BeginInvoke(new Action(delegate { SyncTaskbar(); Rebuild(); KickAutoHide(); })); }
                    // Переключили «Эффекты прозрачности» (и другие общие настройки) — смотрим, нужно ли размытие;
                    // рабочая область экрана сменилась — док встаёт заново (чуть позже: пусть WPF сначала узнает её сам)
                    else if (msg == WM_SETTINGCHANGE)
                    {
                        Dispatcher.BeginInvoke(new Action(ApplyGlass));
                        if (w.ToInt32() == SPI_SETWORKAREA) Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(WorkAreaChanged));
                    }
                    else if (msg == WM_DPICHANGED || msg == WM_DISPLAYCHANGE) { AppBar.Dirty(); Dispatcher.BeginInvoke(new Action(delegate { Rebuild(); SyncGlass(); })); }
                    else if (msg == WM_WINDOWPOSCHANGED) AppBar.Moved();
                    return IntPtr.Zero;
                });
                // Сообщения об окнах — только у настоящего дока; без них (не вышло) окна перечитываются, как раньше, по таймеру
                if (!demo && !headless) shellOn = Win.WatchShell(me);
                // Окно есть — можно занять своё место у Windows и встать в него
                if (!demo && !headless) Dispatcher.BeginInvoke(new Action(Rebuild));
            };
            // Окно дока спрятали (игра, панель Windows) или показали — подложка вместе с ним
            IsVisibleChanged += delegate { SyncGlass(); };
            Closed += delegate { if (glass != null) { glass.Dispose(); glass = null; } };
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
                binInstalled = BinRules.MetlaInstalled;
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
                lastCollect = clock.ElapsedMilliseconds;
                refresh.Tick += delegate { Tick(); };
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
                // Раз в секунду: панель задач спрятана и с автоскрытием; не освободилось ли её место (или не занято ли снова)
                taskbarTimer.Tick += delegate { TaskbarSwap.Keep(store.Dir); ReserveCheck(); };
                autoHideCheck.Tick += delegate { AutoHideTick(); };
                taskbarTimer.Start();
                hookTimer.Tick += delegate { WinKey.Refresh(); };
                hookTimer.Start();
                // Впереди стало другое окно — узнаём сразу, а не через секунду
                if (!headless) Win.WatchForeground(ForegroundChanged);
                AppCatalog.Refresh(null);   // список для меню приложений собирается заранее, в фоне
            }
        }

        internal bool Demoing { get { return demo; } }

        // Для снимков (--demo --bin): full — в корзине 12 объектов, empty — пусто, offline — metla.txt не на связи
        internal void DemoBin(string kind)
        {
            if (!demo) return;
            bin = kind == "offline" ? null : BinInfo.From(HubLink.Msg("count", kind == "empty" ? 0 : 12, "bytes", kind == "empty" ? 0L : 1395864371L));
            Rebuild();
        }

        // Для снимков (--demo --asking N): окно значка N просит внимания. В снимке ничего не движется — сразу оранжевая точка.
        internal void DemoAsk(int n)
        {
            if (!demo || n < 0 || n >= items.Count || !items[n].Running) return;
            attention.Flash(items[n].Windows[items[n].Windows.Count - 1], IntPtr.Zero, clock.ElapsedMilliseconds);
            SyncDots();
            Place(true);
        }

        readonly DispatcherTimer taskbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        readonly DispatcherTimer hookTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };

        // Панель задач Windows, своё место дока и кнопка Windows — по настройкам. Место отдаётся раньше, чем возвращается
        // панель Windows: она сразу встаёт туда, где была, а не над полосой дока.
        void SyncTaskbar()
        {
            if (demo || headless) return;
            if (store.ReplaceTaskbar) TaskbarSwap.Take(store.Dir);
            else
            {
                if (AppBar.Remove()) Rebuild();
                TaskbarSwap.Give(store.Dir);
            }
            // Панель Windows спряталась — её место освободилось: полоса дока опускается к самому краю
            if (store.ReplaceTaskbar) { AppBar.Dirty(); Rebuild(); KickAutoHide(); }
            WinKey.Sync(store.ReplaceTaskbar && store.WinKey, store.ReplaceTaskbar, Dispatcher, WinPressed, WinDigitPressed);
            // Компактная панель Windows: включаем, только когда её выбрали; иначе возвращаем, как было до нас
            bool changed = store.Bar == "modern" ? TaskbarSwap.CompactOn(store.Dir) : TaskbarSwap.CompactBack(store.Dir);
            if (changed) TaskbarSwap.RestartExplorer(null);   // Проводник не закрылся сам — не трогаем: панель сменится при следующем входе
            // Выбрана панель Windows — дока не видно (он остаётся запущенным: настройки, счётчики, меню приложений)
            SyncAway();
        }

        // Одиночное нажатие кнопки Windows. Поверх игры или видео на весь экран (на любом мониторе: фокус у игры) не
        // открывается ничего: перехватчик уже не пустил нажатие в «Пуск», а меню приложений не открываем мы. Игра само
        // нажатие получила и фокус не теряет. Закрыть уже открытое меню можно. Ответ нужен в эту же секунду, поэтому
        // проверяем сами — общим правилом семьи.
        void WinPressed()
        {
            if (ended) return;
            if (appMenu == null && FullScreen.Ahead()) return;
            ToggleApps();
        }

        // Win+1…9: закреплённое приложение с этим номером — как щелчок по его значку. Перехватчик уже не пустил цифру
        // в Windows; поверх игры он её и не берёт, а если игра вышла вперёд только что, здесь — ещё одна проверка: фокус
        // остаётся у игры. Окна перечитываются тут же: порядок «следующего окна» должен быть свежим.
        void WinDigitPressed(int index)
        {
            if (ended || demo) return;
            if (away || FullScreen.Ahead()) return;
            Refresh();
            DockItem item;
            Click what;
            IntPtr target;
            if (!WinDigit.Decide(items, index, Win.Foreground(), out item, out what, out target)) return;
            Slot slot = Find("item:" + item.Key);
            try
            {
                if (what == Click.Launch) { Shell.Launch(item); if (slot != null) Bounce(slot); }
                else if (what == Click.Minimize) Win.Minimize(target);
                else Win.Activate(target);
            }
            catch { }   // программа не запустилась — как и у «Пуска», ничего не происходит
        }

        // Всё, что тикает и слушает, останавливается один раз
        void End()
        {
            if (ended) return;
            ended = true;
            refresh.Stop(); edge.Stop(); minute.Stop(); taskbarTimer.Stop(); hookTimer.Stop(); shellTimer.Stop(); autoHideCheck.Stop();
            previewOpen.Stop(); previewShut.Stop(); previewPoll.Stop();
            if (preview != null) { try { preview.Close(); } catch { } preview = null; }
            StopLoop();
            if (glass != null) glass.Hide();
            Win.Unwatch();
            if (shellOn) { Win.UnwatchShell(me); shellOn = false; }
            // Док уходит — его место у Windows освобождается, панель задач и кнопка Windows возвращаются такими, какими были
            WinKey.Stop();
            AppBar.Remove();
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

        // Раз в секунду — только то, что дёшево: не вышла ли вперёд игра (без core.txt — своя проверка одного окна впереди),
        // какое окно впереди. Раз в пять секунд — батарея и не сменилось ли устройство звука. Сами окна перечитываются по
        // сообщениям Windows (OnShell), а раз в 12 с — на всякий случай; без этих сообщений — каждую секунду, как раньше.
        void Tick()
        {
            SyncGame();
            IntPtr was = fg;
            fg = Win.Foreground();
            if (++ticks % SlowPollTicks == 0)
            {
                BatteryInfo b = Power.Read();
                if (b.Signature != battery.Signature) widgetsDirty = true;
                battery = b;
                if (audio != null) audio.Rebind();
                if (ReadVolume()) widgetsDirty = true;
                // metla.txt поставили или удалили — бак появляется или уходит
                bool metla = BinRules.MetlaInstalled;
                if (metla != binInstalled) { binInstalled = metla; widgetsDirty = true; }
            }
            bool due = !shellOn || clock.ElapsedMilliseconds - lastCollect >= FallbackMs;
            if (due || widgetsDirty || SystemParameters.WorkArea != work) { Refresh(); return; }
            if (fg != was) { attention.Clear(fg); SyncDots(); }
        }

        // Рабочая область экрана сменилась (своя полоса заняла место или отдала его, сменили разрешение)
        void WorkAreaChanged()
        {
            if (ended || demo) return;
            if (SystemParameters.WorkArea != work) Refresh();
        }

        // Перечитать окна и поменять док, только если что-то изменилось. По сообщению Windows, на всякий случай раз в 12 с,
        // перед Win+цифрой, пока над значком открыта панель с окнами.
        void Refresh()
        {
            SyncGame();
            fg = Win.Foreground();
            lastCollect = clock.ElapsedMilliseconds;
            var now = Collect();
            // Окон, которых больше нет, просьбы о внимании тоже нет
            var alive = new HashSet<IntPtr>();
            foreach (DockItem i in now) foreach (IntPtr h in i.Windows) alive.Add(h);
            attention.Keep(delegate(IntPtr h) { return alive.Contains(h); });
            attention.Clear(fg);
            bool moved = SystemParameters.WorkArea != work;   // сменили разрешение или передвинули панель задач
            if (moved) work = SystemParameters.WorkArea;
            if (moved || widgetsDirty || Logic.Signature(now) != signature)
            {
                items = now;
                Rebuild();
                hub.PublishApps(items);
                SyncPreview();
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
            SyncPreview();
        }

        // Впереди стало другое окно: игра, точка под значком, просьба о внимании — сразу. Порядок окон перечитается по
        // сообщению Windows об этом же (HSHELL_WINDOWACTIVATED); если их нет — перечитываем сами, как раньше.
        void ForegroundChanged()
        {
            lastSwitch = clock.ElapsedMilliseconds;
            if (fgPending || ended) return;
            fgPending = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                fgPending = false;
                if (ended) return;
                if (!shellOn) { Refresh(); return; }
                SyncGame();
                fg = Win.Foreground();
                attention.Clear(fg);
                SyncDots();
            }));
        }

        // ───────── Сообщения Windows об окнах ─────────
        // Приходят по одному, разбираются пачкой: срок пачки только приближается (ShellBatch)
        void OnShell(int code, IntPtr hwnd)
        {
            if (ended) return;
            long now = clock.ElapsedMilliseconds;
            bool waiting = shellBatch.Pending;
            long was = shellBatch.Due;
            long due = shellBatch.Add(code, hwnd, now);
            if (due < 0 || (waiting && due >= was && shellTimer.IsEnabled)) return;
            shellTimer.Stop();
            shellTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, due - now));
            shellTimer.Start();
        }

        void ShellFlush()
        {
            if (ended) return;
            ShellSummary s = shellBatch.Take();
            if (s.Windows) Refresh();
            else if (s.Titles) SyncPreview();   // сменились только заголовки: они видны лишь в панели с окнами
            long now = clock.ElapsedMilliseconds;
            IntPtr ahead = Win.Foreground();
            var fresh = new List<IntPtr>();
            foreach (var a in s.Attention)
            {
                if (a.Value) { if (attention.Flash(a.Key, ahead, now)) fresh.Add(a.Key); }
                else { attention.Clear(a.Key); fresh.Remove(a.Key); }
            }
            foreach (IntPtr h in fresh) AskFor(h, now);
            SyncDots();
        }

        // Окно попросило внимания: его значок подпрыгивает (три раза), потом под ним оранжевая точка
        void AskFor(IntPtr hwnd, long at)
        {
            Slot s = slots.Find(delegate(Slot x) { return x.Kind == SlotKind.Item && !x.Leaving && x.Item != null && x.Item.Windows.Contains(hwnd); });
            if (s == null || AttentionStill) return;
            s.BounceAt = at;
            s.BounceStop = Attention.Hops;   // окно у программы уже есть — прыгаем все три раза, а не до первого окна
            StartLoop();
        }

        // Без прыжков: «Меньше движения», «Не беспокоить» в core.txt или окна на экране нет
        bool AttentionStill { get { return Ui.ReducedMotion || Quiet || !CanAnimate; } }

        AttentionPhase PhaseOf(DockItem item)
        {
            return Attention.Phase(attention.Since(item), clock.ElapsedMilliseconds, AttentionStill, BounceMs);
        }

        // Для проверок (--testhook, команда test.shell): будто Windows прислала сообщение об окне программы app
        // (по названию; без него — первая запущенная, чьё окно сейчас не впереди). Ответ — какая программа и окно.
        internal Dictionary<string, object> TestShell(string ev, string app)
        {
            int code = ev == "flash" ? ShellCodes.Flash : ev == "activated" ? ShellCodes.Activated : ev == "destroyed" ? ShellCodes.Destroyed : ev == "created" ? ShellCodes.Created : 0;
            if (code == 0) throw new Exception("такого сообщения нет: flash, activated, destroyed или created");
            IntPtr ahead = Win.Foreground();
            DockItem item = items.Find(delegate(DockItem i)
            {
                return i.Running && (string.IsNullOrEmpty(app) ? !i.Windows.Contains(ahead) : string.Equals(i.Name, app, StringComparison.OrdinalIgnoreCase));
            });
            if (item == null) throw new Exception("нет такой запущенной программы в доке");
            OnShell(code, item.Windows[0]);
            return HubLink.Msg("name", item.Name, "hwnd", item.Windows[0].ToInt64());
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
            else if (topic == "metla.bin") bin = BinInfo.From(data);
            else return;
            Rebuild();
        }

        // Хаб пропал целиком (core.txt закрыли): того, что присылала Kapsula, больше не знаем
        internal void HubGone()
        {
            if (ended || hubRaw.Count == 0) return;
            hubRaw.Clear();
            media = null; weather = null; calendar = null; unread = null; bin = null;
            Rebuild();
        }

        // ───────── Семья dev.txt ─────────
        // Впереди игра или видео на весь экран? С core.txt решает он (core.family.game), без него — своя проверка
        // тем же правилом семьи (FullScreen в Family.cs). Само решение — FamilyRules.GameAway.
        bool GameNow()
        {
            bool online = hub != null && hub.CoreOnline;
            long since = lastSwitch < 0 ? -1 : clock.ElapsedMilliseconds - lastSwitch;
            // Док стоит только на главном мониторе: игра на другом ему не мешает. На каком мониторе окно впереди,
            // core.family не говорит — смотрим сами
            bool screen = Win.OnDockScreen(Win.Foreground());
            // При core.txt своя проверка нужна только сразу после смены окна впереди — иначе её и не делаем
            bool local = screen && (!online || (since >= 0 && since < FamilyRules.GameGraceMs)) && FullScreen.Ahead();
            return FamilyRules.GameAway(online, online && hub.Game, local, since, screen);
        }

        // Игра пришла — док уезжает за край и прячется; ушла — возвращается
        void SyncGame()
        {
            bool full = GameNow();
            WinKey.Game = full;   // в игре Win+цифра остаётся игре
            if (full == fullscreen) return;
            fullscreen = full;
            SyncAway();
        }

        // core.family или список приложений в хабе поменялись, связь с core.txt пропала (тогда снова своя проверка)
        internal void FamilyChanged()
        {
            if (ended || demo) return;
            SyncGame();
            // metla.txt подключилась или ушла — у бака другие пункты меню, а может, он сам появился или ушёл
            if (BinKey() != binKey) Rebuild();
        }

        // «Не беспокоить» в core.txt: док не привлекает внимание движением (счётчики остаются, только не выскакивают)
        bool Quiet { get { return hub != null && hub.Focus; } }

        // Автоскрытие из меню дока и из трея core.txt (пункт dock.tray «Автоскрытие»)
        internal void ToggleAutoHide()
        {
            store.AutoHide = !store.AutoHide;
            store.Save();
            SettingsChanged();
        }

        // Для проверок (--testhook, команда test.state): что док сейчас решил. local — своя проверка прямо сейчас,
        // screen — окно впереди на главном мониторе, coreGame — что сказал core.txt; taskbarTaken и winKeyHooked —
        // спрятана ли панель задач и перехвачена ли кнопка Windows (без окна — никогда)
        internal Dictionary<string, object> TestState()
        {
            return HubLink.Msg("fullscreen", fullscreen, "away", away, "local", FullScreen.Ahead(), "screen", Win.OnDockScreen(Win.Foreground()),
                "coreOnline", hub != null && hub.CoreOnline, "coreGame", hub != null && hub.Game, "quiet", Quiet,
                "autoHide", store.AutoHide, "taskbarTaken", TaskbarSwap.Active, "winKeyHooked", WinKey.Installed, "winDigits", WinKey.Digits, "bin", BinState(),
                "appBar", AppBarState(), "edgeDwellMs", EdgeReveal.DwellMs, "shellHook", shellOn, "fallbackMs", FallbackMs, "attention", AttentionState());
        }

        // Для test.state: занял бы док место у Windows (wanted — будь он на экране), занял ли (без окна — никогда), сколько
        double Scale
        {
            get
            {
                PresentationSource src = PresentationSource.FromVisual(this);
                if (src != null && src.CompositionTarget != null) return src.CompositionTarget.TransformToDevice.M22;
                return Win.SystemScale();
            }
        }

        // Высота дока в покое, в точках WPF: от верха полосы до края экрана (поле под полосой тоже его)
        double RestDip { get { return EdgeGap + PadTop + size + DotZone; } }

        Dictionary<string, object> AppBarState()
        {
            PxRect mon;
            bool known = Win.PrimaryMonitor(out mon);
            int h = AppBarRules.Height(RestDip, Scale);
            PxRect r = AppBar.Registered ? AppBar.Band : AppBarRules.Ask(mon, AppBarRules.Missing(h, reserve), reserve);
            return HubLink.Msg("wanted", known && AppBarRules.Wanted(true, store.ReplaceTaskbar, store.AutoHide, true), "registered", AppBar.Registered,
                "height", h, "reserved", reserve, "restDip", RestDip, "scale", Scale, "rect", new object[] { r.L, r.T, r.R, r.B });
        }

        // Сколько внизу главного монитора держит спрятанная доком панель задач (без автоскрытия) и на каком мониторе
        // это посчитано — по ним своя полоса и низ дока (AppBarRules.Reserved, Missing, BottomPx)
        int reserve;
        PxRect reserveMon;

        int TaskbarReserve(PxRect mon)
        {
            if (demo || headless || !store.ReplaceTaskbar || !TaskbarSwap.Active) return 0;
            PxRect bar;
            bool autoHidden;
            if (!TaskbarSwap.Where(out bar, out autoHidden)) return 0;
            return AppBarRules.Reserved(mon, bar, autoHidden);
        }

        // Автоскрытие спрятанной панели задач: Проводник включает его не сразу, а иногда не принимает с первого раза.
        // Несколько раз по таймеру (не останавливая окно дока) просим ещё раз и смотрим, не освободилось ли её место.
        readonly DispatcherTimer autoHideCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AppBarRules.RecheckMs) };
        int autoHideTries;

        void KickAutoHide()
        {
            if (demo || headless || ended || !store.ReplaceTaskbar || autoHideCheck.IsEnabled) return;
            autoHideTries = AppBarRules.RecheckTries;
            autoHideCheck.Start();
        }

        void AutoHideTick()
        {
            if (ended || !store.ReplaceTaskbar || !TaskbarSwap.Active) { autoHideCheck.Stop(); return; }
            bool on = TaskbarSwap.EnsureAutoHide();
            ReserveCheck();
            if (on || --autoHideTries <= 0) autoHideCheck.Stop();
        }

        // Место под спрятанной панелью задач освободилось или снова занято — своя полоса и док встают заново
        void ReserveCheck()
        {
            if (demo || headless || ended || me == IntPtr.Zero) return;
            PxRect mon;
            if (!Win.PrimaryMonitor(out mon)) return;
            if (TaskbarReserve(mon) != reserve) { AppBar.Dirty(); Rebuild(); }
        }

        object[] AttentionState()
        {
            var list = new List<object>();
            foreach (DockItem i in items)
            {
                AttentionPhase ph = PhaseOf(i);
                if (ph != AttentionPhase.None) list.Add(HubLink.Msg("name", i.Name, "phase", ph == AttentionPhase.Bounce ? "bounce" : "mark"));
            }
            return list.ToArray();
        }

        // Занять своё место у Windows или отдать его — по настройкам. true — полоса появилась, ушла или сдвинулась.
        // Своё место занимается только после того, как панель задач спрятана (AppBarRules.Wanted, taken), и перед этим
        // ещё раз проверяется её автоскрытие: нет его — просим снова, а пока Windows держит её место, занимаем только
        // недостающее до высоты дока (AppBarRules.Missing), и док стоит у самого низа монитора.
        bool SyncAppBar()
        {
            reserve = 0;
            if (demo || headless || me == IntPtr.Zero || ended) return false;
            PxRect mon;
            bool known = Win.PrimaryMonitor(out mon);
            if (known && store.ReplaceTaskbar && TaskbarSwap.Active)
            {
                if (!TaskbarSwap.EnsureAutoHide()) KickAutoHide();
                reserve = TaskbarReserve(mon);
                reserveMon = mon;
            }
            bool want = known && AppBarRules.Wanted(true, store.ReplaceTaskbar, store.AutoHide, TaskbarSwap.Taken);
            if (!want) return AppBar.Sync(me, store.Dir, false, new PxRect(), 0, 0);
            return AppBar.Sync(me, store.Dir, true, mon, AppBarRules.Height(RestDip, Scale), reserve);
        }

        // Где стоит док: рабочая область главного монитора, а пока он занял своё место — по низу этого места
        // (рабочая область тогда кончается над ним). Если ниже осталось только место спрятанной панели задач
        // (Windows не включила ей автоскрытие) — у самого низа монитора, поверх этого места (AppBarRules.BottomPx).
        Rect Area
        {
            get
            {
                int above;
                PxRect wk;
                if (AppBar.Registered) above = AppBar.Band.B;
                else if (reserve > 0 && Win.PrimaryWork(out wk)) above = wk.B;
                else return work;
                int bottomPx = AppBarRules.BottomPx(reserveMon, above, reserve);
                double bottom = AppBarRules.BottomDip(new PxRect(0, 0, 0, bottomPx), Scale);
                return new Rect(work.Left, work.Top, work.Width, Math.Max(0, bottom - work.Top));
            }
        }

        // ───────── Корзина ─────────
        // metla.txt на связи: прислала metla.bin или видна в hub.apps
        bool MetlaOnline { get { return demo ? bin != null : bin != null || BinCan("bin.open") || BinCan("show"); } }

        bool BinCan(string command) { return demo ? bin != null : hub != null && hub.Has("metla", command); }

        bool BinVisible { get { return BinRules.Visible(store.Bin, binInstalled, MetlaOnline); } }

        // Всё, от чего зависят бак и его меню: поменялось — док перерисовывается
        string BinKey()
        {
            return BinVisible + "|" + MetlaOnline + "|" + BinCan("bin.open") + "|" + BinCan("show") + "|" + (bin == null ? "-" : bin.Count + ":" + bin.Bytes);
        }

        List<BinRow> BinMenu() { return BinRules.Menu(bin, BinCan("show")); }

        // Для test.state: есть ли бак, какой он, что в подписи и в меню
        Dictionary<string, object> BinState()
        {
            Slot s = slots.Find(delegate(Slot x) { return x.Kind == SlotKind.Bin && !x.Leaving; });
            var rows = new List<object>();
            foreach (BinRow r in BinMenu()) rows.Add(r.Line ? (object)HubLink.Msg("line", true) : HubLink.Msg("label", r.Label, "act", r.Act, "enabled", r.Enabled));
            return HubLink.Msg("shown", s != null, "sprite", s == null ? null : BinRules.Sprite(bin == null ? 0 : bin.Count, binDropHot) == BinRules.Full ? "bin-full" : "bin",
                "online", MetlaOnline, "installed", binInstalled, "setting", store.Bin, "count", bin == null ? (object)null : bin.Count, "bytes", bin == null ? (object)null : bin.Bytes,
                "tip", BinRules.Tip(bin), "menu", rows.ToArray(), "last", slots.Count > 0 && slots[slots.Count - 1] == s);
        }

        // Пункт меню бака: open, clean, scan, startup. Сюда же — проверочная команда test.act. Что отправлено (null — пункта
        // сейчас нет или он неактивен): «bin.open», «show:bin», «show:scan», «show:startup» или «explorer» — metla.txt нет,
        // корзину открывает сам проводник (в снимках и без окна — не открывает).
        internal string BinAction(string act)
        {
            BinRow row = BinMenu().Find(delegate(BinRow r) { return r.Act == act && r.Act != null; });
            if (row == null || !row.Enabled) return null;
            if (act == "open")
            {
                if (BinCan("bin.open")) { if (!demo) hub.Metla("bin.open", null); return "bin.open"; }
                if (!demo && !headless) try { using (Process.Start("explorer.exe", "shell:RecycleBinFolder")) { } } catch { }
                return "explorer";
            }
            string page = BinRules.Page(act);
            if (page == null) return null;
            if (!demo) hub.Metla("show", HubLink.Msg("page", page));
            return "show:" + page;
        }

        // Бак: пиксельный спрайт из kit.txt, по размеру значка дока. Полный — когда в корзине что-то есть или над ним файлы.
        Slot BinSlot()
        {
            var slot = new Slot { Kind = SlotKind.Bin, Id = "bin", W = size, Built = size };
            var face = new Border { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            // Края пикселей — резкие, как у остальных пиксельных значков семьи, и при увеличении под курсором тоже
            RenderOptions.SetEdgeMode(face, EdgeMode.Aliased);
            var root = new Grid { Width = size, Height = size, RenderTransformOrigin = new Point(0.5, 1), IsHitTestVisible = false, Children = { face } };
            root.RenderTransform = new TransformGroup { Children = { slot.Scale, slot.Move } };
            slot.Root = root;
            string[] now = null;
            slot.Update = delegate
            {
                string[] want = BinRules.Sprite(bin == null ? 0 : bin.Count, binDropHot);
                if (want == now) return;
                now = want;
                face.Child = Ui.Sprite(want, Math.Round(size * 0.8));
            };
            slot.Update();
            return slot;
        }

        // Файлы над доком: над баком — крышка приподнята и курсор «Переместить»; над значком обычной программы — курсор
        // «Копировать» и подпись «Открыть в …»; над остальным — нельзя
        void FilesOver(object sender, DragEventArgs e)
        {
            e.Handled = true;
            Slot at = SlotAt(e.GetPosition(this).X);
            bool files = at != null && !at.Leaving && e.Data.GetDataPresent(DataFormats.FileDrop);
            bool hot = files && at.Kind == SlotKind.Bin;
            bool open = files && at.Kind == SlotKind.Item && !demo && OpenWith.Accepts(at.Item, System.IO.File.Exists);
            e.Effects = hot ? DragDropEffects.Move : open ? DragDropEffects.Copy : DragDropEffects.None;
            SetBinDropHot(hot);
            SetDropFor(open ? at : null);
            StartLoop();   // кнопка мыши у проводника: движения мыши док не получает, кадры идут сами — бак растёт под курсором
        }

        // Подпись над значком, на который несут файлы: «Открыть в «Блокнот»»
        void SetDropFor(Slot s)
        {
            if (s == dropFor) return;
            dropFor = s;
            int at = s == null ? -1 : slots.IndexOf(s);
            if (at >= 0 && hover != at) SetHover(at);
            tipOverride = s != null && s.Item != null ? OpenWith.Tip(s.Item.Name) : null;
            if (hover >= 0) ShowTip();
        }

        void SetBinDropHot(bool hot)
        {
            if (hot == binDropHot) return;
            binDropHot = hot;
            Slot s = Find("bin");
            if (s != null && s.Update != null) s.Update();
        }

        // Бросили на бак: в корзину Windows с возможностью восстановить. Проводнику отвечаем «ничего не сделано» (None):
        // при «Переместить» он мог бы сам удалить исходные файлы — а их уже кладёт в корзину сама Windows по нашей просьбе.
        // Сама просьба — после того, как проводник отпустил перетаскивание, чтобы его окно не ждало вопроса Windows.
        // Бросили на значок программы — она открывает эти файлы (запускается с их путями). Проводнику отвечаем «скопировано»:
        // сами файлы остаются на месте.
        void FilesDropped(object sender, DragEventArgs e)
        {
            Slot open = dropFor;
            SetDropFor(null);
            Slot at = SlotAt(e.GetPosition(this).X);
            if (open == null || at != open || demo || open.Item == null) { BinDrop(e); return; }
            e.Handled = true;
            e.Effects = DragDropEffects.Copy;
            SetBinDropHot(false);
            List<string> paths = BinRules.DropPaths(e.Data.GetData(DataFormats.FileDrop) as string[], delegate(string p) { return System.IO.File.Exists(p) || System.IO.Directory.Exists(p); });
            DockItem item = open.Item;
            string args = OpenWith.Arguments(item.Pin != null ? item.Pin.Args : null, paths);
            if (args == null || !OpenWith.Accepts(item, System.IO.File.Exists)) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                try { Shell.OpenWith(item, args); Bounce(open); }
                catch
                {
                    int i = slots.IndexOf(open);
                    if (i < 0) return;
                    SetHover(i);
                    tipOverride = OpenWith.Failed;
                    ShowTip();
                }
            }));
        }

        void BinDrop(DragEventArgs e)
        {
            e.Handled = true;
            bool hot = binDropHot;
            SetBinDropHot(false);
            e.Effects = DragDropEffects.None;
            Slot at = SlotAt(e.GetPosition(this).X);
            if (!hot || at == null || at.Kind != SlotKind.Bin || demo) return;
            List<string> paths = BinRules.DropPaths(e.Data.GetData(DataFormats.FileDrop) as string[], delegate(string p) { return System.IO.File.Exists(p) || System.IO.Directory.Exists(p); });
            if (paths.Count == 0) return;
            IntPtr owner = new WindowInteropHelper(this).Handle;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
            {
                try { BinRules.Recycle(owner, paths); } catch { }
            }));
        }

        PopMenu BuildBinMenu()
        {
            var menu = new PopMenu();
            foreach (BinRow r in BinMenu())
            {
                if (r.Line) { menu.Line(); continue; }
                if (r.Act == null) { menu.Item(r.Label, false, null); continue; }   // сведение: сколько лежит, неактивно
                string act = r.Act;
                menu.Item(r.Label, false, r.Enabled ? new Action(delegate { BinAction(act); }) : null);
            }
            return menu;
        }

        // «Настройки» в меню дока — страница дока в core.txt. Запущенный core.txt просим через хаб; установленный, но
        // закрытый запускаем сразу на ней (ключ --page; прежний core.txt незнакомую страницу пропускает и открывается
        // как обычно); без core.txt — скачать его.
        void OpenSettings()
        {
            bool online = hub != null && hub.Online;
            if (!online && CoreApp.Installed)
            {
                try { using (Process.Start(CoreApp.Path, "--page " + FamilyRules.CorePage)) { } } catch { }
                return;
            }
            CoreApp.Go(online, delegate { hub.ShowCore(); });
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
            if (hub != null) { hub.PublishSettings(true); hub.PublishApps(items); hub.PublishFamily(); }
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
        FrameworkElement Face(DockItem item, double px)
        {
            ImageSource img = demo ? null : Shell.IconOf(item);
            if (img != null)
            {
                var image = new Image { Source = img, Width = px, Height = px };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                return image;
            }
            return Glyphs.LetterTile(item.Name, px, items.FindIndex(delegate(DockItem i) { return i.Key == item.Key; }));
        }

        Slot IconSlot(DockItem item)
        {
            var slot = new Slot { Kind = SlotKind.Item, Id = "item:" + item.Key, Item = item, W = size, Built = size };
            var root = new Grid { Width = size, Height = size, RenderTransformOrigin = new Point(0.5, 1), IsHitTestVisible = false, Children = { Face(item, size) } };
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
                // Счётчик появился — выскакивает (при «Не беспокоить» — без движения); сменилась цифра — просто меняется
                if (FamilyRules.BadgePops(slot.BadgeText == null, Quiet)) Motion.Pop(slot.Badge);
            }
            slot.BadgeText = badge;
        }

        // Кнопка меню приложений: плитка той же формы, что значки, с сеткой из девяти точек — «все приложения»
        Slot LauncherSlot()
        {
            var slot = new Slot { Kind = SlotKind.Launcher, Id = "launcher", W = size, Built = size };
            var root = new Grid { Width = size, Height = size, RenderTransformOrigin = new Point(0.5, 1), IsHitTestVisible = false, Children = { Glyphs.AppsTile(size) } };
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
            if (armed == s) armed = null;
            if (previewFor == s) ClosePreview(true);
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
            binKey = BinKey();
            binShown = BinVisible;
            int count = items.Count + 1 + (binShown ? 1 : 0);   // значки, кнопка меню приложений и корзина
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
                double taken = (split ? 1 + gap : 0) + (widgets.Count > 0 ? 1 + gap : 0) + (binShown ? 1 + gap : 0);
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
            // Корзина — самая правая, за своей чертой, как на Mac
            if (binShown)
            {
                next.Add(Reuse("line:bin") ?? LineSlot("line:bin"));
                Slot b = Reuse("bin");
                if (b != null) b.Update(); else b = BinSlot();
                next.Add(b);
            }

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
            if (drag != null) KeepDragOrder();

            double panelH = PadTop + size + DotZone;
            // Своё место у Windows — высотой с док в покое; занято или отдано, оно решает, где низ дока
            SyncAppBar();
            Rect area = Area;
            winW = area.Width;
            winH = EdgeGap + panelH + Math.Max(size * (MaxScale - 1) + TipGap + TipH, WidgetTipGap + TipMaxH) + TopRoom;
            baseline = winH - EdgeGap - DotZone;
            panelTop = baseline - size - PadTop;
            panel.Height = panelH;
            Canvas.SetTop(panel, panelTop);
            stage.Width = winW; stage.Height = winH;
            Width = winW; Height = winH;
            Left = area.Left;
            Top = area.Bottom - winH;

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
            if ((hidden || away) && HiddenY != lastHiddenY) { slide.BeginAnimation(TranslateTransform.YProperty, null); slide.Y = HiddenY; SyncGlass(); }
            lastHiddenY = HiddenY;
            if (animate) StartLoop();
            if (hub != null) hub.PublishWidgets(shown, Badges());
        }

        // Точка под значком: нет окон — нет точки; окно программы впереди — точка шире и ярче; окно просит внимания
        // (и уже отпрыгало) — точка оранжевая
        void SyncDots()
        {
            bool any = false;
            foreach (Slot s in slots)
            {
                if (s.Kind != SlotKind.Item || s.Dot == null || s.Item == null) continue;
                Brush paint = !s.Leaving && PhaseOf(s.Item) == AttentionPhase.Mark ? askingDot : Ui.Label;
                if (s.Dot.Background != paint) s.Dot.Background = paint;
                double to = s.Leaving || !s.Item.Running ? 0 : fg != IntPtr.Zero && s.Item.Windows.Contains(fg) ? DotWide : paint == askingDot ? DotAsking : DotSize;
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
                if (s.Show.Running || s.Wide.Running || s.Press.Running || s.DotW.Running || s.BounceAt >= 0 || s.Float.Running || s.Shift.Running || s.Gap.Running) busy = true;
                k[i] = s.Show.Value(now); w[i] = s.Wide.Value(now); press[i] = s.Press.Value(now); dot[i] = s.DotW.Value(now);
            }
            if (drag != null) busy = true;   // значок в руке идёт за курсором каждый кадр
            if (!force && !busy && !moving && cursorU == lastU && amount == lastAmount) return;
            moving = busy;
            lastU = cursorU; lastAmount = amount;

            double[] rw = new double[n], mw = new double[n], scale = new double[n], part = new double[n];
            double total = 0;
            for (int i = 0; i < n; i++) { part[i] = k[i] * slots[i].Gap.Value(now); rw[i] = (w[i] + gap) * part[i]; total += rw[i]; }
            restW = total;
            double a = 0, range = RangeSlots * (size + gap);
            for (int i = 0; i < n; i++)
            {
                double centre = a + rw[i] / 2;
                scale[i] = !slots[i].Icon ? 1 : 1 + amount * (Magnify.Scale(cursorU - centre, range, MaxScale) - 1);
                mw[i] = (w[i] * scale[i] + gap) * part[i];
                a += rw[i];
            }
            double from, to;
            double[] cell = Magnify.Cells(rw, mw, cursorU, out from, out to);
            double origin = Origin;
            panelLeft = origin + from - PadH; panelRight = origin + to + PadH;
            Canvas.SetLeft(panel, panelLeft);
            panel.Width = Math.Max(0, panelRight - panelLeft);
            PlaceShadow();
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
                double shift = s.Shift.Value(now);   // место переехало — значок доезжает до него плавно
                if (s.Kind == SlotKind.Line)
                {
                    s.Move.X = s.Centre - 0.5 + shift;
                    s.Move.Y = baseline - size + (size - ((Rectangle)s.Root).Height) / 2;
                    continue;
                }
                double grow = 0.6 + 0.4 * k[i];   // новый значок вырастает, уходящий — сжимается
                if (s.Kind == SlotKind.Widget)
                {
                    s.Scale.ScaleX = s.Scale.ScaleY = (1 - (1 - press[i]) / 2) * grow;   // капсула широкая — сжимается вдвое слабее значка
                    s.Move.X = s.Centre - s.W / 2 + shift;
                    s.Move.Y = baseline - size;
                    continue;
                }
                double sc = scale[i] * press[i] * grow;
                double mx = s.Centre - size / 2 + shift;   // значок растёт от своего низа и от середины своего места
                double y = baseline - size - size * scale[i] * grow * (1 - press[i]) / 2;   // нажатие сжимает к середине значка, а не к полу
                if (s.BounceAt >= 0)
                {
                    long t = now - s.BounceAt;
                    int hop = (int)(t / BounceMs);
                    if (s.BounceStop < 0 && s.Item != null && s.Item.Running) s.BounceStop = hop + 1;   // окно появилось — допрыгиваем и встаём
                    if (hop >= (s.BounceStop >= 0 ? Math.Min(BounceCycles, s.BounceStop) : BounceCycles) || Ui.ReducedMotion) { s.BounceAt = -1; s.BounceStop = -1; dotsLater = true; }
                    else y -= size * BounceHeight * (1 - 0.2 * hop) * Tween.Bounce(t, BounceMs);
                }
                // В руке: значок чуть крупнее, с тенью, его середина — у курсора. Между местом и рукой он переходит плавно.
                double f = s.Float.Value(now);
                if (f > 0)
                {
                    double up = LiftScale * grow;
                    sc += (up - sc) * f;
                    mx += (s.FloatX - size / 2 - mx) * f;
                    y += (s.FloatY - size + size * up / 2 - y) * f;
                }
                if (s.Shadow != null)
                {
                    s.Shadow.Opacity = LiftShadow * f;
                    if (f <= 0 && s != drag) { s.Root.Effect = null; s.Shadow = null; Panel.SetZIndex(s.Root, 4); }   // сел на место
                }
                s.Scale.ScaleX = s.Scale.ScaleY = sc;
                s.Move.X = mx;
                s.Move.Y = y;
                if (s.Dot == null) continue;
                double dw = Math.Max(0, dot[i]);
                s.Dot.Width = dw;
                double bright = s.Dot.Background == askingDot ? 1 : 0.72 + 0.28 * Math.Max(0, Math.Min(1, (dw - DotSize) / (DotWide - DotSize)));   // оранжевая — всегда в полную силу
                s.Dot.Opacity = k[i] * (1 - f) * bright;
                s.DotMove.X = s.Centre + shift - dw / 2;
                s.DotMove.Y = baseline + (DotZone - DotSize) / 2 - 1;
            }
            PlaceTip();
            SyncGlass();
            if (dotsLater && attention.Count > 0) { dotsLater = false; SyncDots(); }
            dotsLater = false;
        }

        // Тень полосы: на месте полосы, внутренность вырезана
        void PlaceShadow()
        {
            double w = panel.Width, h = panel.Height;
            if (double.IsNaN(h)) return;
            Canvas.SetLeft(shadowBox, panelLeft);
            Canvas.SetTop(shadowBox, panelTop);
            shadowBox.Width = w; shadowBox.Height = h;
            shadowOuter.Rect = new Rect(-60, -60, w + 120, h + 120);
            shadowInner.Rect = new Rect(1, 1, Math.Max(0, w - 2), Math.Max(0, h - 2));
        }

        // ───────── Размытие за полосой ─────────
        // Включить или выключить стекло: по «Эффектам прозрачности» Windows, на ходу
        void ApplyGlass()
        {
            bool on = glass != null && !ended && GlassBackdrop.TransparencyOn();
            if (on == glassOn && glass != null) { SyncGlass(); return; }
            glassOn = on;
            panel.Background = on ? glassFill : solidFill;
            SyncGlass();
        }

        // Подложка встаёт ровно под полосу, в точках экрана. Зовётся там же, где полоса двигается (Place, проезд
        // за край), поэтому в покое не делает ничего; одинаковое место второй раз Windows не передаётся (GlassBackdrop).
        void SyncGlass()
        {
            if (glass == null) return;
            PresentationSource src = glassOn && IsVisible && !ended ? PresentationSource.FromVisual(this) : null;
            double pw = panel.Width, ph = panel.Height;
            if (src == null || src.CompositionTarget == null || double.IsNaN(pw) || double.IsNaN(ph)) { glass.Hide(); return; }
            Matrix m = src.CompositionTarget.TransformToDevice;
            double top = panelTop + slide.Y, bottom = Math.Min(winH, top + ph);   // ниже окна дока полосы не видно — и подложки тоже
            int x = (int)Math.Round((Left + panelLeft) * m.M11), y = (int)Math.Round((Top + top) * m.M22);
            int h = (int)Math.Round((Top + bottom) * m.M22) - y;
            glass.Place(x, y, (int)Math.Round(pw * m.M11), h);
        }

        // Полоса едет за край или обратно (анимация WPF): подложка следует за ней каждый кадр, пока проезд не кончится
        void FollowGlide(int ms)
        {
            if (glass == null) return;
            glideUntil = clock.ElapsedMilliseconds + ms + 60;
            if (gliding) return;
            gliding = true;
            CompositionTarget.Rendering += GlideFrame;
        }

        void GlideFrame(object sender, EventArgs e)
        {
            SyncGlass();
            if (clock.ElapsedMilliseconds < glideUntil && !ended) return;
            gliding = false;
            CompositionTarget.Rendering -= GlideFrame;
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
            return s.Kind == SlotKind.Item ? s.Item.Name : s.Kind == SlotKind.Launcher ? AppsTip : s.Kind == SlotKind.Widget ? s.Tip : s.Kind == SlotKind.Bin ? BinRules.Tip(bin) : null;
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
            PreviewHover();
        }

        void ShowTip()
        {
            if (drag != null) return;   // пока значок в руке, подписью распоряжается перетаскивание
            // Над значком открыта панель с его окнами — подпись с названием ей уступает
            bool on = hover >= 0 && !menuOpen && !(PreviewShown && previewFor == slots[hover]) && FillTip();
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
            if (drag != null) { if (have) DragStep(p); else CancelDrag(); }
            bool reduced = Ui.ReducedMotion;
            double target = inside && drag == null && store.Magnify && !reduced ? 1 : 0;   // пока значок несут, док не растёт
            if (reduced) { amount = 0; velocity = 0; }   // «Меньше движения»: значки не растут вовсе
            else
            {
                Magnify.Spring(ref amount, ref velocity, target, Tokens.Motion.SpringShapeStiffness, Tokens.Motion.SpringShapeDamping, dt);
                if (amount < 0) { amount = 0; velocity = 0; }
            }
            Place();

            if (!menuOpen) SetHover(inside && drag == null ? IndexAt(p.X) : -1);

            if (!inside && !menuOpen && !moving && drag == null && Math.Abs(amount - target) < 0.0005 && Math.Abs(velocity) < 0.005)
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

        // Двадцать раз в секунду, и только при включённом автоскрытии: где курсор
        void EdgeTick()
        {
            Point p;
            if (away) { edgeRule.Reset(); return; }   // игра или видео на весь экран: от края не выезжаем вовсе
            if (!CursorHere(out p)) return;
            long now = clock.ElapsedMilliseconds;
            if (hidden)
            {
                // Нижний край экрана во всю ширину, полоска в две точки: док выезжает, когда курсор простоял на ней
                // EdgeReveal.DwellMs (0,35 с) — по дороге к прокрутке и кнопкам внизу окна он не выскакивает. Если под доком
                // панель задач Windows, это край над ней — правило то же. Перед самым выездом — ещё раз своя проверка
                // игры: core.txt мог ещё не сказать, а курсор у низа экрана в стратегиях — обычное дело.
                bool onEdge = p.X >= 0 && p.X <= winW && p.Y >= winH - 2 && p.Y <= winH + 1;
                if (edgeRule.Step(now, onEdge, menuOpen) && !GameAhead()) { lastInside = now; SetHidden(false); StartLoop(); }
            }
            else if (menuOpen || drag != null || InZone(p) || (preview != null && preview.Hot)) lastInside = now;
            else if (now - lastInside > HideAfterMs) SetHidden(true);
        }

        // Впереди на мониторе дока игра или видео на весь экран — по своей проверке, прямо сейчас
        static bool GameAhead() { return Win.OnDockScreen(Win.Foreground()) && FullScreen.Ahead(); }

        void SetHidden(bool hide)
        {
            if (hidden == hide) return;
            hidden = hide;
            if (hide) ClosePreview(true);
            Glide();
        }

        // Дока не должно быть видно совсем: впереди игра или видео на весь экран, или выбрана панель задач Windows.
        // Он уезжает за край и только потом окно прячется; возвращается — так же, выезжая.
        void SyncAway()
        {
            bool want = fullscreen || store.Bar == "modern";
            if (want == away) return;
            away = want;
            if (away) ClosePreview(true);   // игра или видео на весь экран — панели с окнами тоже не место
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
                SyncGlass();
                if (away && !headless && !demo && IsVisible) Hide();
                return;
            }
            int ms = down ? Tokens.Motion.BaseMs : Tokens.Motion.SlowMs;
            var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = down ? (IEasingFunction)new CubicEase { EasingMode = EasingMode.EaseIn } : Ui.EaseOut };
            if (away) a.Completed += delegate { if (away && !ended) Hide(); };
            slide.BeginAnimation(TranslateTransform.YProperty, a);
            FollowGlide(ms);
        }

        // ───────── Щелчки ─────────
        // Все щелчки приходят на общую полосу; чей столбец — того и щелчок. Нажатие сжимает значок сразу (70 мс),
        // отпускание возвращает мягко (220 мс) — видно, что док услышал.
        void StripDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (drag != null) { CancelDrag(); return; }   // другая кнопка, пока значок в руке, — отмена
            Slot s = SlotAt(e.GetPosition(this).X);
            if (e.ChangedButton == MouseButton.Left)
            {
                Release();
                Disarm();
                ClosePreview(false);
                pressed = s;
                if (s == null) return;
                Go(s.Press, PressScale, PressDownMs);
                Kick();
                // Закреплённый значок можно поднять и перенести. Курсор остаётся за доком, даже если уйдёт выше его окна.
                if (CanDrag(s))
                {
                    armed = s;
                    armAt = e.GetPosition(this);
                    strip.CaptureMouse();
                }
            }
            else if (e.ChangedButton == MouseButton.Middle && s != null) Act(s, true);
        }

        void StripUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (drag != null)
            {
                if (e.ChangedButton == MouseButton.Left) EndDrag();
                return;
            }
            Slot s = SlotAt(e.GetPosition(this).X);
            if (e.ChangedButton == MouseButton.Right) { OpenMenu(s != null && (s.Kind == SlotKind.Item || s.Kind == SlotKind.Bin) ? s : null); return; }
            if (e.ChangedButton != MouseButton.Left) return;
            Slot was = pressed;
            Disarm();
            Release();
            if (was != null && was == s) Act(was, false);
        }

        // ───────── Перетаскивание закреплённых значков ─────────
        // Нажали на закреплённый значок и повели дальше шести точек — он поднимается и идёт за курсором, соседи
        // расступаются, показывая, куда он встанет. Отпустили в доке — новый порядок сохраняется; унесли высоко вверх —
        // значок открепляется. Esc и любая другая кнопка мыши возвращают всё как было. Щелчок без движения — прежний щелчок.
        bool CanDrag(Slot s)
        {
            return !demo && !menuOpen && !fake.HasValue && s != null && s.Kind == SlotKind.Item && !s.Leaving && s.Item != null && s.Item.Pinned;
        }

        void Disarm()
        {
            if (armed == null) return;
            armed = null;
            if (drag == null && strip.IsMouseCaptured) strip.ReleaseMouseCapture();
        }

        void StripMove(object sender, MouseEventArgs e)
        {
            if (armed == null || drag != null) return;
            if (e.LeftButton != MouseButtonState.Pressed || !slots.Contains(armed) || !CanDrag(armed)) { Disarm(); return; }
            Point p = e.GetPosition(this);
            if (Logic.DragStarts(p.X - armAt.X, p.Y - armAt.Y)) BeginDrag(armed, p);
        }

        // Закреплённые значки по порядку — те, что есть сейчас (уходящие не в счёт)
        List<Slot> PinnedSlots()
        {
            return slots.FindAll(delegate(Slot s) { return s.Kind == SlotKind.Item && !s.Leaving && s.Item != null && s.Item.Pinned; });
        }

        void Lift(Slot s)
        {
            if (s.Shadow == null) s.Shadow = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 6, Direction = 270, Opacity = 0, Color = Colors.Black };
            s.Root.Effect = s.Shadow;
            Panel.SetZIndex(s.Root, 8);
        }

        void BeginDrag(Slot s, Point p)
        {
            armed = null;
            int at = PinnedSlots().IndexOf(s);
            if (at < 0) { if (strip.IsMouseCaptured) strip.ReleaseMouseCapture(); return; }
            if (pressed == s) { Go(s.Press, 1, PressUpMs); pressed = null; }   // это уже не щелчок
            ClosePreview(true);
            SetHover(-1);
            drag = s;
            dragTo = at;
            dragOut = false;
            double cy = baseline - size * s.Now / 2;
            grabX = armAt.X - s.Centre; grabY = armAt.Y - cy;
            s.FloatX = p.X - grabX; s.FloatY = p.Y - grabY;
            Lift(s);
            Go(s.Float, 1, LiftMs);
            StartLoop();
        }

        // Каждый кадр, пока значок в руке: он идёт за курсором, а его место в ряду — туда, куда он встанет
        void DragStep(Point p)
        {
            Slot s = drag;
            if (Win.KeyDown(Win.KeyEscape) || !slots.Contains(s) || s.Leaving) { CancelDrag(); return; }
            // Значок не уходит за край окна дока: выше окна его было бы не видно
            double lifted = size * LiftScale;
            s.FloatX = Math.Max(lifted / 2, Math.Min(winW - lifted / 2, p.X - grabX));
            s.FloatY = Math.Max(2 + lifted / 2, Math.Min(baseline - size / 2 + 4, p.Y - grabY));
            bool outside = Logic.DragOut(panelTop - p.Y, size);
            if (outside != dragOut)
            {
                dragOut = outside;
                Go(s.Gap, outside ? 0 : 1, PartMs);   // место в ряду закрывается: видно, что значок уйдёт из дока
                DragTip(outside);
            }
            if (dragOut) { PlaceDragTip(s); return; }
            List<Slot> pinned = PinnedSlots();
            int at = pinned.IndexOf(s);
            if (at < 0) { CancelDrag(); return; }
            double[] left = new double[pinned.Count], right = new double[pinned.Count];
            for (int i = 0; i < pinned.Count; i++) { left[i] = pinned[i].CellL; right[i] = pinned[i].CellR; }
            int to = Logic.DropIndex(left, right, s.FloatX, at, (size + gap) * DropSlack);
            if (to != at) MoveSlot(s, slots.IndexOf(pinned[to]));
            dragTo = to;
        }

        // Переставляет место в ряду. Все, кого это сдвинуло, не прыгают, а плавно доезжают оттуда, где были.
        void MoveSlot(Slot s, int to)
        {
            int from = slots.IndexOf(s);
            if (from < 0 || to < 0 || to >= slots.Count || from == to) return;
            long now = clock.ElapsedMilliseconds;
            var was = new Dictionary<Slot, double>();
            foreach (Slot o in slots) was[o] = o.Centre + o.Shift.Value(now);
            slots.RemoveAt(from);
            slots.Insert(to, s);
            Place(true);
            foreach (Slot o in slots)
            {
                double d = was[o] - o.Centre;
                if (Math.Abs(d) < 0.5) { o.Shift.Set(0); continue; }
                o.Shift.Set(d);
                Go(o.Shift, 0, PartMs);
            }
            Place(true);
        }

        // Док пересобрался, пока значок в руке (открылось или закрылось чьё-то окно): место значка остаётся там, куда его донесли
        void KeepDragOrder()
        {
            List<Slot> pinned = PinnedSlots();
            int at = pinned.IndexOf(drag);
            if (at < 0)
            {
                // Значка больше нет среди закреплённых (открепили из другого места) — нести нечего
                Slot s = drag;
                drag = null; dragOut = false; dragTo = -1;
                if (strip.IsMouseCaptured) strip.ReleaseMouseCapture();
                DragTip(false);
                s.Gap.Set(1);
                Go(s.Float, 0, SettleMs);
                return;
            }
            int to = Math.Max(0, Math.Min(pinned.Count - 1, dragTo));
            dragTo = to;
            if (to == at) return;
            int target = slots.IndexOf(pinned[to]);
            slots.Remove(drag);
            slots.Insert(target, drag);
        }

        // Отпустили: в доке — значок садится на новое место и порядок сохраняется; высоко над доком — открепляется
        void EndDrag()
        {
            Slot s = drag;
            bool unpin = dragOut;
            int to = dragTo;
            drag = null; dragOut = false; dragTo = -1;
            if (strip.IsMouseCaptured) strip.ReleaseMouseCapture();
            DragTip(false);
            lastInside = clock.ElapsedMilliseconds;
            DockItem item = s.Item;
            if (item == null) { s.Gap.Set(1); Go(s.Float, 0, SettleMs); Kick(); return; }
            if (unpin)
            {
                store.UnpinItem(item);
                SettingsChanged();
                // Незапущенное гаснет там, где его отпустили. Запущенное остаётся в доке незакреплённым и садится на своё новое место.
                if (slots.Contains(s) && !s.Leaving) { Go(s.Gap, 1, PartMs); Go(s.Float, 0, SettleMs); }
                Kick();
                return;
            }
            List<DockItem> pinned = items.FindAll(delegate(DockItem i) { return i.Pinned; });
            int from = pinned.FindIndex(delegate(DockItem i) { return i.Key == item.Key; });
            if (from >= 0 && to >= 0 && to < pinned.Count && to != from && store.Move(store.Pins.IndexOf(pinned[from].Pin), store.Pins.IndexOf(pinned[to].Pin)))
            {
                items = Collect();
                Rebuild();
                if (hub != null) hub.PublishApps(items);
            }
            Go(s.Gap, 1, PartMs);
            Go(s.Float, 0, SettleMs);
            Kick();
        }

        // Отмена: значок возвращается туда, откуда его взяли; ничего не сохраняется
        void CancelDrag()
        {
            Slot s = drag;
            if (s == null) return;
            drag = null; dragOut = false; dragTo = -1;
            if (strip.IsMouseCaptured) strip.ReleaseMouseCapture();
            DragTip(false);
            lastInside = clock.ElapsedMilliseconds;
            if (slots.Contains(s) && !s.Leaving && s.Item != null)
            {
                string key = s.Item.Key;
                int home = items.FindAll(delegate(DockItem i) { return i.Pinned; }).FindIndex(delegate(DockItem i) { return i.Key == key; });
                List<Slot> pinned = PinnedSlots();
                int at = pinned.IndexOf(s);
                if (home >= 0 && home < pinned.Count && at >= 0 && home != at) MoveSlot(s, slots.IndexOf(pinned[home]));
            }
            Go(s.Gap, 1, PartMs);
            Go(s.Float, 0, SettleMs);
            Kick();
        }

        // Подпись «Открепить» под значком, который унесли из дока
        void DragTip(bool on)
        {
            if (on)
            {
                tipText.Text = UnpinTip;
                tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                if (drag != null) PlaceDragTip(drag);
            }
            Fade(tip, on ? 1 : 0, on ? Tokens.Motion.FastMs : Tokens.Motion.PressMs);
        }

        void PlaceDragTip(Slot s)
        {
            double w = tip.DesiredSize.Width;
            Canvas.SetLeft(tip, Math.Max(2, Math.Min(winW - w - 2, s.FloatX - w / 2)));
            Canvas.SetTop(tip, s.FloatY + size * LiftScale / 2 + 6);
        }

        // ───────── Окна программы над значком ─────────
        bool PreviewShown { get { return preview != null && preview.Shown; } }

        bool CanPreview(Slot s)
        {
            return !demo && !headless && !fake.HasValue && !menuOpen && appMenu == null && drag == null && !hidden && !away
                && s != null && s.Kind == SlotKind.Item && !s.Leaving && s.Item != null && s.Item.Running;
        }

        // Курсор перешёл на другое место дока (или ушёл с него)
        void PreviewHover()
        {
            Slot s = hover >= 0 && hover < slots.Count ? slots[hover] : null;
            bool can = CanPreview(s);
            previewOpen.Stop();
            if (PreviewShown)
            {
                previewShut.Stop();
                if (can && s == previewFor) return;
                if (can) { OpenPreview(s); return; }   // панель уже открыта — у соседнего значка она появляется без ожидания
                previewShut.Start();                   // курсор мог уйти на саму панель: даём ему время дойти
                return;
            }
            if (can && pressed == null) previewOpen.Start();
        }

        // Курсор простоял на значке положенное время
        void PreviewDue()
        {
            Slot s = hover >= 0 && hover < slots.Count ? slots[hover] : null;
            if (ended || pressed != null || armed != null || !inside || !CanPreview(s)) return;
            OpenPreview(s);
        }

        // Курсор ушёл и со значка, и с панели — и не вернулся
        void PreviewLeft()
        {
            if (!PreviewShown) return;
            if (preview.Hot || (hover >= 0 && hover < slots.Count && slots[hover] == previewFor)) return;
            ClosePreview(false);
        }

        List<PreviewCard> PreviewCards(DockItem item)
        {
            var list = new List<PreviewCard>();
            foreach (IntPtr h in item.Windows)
            {
                if (list.Count >= PreviewRules.MaxWindows) break;
                string title = TitleOf(h);
                list.Add(new PreviewCard { Hwnd = h, Title = title.Length > 0 ? title : item.Name, Minimized = Win.Minimized(h) });
            }
            return list;
        }

        void OpenPreview(Slot s)
        {
            if (preview == null)
            {
                preview = new PreviewWindow();
                preview.Enter = delegate { previewShut.Stop(); };
                preview.Leave = delegate { previewShut.Stop(); previewShut.Start(); };
                preview.Pick = delegate(IntPtr h)
                {
                    ClosePreview(false);
                    try { Win.Activate(h); } catch { }
                };
                preview.Shut = delegate(IntPtr h)
                {
                    try { Win.Close(h); } catch { }
                    previewPolls = 8;   // панель обновится, как только окно исчезнет (или останется, если программа спросила о несохранённом)
                    previewPoll.Stop(); previewPoll.Start();
                };
            }
            DockItem item = s.Item;
            previewFor = s;
            double top = baseline - size * (store.Magnify && !Ui.ReducedMotion ? MaxScale : 1);
            preview.ShowFor(PreviewCards(item), delegate(double px) { return Face(item, px); }, Left + s.Centre, Top + top - PreviewWindow.Gap, work);
            ShowTip();
        }

        // now — без угасания: док уезжает, открыто меню, впереди игра
        void ClosePreview(bool now)
        {
            previewOpen.Stop(); previewShut.Stop();
            if (!PreviewShown) { previewFor = null; return; }
            preview.HideIt(now);
            previewFor = null;
            if (hover >= 0 && hover < slots.Count) ShowTip();
        }

        // Раз в секунду вместе с доком: окна программы могли открыться, закрыться, свернуться, сменить заголовок
        void SyncPreview()
        {
            if (!PreviewShown) return;
            Slot s = previewFor;
            if (s == null || !slots.Contains(s) || !CanPreview(s)) { ClosePreview(true); return; }
            preview.Update(PreviewCards(s.Item));
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
            if (s.Kind == SlotKind.Bin) { if (!middle) OpenMenu(s); return; }   // щелчок по баку — его меню
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
            var menu = new AppMenuWindow(demo, demo ? AppCatalog.DemoPinned() : AppCatalog.FromDock(items, true));
            appMenu = menu;
            menu.Closed += delegate { if (appMenu == menu) appMenu = null; };
            ClosePreview(true);
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
            if (slot != null && slot.Kind == SlotKind.Bin) return BuildBinMenu();
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
                    // У приложения из Магазина папки, которую можно показать, нет
                    if (item.App == null) menu.Item("Показать в папке", false, delegate { if (!demo) try { Shell.Reveal(item.Path); } catch { } });
                }
                if (item.Pinned) menu.Item("Открепить", false, delegate { store.UnpinItem(item); SettingsChanged(); });
                else menu.Item("Закрепить", false, item.CanPin ? new Action(delegate { store.PinItem(item); SettingsChanged(); }) : null);
                if (item.Windows.Count == 1) menu.Item("Закрыть окно", false, delegate { if (!demo && item.Windows.Count > 0) Win.Close(item.Windows[0]); });
                else if (item.Windows.Count > 1) menu.Item("Закрыть все окна", false, delegate { if (!demo) foreach (IntPtr h in item.Windows.ToArray()) Win.Close(h); });
                menu.Line();
            }
            bool online = hub != null && hub.Online;
            menu.Brand(DockSprite, "dock");
            menu.Item("Автоскрытие", store.AutoHide, delegate { ToggleAutoHide(); });
            // С core.txt этот переключатель — на его странице «Общие» → «Запуск и обновления»; без него — здесь
            if (!online) menu.Item("Запускать вместе с Windows", Autostart.IsOn(), delegate { Autostart.Set(!Autostart.IsOn()); SettingsChanged(); });
            menu.Item("Вместо панели задач Windows", store.ReplaceTaskbar, delegate { store.Bar = store.ReplaceTaskbar ? "both" : "dock"; store.Save(); SettingsChanged(); });
            // Значки трея живут в панели задач Windows: показываем её ненадолго, сама спрячется, когда курсор уйдёт
            if (TaskbarSwap.Active) menu.Item("Значки в трее", false, delegate { TaskbarSwap.Peek(); });
            // Настройки дока живут в core.txt: открываем его на странице дока, а если его нет — предлагаем скачать
            menu.Item(online || CoreApp.Installed ? "Настройки" : "Скачать core.txt — настройки и остальные приложения", false,
                delegate { if (!demo) OpenSettings(); });
            menu.Line();
            menu.Item("Выход", false, delegate { Quit(); });
            return menu;
        }

        void OpenMenu(Slot slot)
        {
            if (menuOpen) return;
            Release();
            Disarm();
            if (drag != null) CancelDrag();
            ClosePreview(true);
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
        internal static readonly string[] DemoPins = { "Проводник", "Браузер", "Discord", "Steam", "Музыка", "Заметки" };

        // Выдуманные значки: ни окна, ни диск, ни хаб не трогаются. У «Браузера» три окна, и одно из них впереди.
        List<DockItem> Demo()
        {
            var list = new List<DockItem>();
            string[] pinned = DemoPins;
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
            binInstalled = true;   // в выдуманном доке metla.txt «установлена»: бак есть; без хаба — она не на связи
            if (!hubData) return;
            bin = BinInfo.From(HubLink.Msg("count", 12, "bytes", 1395864371L));
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
        // withPreviews — над значком панель с его окнами (выдуманными); dragged — какой закреплённый значок будто бы несут
        // (−1 — никакой), draggedOut — его унесли высоко над доком.
        public void Shot(string file, int over, string overWidget, bool withMenu, bool withPreviews, int dragged, bool draggedOut)
        {
            Content = null;
            int index = -1, n = 0;
            for (int i = 0; i < slots.Count && over >= 0; i++)
                if (slots[i].Kind == SlotKind.Item && n++ == over) index = i;
            if (overWidget != null) index = slots.FindIndex(delegate(Slot s) { return s.Kind == SlotKind.Widget && s.Widget == overWidget || s.Kind == SlotKind.Launcher && overWidget == "apps" || s.Kind == SlotKind.Bin && overWidget == "bin"; });
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
            List<Slot> pinnedNow = PinnedSlots();
            if (dragged >= 0 && dragged < pinnedNow.Count && index < 0)
            {
                Slot s = pinnedNow[dragged];
                Lift(s);
                s.Float.Set(1);
                if (draggedOut)
                {
                    s.Gap.Set(0);
                    s.FloatX = s.Centre + size * 0.3; s.FloatY = 2 + size * LiftScale / 2 + 6;
                    Place(true);
                    tipText.Text = UnpinTip;
                    tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    PlaceDragTip(s);
                    tip.Opacity = 1;
                }
                else
                {
                    Slot target = pinnedNow[Math.Min(pinnedNow.Count - 1, dragged + 2)];
                    s.FloatX = target.Centre - size * 0.22; s.FloatY = baseline - size / 2 - size * 0.4;
                    MoveSlot(s, slots.IndexOf(target));
                }
            }
            // Панель с окнами программы — та же, что в настоящем доке, только окна в ней нарисованы
            FrameworkElement panes = null;
            if (index >= 0 && withPreviews && !withMenu && slots[index].Kind == SlotKind.Item && slots[index].Item.Running)
            {
                DockItem item = slots[index].Item;
                double[] aspects = { 1.6, 1.33, 1.78, 0.8, 1.6 };
                var cards = new List<PreviewCard>();
                foreach (IntPtr one in item.Windows)
                {
                    if (cards.Count >= PreviewRules.MaxWindows) break;
                    string title = TitleOf(one);
                    cards.Add(new PreviewCard { Hwnd = one, Title = title.Length > 0 ? title : item.Name, Aspect = aspects[cards.Count], Minimized = cards.Count == 2 });
                }
                panes = new PreviewView(cards, delegate(double px) { return Face(item, px); }, true, Tokens.Radius.Block).Root;
                panes.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                tip.Opacity = 0;
            }
            // Меню — то же самое, что по правому щелчку, только нарисованное в этот же снимок
            FrameworkElement box = null;
            if (index >= 0 && withMenu)
            {
                PopMenu menu = BuildMenu(slots[index].Kind == SlotKind.Item || slots[index].Kind == SlotKind.Bin ? slots[index] : null);
                box = (FrameworkElement)menu.Content;
                menu.Content = null;
                box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
            // Сверху — ровно столько места, сколько нужно подписи или меню: окно выше из-за многострочных подписей виджетов
            double above = index >= 0 && !withMenu && !slots[index].Icon ? WidgetTipGap + tip.DesiredSize.Height : size * (MaxScale - 1) + TipGap + TipH;
            double top = box != null ? size * (MaxScale - 1) + box.DesiredSize.Height - 36 + 6 + 24 : above + TopRoom + 36;
            if (panes != null) top = size * (MaxScale - 1) + PreviewWindow.Gap + panes.DesiredSize.Height + 24;
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
            if (panes != null)
            {
                double pw = panes.DesiredSize.Width, ph = panes.DesiredSize.Height;
                panes.HorizontalAlignment = HorizontalAlignment.Left; panes.VerticalAlignment = VerticalAlignment.Top;
                panes.Margin = new Thickness(Math.Max(8, Math.Min(w - pw - 8, (w - winW) / 2 + slots[index].Centre - pw / 2)), h - winH + baseline - size * slots[index].Now - PreviewWindow.Gap - ph, 0, 0);
                panes.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 4, Direction = 270, Opacity = 0.4, Color = Colors.Black };
                root.Children.Add(panes);
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
                if (Shell.IconOf(i) != null) icons++;
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
