using System.IO;
using System.Windows;
using System.Windows.Input;
using HalconVision.Halcon;

namespace HalconVision.App;

public partial class ImageViewerWindow : Window
{
    private readonly string _imagePath;
    private readonly HalconImagePresenter _presenter = new();
    private int _imageWidth;
    private int _imageHeight;
    private bool _panning;
    private Point _lastPointer;

    public ImageViewerWindow(string imagePath)
    {
        InitializeComponent();
        _imagePath = Path.GetFullPath(imagePath);
        Closed += (_, _) => ImageWindow.Dispose();
        Loaded += (_, _) =>
        {
            DisplayImage();
            Dispatcher.BeginInvoke(new Action(FitImage));
        };
    }

    private void DisplayImage()
    {
        try
        {
            var dimensions = _presenter.Display(_imagePath, ImageWindow.HalconWindow);
            _imageWidth = dimensions.Width;
            _imageHeight = dimensions.Height;
            ImageNameText.Text = Path.GetFileName(_imagePath);
            ImageInfoText.Text = $"{_imagePath}    ·    {dimensions.Width} × {dimensions.Height} px";
        }
        catch (Exception ex)
        {
            ImageInfoText.Text = $"图像加载失败：{ex.Message}";
            MessageBox.Show(this, ex.Message, "图像显示失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => _presenter.Zoom(ImageWindow.HalconWindow, 1.25);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => _presenter.Zoom(ImageWindow.HalconWindow, 0.8);
    private void Fit_Click(object sender, RoutedEventArgs e) => FitImage();
    private void FitImage() => _presenter.Fit(ImageWindow.HalconWindow, _imageWidth, _imageHeight, ImageHost.ActualWidth, ImageHost.ActualHeight);

    private void ImageHost_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _presenter.Zoom(ImageWindow.HalconWindow, e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private void ImageHost_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _panning = true;
        _lastPointer = e.GetPosition(ImageHost);
        ImageHost.CaptureMouse();
        ImageHost.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void ImageHost_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning || e.LeftButton != MouseButtonState.Pressed) return;
        var pointer = e.GetPosition(ImageHost);
        var delta = pointer - _lastPointer;
        _presenter.Pan(ImageWindow.HalconWindow, delta.X, delta.Y, ImageHost.ActualWidth, ImageHost.ActualHeight);
        _lastPointer = pointer;
        e.Handled = true;
    }

    private void ImageHost_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        _panning = false;
        ImageHost.ReleaseMouseCapture();
        ImageHost.Cursor = Cursors.Arrow;
        e.Handled = true;
    }
}
