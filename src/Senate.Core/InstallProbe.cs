// 區塊職責：量每個項目**現在在選定的環境裡是什麼狀態**（安裝系統，TASK-0375）。
// 物理意義：五態，⛔ 互不同形：
//           已安裝 ／ 沒安裝 ／ 裝了但壞了 ／ 不完整（下載到一半）／ 量不到（例：找不到 Python）。
//           ⭐ pip 項目在**新的子程序**裡實際 import 才算裝好 —— 版本讀得到不等於能用
//             （knowledge_base.py 的血證：null-byte 污染的套件 metadata 讀得到、import 會炸）。
//           ⭐ 模型以**磁碟上的檔**判定，不看下載指令的 exit code（media_admin.py 同一條）。
// 數值影響：pip 那半會起一個 Python 子程序（import torch 要幾秒）；模型那半純讀檔案。
using System.Text;
using System.Text.Json;

namespace Senate.Core;

public enum InstallState
{
    /// <summary>量不到（找不到 Python、探針自己炸了…）。⚠ 不可以印成「沒安裝」。</summary>
    Unknown = 0,
    Installed = 1,
    Missing = 2,
    /// <summary>裝了但不能用（import 失敗、檢查式不成立、模型缺檔）。</summary>
    Broken = 3,
    /// <summary>下載到一半（HF 快取裡有 .incomplete）。再按一次安裝會從斷點接續（resume_partial）。</summary>
    Partial = 4,
}

/// <summary>一個項目的讀數。</summary>
public sealed record InstallItemStatus(InstallItem Item, InstallState State, string Version, string Detail, long SizeBytes)
{
    public static string StateText(InstallState iState) => iState switch
    {
        InstallState.Installed => "✓ 已安裝",
        InstallState.Missing => "・沒安裝",
        InstallState.Broken => "✗ 裝了但壞了",
        InstallState.Partial => "◐ 不完整",
        _ => "？ 量不到",
    };
}

public static class InstallProbe
{
    /// <summary>量整份清單（pip 項目一次子程序量完，模型逐項看磁碟）。</summary>
    public static List<InstallItemStatus> ProbeAll(InstallEnv iEnv, InstallCatalog iCatalog)
        => Probe(iEnv, iCatalog.Items);

    public static List<InstallItemStatus> Probe(InstallEnv iEnv, IReadOnlyList<InstallItem> iItems)
    {
        var aPip = iItems.Where(i => i.Kind == InstallKind.Pip).ToList();
        Dictionary<string, InstallItemStatus> aPipResults = ProbePip(iEnv, aPip);
        var aOut = new List<InstallItemStatus>();
        foreach (InstallItem i in iItems)
            aOut.Add(i.Kind == InstallKind.Pip ? aPipResults[i.Id] : ProbeModel(iEnv, i));
        return aOut;
    }

    // ── pip ───────────────────────────────────────────────────────────

    // 探針腳本：讀 argv[1] 的 JSON（項目清單），逐項量版本、import、算檢查式，最後印一行 `__PROBE__<json>`。
    // ⚠ 版本走 importlib.metadata（不 import 也讀得到）；import 失敗要帶**原始例外**，不壓成「沒裝」。
    const string ProbeScript = @"
import sys, json
from importlib import metadata as im
items = json.loads(sys.argv[1])
out = {'python': sys.version.split()[0], 'exe': sys.executable, 'prefix': sys.prefix, 'items': {}}
for it in items:
    r = {'versions': {}, 'import_error': '', 'check': None, 'check_error': ''}
    for d in it['dists']:
        try: r['versions'][d] = im.version(d)
        except Exception: r['versions'][d] = ''
    ns = {}
    try:
        for m in it['imports']:
            ns[m.split('.')[0]] = __import__(m)
    except BaseException as e:
        r['import_error'] = type(e).__name__ + ': ' + str(e)[:300]
    if not r['import_error'] and it['check']:
        try: r['check'] = bool(eval(it['check'], ns))
        except BaseException as e: r['check_error'] = type(e).__name__ + ': ' + str(e)[:300]
    out['items'][it['id']] = r
print('__PROBE__' + json.dumps(out))
";

