using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MangaTranslator.App.ViewModels;

namespace MangaTranslator.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WirePickers();
    }

    private void WirePickers()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        vm.PickManyAsync = async () =>
            (await PickOpenAsync(true)).Select(f => f.Path.LocalPath).ToArray();

        vm.PickSaveAsync = async () =>
        {
            var sp = GetTopLevel(this)?.StorageProvider;
            if (sp == null) return null;
            var f = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
            { SuggestedFileName = "page_ko.png", DefaultExtension = "png" });
            return f?.Path.LocalPath;
        };

        vm.Batch.PickFilesAsync = async () =>
            (await PickOpenAsync(true)).Select(f => f.Path.LocalPath).ToArray();

        vm.Batch.PickFolderAsync = async () =>
        {
            var sp = GetTopLevel(this)?.StorageProvider;
            if (sp == null) return null;
            var folders = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "이미지 폴더 선택 (배치)",
                AllowMultiple = false,
            });
            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        };

        // 캔버스 클릭 선택 → 목록으로 스크롤
        var list = this.FindControl<ListBox>("InstanceList");
        if (list != null)
            list.SelectionChanged += (_, _) => list.ScrollIntoView(list.SelectedItem);
    }

    // ---- 오버레이 사각형: 클릭=선택, 드래그=이동 (rect가 pointer capture) ----
    private TextInstanceViewModel? _dragVm;
    private Avalonia.Point _dragAnchor;
    private Control? _dragCanvas;

    // ---- 코너 리사이즈 핸들: Tag=0..3 코너 인덱스, pointer capture ----
    private TextInstanceViewModel? _resizeVm;
    private int _resizeIdx;
    private Control? _resizeCanvas;
    private Avalonia.Point _resizeAnchor;

    private void OnHandlePressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Shapes.Ellipse h) return;
        if (h.DataContext is not TextInstanceViewModel vm) return;
        if (!int.TryParse(h.Tag as string, out var idx)) idx = 3;

        vm.RaiseSelect();
        _resizeVm = vm;
        _resizeIdx = idx;
        _resizeCanvas = (Control?)h.Parent;
        _resizeAnchor = e.GetPosition(_resizeCanvas);
        e.Pointer.Capture(h);
        e.Handled = true; // rect로 버블링 차단 — 핸들 드래그가 이동을 유발하지 않음
    }

    private void OnHandleMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_resizeVm == null || _resizeCanvas == null) return;
        var p = e.GetPosition(_resizeCanvas);
        _resizeVm.ResizeCorner(_resizeIdx, p.X - _resizeAnchor.X, p.Y - _resizeAnchor.Y);
        _resizeAnchor = p;
        e.Handled = true;
    }

    private void OnHandleReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        _resizeVm = null;
        _resizeCanvas = null;
        e.Handled = true;
    }

    private void OnRectPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Avalonia.Controls.Shapes.Rectangle rect) return;
        if (rect.DataContext is not TextInstanceViewModel vm) return;

        vm.RaiseSelect();
        _dragVm = vm;
        _dragCanvas = (Control?)rect.Parent; // ItemsPanel Canvas — 레이아웃(px) 좌표계
        _dragAnchor = e.GetPosition(_dragCanvas); // Viewbox 역스케일 자동 적용
        e.Pointer.Capture(rect);
        e.Handled = true;
    }

    private void OnRectPointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        if (_dragVm == null || _dragCanvas == null) return;
        var p = e.GetPosition(_dragCanvas);
        _dragVm.MoveBy(p.X - _dragAnchor.X, p.Y - _dragAnchor.Y);
        _dragAnchor = p;
        e.Handled = true;
    }

    private void OnRectPointerReleased(object? sender, Avalonia.Input.PointerReleasedEventArgs e)
    {
        _dragVm = null;
        _dragCanvas = null;
        e.Handled = true;
    }

    private async Task<IReadOnlyList<IStorageFile>> PickOpenAsync(bool many)
    {
        var sp = GetTopLevel(this)?.StorageProvider
                 ?? throw new InvalidOperationException("no storage provider");
        return await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = many,
            Title = many ? "만화 페이지 선택 (배치)" : "만화 페이지 선택",
            FileTypeFilter = new[]
            {
                new FilePickerFileType("이미지") { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp" } },
                new FilePickerFileType("모두") { Patterns = new[] { "*" } },
            },
        });
    }
}
