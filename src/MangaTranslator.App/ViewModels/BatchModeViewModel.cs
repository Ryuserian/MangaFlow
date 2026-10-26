using System.Collections.ObjectModel;
using Avalonia.Threading;
using MangaTranslator.App.Services;
using MangaTranslator.Core.Imaging;
using MangaTranslator.Core.Pipeline;
using ReactiveUI;
using ReactiveUI.Primitives; // ReactiveUI 26: Unit → RxVoid

namespace MangaTranslator.App.ViewModels;

/// <summary>
/// 배치 항목 = 파일 하나 + therein 되는 편집기(사용자 2026-10-25: 배치도 Expander로 편집하며 여러장 처리).
/// 펼침(IsExpanded=true) → MainView 캔버스가 entry.Editor를 ActiveEditor로 채택,懒 디코딩+감지 실행.
/// 접힘 → 풀 페이지 비트맵만 회수(懒), 편집 인스턴스(좌표/원문/☑)는 유지 — 실행 시점이면 반영.
/// </summary>
public sealed class BatchEntryViewModel : ReactiveObject
{
    private string _status = "대기";
    private double _progress;
    private bool _isExpanded;

    public string Path { get; init; } = "";
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }
    public double Progress { get => _progress; set => this.RaiseAndSetIfChanged(ref _progress, value); }

    /// <summary>파일당 편집기 — 펼침 시 캔버스 활동 대상. 라지 페이지는懒 디코딩(ReleasePage로 회수).</summary>
    public PageEditorViewModel Editor { get; init; } = null!;

    /// <summary>Expander 열림 상태 — 메인뷰 캔버스 라우팅 +懒 처리 트리거.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            ExpandChanged?.Invoke(this, value);
        }
    }

    public event Action<BatchEntryViewModel, bool>? ExpandChanged;
}

/// <summary>
/// 배치 모드 = 여러 이미지를 같은 엔진 설정으로 연속 자동 처리.
/// queue를 for+await로 직렬 실행(한 장씩). 사용자는 Expander로 각 장의 박스를 개별 편집할 수 있고,
/// 편집된 장은 사용자 편집(☑/박스/크기)을 존중해 Apply, 미편집 장은 자동 Run으로 간다.
/// </summary>
public sealed class BatchModeViewModel : ViewModelBase
{
    private readonly EngineContext _ctx;
    private readonly Func<(string Ocr, string Inpaint, string Translate, string Render)> _keys;
    private readonly Action<string> _status;
    private bool _running;
    private BatchEntryViewModel? _selected;

    // ---- 배치 overlay 진행(전체 대기열 진행률) ----
    private double _busyProgress;
    private string _busyMessage = "";
    private double _busyCeiling = 0.97;
    private CancellationTokenSource? _activeCts;
    private EventHandler? _busyTick;
    private DispatcherTimer? _busyTimer;

    public ObservableCollection<BatchEntryViewModel> Queue { get; } = new();

    public bool IsRunning { get => _running; private set => this.RaiseAndSetIfChanged(ref _running, value); }

    /// <summary>현재 실행 중 진행률(전체 대기열 기준 0~1)과 단계 메시지. View 오버레이가 소비.</summary>
    public double BusyProgress { get => _busyProgress; private set => this.RaiseAndSetIfChanged(ref _busyProgress, value); }
    public string BusyMessage { get => _busyMessage; private set => this.RaiseAndSetIfChanged(ref _busyMessage, value); }

    /// <summary>펼친 항목 변경/추가 제거 → 메인뷰 캔버스 ActiveEditor 재계산 훅.</summary>
    public event Action? ActiveEditorChanged;

    private void BeginBusy(string msg)
    {
        _busyCeiling = 0.97;
        BusyMessage = msg;
        BusyProgress = 0.02;
        _busyTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        if (_busyTick != null) _busyTimer.Tick -= _busyTick;
        _busyTick = (_, _) =>
            BusyProgress = Math.Min(_busyCeiling, BusyProgress + Math.Max(0.003, (_busyCeiling - BusyProgress) * 0.04));
        _busyTimer.Tick += _busyTick;
        _busyTimer.Start();
    }

    /// <summary>대기열 진행: (완료장수 + 현재장 progress)/전체.</summary>
    private void PumpBatch(int doneCount, double curFrac)
    {
        var total = Math.Max(1, Queue.Count);
        BusyProgress = Math.Min(_busyCeiling, (doneCount + Math.Clamp(curFrac, 0, 1)) / total);
    }

    private void EndBusy()
    {
        _busyTimer?.Stop();
        BusyProgress = 1.0;
    }

    public BatchEntryViewModel? SelectedEntry
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    public Func<Task<string[]>>? PickFilesAsync { get; set; }
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    public ReactiveCommand<RxVoid, RxVoid> AddFilesCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddFolderCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> RemoveSelectedCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> RunQueueCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelRunCommand { get; }

    /// <summary>메인 VM 취소 버튼 집계용 — 진행 중 CTS를 직접 취소.</summary>
    public void CancelActive()
    {
        _activeCts?.Cancel();
        // 활동 편집기 작업도 함께 취소 — 펼친 채 실행되던 페이지를 즉시 중단
        Queue.FirstOrDefault(q => q.IsExpanded)?.Editor.CancelActive();
        BusyMessage = "취소 중…";
    }

