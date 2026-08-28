using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoBooth.Admin.UI.ViewModels;

namespace PhotoBooth.Admin.UI.Views
{
    public partial class InterfaceView : UserControl
    {
        Point dragStart;
        double dragPanX, dragPanY;
        bool dragging;

        public InterfaceView() { InitializeComponent(); }

        void PreviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(DataContext is InterfaceViewModel vm) || vm.BackgroundZoom <= 100) return;
            dragStart = e.GetPosition(PreviewCanvas); dragPanX = vm.BackgroundPanX; dragPanY = vm.BackgroundPanY;
            dragging = true; PreviewCanvas.CaptureMouse(); e.Handled = true;
        }

        void PreviewCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!dragging || !(DataContext is InterfaceViewModel vm)) return;
            var point = e.GetPosition(PreviewCanvas);
            var overflowX = vm.PreviewBackgroundWidth - vm.PreviewCanvasWidth;
            var overflowY = vm.PreviewBackgroundHeight - vm.PreviewCanvasHeight;
            if (overflowX > 0) vm.BackgroundPanX = dragPanX - ((point.X - dragStart.X) * 200d / overflowX);
            if (overflowY > 0) vm.BackgroundPanY = dragPanY - ((point.Y - dragStart.Y) * 200d / overflowY);
        }

        void PreviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            dragging = false; PreviewCanvas.ReleaseMouseCapture(); e.Handled = true;
        }

        void PreviewCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(DataContext is InterfaceViewModel vm)) return;
            vm.BackgroundZoom += e.Delta > 0 ? 10 : -10; e.Handled = true;
        }
    }
}
