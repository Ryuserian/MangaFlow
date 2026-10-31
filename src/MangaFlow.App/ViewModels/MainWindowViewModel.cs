using MangaFlow.App.Services;
using ReactiveUI;
using ReactiveUI.Primitives.Disposables;
using System.ComponentModel;
using System.Reactive.Linq;

namespace MangaFlow.App.ViewModels;

public enum AppMode { Single, Batch }

public sealed record TranslateEngineOption(string Key, string Label);

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly EngineContext _ctx;

    private AppMode _mode = AppMode.Single;
    private string _statusText = "이미지를 열어보세요 — PP-OCR det + Qwen3-VL rec / LaMa / Qwen3 번역 / Skia 고정 연결";

    public PageEditorViewModel Single { get; }
    public BatchModeViewModel Batch { get; }

    public AppMode SelectedMode
    {
        get => _mode;
        set
        {
            this.RaiseAndSetIfChanged(ref _mode, value);
            this.RaisePropertyChanged(nameof(IsSingle));
            this.RaisePropertyChanged(nameof(IsBatch));
            this.RaisePropertyChanged(nameof(ActiveEditor));
        }
    }

    public bool IsSingle
    {
        get => SelectedMode == AppMode.Single;
        set { if (value) SelectedMode = AppMode.Single; }
    }
    public bool IsBatch
    {
        get => SelectedMode == AppMode.Batch;
        set { if (value) SelectedMode = AppMode.Batch; }
    }

    public PageEditorViewModel? ActiveEditor
    {
        get
        {
            if (SelectedMode == AppMode.Batch)
            {
                if (Batch.SelectedEntry is { } s) return s.Editor;
                var e = Batch.Queue.FirstOrDefault(q => q.IsExpanded)?.Editor;
                if (e != null) return e;
            }
            return Single;
        }
    }

    public void RaiseActiveEditor() => this.RaisePropertyChanged(nameof(ActiveEditor));

    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public IReadOnlyList<TranslateEngineOption> TranslateEngineOptions { get; } = new[]
    {
        new TranslateEngineOption("qwen_gguf", "Qwen3 GGUF (로컬·무료)"),
        new TranslateEngineOption("web_google", "Google 번역 (웹·무료·最速)"),
        new TranslateEngineOption("web_deepl", "DeepL (웹·DEEPL_API_KEY)"),
        new TranslateEngineOption("web_openai", "GPT/OpenAI (웹·OPENAI_API_KEY)"),
    };

    private TranslateEngineOption _selectedTranslateEngine;
    public TranslateEngineOption SelectedTranslateEngine
    {
        get => _selectedTranslateEngine;
        set
        {
            if (value == null || value.Key == _selectedTranslateEngine.Key) return;
            this.RaiseAndSetIfChanged(ref _selectedTranslateEngine, value);
            EngineContext.TranslateKey = value.Key;
            StatusText = $"번역 엔진 → {value.Label}";
        }
    }

    public Func<Task<string[]>>? PickManyAsync { get; set; }
    public Func<Task<string?>>? PickSaveAsync { get; set; }

    public static readonly string[] WizardStepNames = { "열기", "감지", "편집", "실행·결과" };
    private int _wizardPage = 1;
    private int _maxReachedPage = 1;
    public int WizardPage
    {
        get => _wizardPage;
        private set
        {
            if (value < 1 || value > 4) return;
            if (value == _wizardPage) { RaiseNav(); return; }
            this.RaiseAndSetIfChanged(ref _wizardPage, value);
            _maxReachedPage = Math.Max(_maxReachedPage, value);
            this.RaisePropertyChanged(nameof(MaxReachedPage));
            RaiseNav();
        }
    }

    public int MaxReachedPage => _maxReachedPage;

    public bool CanBack => _wizardPage > 1 && !AnyBusy;
    public bool CanNext => WizardPage switch
    {
        1 => Single.PagePath != null,
        2 => Single.PagePath != null && !AnyBusy,
        3 => CanApply && !AnyBusy,
        _ => false,
    };
    public string WizardNextLabel => WizardPage switch
    {
        1 => "다음 ▶",
        2 => "▶  감지 실행",
        3 => "다음: 번역 실행 ▶",
        _ => "✓ 완료 — 저장하세요",
    };
    public bool CanJump(int page) => page >= 1 && page <= _maxReachedPage && !AnyBusy;

    private void RaiseNav()
    {
        this.RaisePropertyChanged(nameof(CanBack));
        this.RaisePropertyChanged(nameof(CanNext));
        this.RaisePropertyChanged(nameof(WizardNextLabel));
    }

    public ReactiveCommand<RxVoid, RxVoid> BackCommand { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> NextCommand { get; private set; } = null!;
    public ReactiveCommand<string, RxVoid> GoPageCommand { get; private set; } = null!;

    public ReactiveCommand<RxVoid, RxVoid> OpenImageCommand { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> SaveResultCommand { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> CancelAllCommand { get; private set; } = null!;

    public MainWindowViewModel(EngineContext ctx)
    {
        _ctx = ctx;
        _selectedTranslateEngine = TranslateEngineOptions
            .FirstOrDefault(o => o.Key == EngineContext.TranslateKey) ?? TranslateEngineOptions[0];
        // 엔진 스택 — 번역만 런타임 교체 가능(콤보박스/env). keys 람다가 매 실행 시 살아있는 값을 읽는다.
        var keys = () => (EngineContext.OcrKey, EngineContext.InpaintKey,
                          EngineContext.TranslateKey, EngineContext.RenderKey);
        Action<string> setStatus = s => StatusText = s;

        Single = new PageEditorViewModel(ctx, keys, setStatus);
        Batch = new BatchModeViewModel(ctx, keys, setStatus);

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
        OpenImageCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (PickManyAsync == null) return;
            var paths = await PickManyAsync();
            if (paths == null || paths.Length == 0) return;
            await Single.LoadFromAsync(paths[0]);
            if (paths.Length > 1)
            {
                Batch.AddPaths(paths);
                SelectedMode = AppMode.Batch;
                StatusText = $"이미지 {paths.Length}장 열림 — 모두 대기열, 배치 실행으로 연속 처리";
            }
        }).DisposeWith(d);

        SaveResultCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (PickSaveAsync == null) return;
            var path = await PickSaveAsync();
            if (path != null) Single.SaveResultTo(path);
        }).DisposeWith(d);

        BackCommand = ReactiveCommand.Create(() =>
        {
            if (!CanBack) return;
            WizardPage = _wizardPage - 1;
        }).DisposeWith(d);

        // Avalonia 버튼은 자신이 바인딩한 커맨드의 Execute()만 구독한다.
        // 코드에서 다른 ReactiveCommand의 Execute()를 호출하면 IObservable이 구독 없이 dispose되어
        // "눌렀는데 아무 동작 없음"이 된다(실측). → Run() 헬퍼로 Task화해 끝까지 구독 보장.
        NextCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            switch (WizardPage)
            {
                case 1:
                    if (Single.PagePath == null) await Run(OpenImageCommand);
                    else WizardPage = 2;
                    break;
                case 2:
                    if (Single.PagePath == null) { StatusText = "먼저 이미지를 열어주세요"; return; }
                    await Run(Single.DetectCommand);
                    break;
                case 3:
                    if (!CanApply) { StatusText = "☑된 말풍선이 있어야 다음으로 갈 수 있어요"; return; }
                    WizardPage = 4;
                    await Run(Single.ApplyCommand);
                    break;
            }
        }).DisposeWith(d);

        // AXAML의 CommandParameter="1"은 문자열로 전달된다(int 오토컨버전 없음 — 실측 예외) → 문자열 파싱.
        GoPageCommand = ReactiveCommand.Create<string>(p =>
        {
            if (!int.TryParse(p, out var page)) return;
            if (!CanJump(page)) return;
            WizardPage = page;
        }).DisposeWith(d);

        CancelAllCommand = ReactiveCommand.Create(() =>
        {
            if (Batch.IsRunning) Batch.CancelActive();
            else Single.CancelActive();
        }, WhenProp(nameof(AnyBusy), () => AnyBusy)).DisposeWith(d);
    }

    private void InitializeEvents(MultipleDisposable d)
    {
        Observable.FromEventPattern(
                h => Batch.ActiveEditorChanged += h,
                h => Batch.ActiveEditorChanged -= h)
            .Subscribe(_ => RaiseActiveEditor())
            .DisposeWith(d);

        Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => Single.PropertyChanged += h,
                h => Single.PropertyChanged -= h)
            .Subscribe(ev =>
            {
                var n = ev.EventArgs.PropertyName;
                if (n is nameof(PageEditorViewModel.IsBusy)
                    or nameof(PageEditorViewModel.OverlayProgress)
                    or nameof(PageEditorViewModel.OverlayMessage)) NotifyBusy();
                if (n == nameof(PageEditorViewModel.Step)) OnSingleStepChanged();
                if (n == nameof(PageEditorViewModel.PagePath) && Single.PagePath != null)
                {
                    _maxReachedPage = 1;
                    WizardPage = 2;
                }
            })
            .DisposeWith(d);

        Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => Batch.PropertyChanged += h,
                h => Batch.PropertyChanged -= h)
            .Where(ev => ev.EventArgs.PropertyName is nameof(BatchModeViewModel.IsRunning)
                                         or nameof(BatchModeViewModel.BusyProgress)
                                         or nameof(BatchModeViewModel.BusyMessage))
            .Subscribe(_ => NotifyBusy())
            .DisposeWith(d);

        Observable.FromEventPattern(
                h => Single.CanApplyChanged += h,
                h => Single.CanApplyChanged -= h)
            .Subscribe(_ => { this.RaisePropertyChanged(nameof(CanApply)); RaiseNav(); })
            .DisposeWith(d);

        Observable.FromEventPattern(
                h => Single.PageStateChanged += h,
                h => Single.PageStateChanged -= h)
            .Subscribe(_ => OnSingleStepChanged())
            .DisposeWith(d);
    }

    private void InitializeProperties(MultipleDisposable d)
    {
        WhenProp(nameof(AnyBusy), () => AnyBusy)
            .Subscribe(_ => RaiseNav())
            .DisposeWith(d);

        WhenProp(nameof(SelectedMode), () => SelectedMode)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(ActiveEditor)))
            .DisposeWith(d);
    }

    private async Task Run(ReactiveCommand<RxVoid, RxVoid> cmd)
    {
        var tcs = new TaskCompletionSource();
        cmd.Execute().Subscribe(new ObserverSink(tcs, ex => StatusText = $"오류: {ex.Message}"));
        try { await tcs.Task; }
        catch { /* 취소 등 — 자식 커맨드가 StatusText에 남김 */ }
    }

    private sealed class ObserverSink : IObserver<RxVoid>
    {
        private readonly TaskCompletionSource _tcs;
        private readonly Action<Exception> _onError;
        public ObserverSink(TaskCompletionSource tcs, Action<Exception> onError)
        {
            _tcs = tcs; _onError = onError;
        }
        public void OnCompleted() => _tcs.TrySetResult();
        public void OnError(Exception error) { _onError(error); _tcs.TrySetResult(); }
        public void OnNext(RxVoid value) { }
    }

    private void OnSingleStepChanged()
    {
        switch (Single.Step)
        {
            case AppStep.Detected: WizardPage = 3; break;
            case AppStep.Applied: WizardPage = 4; break;
            case AppStep.NoImage when Single.PagePath == null:
                _maxReachedPage = 1; WizardPage = 1; break;
        }
        RaiseNav();
    }

    public bool CanApply => Single.Step == AppStep.Detected && Single.Instances.Any(i => i.Include);

    public bool AnyBusy => Single.IsBusy || Batch.IsRunning;
    public string BusyMessage => Single.IsBusy ? Single.OverlayMessage : Batch.BusyMessage;
    public double BusyProgress => Single.IsBusy ? Single.OverlayProgress : Batch.BusyProgress;

    private void NotifyBusy()
    {
        this.RaisePropertyChanged(nameof(AnyBusy));
        this.RaisePropertyChanged(nameof(BusyMessage));
        this.RaisePropertyChanged(nameof(BusyProgress));
        RaiseNav();
        RaiseActiveEditor();
    }

    protected override void DisposeCore()
    {
        Single.Dispose();
        Batch.Dispose();
    }
}