    private BatchEntryViewModel MakeEntry(string path)
    {
        var ed = new PageEditorViewModel(_ctx, _keys, _status);
        ed.AttachPath(path);
        var entry = new BatchEntryViewModel { Path = path, Editor = ed };
        entry.ExpandChanged += (entry2, open) =>
        {
            if (open) { _ = ed.EnsureDetectedAsync(); }       // 펼침:懒 로드+감지→캔버스 편집 가능
            else ed.ReleasePage();                            // 접힘: 비트맵 회수(편집 내용 유지)
            ActiveEditorChanged?.Invoke();
        };
        ed.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PageEditorViewModel.IsBusy)
                or nameof(PageEditorViewModel.OverlayMessage))
                this.RaisePropertyChanged(nameof(IsRunning)); // AnyBusy 게이트(전역 오버레이) 최신화
        };
        return entry;
    }

    /// <summary>외부(메인 툴바 다중 열기)에서 경로 일괄 큐잉. 기존 엔진 설정으로 연속 처리 대기.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!Queue.Any(q => q.Path == p))
                Queue.Add(MakeEntry(p));
        ActiveEditorChanged?.Invoke();
        _status($"대기열 {Queue.Count}장");
    }

    /// <summary>폴더 입력: 최상위 이미지만 큐잉(.png/.jpg/.jpeg/.webp, 재귀 하위폴더 포함.
    /// 숨김·.app 번들 내부는 제외). 정렬은 이름 순(페이지 번호 순서 보존).</summary>
    public int AddFolder(string folder)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) { _status("폴더를 찾을 수 없습니다"); return 0; }
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp" };
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => exts.Contains(System.IO.Path.GetExtension(f)))
            .Where(f => !f.Contains("/.app/") && !System.IO.Path.GetFileName(f).StartsWith("."))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var before = Queue.Count;
        AddPaths(files);
        _status($"폴더 스캔: {files.Count}장 추가 — 대기열 {Queue.Count}장");
        return Queue.Count - before;
    }

    public BatchModeViewModel(EngineContext ctx,
        Func<(string, string, string, string)> keys, Action<string> status)
    {
        _ctx = ctx; _keys = keys; _status = status;

        AddFilesCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (PickFilesAsync == null) return;
            var paths = await PickFilesAsync();
            AddPaths(paths ?? Array.Empty<string>());
        });

        AddFolderCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (PickFolderAsync == null) return;
            var folder = await PickFolderAsync();
            if (folder != null) AddFolder(folder);
        });

        RemoveSelectedCommand = ReactiveCommand.Create(() =>
        {
            if (SelectedEntry != null) Queue.Remove(SelectedEntry);
            ActiveEditorChanged?.Invoke();
        }, this.WhenAnyValue(x => x.SelectedEntry, (BatchEntryViewModel? s) => s != null));

        // 펼침/접힘에도 라우팅이 변함 — 첫 자동 펼침으로 캔버스 예열(빈 화면 방지).
        RunQueueCommand = ReactiveCommand.CreateFromTask(async ct =>
        {
            var (o, i, t, r) = _keys();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeCts = cts;
            IsRunning = true;
            var snapshot = Queue.ToList();
            BeginBusy($"배치 실행 중… 0/{snapshot.Count}");
            try
            {
                for (var n = 0; n < snapshot.Count; n++)
                {
                    var entry = snapshot[n];
                    cts.Token.ThrowIfCancellationRequested();
                    entry.Status = "실행중"; entry.Progress = 0.05;
                    BusyMessage = $"배치 {n + 1}/{snapshot.Count} — {entry.FileName}";
                    var idx = n;
                    try
                    {
                        // 펼침(감지됨)=편집된 장 → 사용자 편집(☑/박스/크기)을 존중해 Apply.
                        // 미펼침(NoImage)=자동 Run. ☑ 전 해제면 '보존' 의도 → 빈 박스로 Apply(원본 출력).
                        await entry.Editor.EnsurePageAsync(cts.Token);
                        if (entry.Editor.Step == AppStep.NoImage)
                            await entry.Editor.RunAutoAsync((p, m) => PumpBatch(idx, p), cts.Token);
                        else
                            await entry.Editor.ApplyEditedAsyncSafe((p, m) => PumpBatch(idx, p), cts.Token);

                        entry.Progress = 1; entry.Status = "완료";
                        PumpBatch(idx + 1, 0);
                    }
                    catch (OperationCanceledException) { entry.Status = "취소"; throw; }
                    catch (Exception ex)
                    {
                        entry.Status = "실패";
                        entry.Progress = 1;
                        _status($"[{entry.FileName}] 실패: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { _status("배치 취소됨"); }
            finally
            {
                _activeCts = null;
                IsRunning = false;
                EndBusy();
                _status($"배치 종료 — {Queue.Count(e => e.Status == "완료")}/{Queue.Count} 완료");
            }
        }, this.WhenAnyValue(x => x.IsRunning, (bool r) => !r));

        CancelRunCommand = ReactiveCommand.Create(() =>
        {
            _activeCts?.Cancel();
            BusyMessage = "취소 중…";
        }, this.WhenAnyValue(x => x.IsRunning));
    }
}
