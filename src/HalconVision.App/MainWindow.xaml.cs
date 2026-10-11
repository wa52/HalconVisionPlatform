using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HalconDotNet;
using HalconVision.Application;
using HalconVision.Domain;
using HalconVision.Halcon;
using HalconVision.Persistence;
using Microsoft.Win32;

namespace HalconVision.App;

public partial class MainWindow : Window
{
    private string? _imagePath;
    private InspectionRecipe? _recipe;
    private readonly HalconInspectionEngine _engine = new();
    private readonly RecipeStore _recipeStore = new();
    private readonly ResultStore _resultStore = new();
    private readonly InspectionRunner _runner;
    private readonly BatchInspectionRunner _batchRunner;
    private readonly BatchResultStore _batchResultStore = new();
    private readonly HalconImagePresenter _imagePresenter = new();
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private string[] _sequence = [];
    private int _sequenceIndex;
    private int _imageWidth;
    private int _imageHeight;
    private bool _drawRoiArmed;
    private (int Row, int Column)? _roiStart;

    public MainWindow()
    {
        InitializeComponent();
        _runner = new InspectionRunner(_engine, _resultStore);
        _batchRunner = new BatchInspectionRunner(_engine, _resultStore, _batchResultStore);
        _playTimer.Tick += (_, _) => PlayNextFrame();
        CheckEnvironment();
    }

