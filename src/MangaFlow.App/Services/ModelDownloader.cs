using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using MangaFlow.ModelManager;

namespace MangaFlow.App.Services;

public sealed record MissingModel(ModelSpec Spec, long SizeBytes, bool SizeKnown);

public sealed record ModelAudit(IReadOnlyList<MissingModel> Missing, long TotalBytes, bool TotalKnown);

/// <summary>
/// 카탈로그 기반 모델 검사·다운로더 — GitHub 공개 빌드에서 models/가 비어 있어도
/// 첫 실행에 HF에서 자동 보충. 카탈로그가 유일한 진실원이라 모델 추가는 Catalog 한 줄뿐, 코드 무접촉.
/// </summary>
public static class ModelDownloader
{
    private const string HF = "https://huggingface.co";
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static ModelDownloader()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("MangaFlow/0.1");
        Http.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    public static string PartPath(ModelCatalogManager m, ModelSpec s) => m.PathFor(s) + ".part";

    /// <summary>
    /// 필수분만 감사: Optional은 스킵. Group(번역 qwen3 등)은 구성원 중 하나라도 디스크에 있으면
    /// 충족(엔진이 자동 선택). 전부 없으면 **후보 전원을 리스팅**(카탈로그 순서=권장순위, 첫 항목=선택 UI 기본값)하고,
    /// 총 용량은 첫 항목만 합산한다(대안 표시분은 합산 제외 — 사용자는 그중 하나만 받는다).
    /// </summary>
    public static async Task<ModelAudit> AuditAsync(ModelCatalogManager m, CancellationToken ct = default)
    {
        var missing = new List<MissingModel>();
        long total = 0; var totalKnown = true;

        bool Exists(ModelSpec s)
        {
            var p = m.PathFor(s);
            return File.Exists(p) && new FileInfo(p).Length > 0;
        }

        var satisfiedGroups = new HashSet<string>(m.Catalog
            .Where(s => s.Group != null && Exists(s))
            .Select(s => s.Group!));
        var countedGroup = new HashSet<string>();

        foreach (var s in m.Catalog)
        {
            ct.ThrowIfCancellationRequested();
            if (s.Optional) continue;
            if (Exists(s)) continue;
            if (s.Group != null && satisfiedGroups.Contains(s.Group)) continue;

            var size = await HeadSizeAsync(s, ct);
            // Group는 첫(권장) 항목만 총량 합산 — 후속 대안은 표시용
            bool counted = s.Group == null || countedGroup.Add(s.Group);
            if (counted)
            {
                if (size.HasValue) total += size.Value; else totalKnown = false;
            }
            missing.Add(new MissingModel(s, size ?? 0, size.HasValue));
        }
        return new ModelAudit(missing, total, totalKnown);
    }

    /// <summary>Group은 첫(권장) 항목만 남긴 다운로드 선택 기본값 — "택1"군에서 중복 다운로드 방지.</summary>
    public static IReadOnlyList<MissingModel> DefaultSelection(IReadOnlyList<MissingModel> missing) =>
        missing.Where(x => x.Spec.Group == null)
            .Concat(missing.Where(x => x.Spec.Group != null)
                .GroupBy(x => x.Spec.Group!)
                .Select(g => g.First()))
            .ToList();

