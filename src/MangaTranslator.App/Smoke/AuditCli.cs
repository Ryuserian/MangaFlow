using MangaTranslator.App.Services;
using MangaTranslator.ModelManager;

namespace MangaTranslator.App.Smoke;

/// <summary>
/// 카탈로그 감사 CLI — 스플래시 다운로드 UI의 헤드리스 검증/백그라운드 확보 경로.
///   --audit                : 없는 모델 목록+총 용량 출력
///   --getmodel <key>     : 단일 모델 다운로드 (이어받기+sha256)
/// </summary>
internal static class AuditCli
{
    public static int Run(bool downloadAll)
    {
        var models = new ModelCatalogManager();
        Console.WriteLine($"모델 루트: {models.RootDir}");
        var audit = ModelDownloader.AuditAsync(models).GetAwaiter().GetResult();

        if (audit.Missing.Count == 0)
        {
            var req = models.Catalog.Count(s => !s.Optional);
            Console.WriteLine($"✅ 필수 {req}종 충족(Group 충족 포함) — 다운로드 불필요 (전체 카탈로그 {models.Catalog.Count}종)");
            return 0;
        }

        Console.WriteLine($"⬇ {audit.Missing.Count}종 없음 (필수만 리스팅 — Optional은 --getmodel로 별도 확보):");
        foreach (var m in audit.Missing)
        {
            var size = m.SizeKnown ? $"{m.SizeBytes / 1024.0 / 1024.0 / 1024.0:F2} GiB" : "size?";
            var grp = m.Spec.Group != null ? " [군 둘 중 하나]" : "";
            Console.WriteLine($"  · {m.Spec.Key,-28} {m.Spec.Repo}/{m.Spec.File}  [{size}]{grp}");
        }
        Console.WriteLine(audit.TotalKnown
            ? $"총 {audit.TotalBytes / 1024.0 / 1024.0 / 1024.0:F1} GiB"
            : "총 크기: 일부 HEAD 실패로 미확인");

        if (!downloadAll) { Console.WriteLine("(감사만 — 실다운로드는 스플래시 UI 또는 --audit download 사용)"); return 0; }

        var prog = new Progress<(double Pct, string Msg)>(v =>
            Console.Write($"\r[{v.Pct:P0}] {v.Msg}                    "));
        try { ModelDownloader.DownloadAllAsync(models, audit.Missing, prog).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine($"\n중단: {ex.Message}"); return 1; }
        Console.WriteLine("\n완료.");
        return 0;
    }

    public static int Get(string key)
    {
        var models = new ModelCatalogManager();
        var spec = models.Find(key);
        if (spec is null) { Console.Error.WriteLine($"카탈로그에 없음: {key}"); return 2; }
        var dest = models.PathFor(spec);
        if (File.Exists(dest)) { Console.WriteLine($"이미 보유: {dest}"); return 0; }

        var prog = new Progress<double>(p => Console.Write($"\r[{p:P0}] {key}          "));
        try { ModelDownloader.GetOneAsync(models, new MissingModel(spec, 0, false), prog).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine($"\n실패: {ex.Message}"); return 1; }
        Console.WriteLine($"\n✅ {dest}");
        return 0;
    }
}
