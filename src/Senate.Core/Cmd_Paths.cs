// 區塊職責：`senate cmd paths` —— 列出**所有動態路徑**（enum ＋ 解析後的值）。**原生**，不需要 Unity。
// 物理意義：清單由 `SCP_PathRegistry` 描述表生成 ⇒ 加一條路徑＝加一個 enum 成員 ＋ 一筆 descriptor，
//           **本檔一行都不用改**（同「路徑管理頁」）。
//           ⇒ 待列清單的**唯一落點**是那張表，不另外維護一份 md 或一份 switch
//           （兩份清單遲早各說各話，而且兩邊都不報錯 —— 對照 `SCP_Cmd.PortStatus`）。
// 數值影響：純讀。**不寫設定檔**（設定走「路徑管理頁」）。
//
// ⚠ 每一條都印**來源定語**（`手填`／`auto ⇒ 由 X 推導`／`derived ⇒ X/suffix`）——
//   看不出來源的路徑沒辦法被質疑，而路徑錯掉的症狀常常是「一切正常，只是永遠 pending」。
// ⚠ 回傳形狀照 Tim 2026-08-31 拍板：**values 只放平的純量；巢狀走寫檔（JSON）**。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Paths;

namespace Senate.Core;

public sealed class Cmd_Paths : SCP_Cmd
{
    public override string Name => "paths";

    public override string Summary => "列出所有動態路徑（enum ＋ 解析值 ＋ 誰決定的）";

    public override string Details =>
        "清單由 `SCP_PathRegistry` 描述表生成 —— 擴充一條路徑只要加 enum 成員 ＋ descriptor。\n"
        + "每條印：作用域（全域／每專案）、可設定或推導、儲存鍵、算式、解析值、來源、存在性。\n"
        + "⛔ **本 Cmd 不寫任何設定** —— 要改走後台「路徑管理」頁（`senate ui --page paths`）。\n"
        + "⚠ `Derived` 的格子**不儲存**：能被推導的路徑存起來就是給漂移一個住的地方。";

