using Avalonia;
using Avalonia.Controls;
using MangaTranslator.App.ViewModels;

namespace MangaTranslator.App.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        // borderless 창드래그 — 단, ✕ 취소 버튼 위에서의_press는 드래그로 neweat하지 않도록
        // 소스 판정(버튼 조상 탐색)까지 통과했을 때만 BeginMoveDrag.
        PointerPressed += (_, e) =>
        {
            // ✕ 버튼(또는 자식)에서 시작한 press는 창 드래그로 neweat하지 않음
            Avalonia.Visual? v = e.Source as Avalonia.Visual;
            while (v != null)
            {
                if (v is Avalonia.Controls.Button) return;
                v = v.Parent as Avalonia.Visual;
            }
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
    }

    /// <summary>✕ 버튼 누름이 창 드래그로 neweat되지 않도록 차단.</summary>
    private void OnCancelPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) => e.Handled = true;
}
