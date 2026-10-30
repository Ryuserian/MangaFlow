using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MangaTranslator.App.Services;
using MangaTranslator.Core;
using MangaTranslator.Core.Imaging;
using MangaTranslator.Core.Interfaces;
using MangaTranslator.Core.Pipeline;
using MangaTranslator.Core.Batch;
using ReactiveUI;
using ReactiveUI.Primitives.Disposables;
using System.ComponentModel;
using System.Reactive.Linq;

namespace MangaTranslator.App.ViewModels;

public enum AppStep { NoImage, Detected, Applied }

/// <summary>
/// 페이지 단위 편집기 — 캔버스 오버레이·인스턴스 목록·감지/채우기/번역 실행의 단일 진실 units.
/// 싱글 모드는 이걸 하나 들고, 배치는 파일 수만큼 생성해 Expander 마다 붙인다(사용자 2026-10-25:
/// "현재 변경하는 UI를 패널 안 Expander로 두고 batch에서 여러 개 처리").
///懒(lazy) 디코딩: 펼쳐질 때(EnsurePageAsync)만 원본을 디코딩 — 대기열 400장 메모리 보호.
/// </summary>
public sealed class PageEditorViewModel : ViewModelBase
{
    private readonly EngineContext _ctx;
    private readonly Func<(string Ocr, string Inpaint, string Translate, string Render)> _keys;
    private readonly Action<string> _status;

    private ImageData? _page;
    private Bitmap? _display;
    private ImageData? _rendered;
    private string? _pagePath;
    private AppStep _step = AppStep.NoImage;
    private bool _dirty;
    private TextInstanceViewModel? _selected;

    public ObservableCollection<TextInstanceViewModel> Instances { get; } = new();

    // ---- 오버레이 진행(이 페이지 작업 중) ----
    private bool _isBusy;
    private double _overlayProgress;
    private string _overlayMessage = "";
    private double _busyCeiling = 0.9;
    private readonly DispatcherTimer _busyTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private CancellationTokenSource? _activeCts;

    public bool IsBusy { get => _isBusy; private set => this.RaiseAndSetIfChanged(ref _isBusy, value); }
    public double OverlayProgress { get => _overlayProgress; private set => this.RaiseAndSetIfChanged(ref _overlayProgress, value); }
    public string OverlayMessage { get => _overlayMessage; private set => this.RaiseAndSetIfChanged(ref _overlayMessage, value); }
    public string OverlayPercentText => $"{OverlayProgress:P0}";

    public Bitmap? Display { get => _display; private set => this.RaiseAndSetIfChanged(ref _display, value); }
    public string? PagePath { get => _pagePath; private set => this.RaiseAndSetIfChanged(ref _pagePath, value); }

    /// <summary>배치: 에디터 생성 시 파일만 부착(懒 — 펼쳐질 때 EnsureDetectedAsync가 디코딩+감지).</summary>
    public void AttachPath(string path) => PagePath = path;
    public string? ResultPath { get; private set; }

    public AppStep Step
    {
        get => _step;
        private set
        {
            this.RaiseAndSetIfChanged(ref _step, value);
            CanApplyChanged?.Invoke(this, EventArgs.Empty);
            this.RaisePropertyChanged(nameof(StageLabel));
        }
    }

    /// <summary>감지/편집됨 여부 — 배치 실행이 자동(detect 포함)으로 갈지 사용자 편집 반영 Apply로 갈지 분기.</summary>
    public bool Edited => _dirty;

    /// <summary>Expander 헤더 상태 뱃지용 한 어구.</summary>
    public string StageLabel => Step switch
    {
        AppStep.NoImage => "미감지",
        AppStep.Detected => Instances.Count > 0 ? $"감지 {Instances.Count}" : "감지 0",
        _ => "완료",
    };

    public event EventHandler? CanApplyChanged;
    /// <summary>감지/로드/해제 등 페이지 상태 변화 → 마법사 페이지 자동 전환(싱글 전용).</summary>
    public event EventHandler? PageStateChanged;

