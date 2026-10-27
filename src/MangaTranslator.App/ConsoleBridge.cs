using System.Runtime.InteropServices;
using System.Text;

namespace MangaTranslator.App;

/// <summary>
/// WinExe(2026-10-26 사용자: 실행 시 콘솔 창 제거)에서의 CLI 출력 복원 브리지.
///   · 더블클릭 실행: 부모 콘솔 없음 → AttachConsole 실패 → 조용히 무시(콘솔 창 절대 안 뜸).
///   · 터미널에서 실행: 부모 cmd의 콘솔에 붙어 --audit/--smoke 등의 출력을 그대로 보여준다.
/// GUI 로그는 원래 콘솔 불필요 — 이 부재가 오류는 아니다.
/// </summary>
internal static class ConsoleBridge
{
    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetStdHandle(int nStdHandle);

    public static void TryAttachParent()
    {
        if (Console.IsOutputRedirected || Console.IsErrorRedirected) return; // >파이프/리다이렉트 존중
        if (!AttachConsole(ATTACH_PARENT_PROCESS)) return;
        try
        {
            var outH = GetStdHandle(STD_OUTPUT_HANDLE);
            if (outH != 0 && outH != -1)
                Console.SetOut(new StreamWriter(new FileStream(outH, FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true });
            var errH = GetStdHandle(STD_ERROR_HANDLE);
            if (errH != 0 && errH != -1)
                Console.SetError(new StreamWriter(new FileStream(errH, FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch { /* 출력 실패 ≠ 앱 실패 */ }
    }
}
