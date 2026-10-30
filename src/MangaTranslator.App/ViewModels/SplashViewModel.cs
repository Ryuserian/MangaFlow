using System.Diagnostics;
using Avalonia.Threading;
using MangaTranslator.App.Services;
using MangaTranslator.ModelManager;
using ReactiveUI;
using ReactiveUI.Primitives.Disposables; // WhenActivated 블록 파라미터 MultipleDisposable
using System.Reactive.Linq;

namespace MangaTranslator.App.ViewModels;

/// <summary>
/// 스플래시 VM — 앱 부팅 시 ①카탈로그 감사(필수분만: Optional 제외·번역 Group 중 하나 충족) →
/// 모델이 없으면 다운로드 UI(선택) → ②엔진(ONNX·GGUF·폰트) 백그라운드 로드 → Ready.
/// GitHub 공개 빌드(models/ 비어있음) 대응: 감사에서 빈 항목+총 용량을 보여주고
/// [⬇ 모델 다운로드]로 HF에서 자동 확보(이어받기·sha256 검증은 ModelDownloader).
/// ✕ = 앱 종료(CloseRequested → App.Shutdown). 건너뛰기는 웹 번역 폴백으로 메인 진입.
/// </summary>
public sealed class SplashViewModel : ViewModelBase
{
    private double _progress;
    private string _message = "모델 저장소 점검 중…";
    private string _elapsedText = "";
    private readonly DispatcherTimer? _timer;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly CancellationTokenSource _cts = new();
    private int _ready; // 0/1 — 이중 발동 방지

    /// <summary>전 스테이지 종료(성패 무관 — ✕ 종료는 제외). App이 메인 창으로 전환.</summary>
    public event EventHandler? Ready;
    /// <summary>✕ 누름 — 진행 스테이지 취소 후 앱 종료 요청. App이 Shutdown 처리.</summary>
    public event EventHandler? CloseRequested;

