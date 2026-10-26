using MangaTranslator.App.Services;
using ReactiveUI;
using ReactiveUI.Primitives; // ReactiveUI 26: Unit → RxVoid

namespace MangaTranslator.App.ViewModels;

public enum AppMode { Single, Batch }

/// <summary>번역 엔진 선택 항목 — 키는 EngineRegistry 등록명과 동일.</summary>
public sealed record TranslateEngineOption(string Key, string Label);

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly EngineContext _ctx;

    private AppMode _mode = AppMode.Single;
    private string _statusText = "이미지를 열어보세요 — PP-OCR det + Qwen3-VL rec / LaMa / Qwen3 번역 / Skia 고정 연결";

    /// <summary>싱글 편집기 — 캔버스·편집 UI의 단일 진실 units(PageEditor).</summary>
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

    // 라디오 TwoWay 바인딩의 착지점: value=true일 때만 모드 전환(선택된 라디오만 true가 들어옴).
    // setter 없으면 클릭이 조용히 무시되어 배치 UI에 고착됨(버그 2026-10-25: 되돌아오지 않음).
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

    /// <summary>캔버스가 표시할 편집기: 모드=배치 && 펼친 항목이 있으면 배치编辑器, 아니면 싱글.
    /// 배치 Expander를 펼치면 그 페이지가 캔버스에 뜨고(instances도 편집 가능), 사용자 2026-10-25.</summary>
    public PageEditorViewModel? ActiveEditor
    {
        get
        {
            if (SelectedMode == AppMode.Batch)
            {
                var e = Batch.Queue.FirstOrDefault(q => q.IsExpanded)?.Editor;
                if (e != null) return e;
            }
            return Single;
        }
    }

    /// <summary>배치 활성 편집기 변경 → 캔버스 바인딩 다시 해석.</summary>
    public void RaiseActiveEditor() => this.RaisePropertyChanged(nameof(ActiveEditor));

    public string StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    // ---- 번역 엔진 런타임 선택(콤보) — EngineContext.TranslateKey에 반영, 파이프라인은 매 실행 시 라이브 읽기 ----
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

    // 파일 대화상자는 View가 주입(StorageProvider)
    public Func<Task<string[]>>? PickManyAsync { get; set; }
    public Func<Task<string?>>? PickSaveAsync { get; set; }

    // ---- 마법사 내비게이션 (싱글 모드 우측 패널) ----
    // 페이지: 1=열기 2=감지 3=편집 4=실행·결과. 캔버스는 항상 유지, 패널만 페이지 전환.
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

    /// <summary>과거에 도달한 최대 페이지 — 스텝칩 클릭 가능 범위 게이트.</summary>
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
    /// <summary>정삭 페이지 도달 가능 여부(스텝바 클릭 게이트) — 이미 방문했던 페이지는 자유 이동.</summary>
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

    public ReactiveCommand<RxVoid, RxVoid> OpenImageCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> SaveResultCommand { get; }

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
        Batch.ActiveEditorChanged += RaiseActiveEditor;

        // 서브 VM 진행 상태를 집계 → 전역 오버레이(취소 포함)가 소비
        Single.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PageEditorViewModel.IsBusy)
                or nameof(PageEditorViewModel.OverlayProgress)
                or nameof(PageEditorViewModel.OverlayMessage)) NotifyBusy();
            if (e.PropertyName == nameof(PageEditorViewModel.Step)) OnSingleStepChanged();
            if (e.PropertyName == nameof(PageEditorViewModel.PagePath) && Single.PagePath != null)
            {
                // 새 이미지 로드 → 자동 ②감지 페이지로 (max 리셋)
                _maxReachedPage = 1;
                WizardPage = 2;
            }
        };
        Batch.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BatchModeViewModel.IsRunning)
                or nameof(BatchModeViewModel.BusyProgress)
                or nameof(BatchModeViewModel.BusyMessage)) NotifyBusy();
        };

        // 다중 열기: 1장이면 싱글 편집 화면, 여러 장이면 첫 장은 싱글 + 나머지는 배치 대기열
        OpenImageCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (PickManyAsync == null) return;
            var paths = await PickManyAsync();
            if (paths == null || paths.Length == 0) return;
            await Single.LoadFromAsync(paths[0]);
            if (paths.Length > 1)
            {
                Batch.AddPaths(paths.Skip(1));
                SelectedMode = AppMode.Batch;
                setStatus($"이미지 {paths.Length}장 열림 — 배치 실행으로 연속 처리");
            }
        });

        SaveResultCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (PickSaveAsync == null) return;
            var path = await PickSaveAsync();
            if (path != null) Single.SaveResultTo(path);
        });

        Single.CanApplyChanged += () => { this.RaisePropertyChanged(nameof(CanApply)); RaiseNav(); };
        WireCancel();
        WireWizard();
    }

    // ---- 마법사 커맨드 배선 ----
    private void WireWizard()
    {
        BackCommand = ReactiveCommand.Create(() =>
        {
            if (!CanBack) return;
            WizardPage = _wizardPage - 1;
        });

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
                    // 감지 실행 → Step=Detected 이벤트가 자동 ③편집으로 전환
                    await Run(Single.DetectCommand);
                    break;
                case 3:
                    if (!CanApply) { StatusText = "☑된 말풍선이 있어야 다음으로 갈 수 있어요"; return; }
                    // 라벨대로 번역도 이어서 실행 — 페이지만 넘어가고 놀리는 UX 제거(사용자 피드백)
                    WizardPage = 4;
                    await Run(Single.ApplyCommand);
                    break;
            }
        });

        // AXAML의 CommandParameter="1"은 문자열로 전달된다(int 오토컨버전 없음 — 실측 예외) → 문자열 파싱.
        GoPageCommand = ReactiveCommand.Create<string>(p =>
        {
            if (!int.TryParse(p, out var page)) return;
            if (!CanJump(page)) return;
            WizardPage = page;
        });
    }

    /// <summary>ReactiveCommand.Execute()를 구독→Task로 감싼다. 실행 예외는 StatusText로 노출.</summary>
    private async Task Run(ReactiveCommand<RxVoid, RxVoid> cmd)
    {
        var tcs = new TaskCompletionSource();
        // 확장 Subscribe는 RxVoid extension과 BCL이 모호 → IObserver 인터페이스 직접 호출
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

    /// <summary>감지/적용 단계 변화 → 페이지 자동 전환(워즈 뒤로가기로 재편집 후 재감지 시에도 정상).</summary>
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

    /// <summary>Step2 버튼 활성 조건(UI용): ☑(Include)된 인스턴스가 하나라도 있으면.</summary>
    public bool CanApply => Single.Step == AppStep.Detected && Single.Instances.Any(i => i.Include);

    // ---- 전역 오버레이: 어느 모드든 작업 중이면 화면 전체(이미지+우측패널)에 진행률 ----
    public bool AnyBusy => Single.IsBusy || Batch.IsRunning;
    public string BusyMessage => Single.IsBusy ? Single.OverlayMessage : Batch.BusyMessage;
    public double BusyProgress => Single.IsBusy ? Single.OverlayProgress : Batch.BusyProgress;

    private void NotifyBusy()
    {
        this.RaisePropertyChanged(nameof(AnyBusy));
        this.RaisePropertyChanged(nameof(BusyMessage));
        this.RaisePropertyChanged(nameof(BusyProgress));
        RaiseNav(); // 진행 중/완료로 이전·다음 활성 상태 바뀜
        RaiseActiveEditor(); // 배치 펼침 상태도 진행 게이트에 재평가
    }

    public ReactiveCommand<RxVoid, RxVoid> CancelAllCommand { get; private set; } = null!;
    private void WireCancel() =>
        CancelAllCommand = ReactiveCommand.Create(() =>
        {
            // 활성 모드 기준 취소 — 배치 중이면 배치, 아니면 싱글
            if (Batch.IsRunning) Batch.CancelActive();
            else Single.CancelActive();
        }, this.WhenAnyValue(x => x.AnyBusy));
}
