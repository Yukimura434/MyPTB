using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoBooth.Admin.UI.ViewModels;

namespace PhotoBooth.Admin.UI.Views
{
    public partial class BeautyView : UserControl
    {
        Point dragOrigin;
        double translateOriginX;
        double translateOriginY;
        bool dragging;

        public BeautyView() { InitializeComponent(); }

        void View_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            bool portrait = ActualWidth < 900;
            SettingsColumn.Width = new GridLength(portrait ? 0 : 340);
            Grid.SetColumn(SettingsCard, portrait ? 0 : 1);
            Grid.SetRow(SettingsCard, portrait ? 1 : 0);
            PreviewCard.Margin = portrait ? new Thickness(0, 0, 0, 20) : new Thickness(0, 0, 20, 0);
            PreviewCard.Height = portrait ? 450 : 620;
        }

        void Before_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space || e.Key == Key.Enter) { (DataContext as BeautyViewModel)?.SetBeforePreview(true); e.Handled = true; }
        }
        void Before_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space || e.Key == Key.Enter) { (DataContext as BeautyViewModel)?.SetBeforePreview(false); e.Handled = true; }
        }
        void Before_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => (DataContext as BeautyViewModel)?.SetBeforePreview(false);

        void Preview_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var oldZoom = PreviewScale.ScaleX;
            var newZoom = Math.Max(1, Math.Min(6, oldZoom * (e.Delta > 0 ? 1.15 : 1 / 1.15)));
            var cursor = e.GetPosition(PreviewViewport);

            // Keep the image pixel below the pointer stationary while zooming.
            var imageX = (cursor.X - PreviewTranslate.X) / oldZoom;
            var imageY = (cursor.Y - PreviewTranslate.Y) / oldZoom;
            PreviewScale.ScaleX = PreviewScale.ScaleY = newZoom;
            PreviewTranslate.X = cursor.X - imageX * newZoom;
            PreviewTranslate.Y = cursor.Y - imageY * newZoom;
            ClampTranslation();
            e.Handled = true;
        }

        void Preview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (PreviewScale.ScaleX <= 1) return;
            dragging = true;
            dragOrigin = e.GetPosition(PreviewViewport);
            translateOriginX = PreviewTranslate.X;
            translateOriginY = PreviewTranslate.Y;
            PreviewViewport.CaptureMouse();
            e.Handled = true;
        }

        void Preview_MouseMove(object sender, MouseEventArgs e)
        {
            if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
            var position = e.GetPosition(PreviewViewport);
            PreviewTranslate.X = translateOriginX + position.X - dragOrigin.X;
            PreviewTranslate.Y = translateOriginY + position.Y - dragOrigin.Y;
            ClampTranslation();
            e.Handled = true;
        }

        void Preview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            StopDragging();
            e.Handled = true;
        }

        void Preview_LostMouseCapture(object sender, MouseEventArgs e) { dragging = false; }
        void PreviewViewport_SizeChanged(object sender, SizeChangedEventArgs e) { ClampTranslation(); }

        void StopDragging()
        {
            dragging = false;
            if (PreviewViewport.IsMouseCaptured) PreviewViewport.ReleaseMouseCapture();
        }

        void ClampTranslation()
        {
            var zoom = PreviewScale.ScaleX;
            if (zoom <= 1 || PreviewViewport.ActualWidth <= 0 || PreviewViewport.ActualHeight <= 0)
            {
                PreviewTranslate.X = PreviewTranslate.Y = 0;
                return;
            }
            var minX = Math.Min(0, PreviewViewport.ActualWidth - PreviewImage.ActualWidth * zoom);
            var minY = Math.Min(0, PreviewViewport.ActualHeight - PreviewImage.ActualHeight * zoom);
            PreviewTranslate.X = Math.Max(minX, Math.Min(0, PreviewTranslate.X));
            PreviewTranslate.Y = Math.Max(minY, Math.Min(0, PreviewTranslate.Y));
        }

        void Before_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            (DataContext as BeautyViewModel)?.SetBeforePreview(true);
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        void Before_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            (DataContext as BeautyViewModel)?.SetBeforePreview(false);
            ((UIElement)sender).ReleaseMouseCapture();
            e.Handled = true;
        }

        void Before_LostMouseCapture(object sender, MouseEventArgs e)
        {
            (DataContext as BeautyViewModel)?.SetBeforePreview(false);
        }
    }
}