    public double Progress { get => _progress; private set => this.RaiseAndSetIfChanged(ref _progress, value); }
    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }
    public string ElapsedText { get => _elapsedText; private set => this.RaiseAndSetIfChanged(ref _elapsedText, value); }

    // ---- 다운로드 스테이지 상태 ----
    private bool _needsModels;
    private bool _downloading;
    private string _missingSummary = "";
    private ModelAudit? _audit;

    /// <summary>드롭다운 항목(카탈로그 Spec + 사용자 노출 라벨).</summary>
    public sealed record ChoiceItem(ModelSpec Spec, string Label)
    {
        public override string ToString() => Label;
    }

    private IReadOnlyList<ChoiceItem> _translateChoices = Array.Empty<ChoiceItem>();
    private ChoiceItem? _translateChoice;
    private bool _hasTranslateChoice;

    /// <summary>true면 다운로드 선택 화면(감사에서 모델 부족 발견, 아직 안 받음).</summary>
    public bool NeedsModels { get => _needsModels; private set => this.RaiseAndSetIfChanged(ref _needsModels, value); }
    public bool IsDownloading { get => _downloading; private set => this.RaiseAndSetIfChanged(ref _downloading, value); }
    public string MissingSummary { get => _missingSummary; private set => this.RaiseAndSetIfChanged(ref _missingSummary, value); }

    /// <summary>번역 모델 선택지(qwen3 Group 미보유 시) — 카탈로그 순서=기본순위(32B 먼저).</summary>
    public IReadOnlyList<ChoiceItem> TranslateChoices
    {
        get => _translateChoices;
        private set { _translateChoices = value; this.RaisePropertyChanged(nameof(TranslateChoices)); }
    }
    public ChoiceItem? TranslateChoice { get => _translateChoice; set => this.RaiseAndSetIfChanged(ref _translateChoice, value); }
    /// <summary>선택지 유무 — ComboBox 표시 제어.</summary>
    public bool HasTranslateChoice { get => _hasTranslateChoice; private set => this.RaiseAndSetIfChanged(ref _hasTranslateChoice, value); }

    public ReactiveCommand<RxVoid, RxVoid> DownloadCommand { get; private set; } = null!;
    public ReactiveCommand<RxVoid, RxVoid> SkipCommand { get; private set; } = null!;
    /// <summary>✕ = 앱 종료: 진행 중이면 스테이지 중단 후 즉시 종료(메인 창 열지 않음).</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; private set; } = null!;

    private readonly EngineContext _ctx;

    public SplashViewModel(EngineContext ctx)
    {
        _ctx = ctx;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };

        this.WhenActivated((MultipleDisposable d) =>
        {
            InitializeCommands(d);
            InitializeEvents(d);
        });
        ActivateLifetime();

        _timer.Start();
        _ = AuditAsync(ctx);
    }

    private void InitializeCommands(MultipleDisposable d)
    {
        DownloadCommand = ReactiveCommand.CreateFromTask(DownloadAndPreloadAsync).DisposeWith(d);
        // Skip ≠ 모델 전부 생략(사용자 2026-10-25: "웹 번역만"은 성립 불가 — OCR/LaMa 필수).
        // 스킵의 실체 = 번역 GGUF(32B/14B)만 빼고 필수(OCR·LaMa·폰트)는 받고, 번역 엔진은 web_google 폴백.
        SkipCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (_audit == null) { FireReady(); return; }
            NeedsModels = false;
            IsDownloading = true;
            var items = _audit.Missing.Where(m => m.Spec.Group == null).ToList();
            var prog = new Progress<(double Percent, string Msg)>(v => { Progress = v.Percent * 0.9; Message = v.Msg; });
            try { await ModelDownloader.DownloadAllAsync(_ctx.Models, items, prog, _cts.Token); }
            catch (OperationCanceledException) { Message = "✕ 취소 — 이어받기 유지"; }
            catch (Exception ex) { Message = "⚠ 다운로드 실패: " + ex.Message; }
            finally { IsDownloading = false; }
            EngineContext.TranslateKey = "web_google";
            Message = "OCR/LaMa 확보 완료 — 번역은 웹 폴백으로 메인 진입";
            await RunAsync(_ctx);
        }).DisposeWith(d);
        CancelCommand = ReactiveCommand.Create(() =>
        {
            _cts.Cancel();
            _timer?.Stop();
            Message = "✕ 종료합니다…";
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }).DisposeWith(d);
    }

    private void InitializeEvents(MultipleDisposable d)
    {
        if (_timer == null) return;
        Observable.FromEventPattern<EventHandler, EventArgs>(
                h => _timer.Tick += h,
                h => _timer.Tick -= h)
            .Subscribe(_ => ElapsedText = $"{_sw.Elapsed.TotalSeconds:0}초")
            .DisposeWith(d);
    }

    /// <summary>① 감사: 카탈로그 대비 없는 모델+총 용량. 충분하면 바로 엔진 로드.</summary>
    private async Task AuditAsync(EngineContext ctx)
    {
        try
        {
            _audit = await ModelDownloader.AuditAsync(ctx.Models, _cts.Token);
        }
        catch (OperationCanceledException) { return; } // ✕ 종료 — App.Shutdown 진행 중
        catch (Exception ex)
        {
            Message = "⚠ 저장소 점검 실패: " + ex.Message;
            await PreloadAsync(ctx);
            return;
        }

        if (_audit.Missing.Count == 0)
        {
            await PreloadAsync(ctx);
            return;
        }

        // 다운로드 선택 대기 — 스플래시는 여기서 멈춰 있고 사용자가 결정
        var gb = _audit.TotalBytes / 1024.0 / 1024.0 / 1024.0;
        MissingSummary = _audit.TotalKnown
            ? $"💾 필수 모델 없음 · 필수 합계 {gb:F1} GiB (번역은 아래 선택지 중 하나)\n" + string.Join("\n", _audit.Missing.Select(DescribeMissing))
            : "💾 필수 모델 없음\n" + string.Join("\n", _audit.Missing.Select(DescribeMissing));

        // 번역 Group(qwen3) 미보유 → 드롭다운 선택지(카탈로그 순=32B 기본, 14B 저사양 대안)
        var grp = _audit.Missing.Where(m => m.Spec.Group == "qwen3").ToList();
        TranslateChoices = grp.Select(m => new ChoiceItem(m.Spec, ChoiceLabel(m))).ToList();
        TranslateChoice = TranslateChoices.FirstOrDefault();
        HasTranslateChoice = TranslateChoices.Count > 1;
        Message = HasTranslateChoice
            ? "번역 모델을 선택하세요(기본 32B·저사양은 14B). OCR/LaMa는 필수 — 스킵해도 자동 다운로드됩니다."
            : "OCR/LaMa는 필수입니다 — 번역 GGUF만 빼면 스킵(웹 번역 폴백)으로 사용할 수 있어요.";
        NeedsModels = true;
    }

    private static string DescribeMissing(MissingModel m)
    {
        var size = m.SizeKnown ? $"{m.SizeBytes / 1024.0 / 1024.0 / 1024.0:F1} GiB" : "?GiB";
        var grp = m.Spec.Group == "qwen3" ? " ← 번역 택1(선택지 아래)" : "";
        return $"· {m.Spec.Key} [{size}]{grp}";
    }

    /// <summary>드롭다운 라벨: 용량+권장/저사양 표기.</summary>
    private static string ChoiceLabel(MissingModel m)
    {
        var size = m.SizeKnown ? $" · {m.SizeBytes / 1024.0 / 1024.0 / 1024.0:F1} GiB" : "";
        return m.Spec.Key switch
        {
            "translate.qwen3_32b_gguf" => "Qwen3-32B 권장" + size,
            "translate.qwen3_14b_gguf" => "Qwen3-14B 저사양" + size,
            _ => m.Spec.Key + size,
        };
    }

    /// <summary>② 다운로드 → 완료 후 ③엔진 로드. Group은 드롭다운 선택 1건만 받는다.</summary>
    private async Task DownloadAndPreloadAsync()
    {
        if (_audit is null) return;
        NeedsModels = false;
        IsDownloading = true;
        var items = _audit.Missing.Where(m => m.Spec.Group == null).ToList();
        if (TranslateChoice != null)
            items.AddRange(_audit.Missing.Where(m => m.Spec.Key == TranslateChoice.Spec.Key));
        else
            items.AddRange(ModelDownloader.DefaultSelection(_audit.Missing).Where(m => m.Spec.Group != null));
        var prog = new Progress<(double Percent, string Msg)>(v => { Progress = v.Percent * 0.9; Message = v.Msg; });
        try { await ModelDownloader.DownloadAllAsync(_ctx.Models, items, prog, _cts.Token); }
        catch (OperationCanceledException) { Message = "✕ 다운로드 취소 — 부분 수집된 파일은 이어받기 유지"; }
        catch (Exception ex) { Message = "⚠ 다운로드 실패: " + ex.Message; }
        finally { IsDownloading = false; }
        await RunAsync(_ctx);
    }

    private Task PreloadAsync(EngineContext ctx) => RunAsync(ctx);

    private async Task RunAsync(EngineContext ctx)
    {
        var prog = new Progress<(double Percent, string Message)>();
        prog.ProgressChanged += (_, v) => { Progress = v.Percent; Message = v.Message; };
        var canceled = false;
        try { await ctx.PreloadAsync(ctx.DefaultKeys(), prog, _cts.Token); }
        catch (OperationCanceledException) { canceled = true; } // ✕ 종료 경로 — 메인을 열지 않음
        catch (Exception ex) { Message = "⚠ 부팅 실패: " + ex.Message; }
        finally { _timer?.Stop(); }
        if (canceled) return;
        await Task.Delay(350); // 100%가 잠깐 보이게
        FireReady();
    }

    private void FireReady()
    {
        if (Interlocked.Exchange(ref _ready, 1) == 0)
            Ready?.Invoke(this, EventArgs.Empty);
    }

    protected override void DisposeCore()
    {
        _timer?.Stop();
        _cts.Dispose();
    }
}
