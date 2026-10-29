using System.Collections.ObjectModel;
using Avalonia.Threading;
using MangaTranslator.App.Services;
using MangaTranslator.Core.Batch;
using MangaTranslator.Core.Imaging;
using MangaTranslator.Core.Interfaces;
using MangaTranslator.Core.Pipeline;
using ReactiveUI;
using ReactiveUI.Primitives; // ReactiveUI 26: Unit → RxVoid

namespace MangaTranslator.App.ViewModels;

/// <summary>
/// 배치 항목 = 파일 하나 + therein 되는 편집기(사용자 2026-10-25: 배치도 Expander로 편집하며 여러장 처리).
/// 펼침(IsExpanded=true) → MainView 캔버스가 entry.Editor를 ActiveEditor로 채택,懒 디코딩+감지 실행.
/// 접힘 → 풀 페이지 비트맵만 회수(懒), 편집 인스턴스(좌표/원문/☑)는 유지 — 실행 시점이면 반영.
/// 2026-10-28 리팩터: 편집 데이터(single truth)는 BatchProject(통합 설정 JSON)로 이동 —
/// 편집기는 설정의 뷰/편집 프록시. 접혀도 설정은 JSON에 살아있어 캔버스 없이도 전체 재조판 가능.
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

    /// <summary>펼친 항목 변경/추가 제거 → 메인뷰 캔버스 ActiveEditor 재계산 훅.</summary>
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
/// 배치 모드 = 여러 이미지를 같은 엔진 설정으로 연속 자동 처리(2026-10-28 사용자 결정 구조).
///
/// ■ 2단계 파이프라인 — 설정 파일 중심:
///   [1단계: 인식] 🔍 전체 인식 — OCR 모델(PP-OCR det)만 로드. 파일마다 위치+원문을 인식해서
///       BatchProject(통합 설정 JSON)에 저장한다. 번역 모델은 안 올린다(메모리 절약).
///   (사용자 편집: 펼친 장만 캔버스에서 — 편집 즉시 BatchProject에 반영.)
///   [2단계: 번역·재조판] ▶ 설정 기반 실행 — 번역 모델(GGUF)을 딱 한 번 올리고(HoldAcrossPages),
///       BatchProject을 전체적으로 읽어 순차 번역+inpaint+재조판. 장마다 cold 재적재 없음.
///       ★ 배치 전체가 하나의 설정파일로 귀결되므로 개별 파일 설정은 더 이상 만들지 않는다.
///
/// 옛 RunQueueCommand(연속 실행)은 폴백으로 유지: 펼친 장은 편집 반영 Apply, 미펼침 장은 자동 Run.
/// </summary>
public sealed class BatchModeViewModel : ViewModelBase
{
    private readonly EngineContext _ctx;
    private readonly Func<(string Ocr, string Inpaint, string Translate, string Render)> _keys;
    private readonly Action<string> _status;
    private bool _running;
    private BatchEntryViewModel? _selected;

    /// <summary>통합 설정 — 배치 대기열 전체의 단일 진실원. 순서 = 처리 순서, 편집/인식 시 갱신,
    /// 저장 시 단일 JSON 파일(out/batch-project.json)로 커밋(사용자 2026-10-28: 개별 파일 설정 금지).</summary>
    public BatchProject Project { get; private set; } = new();

    /// <summary>통합 설정 JSON 저장 경로. 앱 시작 시점이 아니라 폴더/파일 큐잉이 정해진 뒤 결정한다.</summary>
    public string ProjectJsonPath { get; private set; } = "";

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

