// dock.txt. Preview.cs — окна программы над её значком: курсор задержался на значке запущенной программы —
// над ним появляется панель с живыми картинками её окон. Щелчок по картинке выводит окно вперёд, крестик закрывает его.
// Картинки рисует сама Windows (DWM) прямо в окне панели, поэтому панель — обычное непрозрачное окно, а не
// «слоёное» окно WPF: то отстаёт от картинок DWM и мерцает. По той же причине здесь ничего не ездит и не растёт:
// картинки один раз встают на свои места, а панель только проявляется. Сделать полупрозрачным обычное окно WPF
// целиком нельзя (WPF сам снимает с него этот признак), поэтому проявляется его содержимое вместе с картинками:
// рамка, подписи и окна выступают из ровной тёмной плашки.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DevTxt.DockApp
{
    // Одно окно в панели
    class PreviewCard
    {
        public IntPtr Hwnd;
        public string Title;
        public bool Minimized;   // свёрнуто: живой картинки нет, вместо неё значок и подпись
        public bool Blank;       // картинку получить не удалось — только значок
        public double Aspect;    // ширина окна к высоте; 0 — не знаем
        public IntPtr Thumb;     // картинка окна, которую рисует Windows
    }

    // Правила панели — без экрана, проверяются ключом --selftest
    static class PreviewRules
    {
        public const int MaxWindows = 5, OpenMs = 400, CloseMs = 200;
        public const double ThumbW = 200, ThumbMaxH = 132, ThumbMinW = 104, ThumbMinH = 72, DefaultAspect = 1.6;

        // Сколько места дать картинке окна: около 200 в ширину с пропорциями окна; высокое окно — уже, чтобы не было выше остальных
        public static Size Thumb(double aspect)
        {
            if (!(aspect > 0.05) || double.IsInfinity(aspect)) aspect = DefaultAspect;
            double w = ThumbW, h = w / aspect;
            if (h > ThumbMaxH) { h = ThumbMaxH; w = Math.Max(ThumbMinW, h * aspect); }
            if (h < ThumbMinH) h = ThumbMinH;
            return new Size(Math.Round(w), Math.Round(h));
        }

        // Картинка с пропорциями aspect внутри места w×h, по центру: {x, y, ширина, высота}
        public static Rect Fit(double aspect, double w, double h)
        {
            if (!(aspect > 0.05) || double.IsInfinity(aspect)) aspect = DefaultAspect;
            double tw = w, th = w / aspect;
            if (th > h) { th = h; tw = h * aspect; }
            return new Rect((w - tw) / 2, (h - th) / 2, tw, th);
        }

        // Те же окна в том же виде? Тогда панель не пересобирается, меняются только заголовки.
        public static bool SameWindows(List<PreviewCard> a, List<PreviewCard> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i].Hwnd != b[i].Hwnd || a[i].Minimized != b[i].Minimized) return false;
            return true;
        }
    }

    // Содержимое панели. Отдельно от окна — чтобы его можно было нарисовать в снимок (--previews).
    class PreviewView
    {
        const double Pad = 6, CardPad = 6, TitleH = 22, CloseSize = 20;
        public readonly Border Root;
        public Action<IntPtr> Pick, Shut;
        readonly List<Border> holes = new List<Border>();
        readonly List<TextBlock> titles = new List<TextBlock>();
        readonly List<PreviewCard> cards;

        // icon — значок программы нужного размера (для свёрнутых окон); fake — вместо живых картинок нарисовать выдуманные окна
        public PreviewView(List<PreviewCard> cards, Func<double, FrameworkElement> icon, bool fake, double radius)
        {
            this.cards = cards;
            double rowH = 0;
            var sizes = new List<Size>();
            foreach (PreviewCard c in cards)
            {
                Size s = PreviewRules.Thumb(c.Minimized || c.Blank ? PreviewRules.DefaultAspect : c.Aspect);
                sizes.Add(s);
                rowH = Math.Max(rowH, s.Height);
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            for (int i = 0; i < cards.Count; i++)
            {
                PreviewCard card = cards[i];
                double w = sizes[i].Width;
                var hole = new Border { Width = w, Height = rowH, CornerRadius = new CornerRadius(Tokens.Radius.Row), SnapsToDevicePixels = true };
                if (card.Minimized || card.Blank)
                {
                    // Свёрнутое окно Windows не рисует: на его месте значок программы и подпись
                    hole.Background = Ui.Fill;
                    var inside = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                    FrameworkElement face = icon != null ? icon(40) : null;
                    if (face != null) { face.HorizontalAlignment = HorizontalAlignment.Center; inside.Children.Add(face); }
                    if (card.Minimized)
                    {
                        TextBlock note = Ui.T("Свёрнуто", Tokens.Size.Caption, Ui.Label2, FontWeights.Normal);
                        note.HorizontalAlignment = HorizontalAlignment.Center;
                        note.Margin = new Thickness(0, 8, 0, 0);
                        inside.Children.Add(note);
                    }
                    hole.Child = inside;
                }
                else if (fake) hole.Child = FakeWindow(PreviewRules.Fit(card.Aspect, w, rowH), i);
                holes.Add(hole);

                TextBlock title = Ui.T(card.Title ?? "", Tokens.Size.Caption, Ui.Label, FontWeights.Normal);
                title.VerticalAlignment = VerticalAlignment.Center;
                title.Margin = new Thickness(2, 0, CloseSize + 4, 1);   // место под крестик есть всегда: заголовок не прыгает, когда он появляется
                titles.Add(title);
                TextBlock cross = Ui.Glyph("");
                cross.FontSize = 9;
                var close = new Border
                {
                    Width = CloseSize, Height = CloseSize, CornerRadius = new CornerRadius(Tokens.Radius.Row), Background = Brushes.Transparent, Child = cross,
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Opacity = 0
                };
                var line = new Grid { Width = w, Height = TitleH, Margin = new Thickness(0, 4, 0, 0), Children = { title, close } };
                var box = new Border
                {
                    Padding = new Thickness(CardPad, CardPad, CardPad, 4), CornerRadius = new CornerRadius(Tokens.Radius.Row + 2), Background = Brushes.Transparent,
                    Child = new StackPanel { Children = { hole, line } }
                };
                IntPtr hwnd = card.Hwnd;
                bool down = false;
                box.MouseEnter += delegate { box.Background = Ui.Fill; close.Opacity = 1; };
                box.MouseLeave += delegate { box.Background = Brushes.Transparent; close.Opacity = 0; down = false; };
                box.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; down = true; box.Background = Ui.Press; };
                box.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    box.Background = Ui.Fill;
                    if (!down) return;
                    down = false;
                    if (Pick != null) Pick(hwnd);
                };
                Brush red = Ui.Solid(Tokens.Palette.Red);
                close.MouseEnter += delegate { close.Background = red; };
                close.MouseLeave += delegate { close.Background = Brushes.Transparent; };
                close.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
                close.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; if (Shut != null) Shut(hwnd); };
                row.Children.Add(box);
            }
            Root = new Border
            {
                CornerRadius = new CornerRadius(radius), Background = Ui.Solid(Tokens.Dark.Tooltip), BorderBrush = Ui.Rim, BorderThickness = new Thickness(1),
                Padding = new Thickness(Pad - 1), Child = row, SnapsToDevicePixels = true, UseLayoutRounding = true
            };
            TextElementFont(Root);
        }

        static void TextElementFont(FrameworkElement e)
        {
            System.Windows.Documents.TextElement.SetFontFamily(e, Ui.TextFont);
            TextOptions.SetTextFormattingMode(e, TextFormattingMode.Ideal);
        }

        public int Count { get { return cards.Count; } }

        public void SetTitle(int index, string text)
        {
            if (index >= 0 && index < titles.Count && titles[index].Text != (text ?? "")) titles[index].Text = text ?? "";
        }

        // Где в панели встаёт картинка окна (в единицах WPF, от левого верхнего угла панели). Считать после разметки.
        public Rect ThumbRect(int index)
        {
            Border hole = holes[index];
            Point at = hole.TransformToAncestor(Root).Transform(new Point(0, 0));
            Rect fit = PreviewRules.Fit(cards[index].Aspect, hole.Width, hole.Height);
            return new Rect(at.X + fit.X, at.Y + fit.Y, fit.Width, fit.Height);
        }

        // Выдуманное окно для снимка: шапка и несколько строк
        static FrameworkElement FakeWindow(Rect fit, int index)
        {
            Color[] tints = { Tokens.Palette.Blue, Tokens.Palette.Green, Tokens.Palette.Orange };
            var canvas = new Canvas { Width = fit.Width, Height = fit.Height, ClipToBounds = true, Background = Ui.Solid(Color.FromRgb(0x1C, 0x1C, 0x1E)) };
            canvas.Children.Add(new Rectangle { Width = fit.Width, Height = 12, Fill = Ui.Solid(Color.FromRgb(0x33, 0x33, 0x36)) });
            var accent = new Rectangle { Width = fit.Width * 0.34, Height = fit.Height * 0.3, RadiusX = 3, RadiusY = 3, Fill = Ui.Solid(Ui.Alpha(tints[index % tints.Length], 0.85)) };
            Canvas.SetLeft(accent, 10); Canvas.SetTop(accent, 22);
            canvas.Children.Add(accent);
            for (int i = 0; i < 4; i++)
            {
                var bar = new Rectangle { Width = fit.Width * (i % 2 == 0 ? 0.46 : 0.36), Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = Ui.Solid(Ui.Alpha(Colors.White, i == 0 ? 0.5 : 0.2)) };
                Canvas.SetLeft(bar, fit.Width * 0.34 + 20); Canvas.SetTop(bar, 24 + i * 11);
                canvas.Children.Add(bar);
            }
            for (int i = 0; i < 3; i++)
            {
                var bar = new Rectangle { Width = fit.Width - 20 - i * 26, Height = 5, RadiusX = 2.5, RadiusY = 2.5, Fill = Ui.Solid(Ui.Alpha(Colors.White, 0.14)) };
                Canvas.SetLeft(bar, 10); Canvas.SetTop(bar, 22 + fit.Height * 0.3 + 12 + i * 11);
                canvas.Children.Add(bar);
            }
            return new Border { Width = fit.Width, Height = fit.Height, CornerRadius = new CornerRadius(3), Child = canvas, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }
    }

    // Окно панели: обычное (не прозрачное), поверх остальных, не берёт фокус и не попадает в Alt+Tab
    class PreviewWindow : Window
    {
        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct SIZE { public int W, H; }
        [StructLayout(LayoutKind.Sequential)]
        struct ThumbProps
        {
            public int Flags;
            public RECT Destination, Source;
            public byte Opacity;
            public bool Visible, ClientOnly;
        }

        [DllImport("dwmapi.dll")] static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref ThumbProps props);
        [DllImport("dwmapi.dll")] static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        const int TnpDestination = 1, TnpOpacity = 4, TnpVisible = 8;
        const int DwmCloak = 13, DwmCorners = 33, DwmBorder = 34;
        const int FadeInMs = Tokens.Motion.FastMs, FadeOutMs = Tokens.Motion.PressMs;
        public const double Gap = 8;   // от панели до значка

        public Action Enter, Leave;
        public Action<IntPtr> Pick, Shut;
        readonly Stopwatch clock = Stopwatch.StartNew();
        IntPtr me;
        double radius;
        PreviewView view;
        List<PreviewCard> cards = new List<PreviewCard>();
        Func<double, FrameworkElement> icon;
        double anchorX, anchorBottom;
        Rect area;
        bool fading;
        double alpha, fadeFrom, fadeTo;
        long fadeAt;
        int fadeMs, pass;
        Action fadeDone;

        public PreviewWindow()
        {
            Title = "dock.txt";
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            Focusable = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Background = Ui.Solid(Tokens.Dark.Tooltip);
            FontFamily = Ui.TextFont;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            SourceInitialized += delegate
            {
                me = new WindowInteropHelper(this).Handle;
                Win.MakeBar(me);
                // Windows 11 сама скругляет углы окна; рамку рисуем свою — ту же, что у меню и подписей
                int round = 2, none = unchecked((int)0xFFFFFFFE);
                try
                {
                    if (DwmSetWindowAttribute(me, DwmCorners, ref round, sizeof(int)) == 0) radius = Tokens.Radius.Block;
                    DwmSetWindowAttribute(me, DwmBorder, ref none, sizeof(int));
                }
                catch { }
            };
            MouseEnter += delegate { if (Enter != null) Enter(); };
            MouseLeave += delegate { if (Leave != null) Leave(); };
            Closed += delegate { Stop(); Free(); };
            new WindowInteropHelper(this).EnsureHandle();
        }

        public bool Shown { get; private set; }

        // Курсор над панелью или в промежутке между ней и значком
        public bool Hot
        {
            get
            {
                if (!Shown) return false;
                try
                {
                    Point c;
                    if (!Win.Cursor(out c) || PresentationSource.FromVisual(this) == null) return false;
                    Point p = PointFromScreen(c);
                    return p.X >= -2 && p.X <= ActualWidth + 2 && p.Y >= -2 && p.Y <= ActualHeight + Gap + 4;
                }
                catch { return false; }
            }
        }

        // Показывает окна программы над точкой (cx, bottom) — в единицах WPF, в координатах экрана
        public void ShowFor(List<PreviewCard> list, Func<double, FrameworkElement> face, double cx, double bottom, Rect work)
        {
            icon = face; anchorX = cx; anchorBottom = bottom; area = work;
            Stop();
            int turn = ++pass;
            Free();
            cards = list;
            // Картинки заводятся невидимыми: Windows сразу сообщает размер окна — по нему считаются места
            foreach (PreviewCard c in cards)
            {
                if (c.Minimized) continue;
                c.Blank = true;
                try
                {
                    IntPtr t;
                    SIZE size;
                    if (DwmRegisterThumbnail(me, c.Hwnd, out t) != 0 || t == IntPtr.Zero) continue;
                    c.Thumb = t;
                    if (DwmQueryThumbnailSourceSize(t, out size) != 0 || size.W <= 0 || size.H <= 0) continue;
                    c.Aspect = size.W / (double)size.H;
                    c.Blank = false;
                }
                catch { }
            }
            view = new PreviewView(cards, icon, false, radius);
            view.Pick = delegate(IntPtr win) { if (Pick != null) Pick(win); };
            view.Shut = delegate(IntPtr win) { if (Shut != null) Shut(win); };
            view.Root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = Math.Ceiling(view.Root.DesiredSize.Width), h = Math.Ceiling(view.Root.DesiredSize.Height);

            // Пока панель встаёт на место и рисует первый кадр, Windows её не показывает совсем — ни пустой, ни на старом месте
            Cloak(true);
            Content = view.Root;
            Width = w; Height = h;
            Left = Math.Round(Math.Max(work.Left + 8, Math.Min(work.Right - 8 - w, cx - w / 2)));
            Top = Math.Round(Math.Max(work.Top + 8, bottom - h));
            if (!IsVisible) Show();
            Shown = true;
            SetAlpha(0);
            UpdateLayout();
            PlaceThumbs();
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(delegate
            {
                if (turn != pass || !Shown) return;
                Cloak(false);
                if (Ui.ReducedMotion) SetAlpha(1); else Fade(1, FadeInMs, null);
            }));
        }

        // Окна программы изменились, пока панель открыта. Те же окна — меняются только заголовки; иначе панель собирается заново.
        public void Update(List<PreviewCard> list)
        {
            if (!Shown) return;
            if (PreviewRules.SameWindows(cards, list))
            {
                for (int i = 0; i < list.Count; i++) view.SetTitle(i, list[i].Title);
                return;
            }
            ShowFor(list, icon, anchorX, anchorBottom, area);
        }

        // Прячет панель: гаснет и только потом исчезает; now — сразу (полный экран, щелчок)
        public void HideIt(bool now)
        {
            if (!Shown) return;
            Shown = false;
            pass++;
            if (now || Ui.ReducedMotion || alpha <= 0) { Stop(); Gone(); return; }
            Fade(0, FadeOutMs, Gone);
        }

        void Gone()
        {
            Free();
            if (IsVisible) Hide();
            Content = null;
            view = null;
        }

        void Free()
        {
            foreach (PreviewCard c in cards)
            {
                if (c.Thumb == IntPtr.Zero) continue;
                try { DwmUnregisterThumbnail(c.Thumb); } catch { }
                c.Thumb = IntPtr.Zero;
            }
        }

        void Cloak(bool on)
        {
            int v = on ? 1 : 0;
            try { DwmSetWindowAttribute(me, DwmCloak, ref v, sizeof(int)); } catch { }
        }

        // Картинки окон встают на свои места один раз — и больше не двигаются
        void PlaceThumbs()
        {
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source == null || source.CompositionTarget == null || view == null) return;
            Matrix m = source.CompositionTarget.TransformToDevice;
            Point origin = view.Root.TransformToAncestor(this).Transform(new Point(0, 0));
            for (int i = 0; i < cards.Count; i++)
            {
                PreviewCard c = cards[i];
                if (c.Thumb == IntPtr.Zero) continue;
                Rect r = view.ThumbRect(i);
                var props = new ThumbProps
                {
                    Flags = TnpDestination | TnpOpacity | TnpVisible, Opacity = (byte)Math.Round(255 * alpha), Visible = !c.Blank,
                    Destination = new RECT
                    {
                        Left = (int)Math.Round((origin.X + r.X) * m.M11), Top = (int)Math.Round((origin.Y + r.Y) * m.M22),
                        Right = (int)Math.Round((origin.X + r.Right) * m.M11), Bottom = (int)Math.Round((origin.Y + r.Bottom) * m.M22)
                    }
                };
                try { DwmUpdateThumbnailProperties(c.Thumb, ref props); } catch { }
            }
        }

        // Содержимое панели и картинки окон проявляются и гаснут вместе
        void SetAlpha(double a)
        {
            alpha = Math.Max(0, Math.Min(1, a));
            byte b = (byte)Math.Round(255 * alpha);
            if (view != null) view.Root.Opacity = alpha;
            foreach (PreviewCard c in cards)
            {
                if (c.Thumb == IntPtr.Zero) continue;
                var props = new ThumbProps { Flags = TnpOpacity, Opacity = b };
                try { DwmUpdateThumbnailProperties(c.Thumb, ref props); } catch { }
            }
        }

        void Fade(double to, int ms, Action done)
        {
            Stop();
            fadeFrom = alpha; fadeTo = to; fadeMs = Math.Max(1, ms); fadeAt = clock.ElapsedMilliseconds; fadeDone = done;
            fading = true;
            CompositionTarget.Rendering += Step;
        }

        void Stop()
        {
            if (!fading) return;
            fading = false;
            fadeDone = null;
            CompositionTarget.Rendering -= Step;
        }

        void Step(object sender, EventArgs e)
        {
            double t = (clock.ElapsedMilliseconds - fadeAt) / (double)fadeMs;
            if (t < 1) { SetAlpha(fadeFrom + (fadeTo - fadeFrom) * Tween.Ease(t)); return; }
            Action done = fadeDone;
            SetAlpha(fadeTo);
            Stop();
            if (done != null) done();
        }
    }
}