    private void CheckEnvironment()
    {
        var root = Environment.GetEnvironmentVariable("HALCONROOT");
        var versionPath = string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, "version.txt");
        var version = versionPath is not null && File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "未检测到";
        EnvironmentText.Text = $"HALCON {version}  ·  {Environment.GetEnvironmentVariable("HALCONARCH") ?? "x64"}";
        AddLog($"运行环境：HALCON {version}，{Environment.GetEnvironmentVariable("HALCONARCH") ?? "架构未配置"}；图像处理运行在本机 HALCON Runtime。");
    }

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择图像样本", Filter = "图像文件|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.jp2|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) LoadImage(dialog.FileName, "文件图像");
    }

    private void OpenSequence_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择同尺寸图像序列（可多选）", Multiselect = true, Filter = "图像文件|*.bmp;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.jp2" };
        if (dialog.ShowDialog(this) != true) return;
        StopPlayback();
        _sequence = dialog.FileNames.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        _sequenceIndex = 0;
        PlayButton.IsEnabled = _sequence.Length > 0;
        AddLog($"已载入文件回放序列：{_sequence.Length} 张。回放会逐帧运行当前 Blob 参数，不写入历史结果。");
        LoadImage(_sequence[0], "文件回放");
    }

    private void LoadImage(string path, string mode)
    {
        try
        {
            var dimensions = _imagePresenter.Display(path, ImageWindow.HalconWindow);
            _imageWidth = dimensions.Width; _imageHeight = dimensions.Height; _imagePath = path;
            if (_recipe is null) { _recipe = new InspectionRecipe { RoiWidth = _imageWidth, RoiHeight = _imageHeight }; ApplyRecipe(_recipe); }
            else if (_recipe.RoiWidth == 0 || _recipe.RoiHeight == 0) { _recipe = _recipe with { RoiWidth = _imageWidth, RoiHeight = _imageHeight }; ApplyRecipe(_recipe); }
            ImageInfoText.Text = $"{Path.GetFileName(path)}\n{_imageWidth} × {_imageHeight} px";
            InputStateText.Text = mode; ViewportModeText.Text = mode;
            RunButton.IsEnabled = true; CountText.Text = "—"; SetDecision("待检测", "#71808B", "#EEF1F3"); ElapsedText.Text = "等待运行";
            DrawRoiOverlay();
            AddLog($"已加载{mode}：{Path.GetFileName(path)}（{_imageWidth} × {_imageHeight}）");
        }
        catch (Exception ex) { AddLog($"图像加载失败：{ex}"); MessageBox.Show(this, ex.Message, "无法加载图像", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void DrawRoi_Click(object sender, RoutedEventArgs e)
    {
        if (_imagePath is null) { AddLog("请先打开图像，再绘制 ROI。"); return; }
        _drawRoiArmed = true; _roiStart = null; DrawRoiButton.Content = "拖拽图像绘制…";
        AddLog("ROI 绘制已启用：在图像上按住左键拖出矩形。");
    }

    private void ImageWindow_HMouseDown(object sender, HSmartWindowControlWPF.HMouseEventArgsWPF e)
    {
        if (_drawRoiArmed && e.Button == MouseButton.Left && _imageWidth > 0)
            _roiStart = (Clamp((int)Math.Round(e.Row), 0, _imageHeight - 1), Clamp((int)Math.Round(e.Column), 0, _imageWidth - 1));
    }

    private void ImageWindow_HMouseUp(object sender, HSmartWindowControlWPF.HMouseEventArgsWPF e)
    {
        if (!_drawRoiArmed || _roiStart is null || e.Button != MouseButton.Left) return;
        try
        {
            var row = Clamp((int)Math.Round(e.Row), 0, _imageHeight - 1); var col = Clamp((int)Math.Round(e.Column), 0, _imageWidth - 1);
            var start = _roiStart.Value; var x = Math.Min(start.Column, col); var y = Math.Min(start.Row, row);
            _recipe = ReadRecipe() with { RoiX = x, RoiY = y, RoiWidth = Math.Abs(col - start.Column) + 1, RoiHeight = Math.Abs(row - start.Row) + 1 };
            ApplyRecipe(_recipe); DrawRoiOverlay();
            AddLog($"ROI 已更新：X={_recipe.RoiX}, Y={_recipe.RoiY}, {_recipe.RoiWidth} × {_recipe.RoiHeight} px。");
        }
        catch (Exception ex) { AddLog($"ROI 未更新：请先修正检测参数。{ex.Message}"); }
        finally { _drawRoiArmed = false; _roiStart = null; DrawRoiButton.Content = "绘制 ROI"; }
    }

    private void ImageWindow_HMouseMove(object sender, HSmartWindowControlWPF.HMouseEventArgsWPF e) => CoordinateText.Text = $"X {e.Column:F0}  Y {e.Row:F0}";

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_imagePath is null) return;
        try
        {
            var recipe = ReadRecipe(); var persisted = _runner.Run(_imagePath, recipe); _recipe = recipe;
            DisplayResult(_imagePath, recipe, persisted.Result);
            AddLog($"检测完成：阈值 → 连通域 → 面积筛选，数量 {persisted.Result.ComponentCount}/{recipe.ExpectedComponentCount}，判定 {DecisionText.Text}；记录已保存。");
        }
        catch (Exception ex) { ShowInspectionError(ex); }
    }

    private async void Batch_Click(object sender, RoutedEventArgs e)
    {
        if (_recipe is null || _imageWidth <= 0 || _imageHeight <= 0)
        {
            MessageBox.Show(this, "请先打开一张样本图像并配置 ROI，或加载已有方案。", "需要检测方案", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFolderDialog { Title = "选择待批量检测的图像目录", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".jp2" };
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(dialog.FolderName)
                .Where(path => supportedExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex)
        {
            AddLog($"无法读取批量检测目录：{ex}");
            MessageBox.Show(this, ex.Message, "无法读取图像目录", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (paths.Length == 0)
        {
            MessageBox.Show(this, "所选目录中没有支持的图像文件。", "没有可检测图像", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        InspectionRecipe recipe;
        try { recipe = ReadRecipe(); recipe.Validate(_imageWidth, _imageHeight); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "检测参数无效", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

        BatchButton.IsEnabled = false; RunButton.IsEnabled = false; PlayButton.IsEnabled = false;
        var progress = new Progress<int>(completed => InputStateText.Text = $"批量检测 {completed}/{paths.Length}");
        AddLog($"批量检测开始：{paths.Length} 张图像，方案“{recipe.Name}”。");
        try
        {
            var summary = await Task.Run(() => _batchRunner.Run(paths, recipe, progress));
            var summaryPath = Path.Combine(_batchResultStore.DirectoryPath, $"{summary.BatchId}.json");
            CountText.Text = $"{summary.Passed}/{summary.Total}";
            SetDecision(summary.Errors > 0 ? "含错误" : summary.Failed > 0 ? "含 NG" : "全部 OK",
                summary.Errors + summary.Failed == 0 ? "#16845B" : "#B33A42",
                summary.Errors + summary.Failed == 0 ? "#E5F4ED" : "#FBEAEC");
            ElapsedText.Text = $"OK {summary.Passed}   NG {summary.Failed}   ERROR {summary.Errors}";
            InputStateText.Text = "批量检测完成";
            AddLog($"批量检测完成：OK {summary.Passed}，NG {summary.Failed}，ERROR {summary.Errors}；汇总：{summaryPath}");
        }
        catch (Exception ex)
        {
            InputStateText.Text = "批量检测失败"; AddLog($"批量检测未能生成汇总：{ex}");
            MessageBox.Show(this, ex.Message, "批量检测失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BatchButton.IsEnabled = true; RunButton.IsEnabled = _imagePath is not null; PlayButton.IsEnabled = _sequence.Length > 0;
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_sequence.Length == 0) return;
        _sequenceIndex = 0; _playTimer.Start(); PlayButton.IsEnabled = false; StopButton.IsEnabled = true;
        InputStateText.Text = "文件回放 · 运行中"; ViewportModeText.Text = "文件回放 · RUN"; AddLog("开始文件序列回放；每帧执行检测，结果只显示在界面。"); PlayNextFrame();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void StopPlayback()
    {
        _playTimer.Stop(); StopButton.IsEnabled = false; PlayButton.IsEnabled = _sequence.Length > 0;
        if (_sequence.Length > 0) { InputStateText.Text = "文件回放"; ViewportModeText.Text = "文件回放"; }
    }

    private void PlayNextFrame()
    {
        if (_sequence.Length == 0) { StopPlayback(); return; }
        var path = _sequence[_sequenceIndex++ % _sequence.Length];
        try
        {
            var dims = _imagePresenter.Display(path, ImageWindow.HalconWindow); _imageWidth = dims.Width; _imageHeight = dims.Height; _imagePath = path;
            ImageInfoText.Text = $"{Path.GetFileName(path)}\n{_imageWidth} × {_imageHeight} px";
            var recipe = ReadRecipe(); var result = _engine.Run(path, recipe); DisplayResult(path, recipe, result);
        }
        catch (Exception ex) { ShowInspectionError(ex, false); }
    }

    private void DisplayResult(string path, InspectionRecipe recipe, InspectionResult result)
    {
        _imagePresenter.Display(path, ImageWindow.HalconWindow);
        var window = ImageWindow.HalconWindow; window.SetDraw("margin"); window.SetColor("cyan");
        window.DispRectangle1((double)recipe.RoiY, (double)recipe.RoiX, (double)(recipe.RoiY + recipe.RoiHeight - 1), (double)(recipe.RoiX + recipe.RoiWidth - 1));
        window.SetColor("lime green");
        foreach (var c in result.Components) window.DispRectangle1((double)c.Y, (double)c.X, (double)(c.Y + c.Height - 1), (double)(c.X + c.Width - 1));
        CountText.Text = result.ComponentCount.ToString(CultureInfo.InvariantCulture);
        SetDecision(result.IsPass ? "OK" : "NG", result.IsPass ? "#16845B" : "#B33A42", result.IsPass ? "#E5F4ED" : "#FBEAEC");
        ElapsedText.Text = $"{result.ElapsedMilliseconds:F1} ms   ·   面积筛选 {recipe.MinimumArea:G}–{recipe.MaximumArea:G} px²";
    }

    private void ShowInspectionError(Exception ex, bool showDialog = true)
    {
        CountText.Text = "—"; SetDecision("ERROR", "#B33A42", "#FBEAEC"); ElapsedText.Text = "检测失败 · 未判定";
        AddLog($"HALCON 检测失败：{ex}"); if (showDialog) MessageBox.Show(this, ex.Message, "HALCON 检测失败", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void DrawRoiOverlay()
    {
        if (_imagePath is null || _recipe is null) return;
        try { ImageWindow.HalconWindow.SetDraw("margin"); ImageWindow.HalconWindow.SetColor("cyan"); ImageWindow.HalconWindow.DispRectangle1((double)_recipe.RoiY, (double)_recipe.RoiX, (double)(_recipe.RoiY + _recipe.RoiHeight - 1), (double)(_recipe.RoiX + _recipe.RoiWidth - 1)); } catch { }
    }

    private void LoadRecipe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "加载检测方案", Filter = "方案文件|*.json|所有文件|*.*", InitialDirectory = _recipeStore.DirectoryPath };
        if (dialog.ShowDialog(this) != true) return;
        try { var recipe = _recipeStore.Load(dialog.FileName); if (_imageWidth > 0) recipe.Validate(_imageWidth, _imageHeight); _recipe = recipe; ApplyRecipe(recipe); AddLog($"已加载方案：{recipe.Name}"); }
        catch (Exception ex) { AddLog($"方案加载失败：{ex.Message}"); MessageBox.Show(this, ex.Message, "无法加载方案", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SaveRecipe_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var recipe = ReadRecipe(); recipe.Validate(Math.Max(1, _imageWidth), Math.Max(1, _imageHeight));
            var dialog = new SaveFileDialog { Title = "保存检测方案", Filter = "方案文件|*.json", FileName = $"{recipe.Name}.json", InitialDirectory = _recipeStore.DirectoryPath };
            if (dialog.ShowDialog(this) != true) return;
            var path = _recipeStore.Save(recipe, dialog.FileName); _recipe = recipe; RecipeTitleText.Text = $"Blob / {recipe.Name}"; AddLog($"方案已保存：{path}");
        }
        catch (Exception ex) { AddLog($"方案保存失败：{ex.Message}"); MessageBox.Show(this, ex.Message, "无法保存方案", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private InspectionRecipe ReadRecipe() => new()
    {
        Name = RecipeNameBox.Text.Trim(), ExpectedComponentCount = int.Parse(ExpectedCountBox.Text, CultureInfo.InvariantCulture),
        ThresholdMinimum = double.Parse(ThresholdMinBox.Text, CultureInfo.InvariantCulture), ThresholdMaximum = double.Parse(ThresholdMaxBox.Text, CultureInfo.InvariantCulture),
        MinimumArea = double.Parse(MinAreaBox.Text, CultureInfo.InvariantCulture), MaximumArea = double.Parse(MaxAreaBox.Text, CultureInfo.InvariantCulture),
        RoiX = int.Parse(RoiXBox.Text, CultureInfo.InvariantCulture), RoiY = int.Parse(RoiYBox.Text, CultureInfo.InvariantCulture),
        RoiWidth = int.Parse(RoiWidthBox.Text, CultureInfo.InvariantCulture), RoiHeight = int.Parse(RoiHeightBox.Text, CultureInfo.InvariantCulture)
    };

    private void ApplyRecipe(InspectionRecipe recipe)
    {
        RecipeNameBox.Text = recipe.Name; RecipeTitleText.Text = $"Blob / {recipe.Name}";
        ExpectedCountBox.Text = recipe.ExpectedComponentCount.ToString(CultureInfo.InvariantCulture);
        ThresholdMinBox.Text = recipe.ThresholdMinimum.ToString(CultureInfo.InvariantCulture); ThresholdMaxBox.Text = recipe.ThresholdMaximum.ToString(CultureInfo.InvariantCulture);
        MinAreaBox.Text = recipe.MinimumArea.ToString(CultureInfo.InvariantCulture); MaxAreaBox.Text = recipe.MaximumArea.ToString(CultureInfo.InvariantCulture);
        RoiXBox.Text = recipe.RoiX.ToString(CultureInfo.InvariantCulture); RoiYBox.Text = recipe.RoiY.ToString(CultureInfo.InvariantCulture);
        RoiWidthBox.Text = recipe.RoiWidth.ToString(CultureInfo.InvariantCulture); RoiHeightBox.Text = recipe.RoiHeight.ToString(CultureInfo.InvariantCulture);
    }

    private void SetDecision(string text, string foreground, string background) { DecisionText.Text = text; DecisionText.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground)!; DecisionBadge.Background = (Brush)new BrushConverter().ConvertFromString(background)!; }
    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));
    private void AddLog(string message) => LogList.Items.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
}
