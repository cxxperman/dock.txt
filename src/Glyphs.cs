// dock.txt. Glyphs.cs — маленькие рисунки дока: погода, батарея, динамик, календарь, кнопки музыки, лупа,
// а ещё капсула виджета, счётчик непрочитанного и плитка с буквой. Всё нарисовано простыми фигурами — без картинок
// и без шрифтов со значками; цвета — только из токенов. Рисунки лежат в поле 20×20 и растягиваются до нужного размера.
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DevTxt.DockApp
{
    static class Glyphs
    {
        const double Field = 20;
        const string CloudShape = "M5.6,15 A3.4,3.4 0 0 1 5.2,8.3 A4.6,4.6 0 0 1 14,7.6 A3.7,3.7 0 0 1 14.6,15 Z";
        const string BoltShape = "M11.4,11.6 L8.2,16.2 H10.5 L9.5,19.6 L13.2,14.6 H10.9 L12.6,11.6 Z";

        static readonly Brush yellow = Ui.Solid(Tokens.Palette.Yellow), blue = Ui.Solid(Tokens.Palette.Blue), green = Ui.Solid(Tokens.Palette.Green),
            red = Ui.Solid(Tokens.Palette.Red), brand = Ui.Solid(Tokens.Palette.Brand);
        static readonly Color[] tints = { Tokens.Palette.Blue, Tokens.Palette.Green, Tokens.Palette.Purple, Tokens.Palette.Pink, Tokens.Palette.Orange, Tokens.Palette.Red, Tokens.Palette.Brand };

        static Path Line(string data, Brush color, double width)
        {
            return new Path { Data = Geometry.Parse(data), Stroke = color, StrokeThickness = width, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        }

        static Path Shape(string data, Brush color)
        {
            return new Path { Data = Geometry.Parse(data), Fill = color, Stroke = color, StrokeThickness = 0.8, StrokeLineJoin = PenLineJoin.Round };
        }

        static FrameworkElement Box(double w, double h, double box, params UIElement[] parts)
        {
            var canvas = new Canvas { Width = w, Height = h };
            foreach (UIElement p in parts) if (p != null) canvas.Children.Add(p);
            return new Viewbox { Height = box, Width = box * w / h, Child = canvas, Stretch = Stretch.Uniform, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
        }

        static string N(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // Облако поменьше и повыше — чтобы под ним поместились капли, снег или молния
        static Path SmallCloud()
        {
            Path p = Shape(CloudShape, Ui.Label);
            p.RenderTransform = new TransformGroup { Children = { new ScaleTransform(0.9, 0.9), new TranslateTransform(1.1, -2.6) } };
            return p;
        }

        // Шесть значков погоды: clear, cloudy, rain, snow, storm, fog
        public static FrameworkElement Weather(string icon, double box)
        {
            if (icon == "clear")
            {
                var rays = new System.Text.StringBuilder();
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI * i / 4, c = Math.Cos(a), s = Math.Sin(a);
                    rays.Append("M" + N(10 + 6 * c) + "," + N(10 + 6 * s) + " L" + N(10 + 8 * c) + "," + N(10 + 8 * s) + " ");
                }
                return Box(Field, Field, box, Shape("M10,6.2 A3.8,3.8 0 1 1 9.99,6.2 Z", yellow), Line(rays.ToString(), yellow, 1.5));
            }
            if (icon == "rain") return Box(Field, Field, box, SmallCloud(), Line("M7.2,14 L6.2,17.4 M10.6,14 L9.6,17.4 M14,14 L13,17.4", blue, 1.6));
            if (icon == "snow") return Box(Field, Field, box, SmallCloud(), Line("M6.6,15 L6.6,15.01 M10.2,17 L10.2,17.01 M13.8,15 L13.8,15.01", Ui.Label, 2.1));
            if (icon == "storm") return Box(Field, Field, box, SmallCloud(), Shape(BoltShape, yellow));
            if (icon == "fog") return Box(Field, Field, box, Line("M3.5,6 H14.5 M5.5,10 H16.5 M3.5,14 H12.5", Ui.Label, 1.7), Line("M15.5,14 H16.5 M17.5,6 H17.51", Ui.Label2, 1.7));
            return Box(Field, Field, box, Shape(CloudShape, Ui.Label));
        }

        // Батарея: контур, носик и заливка по заряду. Заряжается — зелёная и с молнией, мало — красная.
        public static FrameworkElement Battery(int percent, bool charging, double box)
        {
            double fill = 16.4 * Math.Max(0, Math.Min(100, percent)) / 100;
            Brush level = charging ? green : percent <= 20 ? red : Ui.Label;
            UIElement bolt = charging ? Shape("M-2.4,0.6 L-6,6.4 H-3.6 L-4.6,10.6 L-0.8,4.8 H-3.2 Z", green) : null;
            var body = new Rectangle { Width = 21, Height = 11, RadiusX = 3.2, RadiusY = 3.2, Stroke = Ui.Label2, StrokeThickness = 1.2 };
            Canvas.SetLeft(body, 0.5); Canvas.SetTop(body, 0.5);
            var nub = new Rectangle { Width = 2, Height = 4.6, RadiusX = 1, RadiusY = 1, Fill = Ui.Label2 };
            Canvas.SetLeft(nub, 22.4); Canvas.SetTop(nub, 3.7);
            var inside = new Rectangle { Width = Math.Max(fill, percent > 0 ? 2 : 0), Height = 6.4, RadiusX = 1.4, RadiusY = 1.4, Fill = level };
            Canvas.SetLeft(inside, 2.8); Canvas.SetTop(inside, 2.8);
            FrameworkElement glyph = Box(25, 12, box, body, nub, inside, bolt);
            if (charging) glyph.Margin = new Thickness(box * 0.6, 0, 0, 0);   // молния рисуется левее поля
            return glyph;
        }

        // Динамик: дуг столько, сколько громкости; выключен — крестик
        public static FrameworkElement Volume(int bars, bool muted, double box)
        {
            Brush body = muted ? Ui.Label3 : Ui.Label;
            UIElement speaker = Shape("M3,8 H5.8 L9.6,4.6 V15.4 L5.8,12 H3 Z", body);
            if (muted) return Box(Field, Field, box, speaker, Line("M12.8,7.6 L17.4,12.4 M17.4,7.6 L12.8,12.4", Ui.Label2, 1.5));
            string[] arcs = { "M12.2,8 A2.9,2.9 0 0 1 12.2,12", "M14.1,6 A5.6,5.6 0 0 1 14.1,14", "M16,4.1 A8.4,8.4 0 0 1 16,15.9" };
            var parts = new UIElement[4];
            parts[0] = speaker;
            for (int i = 0; i < 3; i++) parts[i + 1] = Line(arcs[i], i < bars ? Ui.Label : Ui.Label3, 1.5);
            return Box(Field, Field, box, parts);
        }

        public static FrameworkElement Calendar(double box)
        {
            var sheet = new Rectangle { Width = 14.6, Height = 13, RadiusX = 3, RadiusY = 3, Stroke = Ui.Label2, StrokeThickness = 1.4 };
            Canvas.SetLeft(sheet, 2.7); Canvas.SetTop(sheet, 4.3);
            return Box(Field, Field, box, sheet, Line("M3,8.6 H17 M6.6,2.6 V5.4 M13.4,2.6 V5.4", Ui.Label2, 1.4), Line("M6.6,12.4 H6.61 M10,12.4 H10.01 M13.4,12.4 H13.41", brand, 1.9));
        }

        // Музыка играет — три столбика цвета dev.txt; на паузе — две приглушённые черты
        public static FrameworkElement Playing(bool playing, double box)
        {
            if (playing) return Box(14, 14, box, Line("M2.5,7.5 V12 M7,2.6 V12 M11.5,5.6 V12", brand, 2.3));
            return Box(14, 14, box, Line("M4.4,3 V11.6 M9.6,3 V11.6", Ui.Label3, 2.3));
        }

        // Кнопки музыки: prev, toggle (играет — пауза, иначе — пуск), next
        public static FrameworkElement Media(string which, bool playing, double box)
        {
            string data = which == "prev" ? "M3,3.4 H4.6 V12.6 H3 Z M13,3.4 V12.6 L6.4,8 Z"
                : which == "next" ? "M11.4,3.4 H13 V12.6 H11.4 Z M3,3.4 L9.6,8 L3,12.6 Z"
                : playing ? "M4,3.2 H6.6 V12.8 H4 Z M9.4,3.2 H12 V12.8 H9.4 Z" : "M4.8,2.8 L13.2,8 L4.8,13.2 Z";
            return Box(16, 16, box, Shape(data, Ui.Label));
        }

        public static FrameworkElement Search(double box)
        {
            return Box(Field, Field, box, Line("M13.4,8.6 A4.8,4.8 0 1 1 13.39,8.5 M12.2,12.2 L16.6,16.6", Ui.Label2, 1.7));
        }

        // Капсула виджета — общая строка стекла из kit.txt, только низкая и скруглённая до конца
        public static Border Capsule(UIElement child, double height, double pad)
        {
            Border b = Ui.Row(child);
            b.Height = height;
            b.CornerRadius = new CornerRadius(height / 2);
            b.Padding = new Thickness(pad, 0, pad, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            return b;
        }

        // Красный счётчик в правом верхнем углу значка. Растёт вместе со значком: он лежит внутри него.
        public static FrameworkElement Badge(string text, double size)
        {
            const double h = 17;
            var label = new TextBlock { Text = text, FontSize = Tokens.Size.Micro, FontWeight = FontWeights.Bold, FontFamily = Ui.TextFont, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -1, 0, 0) };
            var b = new Border
            {
                MinWidth = h, Height = h, CornerRadius = new CornerRadius(h / 2), Padding = new Thickness(4, 0, 4, 0), Background = red, Child = label,
                BorderBrush = Ui.Solid(Ui.Alpha(Tokens.Dark.Bg, 0.55)), BorderThickness = new Thickness(1), SnapsToDevicePixels = false,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false
            };
            double k = size / 48;
            b.LayoutTransform = new ScaleTransform(k, k);
            b.Margin = new Thickness(0, -size * 0.07, -size * 0.09, 0);
            return b;
        }

        // Цветная плитка с первой буквой — когда у программы нет значка, как в core.txt
        public static FrameworkElement LetterTile(string name, double size, int index)
        {
            var letter = new TextBlock
            {
                Text = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant(), FontSize = size * 0.46, FontWeight = FontWeights.Bold, FontFamily = Ui.DisplayFont,
                Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            return new Border
            {
                Width = size * 0.92, Height = size * 0.92, CornerRadius = new CornerRadius(size * 0.23), Background = Ui.Tinted(tints[Math.Abs(index) % tints.Length]),
                BorderBrush = Ui.Solid(Ui.Alpha(Colors.White, 0.2)), BorderThickness = new Thickness(1), Child = letter, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
        }
    }
}
