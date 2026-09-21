using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Sophon.UI.ViewModels.Vision;

namespace Sophon.UI.Views.Vision
{
    /// <summary>
    /// VisionMonitorView.xaml 的交互逻辑（视觉监视与视口点击/拖拽对位）。
    /// </summary>
    public partial class VisionMonitorView : UserControl
    {
        private Point _mouseDownPos;
        private bool _isDragging;

        public VisionMonitorView()
        {
            InitializeComponent();
        }

        private void OnCameraImageMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is VisionMonitorViewModel vm && vm.IsVisualGuidanceEnabled)
            {
                _mouseDownPos = e.GetPosition(CameraImage);
                _isDragging = true;
                CameraImage.CaptureMouse();
            }
        }

        private async void OnCameraImageMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            CameraImage.ReleaseMouseCapture();

            if (DataContext is not VisionMonitorViewModel vm || !vm.IsVisualGuidanceEnabled)
            {
                return;
            }

            if (CameraImage.Source is not BitmapSource bmp || bmp.PixelWidth <= 0 || bmp.PixelHeight <= 0)
            {
                return;
            }

            var mouseUpPos = e.GetPosition(CameraImage);

            // 计算 Uniform 缩放下图像在 Image 控件内的真实显示区域与有效缩放比
            double actualW = CameraImage.ActualWidth;
            double actualH = CameraImage.ActualHeight;
            if (actualW <= 0 || actualH <= 0) return;

            double scale = Math.Min(actualW / bmp.PixelWidth, actualH / bmp.PixelHeight);
            if (scale <= 1e-6) return;

            double renderedW = bmp.PixelWidth * scale;
            double renderedH = bmp.PixelHeight * scale;
            double offsetX = (actualW - renderedW) / 2.0;
            double offsetY = (actualH - renderedH) / 2.0;

            double deltaCtrlX = mouseUpPos.X - _mouseDownPos.X;
            double deltaCtrlY = mouseUpPos.Y - _mouseDownPos.Y;
            double moveDist = Math.Sqrt(deltaCtrlX * deltaCtrlX + deltaCtrlY * deltaCtrlY);

            if (moveDist < 6.0)
            {
                // 1. 点击对位操作 (Click to Align to Center)
                double pixelX = (_mouseDownPos.X - offsetX) / scale;
                double pixelY = (_mouseDownPos.Y - offsetY) / scale;

                // 边界校验：必须在真实图像内容区内
                if (pixelX >= 0 && pixelX <= bmp.PixelWidth && pixelY >= 0 && pixelY <= bmp.PixelHeight)
                {
                    await vm.ExecuteClickToAlignAsync(pixelX, pixelY, bmp.PixelWidth, bmp.PixelHeight);
                }
            }
            else
            {
                // 2. 拖拽微调操作 (Drag Vector Move)
                double dragPixelDeltaX = deltaCtrlX / scale;
                double dragPixelDeltaY = deltaCtrlY / scale;

                await vm.ExecuteDragAlignAsync(dragPixelDeltaX, dragPixelDeltaY);
            }
        }
    }
}