    public override string Example => SCP_CmdRegistry.Invoke("paths --arg id=LettersRoot");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("id", "只看某一條（enum 成員名，如 LettersRoot）"),
        new SCP_CmdArgSpec("out_json", "把完整清單落成 JSON 的路徑（巢狀資料走檔案，不進 values）"),
    };

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        var aResult = new SCP_CmdResult();

        if (UnityDelegateCmd.ConfigProvider == null)
            return SCP_CmdResult.Fail(70,
                "✗ 宿主沒有裝上設定來源（UnityDelegateCmd.ConfigProvider）——",
                "  這是程式錯誤不是用法錯：本 Cmd 要讀 senate.local.json 才知道那幾格存了什麼。");

        (SenateConfig? aConfig, string aConfigPath) = UnityDelegateCmd.ConfigProvider();
        if (aConfig == null)
            return SCP_CmdResult.Fail(2, "✗ 讀不到設定檔：" + aConfigPath, "  先跑 `senate init`");

        // ── 資料根只有一組 ⇒ 這裡不挑專案，只報「那個唯一的是誰」──────────────
        // ⚠ 有兩個啟用專案時**不替人挑**：靜默挑一個的症狀是「路徑全對，只是屬於別的專案」。
        SenateProject? aProj = SenatePathBinding.SingleProject(aConfig, out string? aSingleErr);
        string aProjNote = aProj != null
            ? "唯一啟用的專案：'" + aProj.Name + "'　`" + aProj.Root + "`"
            : "⚠ " + aSingleErr;

        SCP_PathStoredValue StoredOf(SCP_PathId iId) => SenatePathBinding.StoredOf(aConfig, iId);

        string aIdArg = iArgs.Get("id");
        var aWanted = new List<SCP_PathDescriptor>();
        if (aIdArg.Length > 0)
        {
            bool aHit = false;
            foreach (SCP_PathDescriptor d in SCP_PathRegistry.All)
                if (string.Equals(d.Id.ToString(), aIdArg, StringComparison.OrdinalIgnoreCase))
                { aWanted.Add(d); aHit = true; break; }
            if (!aHit)
            {
                var aAll = new List<string>();
                foreach (SCP_PathDescriptor d in SCP_PathRegistry.All) aAll.Add(d.Id.ToString());
                return SCP_CmdResult.Fail(2, "✗ 不認得的 id '" + aIdArg + "'",
                    "  可用：" + string.Join("、", aAll.ToArray()));
            }
        }
        else aWanted.AddRange(SCP_PathRegistry.All);

        aResult.Lines.Add("# 🗂 動態路徑 —— 共 " + SCP_PathRegistry.All.Count + " 條"
                          + (aIdArg.Length > 0 ? "（只列 " + aIdArg + "）" : ""));
        aResult.Lines.Add("· 設定檔：" + aConfigPath);
        aResult.Lines.Add("· " + aProjNote);
        aResult.Lines.Add("");

        int aUnresolved = 0, aMissing = 0;
        foreach (SCP_PathDescriptor d in aWanted)
        {
            SCP_PathResolution aRes = SCP_PathRegistry.Resolve(d.Id, StoredOf);
            string aScope = d.Scope == SCP_PathScope.Global ? "全域" : "專案";
            string aKind = d.Kind == SCP_PathKind.Stored ? "可設定" : "推導";
            aResult.Lines.Add("## " + d.Id + "　" + d.Label + "　[" + aScope + "／" + aKind + "]");
            if (d.Kind == SCP_PathKind.Stored)
                aResult.Lines.Add("- 儲存鍵：`" + d.JsonKey + "`　現值：`"
                                  + (StoredOf(d.Id).Raw.Length == 0 ? "（未設定）" : StoredOf(d.Id).Raw) + "`");
            aResult.Lines.Add("- 算式：" + SCP_PathRegistry.Formula(d.Id));
            if (aRes.Error != null)
            {
                aUnresolved++;
                aResult.Lines.Add("- ⚠ 解不出來（" + aRes.Origin + "）：" + aRes.Error);
            }
            else
            {
                string aExist = Existence(aRes.Value, ref aMissing);
                aResult.Lines.Add("- ⇒ `" + aRes.Value + "`　（" + aRes.Origin + "）" + aExist);
            }
            aResult.Lines.Add("- " + d.Note);
            aResult.Lines.Add("");
        }

        aResult.AddValue("path_count", SCP_PathRegistry.All.Count.ToString(CultureInfo.InvariantCulture));
        aResult.AddValue("listed_count", aWanted.Count.ToString(CultureInfo.InvariantCulture));
        // 0 也印：只在非零時出現的欄位，讀者分不出「乾淨」與「沒量」。
        aResult.AddValue("unresolved", aUnresolved.ToString(CultureInfo.InvariantCulture));
        aResult.AddValue("missing_on_disk", aMissing.ToString(CultureInfo.InvariantCulture));
        aResult.AddValue("project", aProj?.Name ?? "");
        EmitJson(aResult, iArgs, aWanted, StoredOf);
        return aResult;
    }

    static string Existence(string iPath, ref int ioMissing)
    {
        if (iPath.Length == 0) return "";
        if (Directory.Exists(iPath)) return "　✓ 存在";
        if (File.Exists(iPath)) return "　⚠ 是檔案不是目錄";
        ioMissing++;
        // ⚠「不存在」不等於錯 —— 有些路徑第一次用才被建出來。但它跟「存在」必須看得出差別。
        return "　⚠ 不存在（可能還沒被建出來）";
    }

    static void EmitJson(SCP_CmdResult oResult, SCP_CmdArgs iArgs,
                         List<SCP_PathDescriptor> iRows, Func<SCP_PathId, SCP_PathStoredValue> iStored)
    {
        string aOut = iArgs.Get("out_json");
        if (aOut.Length == 0) return;
        var sb = new StringBuilder();
        sb.Append("{\"count\":").Append(iRows.Count).Append(",\"paths\":[");
        for (int i = 0; i < iRows.Count; i++)
        {
            if (i > 0) sb.Append(',');
            SCP_PathDescriptor d = iRows[i];
            SCP_PathResolution r = SCP_PathRegistry.Resolve(d.Id, iStored);
            sb.Append('{');
            J(sb, "id", d.Id.ToString()); sb.Append(',');
            J(sb, "label", d.Label); sb.Append(',');
            J(sb, "kind", d.Kind.ToString()); sb.Append(',');
            J(sb, "scope", d.Scope.ToString()); sb.Append(',');
            J(sb, "json_key", d.JsonKey); sb.Append(',');
            J(sb, "stored_raw", d.Kind == SCP_PathKind.Stored ? iStored(d.Id).Raw : ""); sb.Append(',');
            J(sb, "formula", SCP_PathRegistry.Formula(d.Id)); sb.Append(',');
            J(sb, "value", r.Value); sb.Append(',');
            J(sb, "origin", r.Origin); sb.Append(',');
            J(sb, "error", r.Error ?? ""); sb.Append(',');
            J(sb, "exists", r.Value.Length > 0 && Directory.Exists(r.Value) ? "1" : "0");
            sb.Append('}');
        }
        sb.Append("]}");
        string? aDir = Path.GetDirectoryName(aOut);
        if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
        File.WriteAllText(aOut, sb.ToString(), new UTF8Encoding(false));
        oResult.AddOutput(aOut);
        oResult.Lines.Add("📄 JSON：" + aOut + "（" + iRows.Count + " 條）");
    }

    static void J(StringBuilder sb, string iKey, string iValue)
    {
        sb.Append('"').Append(iKey).Append("\":\"");
        foreach (char c in iValue ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
