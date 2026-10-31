using System.ComponentModel;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Primitives.Disposables;

namespace MangaFlow.App.ViewModels;

/// <summary>
/// 앱 VM 공통 기반 — ReactiveUI 26에서 ReactiveObject는 IActivatableViewModel을
/// 구현하지 않으므로 여기서 직접 구현해 WhenActivated를 복원한다.
///
/// · Activator: 전용 뷰(ReactiveWindow)가 DataContext 어댑 시 자동 Activate.
///   뷰 없는 자식 VM은 생성자에서 ActivateLifetime()로 수동 활성화한다.
/// · Lifetime: VM 수명 바인딩 버킷. 자식 VM 오너가 Subscribe(...).DisposeWith(child.Lifetime)
///   로订阅비를 자식 수명에 맞춘다(ReactiveUI 26가 제거한 DisposeWith는 RxMixins shim 복원).
/// </summary>
public class ViewModelBase : ReactiveObject, IActivatableViewModel, IDisposable
{
    public ViewModelActivator Activator { get; } = new();

    /// <summary>VM lifetime 구독 바구니 — Dispose 시 전 구독 해제.</summary>
    public CompositeDisposable Lifetime { get; } = new();

    private IDisposable? _activationScope;

    /// <summary>뷰 없는 VM용: 즉시 활성화 → WhenActivated 블록 실행. 반환 scope Dispose 시 역활성화.</summary>
    public IDisposable ActivateLifetime()
    {
        var scope = Activator.Activate();
        Lifetime.Add(scope);
        _activationScope = scope;
        return scope;
    }

    private bool _disposed;
    protected bool IsDisposed => _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeCore();
        Lifetime.Dispose();
        Activator.Dispose();
    }

    /// <summary>VM별 해제(커맨드 Dispose·타이머 정지·자식 VM Dispose).</summary>
    protected virtual void DisposeCore() { }

    /// <summary>WhenAnyValue 대체 — ReactiveUI 26은 non-generated 프로퍼티에서 런타임 예외.
    /// PropertyChanged 이벤트 기반 + 현재値 1회 방출로 같은 시맨틱을 제공한다.</summary>
    protected IObservable<T> WhenProp<T>(string name, Func<T> read) =>
        Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                h => PropertyChanged += h, h => PropertyChanged -= h)
            .Where(ev => ev.EventArgs.PropertyName == name)
            .Select(_ => read())
            .StartWith(read());
}