    static Dictionary<string, InstallItemStatus> ProbePip(InstallEnv iEnv, List<InstallItem> iItems)
    {
        var d = new Dictionary<string, InstallItemStatus>(StringComparer.OrdinalIgnoreCase);
        if (iItems.Count == 0) return d;
        if (iEnv.PythonExe == null)
        {
            foreach (InstallItem i in iItems) d[i.Id] = new(i, InstallState.Unknown, "", "量不到：" + (iEnv.PythonError ?? "沒有 Python"), 0);
            return d;
        }

        var aSpec = new StringBuilder("[");
        for (int k = 0; k < iItems.Count; k++)
        {
            InstallItem i = iItems[k];
            if (k > 0) aSpec.Append(',');
            aSpec.Append("{\"id\":").Append(JsonSerializer.Serialize(i.Id))
                 .Append(",\"dists\":").Append(JsonSerializer.Serialize(i.Dists))
                 .Append(",\"imports\":").Append(JsonSerializer.Serialize(i.Imports))
                 .Append(",\"check\":").Append(JsonSerializer.Serialize(i.Check)).Append('}');
        }
        aSpec.Append(']');

        InstallProcessResult r = InstallProcess.Run(iEnv.PythonExe, new[] { "-c", ProbeScript, aSpec.ToString() },
                                                    iEnv.ChildEnvironment(), 240, null, "install probe");
        string? aLine = r.StdOut.Split('\n').Select(s => s.Trim()).LastOrDefault(s => s.StartsWith("__PROBE__", StringComparison.Ordinal));
        if (aLine == null)
        {
            string aWhy = r.StartError ?? (r.TimedOut ? "探針逾時（240 秒）" : $"探針沒有回讀數（exit {r.ExitCode}）：{Tail(r.StdErr)}");
            foreach (InstallItem i in iItems) d[i.Id] = new(i, InstallState.Unknown, "", "量不到：" + aWhy, 0);
            return d;
        }

        using JsonDocument aDoc = JsonDocument.Parse(aLine.Substring("__PROBE__".Length));
        JsonElement aItemsEl = aDoc.RootElement.GetProperty("items");
        foreach (InstallItem i in iItems)
        {
            JsonElement e = aItemsEl.GetProperty(i.Id);
            var aVers = new List<string>();
            int aHave = 0;
            foreach (JsonProperty p in e.GetProperty("versions").EnumerateObject())
            {
                string v = p.Value.GetString() ?? "";
                if (v.Length > 0) { aHave++; aVers.Add($"{p.Name} {v}"); }
            }
            string aVer = string.Join("、", aVers);
            string aImportErr = e.GetProperty("import_error").GetString() ?? "";
            string aCheckErr = e.GetProperty("check_error").GetString() ?? "";
            JsonElement aCheck = e.GetProperty("check");

            InstallState s; string aDetail;
            if (aHave == 0 && aImportErr.StartsWith("ModuleNotFoundError", StringComparison.Ordinal))
            { s = InstallState.Missing; aDetail = ""; }
            else if (aImportErr.Length > 0)
            { s = InstallState.Broken; aDetail = "import 失敗：" + aImportErr; }
            else if (aHave < i.Dists.Count)
            { s = InstallState.Broken; aDetail = "import 得進來，但有 distribution 讀不到版本（可能是殘留檔或別處的同名模組）"; }
            else if (aCheckErr.Length > 0)
            { s = InstallState.Broken; aDetail = "檢查式炸了：" + aCheckErr; }
            else if (aCheck.ValueKind == JsonValueKind.False)
            { s = InstallState.Broken; aDetail = i.CheckHint.Length > 0 ? i.CheckHint : $"檢查式 `{i.Check}` 不成立"; }
            else
            { s = InstallState.Installed; aDetail = ""; }
            d[i.Id] = new(i, s, aVer, aDetail, 0);
        }
        return d;
    }

    // ── hf_model ──────────────────────────────────────────────────────

    /// <summary>模型在 HF 快取裡的目錄（`hub/models--組織--名稱`）。</summary>
    public static string ModelDir(InstallEnv iEnv, InstallItem iItem)
        => Path.Combine(iEnv.ModelsRoot, "hub", "models--" + iItem.Repo.Replace("/", "--")).Replace('\\', '/');

