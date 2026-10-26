using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MangaTranslator.App.Services;
using MangaTranslator.App.ViewModels;
using MangaTranslator.App.Views;

namespace MangaTranslator.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var ctx = EngineContext.CreateDefault();

            // v4 흐름: 스플래시(엔진 로드 progress) → 로드 종료 후 메인으로 교체.
            // 스플래시 실패/예외에도 메인은 반드시 열린다(엔진별 실패는 스플래시 내부에서 흡수).
            var splash = new SplashWindow { DataContext = new SplashViewModel(ctx) };
            var mainVm = new MainWindowViewModel(ctx);
            var main = new MainWindow { DataContext = mainVm, IsVisible = false };
            desktop.MainWindow = splash;

            var svm = (SplashViewModel)splash.DataContext!;
            svm.Ready += () =>
            {
                desktop.MainWindow = main;
                main.Show();      // 먼저 보여야 OnLastWindowClose 중개 공백이 없다
                splash.Close();
            };
            // ✕ = 앱 종료 — 메인을 열지 않고 즉시 종료(미다운 모델은 .part 이어받기 유지)
            svm.CloseRequested += () => desktop.Shutdown();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
