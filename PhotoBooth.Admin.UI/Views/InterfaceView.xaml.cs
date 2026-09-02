using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PhotoBooth.Admin.UI.ViewModels;

namespace PhotoBooth.Admin.UI.Views
{
    public partial class InterfaceView : UserControl
    {
        Point dragStart;
        double dragPanX, dragPanY, dragLiveViewX, dragLiveViewY;
        DragTarget dragTarget;

        enum DragTarget
        {
            None,
            Background,
            LiveView
        }

        public InterfaceView() { InitializeComponent(); }

        void PreviewCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(DataContext is InterfaceViewModel vm)) return;

            dragStart = e.GetPosition(PreviewCanvas);
            if (vm.ShowLiveView && IsInsideLiveView(dragStart, vm))
            {
                dragLiveViewX = vm.LiveViewX;
                dragLiveViewY = vm.LiveViewY;
                dragTarget = DragTarget.LiveView;
            }
            else if (vm.BackgroundZoom > 100)
            {
                dragPanX = vm.BackgroundPanX;
                dragPanY = vm.BackgroundPanY;
                dragTarget = DragTarget.Background;
            }
            else
            {
                return;
            }

            PreviewCanvas.CaptureMouse();
            e.Handled = true;
        }

        void PreviewCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (dragTarget == DragTarget.None || !(DataContext is InterfaceViewModel vm)) return;
            var point = e.GetPosition(PreviewCanvas);

            if (dragTarget == DragTarget.LiveView)
            {
                var availableX = vm.PreviewCanvasWidth - vm.PreviewLiveViewWidth;
                var availableY = vm.PreviewCanvasHeight - vm.PreviewLiveViewHeight;
                if (availableX > 0) vm.LiveViewX = dragLiveViewX + ((point.X - dragStart.X) * 100d / availableX);
                if (availableY > 0) vm.LiveViewY = dragLiveViewY + ((point.Y - dragStart.Y) * 100d / availableY);
                return;
            }

            var overflowX = vm.PreviewBackgroundWidth - vm.PreviewCanvasWidth;
            var overflowY = vm.PreviewBackgroundHeight - vm.PreviewCanvasHeight;
            if (overflowX > 0) vm.BackgroundPanX = dragPanX - ((point.X - dragStart.X) * 200d / overflowX);
            if (overflowY > 0) vm.BackgroundPanY = dragPanY - ((point.Y - dragStart.Y) * 200d / overflowY);
        }

        void PreviewCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (dragTarget == DragTarget.None) return;
            dragTarget = DragTarget.None;
            PreviewCanvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        void PreviewCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(DataContext is InterfaceViewModel vm)) return;
            vm.BackgroundZoom += e.Delta > 0 ? 10 : -10; e.Handled = true;
        }

        static bool IsInsideLiveView(Point point, InterfaceViewModel vm) =>
            point.X >= vm.PreviewLiveViewX &&
            point.X <= vm.PreviewLiveViewX + vm.PreviewLiveViewWidth &&
            point.Y >= vm.PreviewLiveViewY &&
            point.Y <= vm.PreviewLiveViewY + vm.PreviewLiveViewHeight;
    }
}