    /// <summary>큐 선택(2026-10-28 사용자 요청): 선택 즉시 캔버스가 그 장으로 전환된다.
    /// 펼쳐진 장만 가능했던 캔버스 미리보기가 선택만으로 가능 — 인식된 세트에서 골라 확인.
    /// ★ 선택만으로는 감지를 자동 실행하지 않는다(allowDetect=false) — ①전체 인식·🔄·펼침의
    /// 명시적 경로만 OCR을 태운다. 선택마다 자동 detect가 붙으면 ①스캔의 VLM 세션과 경합해
    /// 인식이 엉키거나 지연된다(실측 버그 2026-10-28 "배치 OCR 인식 제대로 동작 안됨").</summary>
    public BatchEntryViewModel? SelectedEntry
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            this.RaiseAndSetIfChanged(ref _selected, value);
            if (value != null)
            {
                ActiveEditorChanged?.Invoke();            // 캔버스 즉시 재라우팅(로딩 전 자리 확보)
                _ = RestoreEditorFromConfigAsync(value, allowDetect: false); // 디코딩+설정 하이드레이트만
            }
        }
    }

    public Func<Task<string[]>>? PickFilesAsync { get; set; }
    public Func<Task<string?>>? PickFolderAsync { get; set; }

    public ReactiveCommand<RxVoid, RxVoid> AddFilesCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> AddFolderCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> RemoveSelectedCommand { get; }
    /// <summary>① 전체 인식 — OCR(det)만 올리고 파일마다 위치+원문 인식 → BatchProject 채움(JSON 커밋).</summary>
    public ReactiveCommand<RxVoid, RxVoid> ScanAllCommand { get; }
    /// <summary>② 설정 기반 번역·재조판 — 번역 GGUF 1회 적재(hold), BatchProject 전체를 읽어 순차 실행.</summary>
    public ReactiveCommand<RxVoid, RxVoid> TranslateFromConfigCommand { get; }
    /// <summary>폴백: 옛 연속 실행(펼침=편집 반영 Apply / 미펼침=자동 Run).</summary>
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
            if (open) { _ = RestoreEditorFromConfigAsync(entry2, allowDetect: true); } // 펼침: 설정→하이드레이트, 없으면 detect
            else if (!entry2.IsExpanded) ed.ReleasePage();          // 접힘: 비트맵 회수(설정=진실원 유지)
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

    /// <summary>통합 설정 JSON 경로 = 대기열 첫 항목 폴더 + (queue가 비면 out).
    /// 프로젝트 폴더 내 단일 파일. 개별 sidecar는 만들지 않는다(사용자 2026-10-28).</summary>
    private void EnsureProjectPath()
    {
        if (!string.IsNullOrEmpty(ProjectJsonPath)) return;
        var dir = Queue.FirstOrDefault() is { } q
            ? (System.IO.Path.GetDirectoryName(q.Path) ?? _ctx.OutputDir)
            : _ctx.OutputDir;
        ProjectJsonPath = System.IO.Path.Combine(dir, "batch-project.json");
    }

    /// <summary>설정 커밋: 편집 완료 후 단일 JSON으로 저장(멱등·빠름 —pages는 JSON화 가능 DTO뿐).</summary>
    public void CommitProject()
    {
        EnsureProjectPath();
        try
        {
            Project.TranslateEngine = EngineContext.TranslateKey;
            Project.SaveJson(ProjectJsonPath);
            _status($"설정 저장: {System.IO.Path.GetFileName(ProjectJsonPath)} ({Project.Count}장)");
        }
        catch (Exception ex) { _status($"설정 저장 실패: {ex.Message}"); }
    }

    /// <summary>통합 설정을 이 entry 편집기에 반영(펼침/선택 시).
    /// 이미 편집/감지된 편집기는 편집 내용을 보존하고 비트맵만 보장 — 재클릭이 미커밋 편집을 덮어쓰지 않는다.
    /// allowDetect=false(선택 경로): 설정 없으면 감지하지 않고 빈 캔버스 미리보기만 —
    /// allowDetect=true(펼침 경로): 설정 없으면 감지 실행(기존 UX).</summary>
    private async Task RestoreEditorFromConfigAsync(BatchEntryViewModel entry, bool allowDetect)
    {
        var ed = entry.Editor;
        // 이미 편집/감지됨 또는 사용자가 손으로 추가한 박스가 있으면 보존 — 비트맵만 보장.
        if (ed.Step != AppStep.NoImage || ed.Instances.Count > 0) { await ed.EnsurePageAsync(); return; }
        var pc = Project.For(entry.Path);
        if (pc != null)
        {
            await ed.EnsurePageAsync();
            ed.HydrateInstances(pc.Regions);
        }
        else if (allowDetect) await ed.EnsureDetectedAsync();
        else await ed.EnsurePageAsync(); // 선택 미리보기: 디코딩만 (detect는 명시 경로)
    }

    /// <summary>외부(메인 툴바 다중 열기)에서 경로 일괄 큐잉. 기존 엔진 설정으로 연속 처리 대기.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!Queue.Any(q => q.Path == p))
                Queue.Add(MakeEntry(p));
        EnsureProjectPath();
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

    /// <summary>Queue 변경(제거) → BatchProject과 동기화: 없는 장의 설정을 정리하고 커밋.</summary>
    private void SyncProjectWithQueue()
    {
        var alive = new HashSet<string>(Queue.Select(q => q.Path), StringComparer.OrdinalIgnoreCase);
        Project.Pages.RemoveAll(p => !alive.Contains(p.Path));
        foreach (var e in Queue)
        {
            if (e.Editor.Step == AppStep.NoImage) continue;
            Project.Upsert(e.Path, e.Editor.SnapshotRegions());
        }
        CommitProject();
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
            SyncProjectWithQueue();
            ActiveEditorChanged?.Invoke();
        }, this.WhenAnyValue(x => x.SelectedEntry, (BatchEntryViewModel? s) => s != null));

        // ①/② 2단계 커맨드 — 통합 설정 파일 중심 워크플로(2026-10-28 사용자 결정).
        ScanAllCommand = ReactiveCommand.CreateFromTask(async ct => await ScanAllAsync(ct),
            this.WhenAnyValue(x => x.IsRunning, (bool r) => !r));
        TranslateFromConfigCommand = ReactiveCommand.CreateFromTask(async ct => await TranslateFromConfigAsync(ct),
            this.WhenAnyValue(x => x.IsRunning, (bool r) => !r));

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
                SyncProjectWithQueue();
                _status($"배치 종료 — {Queue.Count(e => e.Status == "완료")}/{Queue.Count} 완료");
            }
        }, this.WhenAnyValue(x => x.IsRunning, (bool r) => !r));

        CancelRunCommand = ReactiveCommand.Create(() =>
        {
            _activeCts?.Cancel();
            BusyMessage = "취소 중…";
        }, this.WhenAnyValue(x => x.IsRunning));
    }

    // ================================================================
    // ① 전체 인식(OCR) → 통합 설정 채움
    // ================================================================
    /// <summary>OCR(det+rec) 모델만 로드해 전 파일을 인식하고, 결과(박스/원문/방향/신뢰도)를
    /// BatchProject에 기록한 뒤 단일 JSON으로 커밋한다. 번역 엔진은 건드리지 않는다(메모리 절약).</summary>
    public async Task ScanAllAsync(CancellationToken ct = default)
    {
        if (Queue.Count == 0) { _status("대기열이 비어 있습니다"); return; }
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeCts = cts;
        IsRunning = true;
        var snapshot = Queue.ToList();
        BeginBusy($"① 전체 인식… 0/{snapshot.Count}");
        TranslationPipeline? pipe = null;
        try
        {
            // 번역 GGUF는 lazy — 이 루프는 DetectAsync만 호출하므로 절대 로드되지 않는다(메모리 절약).
            pipe = _ctx.BuildDefaultPipeline();
            pipe.HoldOcrLoaded = true;     // 스캔 루프 동안 det warm — 장마다 cold 재적재 방지
            var proj = new BatchProject { TranslateEngine = EngineContext.TranslateKey };
            for (var n = 0; n < snapshot.Count; n++)
            {
                var entry = snapshot[n];
                cts.Token.ThrowIfCancellationRequested();
                entry.Status = "인식중"; entry.Progress = 0.1;
                BusyMessage = $"① 인식 {n + 1}/{snapshot.Count} — {entry.FileName}";
                var idx = n;
                try
                {
                    var regions = await entry.Editor.DetectWithPipelineAsync(pipe, cts.Token);
                    // 편집기(펼쳐진 경우)에도 동일 설정을 하이드레이트 — 진실원은 설정, 편집기는 뷰
                    entry.Editor.HydrateInstances(regions.Select(RegionConfig.FromRegion));
                    proj.Upsert(entry.Path, regions.Select(RegionConfig.FromRegion));
                    entry.Progress = 1; entry.Status = "인식됨";
                    PumpBatch(idx + 1, 0);
                }
                catch (OperationCanceledException) { entry.Status = "취소"; throw; }
                catch (Exception ex)
                {
                    entry.Status = "실패"; entry.Progress = 1;
                    _status($"[{entry.FileName}] 인식 실패: {ex.Message}");
                }
            }
            Project = proj;   // 완성본만 공개 — 도중에 반쪽 설정 노출 금지(UI 스레드 직렬 실행)
            CommitProject();
            _status($"① 인식 완료 — 설정 {Project.Count}장 커밋: {System.IO.Path.GetFileName(ProjectJsonPath)}");
        }
        catch (OperationCanceledException) { _status("① 인식 취소됨"); }
        finally
        {
            // OCR도 내린다 — 2단계(번역)가 GGUF 자리를 쓸 수 있게.
            pipe?.ReleaseHeldEngines();
            _activeCts = null;
            IsRunning = false;
            EndBusy();
        }
    }

    // ================================================================
    // ② 설정 기반 번역·재조판 — 통합 설정 JSON을 전체적으로 읽어 재조판
    // ================================================================
    /// <summary>번역 엔진(GGUF)을 1회 적재하고(HoldAcrossPages=true) BatchProject의 전체 페이지를
    /// 순서대로 inpaint→translate→render한다. 펼친 장의 편집 내용도 커밋 후 읽어오므로,
    /// 개별 파일 편집 없이 설정 파일 하나로 전체가 결정된다(사용자 2026-10-28).</summary>
    public async Task TranslateFromConfigAsync(CancellationToken ct = default)
    {
        if (Queue.Count == 0) { _status("대기열이 비어 있습니다"); return; }
        EnsureProjectPath();
        // 편집기의 미커밋 편집(박스 추가/삭제/☑/이동 — 2026-10-28 배치 편집 지원)을 설정으로 수거
        // → 설정이 최종 진실원이 된다. NoImage라도 인스턴스가 있으면 사용자가 손으로 추가한 박스다.
        foreach (var e in Queue)
            if (e.Editor is { Step: AppStep.Detected } || e.Editor.Instances.Count > 0)
                Project.Upsert(e.Path, e.Editor.SnapshotRegions());
        CommitProject();

        // 설정에 없는 장이 있으면 ①인식을 앞세운다(설정=실행의 유일한 입력, 사용자 2026-10-28).
        // Regions가 빈 장은 합법(텍스트 없는 페이지) — '설정 부재'만 재스캔 대상.
        var missing = Queue.Count(q => Project.For(q.Path) == null);
        if (Project.Count == 0 || missing > 0)
        {
            _status(missing > 0 ? $"{missing}장 미인식 — ① 인식을 먼저 실행합니다" : "설정 없음 — ① 인식을 실행합니다");
            await ScanAllAsync(ct);
            if (Project.Count == 0) return;
        }

        var (o, i, t, r) = _keys();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeCts = cts;
        IsRunning = true;
        var snapshot = Queue.ToList();
        BeginBusy($"② 번역 엔진 로드… ({t})");
        // 먼저 번역 GGUF를 올린다(언제나 1회) — 실패 시 즉시 알림 후 중단.
        var pipe = _ctx.BuildPipeline(o, i, t, r);
        try
        {
            if (_ctx.GetTranslate(t) is ILoadableEngine le)
                await Task.Run(() => le.EnsureLoadedAsync(cts.Token), cts.Token);
        }
        catch (Exception ex)
        {
            pipe.ReleaseHeldEngines();
            _activeCts = null; IsRunning = false; EndBusy();
            _status($"번역 엔진 로드 실패: {ex.Message}");
            return;
        }
        pipe.HoldAcrossPages = true; // 루프 진입 — 장마다 unload 억제(엔진 1회 적재 유지)
        try
        {
            for (var n = 0; n < snapshot.Count; n++)
            {
                var entry = snapshot[n];
                cts.Token.ThrowIfCancellationRequested();
                entry.Status = "번역중"; entry.Progress = 0.1;
                BusyMessage = $"② 번역·재조판 {n + 1}/{snapshot.Count} — {entry.FileName}";
                var idx = n;
                try
                {
                    var pc = Project.For(entry.Path);
                    if (pc == null) throw new InvalidOperationException("설정에 이 파일의 페이지가 없습니다");
                    // ToRegion→ApplyAsync가 번역문을 region에 in-place 기입 → FromRegion으로 설정 회수.
                    // ☑ 필터는 pipeline 내부 — Include=false는 원본 보존으로 통과하며 설정에도 남는다.
                    var pageRegions = pc.Regions.Select(rc => rc.ToRegion()).ToList();
                    await entry.Editor.ApplyWithPipelineAsync(pipe, pageRegions, cts.Token);
                    pc.Regions = pageRegions.Select(RegionConfig.FromRegion).ToList();
                    if (entry.IsExpanded) entry.Editor.HydrateInstances(pc.Regions, applied: true);
                    entry.Progress = 1; entry.Status = "완료";
                    PumpBatch(idx + 1, 0);
                }
                catch (OperationCanceledException) { entry.Status = "취소"; throw; }
                catch (Exception ex)
                {
                    entry.Status = "실패"; entry.Progress = 1;
                    _status($"[{entry.FileName}] 실패: {ex.Message}");
                }
            }
            CommitProject();   // 실행 종료 — 정상 완료 시에만 커밋
            _status($"② 완료 — {Queue.Count(e => e.Status == "완료")}/{Queue.Count}장 번역·재조판");
        }
        catch (OperationCanceledException) { CommitProject(); _status("② 취소됨 — 생성된 만큼 저장"); }
        finally
        {
            pipe.ReleaseHeldEngines();   // 번역 GGUF·inpaint 한 번에 내림 (hold 종료)
            _activeCts = null;
            IsRunning = false;
            EndBusy();
        }
    }
}
