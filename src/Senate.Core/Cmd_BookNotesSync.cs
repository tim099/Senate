// 區塊職責：`booknotes-sync` —— 閱讀筆記（`<資料根>/BookNotes`）跟遠端 repo（`sync.bookNotes`）的狀態與手動同步。
// 物理意義：平常不用打它：library／book 跑之前 CLI 先拉（節流）、跑完自動 commit＋push。
//           它給兩種時候用：① 想看現在跟遠端差多少（status）；② 手動解完衝突、或斷網一陣子之後，立刻拉推一次（sync）。
// 數值影響：status 不帶 fetch ⇒ 純讀本機；帶 fetch=1 ⇒ 抓一次遠端。sync ⇒ clone／就地接上／拉／推（不節流）。
// ⚠ **本地跑**（不經過 Server）：經 WMI 起的 Server 跑 git 同步會被 Defender 砍（見 SenateBookNotesSync 檔頭）。
using SCP.Core.Cmd;

namespace Senate.Core;

public sealed class Cmd_BookNotesSync : SCP_Cmd
{
    public override string Name => "booknotes-sync";
    public override string Category => SCP_CmdCategory.Reading;

    public override string Summary =>
        "閱讀筆記（BookNotes）跟遠端 repo（senate.local.json 的 sync.bookNotes）同步：status 看差多少／sync 手動拉推。**本地跑。**";

    public override string Details =>
        "平常不用打：library／book 跑之前先拉（每分鐘最多一次）、跑完自動 commit＋push。\n"
        + "⚠ 兩邊改了同一個檔、rebase 接不上 ⇒ 不自動解：本機的提交留著（推不上去、不會丟），"
        + "報告在 BookNotes 的 .git/senate-booknotes-sync/ 底下、我的版本在備份 ref（refs/senate/booknotes-sync/…）；照報告解完再打 `op=sync`。\n"
        + "⚠ BookNotes 已經在、但還不是 git repo ⇒ 第一次同步會就地接上：本機獨有的檔收進去推上去，兩邊都有而內容不同的以遠端為準、本機那份備份起來。";

    public override string Example => SCP_CmdRegistry.Invoke("booknotes-sync --arg op=status --arg fetch=1");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("op", "status（預設）｜sync", iDefault: "status", iChoices: new[] { "status", "sync" }),
        new SCP_CmdArgSpec("data_root", "資料根（宿主照設定檔補上）"),
        new SCP_CmdArgSpec("fetch", "status 用：1 ⇒ 先抓一次遠端（走網路）"),
        new SCP_CmdArgSpec("persona", "sync 用：有衝突要發酒館通知時署誰的名（不給 ⇒ 只印、不發）"),
    };

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        if (SenateConfigSource.Provider == null)
            return SCP_CmdResult.Fail(70, "✗ 宿主沒有裝上設定來源（SenateConfigSource.Provider）—— 程式錯誤，不是用法錯");
        (SenateConfig? aConfig, string aConfigPath) = SenateConfigSource.Provider();
        if (aConfig == null) return SCP_CmdResult.Fail(2, "✗ 讀不到設定檔：" + aConfigPath, "  先跑 `senate init`");
        string aDataRoot = iArgs.Get("data_root").Trim();
        if (aDataRoot.Length == 0)
        {
            string? aRoot = SenatePathBinding.ResolveDataRoot(aConfig, out string? aErr);
            if (aRoot == null) return SCP_CmdResult.Fail(2, "✗ 資料根解不出來：" + aErr);
            aDataRoot = aRoot;
        }
        aDataRoot = aDataRoot.Replace('\\', '/').TrimEnd('/');
        RepoSyncTarget? aTarget = RepoSyncTarget.From(aConfig.Sync.BookNotes);

        var r = new SCP_CmdResult();
        r.Lines.Add($"⤷ 閱讀筆記同步（本地）@ {SenateBookNotesSync.Dir(aDataRoot)}");
        if (iArgs.Get("op").Trim() == "sync")
        {
            if (aTarget == null)
                return SCP_CmdResult.Fail(2, "✗ 沒設定遠端（sync.bookNotes 的 repo、branch 兩格都要填）—— 後台「路徑管理」頁最下面那一格");
            RepoSyncReport s = SenateBookNotesSync.BeforeUse(aDataRoot, aTarget, iPullEverySec: 0);
            foreach (string l in s.Lines) r.Lines.Add(l);
            if (s.Lines.Count == 0) r.Lines.Add("✓ 跟遠端一致（沒有要拉、也沒有要推的）");
            SenateBookNotesSync.Notify(aDataRoot, iArgs.Get("persona").Trim(), s, r.Lines);
            r.AddValue("sync", s.StateWord);
            if (s.ConflictFile.Length > 0) r.AddValue("sync_conflict_file", s.ConflictFile);
            r.ExitCode = s.State is RepoSyncState.Conflict or RepoSyncState.Failed or RepoSyncState.Mismatch ? 1 : 0;
            return r;
        }

        BookNotesRepoStatus st = SenateBookNotesSync.Status(aDataRoot, aTarget, iArgs.Get("fetch").Trim() == "1");
        r.Lines.Add(st.Configured ? $"· 遠端：`{aTarget!.Repo}`（{aTarget.Branch}）" : "· 遠端：（沒設定 sync.bookNotes）");
        if (st.IsRepo) r.Lines.Add($"· 本機：origin `{st.Origin}`／`{st.Branch}`，領先 {st.Ahead}、落後 {st.Behind}、未提交 {st.Dirty}");
        r.Lines.Add("⇒ " + st.Note);
        r.AddValue("configured", st.Configured ? "1" : "0");
        r.AddValue("exists", st.Exists ? "1" : "0");
        r.AddValue("is_repo", st.IsRepo ? "1" : "0");
        r.AddValue("ahead", st.Ahead.ToString());
        r.AddValue("behind", st.Behind.ToString());
        return r;
    }
}