    public static InstallItemStatus ProbeModel(InstallEnv iEnv, InstallItem iItem)
    {
        if (iEnv.ModelsRoot.Length == 0) return new(iItem, InstallState.Unknown, "", "量不到：沒有模型位置", 0);
        string aDir = ModelDir(iEnv, iItem);
        try
        {
            if (!Directory.Exists(aDir)) return new(iItem, InstallState.Missing, "", "", 0);
            // ⚠ 大小算整個目錄，不只 blobs/：Windows 上沒有開發者模式時 HF 不建 symlink，
            //   檔案直接放在 snapshots/ 裡、blobs/ 是空的（2026-10-02 實測 bge-m3 就是這樣）——
            //   只數 blobs/ 會量出 0，而 0 會讓「已下載 4 GB 但缺一個檔」被判成「沒安裝」。
            //   symlink 本身的 Length 是連結的大小不是目標的 ⇒ 有 blobs 時兩邊各算一次也不會重複（連結近乎 0）。
            long aSize = 0; int aIncomplete = 0; long aIncompleteBytes = 0;
            foreach (string f in Directory.EnumerateFiles(aDir, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase)) { aIncomplete++; aIncompleteBytes += new FileInfo(f).Length; continue; }
                var fi = new FileInfo(f);
                if (fi.LinkTarget == null) aSize += fi.Length;
            }

            // snapshot 裡**同一份**要有全部必要檔，才算裝好（兩個版本各有一半不算）。
            string? aGood = null;
            string aSnapRoot = Path.Combine(aDir, "snapshots");
            var aMissingBest = new List<string>(iItem.RequiredFiles);
            if (Directory.Exists(aSnapRoot))
                foreach (string aSnap in Directory.EnumerateDirectories(aSnapRoot))
                {
                    var aMiss = iItem.RequiredFiles.Where(rf => !File.Exists(Path.Combine(aSnap, rf))).ToList();
                    if (aMiss.Count == 0) { aGood = Path.GetFileName(aSnap); break; }
                    if (aMiss.Count < aMissingBest.Count) aMissingBest = aMiss;
                }

            // ⭐ 必要檔齊 ⇒ 已安裝，**就算旁邊還有 .incomplete**。
            // 🩸 2026-10-02 實測推翻了我原本的寫法（「有 .incomplete 就是不完整」）：HF 把檔案下載完才放進 snapshots/，
            //   而 huggingface_hub 1.24 每一次嘗試的暫存檔都帶**隨機後綴**（`<hash>.<rand>.incomplete`）⇒
            //   中斷之後再跑**不會從斷點接續**，是重新下載一份；被砍掉那次的暫存檔就變成孤兒留在 blobs/。
            //   舊判準因此把「已經下載完的 2.3 GB 模型」判成「不完整」，而再按一次安裝也永遠不會變綠。
            //   ⇒ 殘留暫存檔只寫進說明（附清除指令），不影響判定。
            if (aGood != null)
                return new(iItem, InstallState.Installed, "snapshot " + Short(aGood),
                           aIncomplete > 0
                               ? $"有 {aIncomplete} 個上次中斷留下的暫存檔（{FormatSize(aIncompleteBytes)}），可清掉：senate cmd install --arg op=clean_partial --arg ids={iItem.Id} --arg confirm=1"
                               : "", aSize);
            if (aIncomplete > 0)
                return new(iItem, InstallState.Partial, "",
                           $"有 {aIncomplete} 個下載到一半的檔（{FormatSize(aIncompleteBytes)}）—— 再按一次安裝會先從斷點接續、再補齊其他檔；不要了就 op=clean_partial 清掉", aSize);
            return new(iItem, aSize > 0 ? InstallState.Broken : InstallState.Missing, "",
                       aSize > 0 ? "快取目錄在，但缺必要檔：" + string.Join("、", aMissingBest) : "", aSize);
        }
        catch (Exception e)
        {
            return new(iItem, InstallState.Unknown, "", $"量不到：{e.GetType().Name}: {e.Message}", 0);
        }
    }

    static string Short(string s) => s.Length > 10 ? s.Substring(0, 10) : s;

    internal static string Tail(string s, int iMax = 400)
    {
        s = s.Trim();
        return s.Length <= iMax ? s : "…" + s.Substring(s.Length - iMax);
    }

    public static string FormatSize(long iBytes)
        => iBytes <= 0 ? "—" : iBytes >= 1L << 30 ? $"{iBytes / (double)(1L << 30):0.0} GB" : $"{iBytes / (double)(1L << 20):0} MB";
}
