// dock.txt. Menu.cs — маленькое меню по правому щелчку, в общем виде dev.txt: тёмное стекло, кромка, строки со скруглением.
// Меню — отдельное окно: оно, в отличие от самого дока, берёт фокус и закрывается, как только его теряет.
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DevTxt.DockApp
{
    class MenuWindow : Window
    {
        const double Shadow = 18, RowH = 30;
        readonly StackPanel list = new StackPanel();
        readonly Border box;
        bool closing;

        public MenuWindow()
        {
            Title = "dock.txt";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            FontFamily = Ui.TextFont;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            box = new Border
            {
                Margin = new Thickness(Shadow), MinWidth = 196, CornerRadius = new CornerRadius(Tokens.Radius.Row), Background = Ui.Solid(Tokens.Dark.Menu),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = Shadow, ShadowDepth = 4, Direction = 270, Opacity = 0.4, Color = Colors.Black },
                Child = new Border { CornerRadius = new CornerRadius(Tokens.Radius.Row), Background = Ui.CardFill, BorderBrush = Ui.Rim, BorderThickness = new Thickness(1), Padding = new Thickness(5), Child = list }
            };
            Content = box;
            Deactivated += delegate { Shut(); };
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Shut(); };
            Closing += delegate { closing = true; };
        }

        void Shut()
        {
            if (closing) return;
            closing = true;
            Close();
        }

        // Заголовок: название приложения или что угодно ещё
        public void Header(UIElement what)
        {
            list.Children.Add(new Border { Padding = new Thickness(10, 5, 10, 6), Child = what });
        }

        public void Line()
        {
            list.Children.Add(new Border { Height = 1, Margin = new Thickness(10, 5, 10, 5), Background = Ui.Fill2 });
        }

        // Строка меню. check — показать галочку справа; action == null — строка недоступна.
        public void Item(string label, bool check, Action action)
        {
            bool enabled = action != null;
            var text = Ui.T(label, Tokens.Size.Body, enabled ? Ui.Label : Ui.Label3, FontWeights.Normal);
            text.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel { LastChildFill = true };
            if (check)
            {
                var mark = new TextBlock { Text = "", FontFamily = Ui.IconFont, FontSize = Tokens.Size.Micro, Foreground = Ui.Label2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 1, 0, 0) };
                DockPanel.SetDock(mark, Dock.Right);
                row.Children.Add(mark);
            }
            row.Children.Add(text);
            var b = new Border { Height = RowH, CornerRadius = new CornerRadius(Tokens.Radius.Tip), Padding = new Thickness(10, 0, 10, 1), Background = Brushes.Transparent, Child = row };
            if (enabled)
            {
                b.MouseEnter += delegate { b.Background = Ui.Fill2; };
                b.MouseLeave += delegate { b.Background = Brushes.Transparent; };
                b.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
                {
                    e.Handled = true;
                    Shut();
                    action();
                };
            }
            list.Children.Add(b);
        }

        // Показывает меню над точкой (cx, bottom) — в единицах WPF, в координатах экрана; за края рабочей области не вылезает
        public void ShowAbove(double cx, double bottom, Rect work)
        {
            box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = box.DesiredSize.Width, h = box.DesiredSize.Height;
            Left = Math.Max(work.Left - Shadow + 4, Math.Min(work.Right + Shadow - 4 - w, cx - w / 2));
            Top = Math.Max(work.Top, bottom - h + Shadow);
            Show();
            Motion.GrowFrom(box, new Point(0.5, 1));   // меню вырастает из того, по чему щёлкнули
            Activate();
        }
    }
}
