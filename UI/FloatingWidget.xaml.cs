using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using PropFirmGuardian.Models;
using PropFirmGuardian.UI.ViewModels;

namespace PropFirmGuardian.UI
{
    public partial class FloatingWidget : UserControl
    {
        public event Action OpenRequested;
        public event Action EmergencyFlattenRequested;

        public FloatingWidget()
        {
            InitializeComponent();
            MouseLeftButtonDown += OnMouseLeftButtonDown;
        }

        public void Update(AccountViewModel account)
        {
            if (account == null)
                return;

            AccountText.Text = account.AccountName;
            PnlText.Text = string.Format("{0:C0}", account.RealizedPnL + account.UnrealizedPnL);
            TradesText.Text = "Trades " + account.TradesTodayDisplay;
            PassText.Text = string.Format("Pass: {0:0}%", account.PassProbability);
            RingText.Text = string.Format("{0:0}%", account.PassProbability);
            StatusDot.Fill = account.StatusColor;
            GuardText.Text = string.IsNullOrWhiteSpace(account.ActiveGuards) ? "All clear" : account.ActiveGuards;
            SizeText.Text = account.TradeCapSeverity >= 2 ? "Size: 50%" : "Size: 100%";
            SizeText.Foreground = account.TradeCapSeverity >= 2
                ? new SolidColorBrush(Color.FromRgb(245, 158, 11))
                : new SolidColorBrush(Color.FromRgb(0, 212, 170));

            PassRing.Stroke = GetPassBrush(account.PassProbability);
            DrawRing(account.PassProbability);
        }

        private static Brush GetPassBrush(double probability)
        {
            if (probability >= 70.0)
                return new SolidColorBrush(Color.FromRgb(0, 212, 170));

            if (probability >= 40.0)
                return new SolidColorBrush(Color.FromRgb(245, 158, 11));

            return new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }

        private void DrawRing(double percent)
        {
            double normalized = Math.Max(0.0, Math.Min(100.0, percent)) / 100.0;
            double angle = normalized * 359.99;
            double radians = (angle - 90.0) * Math.PI / 180.0;
            Point center = new Point(40, 40);
            double radius = 36.0;
            Point end = new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
            Point start = new Point(center.X, center.Y - radius);

            PathFigure figure = new PathFigure { StartPoint = start, IsClosed = false };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Size(radius, radius),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = normalized > 0.5
            });

            PassRing.Data = new PathGeometry(new[] { figure });
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                RaiseOpenRequested();
                return;
            }

            Window window = Window.GetWindow(this);
            if (window != null)
                window.DragMove();
        }

        private void OpenGuardian_Click(object sender, RoutedEventArgs e)
        {
            RaiseOpenRequested();
        }

        private void EmergencyFlatten_Click(object sender, RoutedEventArgs e)
        {
            Action handler = EmergencyFlattenRequested;
            if (handler != null)
                handler();
        }

        private void RaiseOpenRequested()
        {
            Action handler = OpenRequested;
            if (handler != null)
                handler();
        }
    }
}