    public static async Task DownloadAllAsync(ModelCatalogManager m, IReadOnlyList<MissingModel> items,
        IProgress<(double Percent, string Message)> prog, CancellationToken ct = default)
    {
        var grand = items.Where(i => i.SizeKnown).Sum(i => i.SizeBytes);
        long doneBase = 0;
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var label = $"{item.Spec.Kind}/{item.Spec.File}";
            var p = new Progress<double>(f =>
            {
                var g = grand > 0 && item.SizeKnown
                    ? Math.Clamp((double)(doneBase + (long)(f * item.SizeBytes)) / grand, 0, 1)
                    : f * 0.5;
                prog.Report((g, $"⬇ {label} ({f:P0})"));
            });
            try
            {
                await GetCoreAsync(m, item.Spec, p, ct);
                doneBase += item.SizeKnown ? item.SizeBytes : 0;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                prog.Report((1.0, $"⚠ {label}: {ex.Message}"));
                if (item.SizeKnown) doneBase += item.SizeBytes;
            }
        }
        prog.Report((1.0, "모델 확보 완료"));
    }

    public static Task GetOneAsync(ModelCatalogManager m, MissingModel item,
        IProgress<double> prog, CancellationToken ct = default) => GetCoreAsync(m, item.Spec, prog, ct);

    // ---- 내부: 이어받기(.part) + sha256/CONTENTLEN 검증 ----
    private static async Task<long?> HeadSizeAsync(ModelSpec s, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, Url(s));
            using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!res.IsSuccessStatusCode) return null;
            var cl = res.Content.Headers.ContentLength;
            if (cl.HasValue && cl.Value > 0) return cl.Value;
            if (res.Headers.TryGetValues("X-Linked-Size", out var vs)
                && long.TryParse(vs.FirstOrDefault(), out var l) && l > 0) return l;
        }
        catch { }
        return null;
    }

    // 경로 이스케이프는 세그먼트 단위 — EscapeDataString이 '/'를 %2F로 만들어 GitHub raw가 404 내는 것 방지
    private static string Esc(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string Url(ModelSpec s) => s.Host == "github"
        ? $"https://raw.githubusercontent.com/{s.Repo}/{s.Rev}/{Esc(s.File)}"
        : $"{HF}/{s.Repo}/resolve/{s.Rev}/{Esc(s.File)}";

    private static async Task GetCoreAsync(ModelCatalogManager m, ModelSpec s, IProgress<double> prog, CancellationToken ct)
    {
        var dest = m.PathFor(s);
        if (File.Exists(dest) && new FileInfo(dest).Length > 0) { prog.Report(1.0); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = PartPath(m, s);
        var url = Url(s);
        var total = 0L; var verified = false;

        for (var attempt = 1; attempt <= 6; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var existing = File.Exists(part) ? new FileInfo(part).Length : 0L;

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);

            System.IO.FileMode mode;
            try
            {
                using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (res.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    try { File.Delete(part); } catch { }
                    existing = 0; mode = System.IO.FileMode.Create;
                }
                else if (res.StatusCode == System.Net.HttpStatusCode.PartialContent && existing > 0)
                {
                    // ContentRange: "bytes <from>-<to>/<length>" — 시작 위치가 .part 크기와 다르면 처음부터
                    var cr = res.Content.Headers.ContentRange;
                    var crFrom = cr?.From ?? existing;
                    var mismatch = cr is not null && crFrom != existing;
                    if (mismatch) { try { File.Delete(part); } catch { } existing = 0; mode = System.IO.FileMode.Create; }
                    else
                    {
                        mode = System.IO.FileMode.Append;
                        total = cr?.Length ?? (existing + (res.Content.Headers.ContentLength ?? 0));
                    }
                }
                else
                {
                    res.EnsureSuccessStatusCode();
                    mode = System.IO.FileMode.Create;
                    try { if (existing > 0) File.Delete(part); } catch { }
                    total = res.Content.Headers.ContentLength ?? 0;
                }

                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await using (var dst = new FileStream(part, mode, FileAccess.Write, FileShare.None))
                {
                    var buf = new byte[1 << 21];
                    var done = existing; int n;
                    while ((n = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, n), ct);
                        done += n;
                        if (total > 0) prog.Report(Math.Clamp((double)done / total, 0, 1));
                    }
                }

                if (!await VerifyAsync(s, part, total))
                {
                    try { File.Delete(part); } catch { }
                    continue;
                }
                verified = true;
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < 6)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), CancellationToken.None);
                Console.Error.WriteLine($"[model-dl] retry {attempt}: {s.File} ({ex.GetType().Name})");
            }
        }

        if (!verified) throw new InvalidDataException($"다운로드 실패: {s.Repo}/{s.File}");
        File.Move(part, dest, true);
        prog.Report(1.0);
    }

    private static async Task<bool> VerifyAsync(ModelSpec s, string part, long total)
    {
        var fi = new FileInfo(part);
        if (!fi.Exists || fi.Length == 0) return false;
        if (total > 0 && fi.Length != total) return false;
        if (s.Sha256 is null) return true;
        await using var fs = File.OpenRead(part);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
        return hash.Equals(s.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}