    public TextInstanceViewModel? SelectedInstance
    {
        get => _selected;
        set
        {
            this.RaiseAndSetIfChanged(ref _selected, value);
            foreach (var i in Instances) i.IsSelected = ReferenceEquals(i, value);
            this.RaisePropertyChanged(nameof(HasSelection));
            this.RaisePropertyChanged(nameof(SelectedBoxInfo));
        }
    }

    public bool HasSelection => SelectedInstance != null;
    public string SelectedBoxInfo => SelectedInstance is { } s
        ? $"({(int)s.Left},{(int)s.Top})–({(int)s.Right},{(int)s.Bottom})  {s.ReadingDirOption.Label}"
        : "";

    public ReactiveCommand<RxVoid, RxVoid> DetectCommand { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> ApplyCommand { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> RunAllCommand { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> AddInstanceCommand { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> DeleteSelectedCommand { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> SelectAllCommand { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> DeselectAllCommand { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; private set; } = null!;

    public PageEditorViewModel(EngineContext ctx,
        Func<(string, string, string, string)> keys, Action<string> status)
    {
        _ctx = ctx; _keys = keys; _status = status;

        this.WhenActivated((MultipleDisposable d) =>
        {
            InitializeCommands(d);
            InitializeEvents(d);
            InitializeProperties(d);
        });
        ActivateLifetime();
    }

    private void InitializeCommands(MultipleDisposable d)
    {
        AddInstanceCommand = ReactiveCommand.Create(() =>
        {
            if (_page == null) return;
            var vm = Link(new TextInstanceViewModel
            { Left = _page.Width * 0.1, Top = _page.Height * 0.1, Right = _page.Width * 0.3, Bottom = _page.Height * 0.18, Text = "새 문장" });
            Instances.Add(vm);
            SelectedInstance = vm;
            _dirty = true;
            CanApplyChanged?.Invoke(this, EventArgs.Empty);
            this.RaisePropertyChanged(nameof(StageLabel));
            _status($"인스턴스 {Instances.Count}개");
        });

        DeleteSelectedCommand = ReactiveCommand.Create(() =>
        {
            if (SelectedInstance is { } s)
            {
                Instances.Remove(s);
                s.Dispose();
                _dirty = true;
                CanApplyChanged?.Invoke(this, EventArgs.Empty);
                _status($"인스턴스 {Instances.Count}개");
            }
        }, WhenProp(nameof(SelectedInstance), () => SelectedInstance != null)).DisposeWith(d);

        SelectAllCommand = ReactiveCommand.Create(() =>
        {
            foreach (var i in Instances) i.Include = true;
            _dirty = true;
            CanApplyChanged?.Invoke(this, EventArgs.Empty);
            _status($"전체 선택 — {Instances.Count}개 처리 예정");
        });

        DeselectAllCommand = ReactiveCommand.Create(() =>
        {
            foreach (var i in Instances) i.Include = false;
            _dirty = true;
            CanApplyChanged?.Invoke(this, EventArgs.Empty);
            _status("전체 해제 — [번역 실행] 대상이 없습니다");
        });

        DetectCommand = ReactiveCommand.CreateFromTask(async ct =>
        {
            await EnsurePageAsync(ct);
            if (_page == null) { _status("먼저 이미지를 열어주세요"); return; }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeCts = cts;
            BeginBusy("일본어 텍스트 인식 중…\n(Qwen3-VL — 박스당 수 초)", 0.88);
            try
            {
                var pipeline = BuildPipeline();
                pipeline.HoldOcrLoaded = true; // Review 체류: 🔄 행 재인식 시 17GB cold 재적재(≈80초) 방지
                pipeline.Progress += p => PumpExternal(Math.Min(0.8, p.Percent), null);
                var regions = await Task.Run(() => pipeline.DetectAsync(_page, cts.Token).GetAwaiter().GetResult(), cts.Token);

                Instances.Clear();
                SelectedInstance = null;
                foreach (var r in regions) Instances.Add(Link(TextInstanceViewModel.FromRegion(r)));
                _dirty = true;
                Step = AppStep.Detected;
                PageStateChanged?.Invoke(this, EventArgs.Empty);
                _status($"검출 완료: {regions.Count}개 — ☑한 말풍선만 제거·번역됩니다");
            }
            catch (OperationCanceledException) { _status("감지 취소됨"); }
            catch (Exception ex) { _status($"감지 오류: {ex.Message}"); }
            finally { _activeCts = null; EndBusy(); }
        });

        ApplyCommand = ReactiveCommand.CreateFromTask(async ct =>
        {
            await EnsurePageAsync(ct);
            if (_page == null) { _status("먼저 이미지를 열어주세요"); return; }
            var input = Instances.Where(i => i.Include).ToList();
            if (input.Count == 0) { _status("처리할 말풍선이 없습니다 — ☑하세요"); return; }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeCts = cts;
            BeginBusy($"제거·번역·재조판 실행 중…\n(선택 {input.Count}/{Instances.Count})", 0.9);
            try
            {
                var result = await ApplyCoreAsync(input, cts.Token);
                _status($"적용 완료 — 출력: {ResultPath}");
            }
            catch (OperationCanceledException) { _status("실행 취소됨"); }
            catch (Exception ex) { _status($"실행 오류: {ex.Message}"); }
            finally { _activeCts = null; EndBusy(); }
        });

        RunAllCommand = ReactiveCommand.CreateFromTask(async ct =>
        {
            await EnsurePageAsync(ct);
            if (_page == null) { _status("먼저 이미지를 열어주세요"); return; }
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _activeCts = cts;
            BeginBusy("전체 자동 실행 중…\n(감지 → 제거 → 번역 → 재조판)", 0.92);
            try
            {
                var pipeline = BuildPipeline();
                pipeline.Progress += p => PumpExternal(Math.Min(0.97, p.Percent), null);
                var result = await Task.Run(() => pipeline.RunAsync(_page, "ja", "ko", cts.Token).GetAwaiter().GetResult(), cts.Token);
                Instances.Clear();
                foreach (var r in result.Regions) Instances.Add(Link(TextInstanceViewModel.FromRegion(r)));
                _dirty = true;
                Ingest(result);
                PageStateChanged?.Invoke(this, EventArgs.Empty);
                _status($"전체 완료 — 출력: {ResultPath}");
            }
            catch (OperationCanceledException) { _status("전체 실행 취소됨"); }
            catch (Exception ex) { _status($"실행 오류: {ex.Message}"); }
            finally { _activeCts = null; EndBusy(); }
        });

        CancelCommand = ReactiveCommand.Create(CancelActive, WhenProp(nameof(IsBusy), () => IsBusy)).DisposeWith(d);
    }

    private void InitializeEvents(MultipleDisposable d)
    {
        Observable.FromEventPattern<EventHandler, EventArgs>(
                h => _busyTimer.Tick += h,
                h => _busyTimer.Tick -= h)
            .Subscribe(_ =>
            {
                OverlayProgress = Math.Min(_busyCeiling,
                    OverlayProgress + Math.Max(0.004, (_busyCeiling - OverlayProgress) * 0.05));
                this.RaisePropertyChanged(nameof(OverlayPercentText));
            })
            .DisposeWith(d);
    }

    private void InitializeProperties(MultipleDisposable d)
    {
        this.WhenAnyValue(x => x.IsBusy)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(OverlayPercentText)))
            .DisposeWith(d);
    }

    /// <summary>메인 VM 취소 집계용 — 진행 중 CTS 직접 취소.</summary>
    public void CancelActive()
    {
        _activeCts?.Cancel();
        OverlayMessage = "취소 중…";
    }

    // ---- 핵심 실행 reuse: 배치 루프가 외부 진행 펌프와 함께 호출 ----
    private async Task<PipelineResult> ApplyCoreAsync(List<TextInstanceViewModel> input, CancellationToken ct)
    {
        var pipeline = BuildPipeline();
        pipeline.Progress += p => PumpExternal(Math.Min(0.97, p.Percent), null);
        var result = await Task.Run(() =>
            pipeline.ApplyAsync(_page!, input.Select(i => i.ToRegion()).ToList(), "ja", "ko", ct).GetAwaiter().GetResult(), ct);
        for (var i = 0; i < input.Count && i < result.Regions.Count; i++)
            input[i].TranslatedText = result.Regions[i].TranslatedText;
        Ingest(result);
        return result;
    }

    /// <summary>배치 루프가 부를 공개 Apply(편집된 인스턴스 반영). 외부 펌프 주입 가능.</summary>
    public Task<PipelineResult> ApplyEditedAsync(Action<double, string?>? progress, CancellationToken ct)
    {
        var input = Instances.Where(i => i.Include).ToList();
        if (_page == null) throw new InvalidOperationException("page not loaded");
        if (input.Count == 0) throw new InvalidOperationException("☑된 말풍선 없음");
        return ApplyCoreExternalAsync(input, progress, ct);
    }

    private async Task<PipelineResult> ApplyCoreExternalAsync(List<TextInstanceViewModel> input, Action<double, string?>? progress, CancellationToken ct)
    {
        var pipeline = BuildPipeline();
        pipeline.Progress += p => { PumpExternal(Math.Min(0.97, p.Percent), null); progress?.Invoke(p.Percent, p.Message); };
        var result = await Task.Run(() =>
            pipeline.ApplyAsync(_page!, input.Select(i => i.ToRegion()).ToList(), "ja", "ko", ct).GetAwaiter().GetResult(), ct);
        for (var i = 0; i < input.Count && i < result.Regions.Count; i++)
            input[i].TranslatedText = result.Regions[i].TranslatedText;
        Ingest(result);
        return result;
    }

    /// <summary>배치 루프용 자동 실행(감지 포함 전 자동).</summary>
    public async Task<PipelineResult> RunAutoAsync(Action<double, string?>? progress, CancellationToken ct)
    {
        if (_page == null) throw new InvalidOperationException("page not loaded");
        var pipeline = BuildPipeline();
        pipeline.Progress += p => { PumpExternal(Math.Min(0.97, p.Percent), null); progress?.Invoke(p.Percent, p.Message); };
        var result = await Task.Run(() => pipeline.RunAsync(_page, "ja", "ko", ct).GetAwaiter().GetResult(), ct);
        Instances.Clear();
        foreach (var r in result.Regions) Instances.Add(Link(TextInstanceViewModel.FromRegion(r)));
        Ingest(result);
        return result;
    }

    // ---- 페이지 디코딩(懒) ----
    /// <summary>배치 루프가 부를 공개 Apply(편집된 인스턴스 반영). ☑이 0개면 '전면 보존' 의도로 간주 —
    /// 박스 0개로 Apply(원본 클론 출력)한다. 예외로 터뜨려 자동 Run으로 덮어쓰면 사용자 ☐해제가 무효화된다.</summary>
    public Task<PipelineResult> ApplyEditedAsyncSafe(Action<double, string?>? progress, CancellationToken ct)
    {
        if (_page == null) return Task.FromException<PipelineResult>(new InvalidOperationException("page not loaded"));
        var input = Instances.Where(i => i.Include).ToList();
        return ApplyCoreExternalAsync(input, progress, ct);
    }

    /// <summary>배치 Expander 펼침: 페이지 로드 + 아직 미감지면 감지 실행(편집 가능하게). 외부 진행 없이 자체 오버레이.</summary>
    public async Task EnsureDetectedAsync(CancellationToken ct = default)
    {
        if (Step != AppStep.NoImage) return;
        if (_page == null) { _status("페이지 불러오는 중…"); }
        await EnsurePageAsync(ct);
        if (_page == null) return;
        BeginBusy(" 텍스트 감지 중…", 0.88);
        try
        {
            var pipeline = BuildPipeline();
            pipeline.HoldOcrLoaded = true; // Edit 진입 감지 — 🔄 warm (Apply 시점 unload)
            pipeline.Progress += p => PumpExternal(Math.Min(0.8, p.Percent), null);
            var regions = await Task.Run(() => pipeline.DetectAsync(_page, ct).GetAwaiter().GetResult(), ct);
            Instances.Clear();
            SelectedInstance = null;
            foreach (var r in regions) Instances.Add(Link(TextInstanceViewModel.FromRegion(r)));
            _dirty = true; // 편집된 간주 — 이후 실행은 사용자 편집(☑/박스)을 존중해 Apply 경로
            Step = AppStep.Detected;
            PageStateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status($"감지 오류: {ex.Message}"); }
        finally { EndBusy(); }
    }

    /// <summary>원본 디코딩 보장(이미 디코딩됨이면 no-op). 배치: 펼칠 때/실행 직전 호출.</summary>
    public async Task EnsurePageAsync(CancellationToken ct = default)
    {
        if (_page != null || PagePath == null) return;
        var path = PagePath;
        var data = await Task.Run(() => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? ImageBridge.FromBitmap(new Bitmap(path))
            : Smoke.ImageIO.Decode(path), ct);
        _page = data;
        Dispatcher.UIThread.Post(() =>
        {
            Display = ImageBridge.ToBitmap(data);
            this.RaisePropertyChanged(nameof(StageLabel));
        });
    }

    /// <summary>折叠(접힘) 시 원본 풀 비트맵만 회수 — 편집/진행 데이터는 유지. 재접근 시 EnsurePageAsync가 재디코딩.</summary>
    public void ReleasePage() => _page = null;

    protected override void DisposeCore()
    {
        _busyTimer.Stop();
        CancelActive();
        foreach (IDisposable? c in new IDisposable?[]
                 { DetectCommand, ApplyCommand, RunAllCommand, AddInstanceCommand, DeleteSelectedCommand, SelectAllCommand, DeselectAllCommand, CancelCommand })
            c?.Dispose();
        foreach (var i in Instances) i.Dispose();
        Instances.Clear();
        SelectedInstance = null;
        ReleasePage();
        Display = null;
    }

    /// <summary>배치 자동 실행이 부를 공개 엔트리포인트(page 로드 포함).</summary>
    public async Task AutoRunAsync(Action<double, string?>? progress, CancellationToken ct)
    {
        await EnsurePageAsync(ct);
        await RunAutoAsync(progress, ct);
    }

    // ---- 통합 설정(BatchProject) 브리지(2026-10-28 사용자 결정) ----
    // 배치 ①인식 결과는 편집기 인스턴스를 거쳐 통합 설정에 모이고, ②실행은 설정만 읽어
    // 호출자가 건네는 공유 파이프라인(모델 1회 적재·전 대기열 상주)으로 재조판한다.

    /// <summary>현재 편집 인스턴스 → 통합 설정 스냅샷(☑/박스/방향/말투/크기 knob 포함).</summary>
    public List<RegionConfig> SnapshotRegions() =>
        Instances.Select(i => RegionConfig.FromRegion(i.ToRegion())).ToList();

    /// <summary>공유 파이프라인으로 이 장만 detect(배치 ①단계 — 엔진은 호출자 소유).</summary>
    public async Task<IReadOnlyList<TextRegion>> DetectWithPipelineAsync(TranslationPipeline pipe, CancellationToken ct = default)
    {
        await EnsurePageAsync(ct);
        if (_page == null) throw new InvalidOperationException("page not loaded");
        return await Task.Run(() => pipe.DetectAsync(_page, ct).GetAwaiter().GetResult(), ct);
    }

    /// <summary>통합 설정 인스턴스 → 하이드레이트(캔버스 편집 가능 상태로). applied=true면 실행 후 표시.</summary>
    public void HydrateInstances(IEnumerable<RegionConfig> cfgs, bool applied = false)
    {
        Instances.Clear();
        SelectedInstance = null;
        foreach (var c in cfgs) Instances.Add(Link(TextInstanceViewModel.FromConfig(c)));
        _dirty = false;
        Step = applied ? AppStep.Applied : AppStep.Detected;
        this.RaisePropertyChanged(nameof(StageLabel));
    }

    /// <summary>설정 기반 재조판: 공유 파이프라인(호출자가 hold 설정)으로 fill+translate+render 1회.</summary>
    public async Task<PipelineResult> ApplyWithPipelineAsync(TranslationPipeline pipe,
        IReadOnlyList<TextRegion> regions, CancellationToken ct = default)
    {
        if (_page == null) throw new InvalidOperationException("page not loaded");
        var result = await Task.Run(() =>
            pipe.ApplyAsync(_page, regions, "ja", "ko", ct).GetAwaiter().GetResult(), ct);
        Ingest(result);
        return result;
    }

    public async Task LoadFromAsync(string path)
    {
        PagePath = path;
        _page = null;
        Instances.Clear();
        SelectedInstance = null;
        ResultPath = null;
        _rendered = null;
        _dirty = false;
        Step = AppStep.NoImage;
        await EnsurePageAsync();
        _status($"열림: {System.IO.Path.GetFileName(path)}  {_page?.Width}×{_page?.Height}");
    }

    /// <summary>이미지 열림/감지/완료 전환 → 메인 VM(마법사) 구독.</summary>
    public void NotifyPageChanged() => PageStateChanged?.Invoke(this, EventArgs.Empty);

    private void Ingest(PipelineResult result)
    {
        _rendered = result.RenderedPage;
        ResultPath = System.IO.Path.Combine(_ctx.OutputDir,
            System.IO.Path.GetFileNameWithoutExtension(PagePath ?? "page") + "_ko.png");
        PngCodec.Encode(result.RenderedPage, ResultPath);
        Dispatcher.UIThread.Post(() =>
        {
            Display = ImageBridge.ToBitmap(result.RenderedPage);
            Step = AppStep.Applied;
        });
    }

    public void SaveResultTo(string path)
    {
        if (_rendered != null) { PngCodec.Encode(_rendered, path); _status($"저장: {path}"); }
    }

    private TranslationPipeline BuildPipeline()
    {
        var (o, i, t, r) = _keys();
        return _ctx.BuildPipeline(o, i, t, r);
    }

    /// <summary>인스턴스 생성 후 소유 편집기 연결 — 구독은 자식 수명(v.Lifetime)에 맞춘다.</summary>
    private TextInstanceViewModel Link(TextInstanceViewModel v)
    {
        Observable.FromEventPattern<EventHandler, EventArgs>(
                h => v.RequestRemove += h, h => v.RequestRemove -= h)
            .Subscribe(ev =>
            {
                Instances.Remove((TextInstanceViewModel)ev.Sender!);
                _dirty = true; CanApplyChanged?.Invoke(this, EventArgs.Empty);
            })
            .DisposeWith(v.Lifetime);

        Observable.FromEventPattern<EventHandler, EventArgs>(
                h => v.RequestSelect += h, h => v.RequestSelect -= h)
            .Subscribe(ev => SelectedInstance = (TextInstanceViewModel)ev.Sender!)
            .DisposeWith(v.Lifetime);

        Observable.FromEventPattern<EventHandler, EventArgs>(
                h => v.RequestReocr += h, h => v.RequestReocr -= h)
            .Subscribe(ev => ExecuteReocrAsync((TextInstanceViewModel)ev.Sender!))
            .DisposeWith(v.Lifetime);

        Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => v.PropertyChanged += h, h => v.PropertyChanged -= h)
            .Where(ev => ev.EventArgs.PropertyName == nameof(TextInstanceViewModel.Include))
            .Subscribe(_ => { _dirty = true; CanApplyChanged?.Invoke(this, EventArgs.Empty); })
            .DisposeWith(v.Lifetime);

        return v;
    }

    /// <summary>인스턴스 행 🔄 버튼: 이 박스 하나만 OCR 재판독(도구 모음 공용 경로와 동일 엔진 경로).</summary>
    private bool _reocrRunning;

    private async void ExecuteReocrAsync(TextInstanceViewModel sel)
    {
        if (sel == null) return;
        if (_reocrRunning) { _status("재인식 진행 중… 완료까지 잠시만 기다려주세요"); return; }
        if (_page == null) { _status("이미지 로딩 중… 잠시 후 다시 시도하세요"); await EnsurePageAsync(); }
        if (_page == null) { _status("먼저 이미지를 열어주세요"); return; }
        var rect = new BoundingBox((int)sel.Left, (int)sel.Top, (int)sel.Right, (int)sel.Bottom);
        var ocr = _ctx.GetOcr(_keys().Ocr);
        var needReload = ocr is IUnloadable ul && !ul.IsLoaded;
        BeginBusy(needReload
            ? "OCR 모델 다시 얹는 중…\n(최대 수 분 걸릴 수 있어요)\n완료 후 선택 영역을 재인식합니다"
            : $"선택 영역 OCR 재인식 중…\n({(int)rect.Width}×{(int)rect.Height})", 0.95);
        _reocrRunning = true;
        try
        {
            string text; double score; bool vertical;
            if (ocr is Plugins.Ocr.VlmOcr.VlmOcrEngine mo && _page != null)
            {
                (text, score, vertical) = await Task.Run(() => mo.RecognizeBoxAsync(_page, rect));
            }
            else
            {
                text = ""; score = 0; vertical = sel.Vertical;
                var regions = await ocr.RecognizeAsync(_page);
                var hit = regions.FirstOrDefault(r =>
                    r.Box.Left < rect.Right && r.Box.Right > rect.Left &&
                    r.Box.Top < rect.Bottom && r.Box.Bottom > rect.Top);
                if (hit != null) { text = hit.Text; vertical = hit.Vertical; score = hit.Confidence * 2 - 1; }
            }
            if (string.IsNullOrWhiteSpace(text))
                _status("재인식 결과 빈 문자열 — 박스를 조정해 다시 시도하세요");
            else
            {
                sel.Text = text.Replace("\n", "");
                sel.Vertical = vertical;
                sel.Confidence = (float)Math.Clamp(0.5 * (1.0 + score), 0.05, 0.99);
                _dirty = true;
                _status($"재인식 완료: {sel.Text}");
            }
        }
        catch (Exception ex) { _status($"재인식 오류: {ex.Message}"); }
        finally { _reocrRunning = false; EndBusy(); }
    }

    // ---- 오버레이 헬퍼 ----
    // tick 진행 펌프 자체는 InitializeEvents의 FromEventPattern 구독이 담당 — 여기서는 ceiling/메시지/시작만.
    private void BeginBusy(string msg, double ceiling)
    {
        _busyCeiling = ceiling;
        OverlayMessage = msg;
        OverlayProgress = 0.02;
        IsBusy = true;
        _busyTimer.Start();
    }

    /// <summary>파이프라인/배치 진행 이벤트 → 오버레이 반영(전역 메인 오버레이도 이 값을 소비).</summary>
    public void PumpExternal(double pct, string? msg)
    {
        if (pct > OverlayProgress) OverlayProgress = pct;
        if (!string.IsNullOrEmpty(msg)) OverlayMessage = msg;
        this.RaisePropertyChanged(nameof(OverlayPercentText));
    }

    private void EndBusy()
    {
        _busyTimer?.Stop();
        OverlayProgress = 1.0;
        IsBusy = false;
    }
}
