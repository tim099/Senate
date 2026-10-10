// 區塊職責：雕刻（TASK-0363 搬到 Senate、TASK-0377 引擎改 in-process C#）的自我對拍 —— 全部在 temp 目錄淨室跑。
// 物理意義：這幾格錯了都不會叫：
//           ① 預授權的分母（clamp 後體積、PNG 寬高）—— 量錯 ⇒ 門擋錯人，或讓超額的一刀過去；費率 ⌈n/100⌉ 的邊界。
//           ② view 的輸出規則：沒有 out 也沒有 persona ⇒ 擋；⛔ 不寫任何共用的固定檔名圖；檔案與 values 對得上；
//              沒有渲染器 ⇒ exit 1 而且**沒有檔**（不出空白圖）。
//           ③ 渲染設定：set／use／show／list／copy／delete／reset；作用中的不准刪；不認得的鍵擋；persona 層要 persona；
//              疊層（共用 → 個人 → CLI）在 `layers` 看得到、而且真的照那個順序蓋；skybox 檔不存在 ⇒ 擋。
//           ③b 地板（Tim 2026-10-02）：設定檔 floor 物件逐欄疊、沒寫 enabled 的層不改開關、關著時欄位也記得；
//              不認得的鍵／越界／貼圖不存在 ⇒ 擋且零寫入；view 的 floor_* 一次性參數真的到渲染器參數。
//           ③c 框景放大（fit_upscale）：給了 region ⇒ 預設開、顯式 0 關、全景維持關、設定檔 camera.fit_upscale；
//              正交背景上抬（skybox_tilt）：CLI／設定檔都到參數、越界擋。
//           ④ 落子：收費（實際落地 ⇒ 扣幾張）、餘額不足 ⇒ 引擎不跑；沒有渲染器 ⇒ 落子照樣成功、分享略過並寫明原因；
//              有渲染器 ⇒ 分享圖直接寫 `previews/share_*.png` 並交給發文端。
//           ⚠ 渲染器與付款閘、發文端都換成探針（GPU／Server／酒館不在本對拍的射程 —— 那幾格走 exe 實跑）。
// 數值影響：只在 temp 目錄寫，跑完刪；⛔ 不碰真實資料根、不發酒館。全域掛點（渲染器／付款閘工廠／發文端）跑完一律還原。
#nullable enable
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureContractCleanRoom()
    {
        const string aName = "雕刻：預授權分母（clamp 體積／PNG 寬高）、費率邊界（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_sculpt_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aTmp);
            // ① 兩角任意順序 ＋ clamp 0..255：x 5..3 ⇒ 3..5（3 格）、y 0..0（1 格）、z 250..300 ⇒ 250..255（6 格）
            int x1 = 5, x2 = 3, y1 = 0, y2 = 0, z1 = 250, z2 = 300;
            long aVol = Cmd_Sculpture.ClampedVolume(ref x1, ref x2, ref y1, ref y2, ref z1, ref z2);
            bool aClamp = aVol == 18 && x1 == 3 && x2 == 5 && z2 == 255;
            // 🔴 整段在界外 ⇒ 0（⛔ 不准算成負數或 1）
            int a1 = 300, a2 = 400, b1 = 0, b2 = 0, c1 = 0, c2 = 0;
            bool aOutside = Cmd_Sculpture.ClampedVolume(ref a1, ref a2, ref b1, ref b2, ref c1, ref c2) == 0;

            // ② 費率邊界
            bool aRate = Cmd_Sculpture.CeilDiv(0, 100) == 0 && Cmd_Sculpture.CeilDiv(1, 100) == 1
                         && Cmd_Sculpture.CeilDiv(100, 100) == 1 && Cmd_Sculpture.CeilDiv(101, 100) == 2;

            // ① PNG 寬高由本檔自己量（3x2）；🔴 不是 PNG ⇒ false（⛔ 不猜預設值）
            string aPng = Path.Combine(aTmp, "probe.png");
            File.WriteAllBytes(aPng, SCP_CanvasPng.EncodeRgb(new byte[6], 0, 0, 3, 2, 3));
            bool aSize = Cmd_Sculpture.TryReadPngSize(aPng, out int aW, out int aH) && aW == 3 && aH == 2;
            string aFake = Path.Combine(aTmp, "fake.png");
            File.WriteAllText(aFake, "not a png at all, just text");
            bool aFakeRejected = !Cmd_Sculpture.TryReadPngSize(aFake, out _, out _);

            bool aOk = aClamp && aOutside && aRate && aSize && aFakeRejected;
            return new CheckRow(aName,
                $"clamp 體積={aVol}（期望 18）／🔴 全在界外⇒0={aOutside}／費率邊界={aRate}／PNG 寬高={aW}x{aH}"
                + $"／🔴 假 PNG 拒絕={aFakeRejected}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }

    // ───────────────────────────── 探針 ─────────────────────────────

    /// <summary>渲染器探針：記下最後一次收到的參數，回一張純色圖（不碰 GPU）。</summary>
    sealed class SculptProbeRenderer : ISCP_SculptRenderer
    {
        public SCP_SculptRenderParams? Last;
        public int LastVoxels = -1;
        public int Calls;
        public string Name => "selftest 探針渲染器";

        public bool TryRender(IReadOnlyList<SCP_SculptVoxel> iVoxels, SCP_SculptRenderParams iParams, out byte[] oRgba, out string oError)
        {
            Calls++;
            Last = iParams;
            LastVoxels = iVoxels.Count;
            oRgba = new byte[iParams.Width * iParams.Height * 4];
            for (int i = 0; i < oRgba.Length; i += 4) { oRgba[i] = 200; oRgba[i + 1] = 100; oRgba[i + 2] = 50; oRgba[i + 3] = 255; }
            oError = "";
            return true;
        }
    }

    /// <summary>付款閘探針：只有永久券（張數可設）、token 餘額 0；記下扣了幾張。</summary>
    sealed class SculptProbeGateway : SCP_ICanvasGateway
    {
        public int Vouchers;
        public int Consumed;
        public string HostQualifier => "selftest 探針付款閘";
        public SCP_CanvasTriState QueryInFreeTime(string iPersona, out string oDetail) { oDetail = "探針"; return SCP_CanvasTriState.No; }
        public int QueryExpiringVouchers(string iPersona, out string oDetail) { oDetail = "探針"; return 0; }
        public int QueryPermanentVouchers(string iPersona, out string oDetail) { oDetail = "探針"; return Vouchers; }
        public int QueryTavernVouchers(string iPersona, out string oDetail) { oDetail = "探針"; return 0; }
        public long QueryTokenBalance(string iAccountId, out string oDetail) { oDetail = "探針"; return 0; }
        public SCP_CanvasGateResult ConsumeVouchers(string iPersona, int iCount, string iSourceRef, string iDescription)
        {
            if (iCount > Vouchers) return SCP_CanvasGateResult.Bad("探針：不夠");
            Vouchers -= iCount; Consumed += iCount;
            return SCP_CanvasGateResult.Good("探針：扣 " + iCount);
        }
        public SCP_CanvasGateResult ConsumeTavernVouchers(string iPersona, int iCount, string iSourceRef, string iDescription)
            => SCP_CanvasGateResult.Bad("探針：沒有酒館券");
        public SCP_CanvasGateResult DebitTokens(string iAccountId, int iAmount, string iSourceKind, string iSourceRef, string iDescription)
            => SCP_CanvasGateResult.Bad("探針：沒有 token");
        public SCP_CanvasGateResult Share(string iPersona, string iRoom, string iBody, string? iAttachAbsolutePath = null, string? iTag = null)
            => SCP_CanvasGateResult.Bad("探針：雕刻不該走畫布閘的 Share");
    }

    /// <summary>淨室：temp 資料根＋信件夾根，`Run` 一律帶這兩個根（⛔ 不讓 Cmd 碰到真的 AgentCommands）。</summary>
    sealed class SculptRoom : IDisposable
    {
        public readonly string Root, Data, Letters;
        readonly ISCP_SculptRenderer? m_PrevRenderer = SCP_SculptRenderers.Current;
        readonly Func<string, SCP_ICanvasGateway?>? m_PrevGate = SCP_CanvasGatewayHost.Factory;
        readonly Func<IReadOnlyDictionary<string, string>, SCP_CmdResult> m_PrevPoster = Cmd_Sculpture.SharePoster;

        public SculptRoom()
        {
            Root = Path.Combine(Path.GetTempPath(), "senate_sculpt_" + Guid.NewGuid().ToString("N")[..8]).Replace('\\', '/');
            Data = Root + "/AgentCommands";
            Letters = Root + "/letters";
            Directory.CreateDirectory(Data);
            Directory.CreateDirectory(Letters);
        }

        public SCP_CmdResult Run(params (string K, string V)[] iArgs)
        {
            var a = new Dictionary<string, string>(StringComparer.Ordinal) { ["data_root"] = Data, ["letters_root"] = Letters };
            foreach (var (k, v) in iArgs) a[k] = v;
            return SCP_CmdRegistry.Dispatch("sculpture", a);
        }

        public static string V(SCP_CmdResult iR, string iKey) => iR.Values.LastOrDefault(kv => kv.Key == iKey).Value ?? "";
        public static string All(SCP_CmdResult iR) => string.Join("\n", iR.Lines);

        public string Sculpt => Data + "/Sculpture";
        public SCP_SculptEngine Engine() => new SCP_SculptEngine(new SCP_DataRoot(Data));
        public int EventCount() => Directory.Exists(Sculpt + "/events") ? Directory.GetFiles(Sculpt + "/events", "*.json", SearchOption.AllDirectories).Length : 0;
        public bool NoSharedViewPng() => !File.Exists(Sculpt + "/_last_view.png");

        public void Dispose()
        {
            SCP_SculptRenderers.Register(m_PrevRenderer!);
            SCP_CanvasGatewayHost.Factory = m_PrevGate;
            Cmd_Sculpture.SharePoster = m_PrevPoster;
            try { Directory.Delete(Root, true); } catch (Exception) { }
        }
    }

    // ───────────────────────────── view ─────────────────────────────

    static CheckRow SculptureViewCleanRoom()
    {
        const string aName = "雕刻 view：沒 out 沒 persona⇒擋／persona⇒個人 cmd 夾／out⇒那個檔＋values 對得上／沒渲染器⇒exit 1 且沒檔／⛔ 不寫共用檔名（淨室，TASK-0377）";
        try
        {
            using var r = new SculptRoom();
            var aProbe = new SculptProbeRenderer();
            SCP_SculptRenderers.Register(aProbe);
            r.Engine().Box(new SCP_SculptBoxArgs { X1 = 0, X2 = 2, Y1 = 0, Y2 = 1, Z1 = 0, Z2 = 0, Color = 19, Persona = "seed" });   // 6 顆

            // 🔴 沒 out 也沒 persona ⇒ 2；out 相對路徑 ⇒ 2
            SCP_CmdResult aNo = r.Run(("op", "view"));
            SCP_CmdResult aRel = r.Run(("op", "view"), ("out", "relative.png"));
            bool aGate = aNo.ExitCode == 2 && aRel.ExitCode == 2 && aProbe.Calls == 0;

            // persona ⇒ letters/p/cmd/sculpture_view.png
            SCP_CmdResult aP = r.Run(("op", "view"), ("persona", "p"));
            string aPPath = r.Letters + "/p/cmd/" + Cmd_Sculpture.ViewPngName;
            bool aPersona = aP.ExitCode == 0 && File.Exists(aPPath) && SculptRoom.V(aP, "path") == aPPath
                            && SculptRoom.V(aP, "visible_voxels") == "6" && SculptRoom.V(aP, "total_voxels") == "6"
                            && SculptRoom.V(aP, "width") == "1024" && SculptRoom.V(aP, "renderer") == aProbe.Name
                            && SculptRoom.V(aP, "sha256") == SCP_SculptPy.Sha256Hex(File.ReadAllBytes(aPPath))
                            && File.Exists(r.Letters + "/p/cmd/sculpture_view.md");

            // out=絕對路徑 ＋ 尺寸 ⇒ 檔就在那裡、PNG 真的是 64x32
            string aOut = r.Root + "/scratch/v.png";
            int aW = 0, aH = 0;
            SCP_CmdResult aO = r.Run(("op", "view"), ("out", aOut), ("width", "64"), ("height", "32"), ("region", "0..0,0..1,0..0"));
            bool aOutOk = aO.ExitCode == 0 && SculptRoom.V(aO, "path") == aOut
                          && Cmd_Sculpture.TryReadPngSize(aOut, out aW, out aH) && aW == 64 && aH == 32
                          && SculptRoom.V(aO, "visible_voxels") == "2" && SculptRoom.V(aO, "height") == "32";

            // 🔴 沒有渲染器 ⇒ exit 1、沒有檔
            SCP_SculptRenderers.Register(null!);
            string aNone = r.Root + "/scratch/none.png";
            SCP_CmdResult aN = r.Run(("op", "view"), ("out", aNone));
            bool aNoRenderer = aN.ExitCode == 1 && !File.Exists(aNone) && SculptRoom.All(aN).Contains("渲染器");

            bool aShared = r.NoSharedViewPng();
            bool aOk = aGate && aPersona && aOutOk && aNoRenderer && aShared;
            return new CheckRow(aName,
                $"🔴 沒 out/persona⇒{aNo.ExitCode}、相對 out⇒{aRel.ExitCode}={aGate}／persona 落點與 values={aPersona}（exit {aP.ExitCode}）"
                + $"／out＋64x32={aOutOk}（exit {aO.ExitCode}，{aW}x{aH}）／🔴 沒渲染器⇒exit {aN.ExitCode} 且沒檔={aNoRenderer}"
                + $"／沒有 _last_view.png={aShared}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    // ───────────────────────────── render-profile ─────────────────────────────

    static CheckRow SculptureRenderProfileCleanRoom()
    {
        const string aName = "雕刻 render-profile：set/use/show/list/copy/delete/reset、作用中不准刪、不認得的鍵擋、persona 層要 persona、"
                             + "疊層 共用→個人→CLI 照順序蓋且 layers 看得到、skybox 不存在⇒擋、view 一次性多燈、共用 use none（淨室，TASK-0377）";
        try
        {
            using var r = new SculptRoom();
            var aProbe = new SculptProbeRenderer();
            SCP_SculptRenderers.Register(aProbe);
            r.Engine().Box(new SCP_SculptBoxArgs { X1 = 0, X2 = 0, Y1 = 0, Y2 = 0, Z1 = 0, Z2 = 0, Color = 19, Persona = "seed" });
            string aSharedDir = r.Sculpt + "/render_profiles";
            string aBase = aSharedDir + "/base.json";

            // set（新建）＋ use
            SCP_CmdResult aSet = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("yaw", "100"), ("skybox", "none"));
            SCP_CmdResult aUse = r.Run(("op", "render-profile"), ("sub", "use"), ("name", "base"));
            bool aSetUse = aSet.ExitCode == 0 && File.Exists(aBase) && aUse.ExitCode == 0
                           && SCP_SculptRenderProfiles.GetActive(r.Sculpt) == "base";

            // 🔴 不認得的鍵：① 規格外（yaww）由預檢擋 ② 規格內但 set 不吃（light_dir）③ 值越界（pitch 100）—— 三個都擋、檔案位元組不變
            byte[] aBefore = File.ReadAllBytes(aBase);
            SCP_CmdResult aBad1 = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("yaww", "90"));
            SCP_CmdResult aBad2 = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("light_dir", "1,1,1"));
            SCP_CmdResult aBad3 = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("pitch", "100"));
            SCP_CmdResult aBad4 = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("skybox", "nope.jpg"));
            bool aUnknown = aBad1.ExitCode == 2 && aBad2.ExitCode == 2 && aBad3.ExitCode == 2 && aBad4.ExitCode == 2
                            && File.ReadAllBytes(aBase).SequenceEqual(aBefore);

            // 🔴 persona 層沒給 persona ⇒ 擋
            SCP_CmdResult aNoP = r.Run(("op", "render-profile"), ("sub", "set"), ("scope", "persona"), ("name", "mine"), ("pitch", "10"));
            bool aNeedPersona = aNoP.ExitCode == 2 && !Directory.Exists(r.Letters + "/p");

            // copy 共用 → 個人，再改個人一格、設作用中
            SCP_CmdResult aCopy = r.Run(("op", "render-profile"), ("sub", "copy"), ("persona", "p"),
                                        ("from_scope", "shared"), ("from", "base"), ("to_scope", "persona"), ("to", "mine"));
            SCP_CmdResult aCopyAgain = r.Run(("op", "render-profile"), ("sub", "copy"), ("persona", "p"),
                                             ("from_scope", "shared"), ("from", "base"), ("to_scope", "persona"), ("to", "mine"));
            SCP_CmdResult aSetP = r.Run(("op", "render-profile"), ("sub", "set"), ("scope", "persona"), ("persona", "p"),
                                        ("name", "mine"), ("pitch", "10"), ("unset", "skybox,yaw"));
            SCP_CmdResult aUseP = r.Run(("op", "render-profile"), ("sub", "use"), ("scope", "persona"), ("persona", "p"), ("name", "mine"));
            bool aCopyOk = aCopy.ExitCode == 0 && aCopyAgain.ExitCode == 2 && aSetP.ExitCode == 0 && aUseP.ExitCode == 0
                           && File.Exists(r.Letters + "/p/sculpture/render_profiles/mine.json");

            // 疊層：共用 base（yaw 100、skybox none）→ 個人 mine（pitch 10）→ CLI（yaw 135）
            SCP_CmdResult aV = r.Run(("op", "view"), ("persona", "p"), ("yaw", "135"));
            string aLayers = SculptRoom.V(aV, "layers");
            int iShared = aLayers.IndexOf("共用設定 `base`", StringComparison.Ordinal);
            int iMine = aLayers.IndexOf("p 個人設定 `mine`", StringComparison.Ordinal);
            int iCli = aLayers.IndexOf("CLI（yaw=135）", StringComparison.Ordinal);
            SCP_SculptRenderParams? aP1 = aProbe.Last;
            bool aLayering = aV.ExitCode == 0 && iShared >= 0 && iMine > iShared && iCli > iMine
                             && aP1 != null && aP1.YawDeg == 135 && aP1.PitchDeg == 10 && aP1.Skybox == SCP_SculptRenderParams.SkyboxNone;
            // 沒 CLI ⇒ 共用的 yaw 100 生效
            SCP_CmdResult aV2 = r.Run(("op", "view"), ("persona", "p"));
            bool aNoCli = aV2.ExitCode == 0 && aProbe.Last != null && aProbe.Last.YawDeg == 100 && !SculptRoom.V(aV2, "layers").Contains("CLI");
            // profile= 一次性指定 ⇒ 不走個人那層（pitch 回到預設 30）
            SCP_CmdResult aV3 = r.Run(("op", "view"), ("persona", "p"), ("profile", "base"));
            bool aOneOff = aV3.ExitCode == 0 && aProbe.Last != null && aProbe.Last.PitchDeg == 30 && aProbe.Last.YawDeg == 100
                           && SculptRoom.V(aV3, "layers").Contains("profile= 指定");

            // 一次性多燈（CLI 層，語法同 set）：light_add 接在下層的燈之後；lights 整組取代；light_clear 清空；與 light_dir 同給 ⇒ 擋
            SCP_CmdResult aLa = r.Run(("op", "view"), ("persona", "p"), ("light_add", "1,0,-1;#ff0000;0.5;0"));
            SCP_SculptRenderParams? aPl = aProbe.Last;
            bool aLightAdd = aLa.ExitCode == 0 && aPl != null && aPl.Lights.Count == 2 && aPl.Lights[1].R == 255 && aPl.Lights[1].G == 0
                             && aPl.Lights[1].Intensity == 0.5 && !aPl.Lights[1].CastShadow && aPl.Lights[0].DirZ == -1
                             && SculptRoom.V(aLa, "layers").Contains("light_add=");
            SCP_CmdResult aLs = r.Run(("op", "view"), ("persona", "p"), ("lights", "[{\"dir\":[0,0,-1]},{\"dir\":[1,1,-1],\"intensity\":2}]"));
            bool aLightsSet = aLs.ExitCode == 0 && aProbe.Last != null && aProbe.Last.Lights.Count == 2 && aProbe.Last.Lights[1].Intensity == 2;
            SCP_CmdResult aLc = r.Run(("op", "view"), ("persona", "p"), ("light_clear", "1"));
            bool aLightClear = aLc.ExitCode == 0 && aProbe.Last != null && aProbe.Last.Lights.Count == 0;
            int aCallsLight = aProbe.Calls;
            SCP_CmdResult aLBoth = r.Run(("op", "view"), ("persona", "p"), ("light_dir", "1,1,-1"), ("light_add", "1,0,-1"));
            SCP_CmdResult aLBad = r.Run(("op", "view"), ("persona", "p"), ("light_add", "1,0;#zz0000"));
            bool aLightGuards = aLBoth.ExitCode == 2 && aLBad.ExitCode == 2 && aProbe.Calls == aCallsLight;
            bool aLightsOk = aLightAdd && aLightsSet && aLightClear && aLightGuards;

            // show／list
            SCP_CmdResult aShowN = r.Run(("op", "render-profile"), ("sub", "show"), ("name", "base"));
            SCP_CmdResult aShowE = r.Run(("op", "render-profile"), ("sub", "show"), ("persona", "p"));
            SCP_CmdResult aList = r.Run(("op", "render-profile"), ("sub", "list"), ("persona", "p"));
            bool aShowList = aShowN.ExitCode == 0 && SculptRoom.V(aShowN, "active") == "1"
                             && aShowE.ExitCode == 0 && SculptRoom.V(aShowE, "layers").Contains("p 個人設定 `mine`")
                             && SculptRoom.All(aShowE).Contains("\"pitch\"")
                             && aList.ExitCode == 0 && SculptRoom.V(aList, "shared_active") == "base"
                             && SculptRoom.V(aList, "persona_active") == "mine" && SculptRoom.V(aList, "count") == "2";

            // 🔴 作用中不准刪；reset 出一份再刪 ⇒ 可以
            SCP_CmdResult aDelActive = r.Run(("op", "render-profile"), ("sub", "delete"), ("name", "base"));
            SCP_CmdResult aReset = r.Run(("op", "render-profile"), ("sub", "reset"), ("name", "extra"));
            bool aResetEmpty = File.Exists(aSharedDir + "/extra.json") && File.ReadAllText(aSharedDir + "/extra.json").Trim() == "{}";
            SCP_CmdResult aDel = r.Run(("op", "render-profile"), ("sub", "delete"), ("name", "extra"));
            bool aDelete = aDelActive.ExitCode == 2 && File.Exists(aBase) && aReset.ExitCode == 0 && aResetEmpty
                           && aDel.ExitCode == 0 && !File.Exists(aSharedDir + "/extra.json");

            // use name=none（個人層）⇒ 回到只跟共用走
            SCP_CmdResult aNone = r.Run(("op", "render-profile"), ("sub", "use"), ("scope", "persona"), ("persona", "p"), ("name", "none"));
            bool aFollow = aNone.ExitCode == 0 && !SculptRoom.V(aNone, "layers").Contains("個人");

            // 🔴 skybox 不存在：CLI 給 ⇒ 擋、沒渲染；設定檔裡有不認得的鍵 ⇒ use 擋、作用中被手改指過去 ⇒ view 擋
            int aCallsBefore = aProbe.Calls;
            SCP_CmdResult aSky = r.Run(("op", "view"), ("persona", "p"), ("skybox", "missing_sky.jpg"));
            File.WriteAllText(aSharedDir + "/typo.json", "{ \"yaww\": 90 }\n");
            SCP_CmdResult aUseTypo = r.Run(("op", "render-profile"), ("sub", "use"), ("name", "typo"));
            SCP_SculptRenderProfiles.SetActive(r.Sculpt, "typo");
            SCP_CmdResult aVTypo = r.Run(("op", "view"), ("persona", "p"));
            bool aGuards = aSky.ExitCode == 2 && SculptRoom.All(aSky).Contains("skybox 圖不存在")
                           && aUseTypo.ExitCode == 2 && aVTypo.ExitCode == 2 && SculptRoom.All(aVTypo).Contains("yaww")
                           && aProbe.Calls == aCallsBefore;
            // 共用層 use name=none ⇒ 作用中清掉、回到只剩內建預設（壞掉的 typo 也就不再擋 view）
            SCP_CmdResult aSharedNone = r.Run(("op", "render-profile"), ("sub", "use"), ("name", "none"));
            SCP_CmdResult aVBuiltin = r.Run(("op", "view"), ("persona", "p"));
            bool aSharedCleared = aSharedNone.ExitCode == 0 && SCP_SculptRenderProfiles.GetActive(r.Sculpt) == null
                                  && aVBuiltin.ExitCode == 0 && SculptRoom.V(aVBuiltin, "layers") == "內建預設"
                                  && aProbe.Last != null && aProbe.Last.YawDeg == 45;

            bool aOk = aSetUse && aUnknown && aNeedPersona && aCopyOk && aLayering && aNoCli && aOneOff && aShowList && aDelete && aFollow && aGuards
                       && aLightsOk && aSharedCleared && r.NoSharedViewPng();
            return new CheckRow(aName,
                $"set+use={aSetUse}／🔴 不認得鍵/不吃的鍵/越界/天空不存在 都擋且零寫入={aUnknown}（{aBad1.ExitCode},{aBad2.ExitCode},{aBad3.ExitCode},{aBad4.ExitCode}）"
                + $"／🔴 persona 層要 persona={aNeedPersona}／copy（重複⇒擋）+個人 set/use={aCopyOk}"
                + $"／疊層照順序={aLayering}〔{aLayers}〕／沒 CLI⇒共用值={aNoCli}／profile= 一次性={aOneOff}"
                + $"／show+list={aShowList}／🔴 作用中不准刪、reset+delete={aDelete}／use none⇒跟共用={aFollow}／🔴 天空與壞設定擋={aGuards}"
                + $"／一次性多燈 add={aLightAdd} lights={aLightsSet} clear={aLightClear} 🔴 與 light_dir 同給／壞語法擋={aLightGuards}"
                + $"／共用 use none⇒內建預設={aSharedCleared}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    // ───────────────────────────── 地板 ─────────────────────────────

    static CheckRow SculptureFloorCleanRoom()
    {
        const string aName = "雕刻地板：設定檔 floor 逐欄疊（沒寫 enabled 的層不改開關、關著也記得欄位）、不認得鍵／越界／貼圖不存在⇒擋且零寫入、"
                             + "unset floor／floor_*、view 的 floor_* 到渲染器參數（淨室，TASK-0377）";
        try
        {
            using var r = new SculptRoom();
            var aProbe = new SculptProbeRenderer();
            SCP_SculptRenderers.Register(aProbe);
            r.Engine().Box(new SCP_SculptBoxArgs { X1 = 0, X2 = 1, Y1 = 0, Y2 = 1, Z1 = 0, Z2 = 0, Color = 19, Persona = "seed" });
            string aFloors = r.Sculpt + "/floors";
            Directory.CreateDirectory(aFloors);
            File.WriteAllBytes(aFloors + "/t.png", SCP_CanvasPng.EncodeRgb(new byte[12], 0, 0, 2, 2, 2));
            string aTexAbs = aFloors + "/t.png";
            string aBase = r.Sculpt + "/render_profiles/base.json";

            // 內建預設 ⇒ 沒有地板
            SCP_CmdResult aV0 = r.Run(("op", "view"), ("persona", "p"));
            bool aDefaultNone = aV0.ExitCode == 0 && aProbe.Last != null && aProbe.Last.Floor == null;

            // 共用：開地板、內建網格、z 2；個人：只寫貼圖 ⇒ 開關沿用共用（開）、z 沿用 2、貼圖換成 t.png
            SCP_CmdResult aSet = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("floor", "on"), ("floor_texture", "builtin"), ("floor_z", "2"));
            SCP_CmdResult aUse = r.Run(("op", "render-profile"), ("sub", "use"), ("name", "base"));
            SCP_CmdResult aSetP = r.Run(("op", "render-profile"), ("sub", "set"), ("scope", "persona"), ("persona", "p"), ("name", "mine"), ("floor_texture", "t.png"));
            SCP_CmdResult aUseP = r.Run(("op", "render-profile"), ("sub", "use"), ("scope", "persona"), ("persona", "p"), ("name", "mine"));
            SCP_CmdResult aV1 = r.Run(("op", "view"), ("persona", "p"));
            SCP_SculptFloor? f1 = aProbe.Last?.Floor;
            bool aLayerOn = aSet.ExitCode == 0 && aUse.ExitCode == 0 && aSetP.ExitCode == 0 && aUseP.ExitCode == 0 && aV1.ExitCode == 0
                            && f1 != null && f1.Z == 2 && f1.Texture == aTexAbs && !f1.FullGrid && f1.Margin == 24;
            // 沒給 persona（只有共用層）⇒ 內建網格（Texture null）
            SCP_CmdResult aV1s = r.Run(("op", "view"), ("out", r.Root + "/s.png"));
            bool aSharedOnly = aV1s.ExitCode == 0 && aProbe.Last?.Floor != null && aProbe.Last.Floor.Texture == null && aProbe.Last.Floor.Z == 2;

            // 共用關掉 ⇒ 個人只寫貼圖的那層「不改開關」⇒ 沒有地板；但 CLI floor=on ⇒ 開，且記得 z 2 與 t.png
            SCP_CmdResult aOff = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("floor", "off"));
            SCP_CmdResult aV2 = r.Run(("op", "view"), ("persona", "p"));
            bool aOffKept = aOff.ExitCode == 0 && aV2.ExitCode == 0 && aProbe.Last != null && aProbe.Last.Floor == null;
            SCP_CmdResult aV3 = r.Run(("op", "view"), ("persona", "p"), ("floor", "on"));
            SCP_SculptFloor? f3 = aProbe.Last?.Floor;
            bool aRemembered = aV3.ExitCode == 0 && f3 != null && f3.Z == 2 && f3.Texture == aTexAbs
                               && SculptRoom.V(aV3, "layers").Contains("floor=on");

            // view 的一次性 floor_* 全部到參數
            SCP_CmdResult aV4 = r.Run(("op", "view"), ("persona", "p"), ("floor", "on"), ("floor_full_grid", "1"), ("floor_margin", "3"),
                                      ("floor_tile", "4"), ("floor_color", "#102030"), ("floor_fade", "0.25"), ("floor_z", "-1.5"), ("floor_texture", "builtin"));
            SCP_SculptFloor? f4 = aProbe.Last?.Floor;
            bool aCli = aV4.ExitCode == 0 && f4 != null && f4.FullGrid && f4.Margin == 3 && f4.TileSize == 4 && f4.R == 0x10 && f4.G == 0x20
                        && f4.B == 0x30 && f4.Fade == 0.25 && f4.Z == -1.5 && f4.Texture == null;

            // 🔴 擋：越界、貼圖不存在、規格內但值壞（color）—— set 零寫入；view 不渲染
            byte[] aBefore = File.ReadAllBytes(aBase);
            int aCalls = aProbe.Calls;
            SCP_CmdResult aB1 = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("floor_fade", "0.9"));
            SCP_CmdResult aB2 = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("floor_texture", "missing.jpg"));
            SCP_CmdResult aB3 = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("floor_color", "#zz0000"));
            SCP_CmdResult aB4 = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("floor", "maybe"));
            SCP_CmdResult aB5 = r.Run(("op", "view"), ("persona", "p"), ("floor_texture", "missing.jpg"));
            SCP_CmdResult aB6 = r.Run(("op", "view"), ("persona", "p"), ("floor_tile", "0"));
            bool aGuards = aB1.ExitCode == 2 && aB2.ExitCode == 2 && aB3.ExitCode == 2 && aB4.ExitCode == 2 && aB5.ExitCode == 2 && aB6.ExitCode == 2
                           && SculptRoom.All(aB2).Contains("地板貼圖不存在") && SculptRoom.All(aB5).Contains("地板貼圖不存在")
                           && File.ReadAllBytes(aBase).SequenceEqual(aBefore) && aProbe.Calls == aCalls;
            // 🔴 設定檔裡 floor 物件有不認得的鍵 ⇒ use 擋
            File.WriteAllText(r.Sculpt + "/render_profiles/typo.json", "{ \"floor\": { \"enabeld\": true } }\n");
            SCP_CmdResult aTypo = r.Run(("op", "render-profile"), ("sub", "use"), ("name", "typo"));
            bool aTypoBlocked = aTypo.ExitCode == 2 && SculptRoom.All(aTypo).Contains("floor.enabeld") && SCP_SculptRenderProfiles.GetActive(r.Sculpt) == "base";

            // unset：floor_z 拿掉一格、floor 拿掉整個物件
            SCP_CmdResult aU1 = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("unset", "floor_z"));
            string aJ1 = File.ReadAllText(aBase);
            SCP_CmdResult aU2 = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("unset", "floor"));
            string aJ2 = File.ReadAllText(aBase);
            bool aUnset = aU1.ExitCode == 0 && aJ1.Contains("\"floor\"") && !aJ1.Contains("\"z\"") && aU2.ExitCode == 0 && !aJ2.Contains("\"floor\"");

            // show（沒給 name）印出生效的 floor 物件
            SCP_CmdResult aShow = r.Run(("op", "render-profile"), ("sub", "show"), ("persona", "p"));
            bool aShowFloor = aShow.ExitCode == 0 && SculptRoom.All(aShow).Contains("\"floor\"") && SculptRoom.All(aShow).Contains("t.png");

            bool aOk = aDefaultNone && aLayerOn && aSharedOnly && aOffKept && aRemembered && aCli && aGuards && aTypoBlocked && aUnset && aShowFloor;
            return new CheckRow(aName,
                $"內建預設沒地板={aDefaultNone}／共用開＋個人只換貼圖⇒開著且換了={aLayerOn}／只看共用⇒網格={aSharedOnly}"
                + $"／共用關⇒個人層不改開關={aOffKept}／CLI 開⇒記得 z 與貼圖={aRemembered}／CLI floor_* 全到參數={aCli}"
                + $"／🔴 越界/貼圖不存在/壞色/壞開關 擋且零寫入、view 不渲染={aGuards}（{aB1.ExitCode},{aB2.ExitCode},{aB3.ExitCode},{aB4.ExitCode},{aB5.ExitCode},{aB6.ExitCode}）"
                + $"／🔴 floor 物件錯字⇒use 擋={aTypoBlocked}／unset floor_z、floor={aUnset}／show 印 floor={aShowFloor}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    static CheckRow SculptureFitAndTiltCleanRoom()
    {
        const string aName = "雕刻框景放大＋背景上抬：region⇒fit_upscale 開、顯式 0 關、全景維持關、設定檔 camera.fit_upscale；skybox_tilt CLI／設定檔到參數、越界擋（淨室，TASK-0377）";
        try
        {
            using var r = new SculptRoom();
            var aProbe = new SculptProbeRenderer();
            SCP_SculptRenderers.Register(aProbe);
            r.Engine().Box(new SCP_SculptBoxArgs { X1 = 0, X2 = 1, Y1 = 0, Y2 = 1, Z1 = 0, Z2 = 0, Color = 19, Persona = "seed" });

            SCP_CmdResult aFull = r.Run(("op", "view"), ("persona", "p"));
            bool aFullOff = aFull.ExitCode == 0 && aProbe.Last != null && !aProbe.Last.FitUpscale;
            SCP_CmdResult aReg = r.Run(("op", "view"), ("persona", "p"), ("region", "0..1,0..1,0..0"));
            bool aRegOn = aReg.ExitCode == 0 && aProbe.Last != null && aProbe.Last.FitUpscale;
            SCP_CmdResult aReg0 = r.Run(("op", "view"), ("persona", "p"), ("region", "0..1,0..1,0..0"), ("fit_upscale", "0"));
            bool aRegExplicitOff = aReg0.ExitCode == 0 && aProbe.Last != null && !aProbe.Last.FitUpscale;
            SCP_CmdResult aFull1 = r.Run(("op", "view"), ("persona", "p"), ("fit_upscale", "1"));
            bool aFullExplicitOn = aFull1.ExitCode == 0 && aProbe.Last != null && aProbe.Last.FitUpscale;

            // 設定檔 camera.fit_upscale ＋ skybox.tilt
            SCP_CmdResult aSet = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("fit_upscale", "1"), ("skybox_tilt", "20"));
            SCP_CmdResult aUse = r.Run(("op", "render-profile"), ("sub", "use"), ("name", "base"));
            SCP_CmdResult aVp = r.Run(("op", "view"), ("persona", "p"));
            bool aProfile = aSet.ExitCode == 0 && aUse.ExitCode == 0 && aVp.ExitCode == 0 && aProbe.Last != null
                            && aProbe.Last.FitUpscale && aProbe.Last.SkyboxTiltDeg == 20
                            && File.ReadAllText(r.Sculpt + "/render_profiles/base.json").Contains("\"fit_upscale\"");
            // CLI skybox_tilt 蓋過設定檔；🔴 越界擋（CLI 與 set 都擋、零渲染零寫入）
            SCP_CmdResult aTilt = r.Run(("op", "view"), ("persona", "p"), ("skybox_tilt", "-12.5"));
            bool aTiltCli = aTilt.ExitCode == 0 && aProbe.Last != null && aProbe.Last.SkyboxTiltDeg == -12.5;
            int aCalls = aProbe.Calls;
            byte[] aBefore = File.ReadAllBytes(r.Sculpt + "/render_profiles/base.json");
            SCP_CmdResult aBad1 = r.Run(("op", "view"), ("persona", "p"), ("skybox_tilt", "100"));
            SCP_CmdResult aBad2 = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("skybox_tilt", "95"));
            SCP_CmdResult aBad3 = r.Run(("op", "view"), ("persona", "p"), ("fit_upscale", "maybe"));
            bool aGuards = aBad1.ExitCode == 2 && aBad2.ExitCode == 2 && aBad3.ExitCode == 2 && aProbe.Calls == aCalls
                           && File.ReadAllBytes(r.Sculpt + "/render_profiles/base.json").SequenceEqual(aBefore);
            // unset skybox_tilt ⇒ 回到 0
            SCP_CmdResult aUn = r.Run(("op", "render-profile"), ("sub", "set"), ("name", "base"), ("unset", "skybox_tilt,fit_upscale"));
            SCP_CmdResult aVu = r.Run(("op", "view"), ("persona", "p"));
            bool aUnset = aUn.ExitCode == 0 && aVu.ExitCode == 0 && aProbe.Last != null && aProbe.Last.SkyboxTiltDeg == 0 && !aProbe.Last.FitUpscale;

            bool aOk = aFullOff && aRegOn && aRegExplicitOff && aFullExplicitOn && aProfile && aTiltCli && aGuards && aUnset;
            return new CheckRow(aName,
                $"全景⇒關={aFullOff}／region⇒開={aRegOn}／region＋fit_upscale=0⇒關={aRegExplicitOff}／全景＋fit_upscale=1⇒開={aFullExplicitOn}"
                + $"／設定檔 fit_upscale＋tilt={aProfile}／CLI tilt 蓋設定檔={aTiltCli}／🔴 越界與壞值擋、零渲染零寫入={aGuards}"
                + $"（{aBad1.ExitCode},{aBad2.ExitCode},{aBad3.ExitCode}）／unset⇒回預設={aUnset}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    // ───────────────────────────── 落子 ─────────────────────────────

    static CheckRow SculpturePlaceCleanRoom()
    {
        const string aName = "雕刻落子：按實際落地收費／不夠⇒引擎不跑零事件／沒渲染器⇒落子成功、分享略過有原因／有渲染器⇒share_*.png 交給發文端／"
                             + "貼圖對不上 expect_pixels⇒exit 5 不收費（淨室，TASK-0377）";
        try
        {
            using var r = new SculptRoom();
            var aGate = new SculptProbeGateway { Vouchers = 10 };
            SCP_CanvasGatewayHost.Factory = _ => aGate;
            var aPosts = new List<IReadOnlyDictionary<string, string>>();
            Cmd_Sculpture.SharePoster = a =>
            {
                aPosts.Add(a);
                var aRes = new SCP_CmdResult();
                aRes.AddValue("post_seq", "42");
                return aRes;
            };
            (string, string)[] Box(int iX) => new[]
            {
                ("op", "box"), ("persona", "p"), ("account", "acct"),
                ("x1", iX.ToString()), ("x2", (iX + 1).ToString()), ("y1", "0"), ("y2", "1"), ("z1", "0"), ("z2", "0"), ("color", "19"),
            };

            // ① 沒渲染器：落子成功（4 顆 ⇒ 收 1 張）、分享略過、原因在 values 與回傳檔
            SCP_SculptRenderers.Register(null!);
            SCP_CmdResult aB1 = r.Run(Box(0));
            string aReport = r.Letters + "/p/cmd/sculpture_box.md";
            bool aNoRenderer = aB1.ExitCode == 0 && SculptRoom.V(aB1, "placed") == "4" && SculptRoom.V(aB1, "charged") == "1"
                               && aGate.Consumed == 1 && SculptRoom.V(aB1, "share_skipped").Contains("渲染器")
                               && aPosts.Count == 0 && !Directory.Exists(r.Sculpt + "/previews")
                               && File.Exists(aReport) && File.ReadAllText(aReport).Contains("## share")
                               && File.Exists(SculptRoom.V(aB1, "event_file"));

            // ② 有渲染器：分享圖寫 previews/share_*.png、發文端拿到 refs 指向它
            var aProbe = new SculptProbeRenderer();
            SCP_SculptRenderers.Register(aProbe);
            SCP_CmdResult aB2 = r.Run(Box(10));
            string aShare = SculptRoom.V(aB2, "share_png");
            bool aShared = aB2.ExitCode == 0 && aShare.Contains("/Sculpture/previews/share_") && File.Exists(aShare)
                           && aPosts.Count == 1 && aPosts[0]["refs"] == aShare && aPosts[0]["persona"] == "p"
                           && SculptRoom.V(aB2, "share_seq") == "42" && SculptRoom.V(aB2, "share_skipped") == ""
                           && aProbe.LastVoxels == 8 && aGate.Consumed == 2;

            // ③ 不夠：最壞 ⌈(20..39 × 0..9 × 0..0 = 200)/100⌉ = 2 張，只剩 0 張 ⇒ exit 3、引擎沒跑
            aGate.Vouchers = 0;
            int aEvBefore = r.EventCount();
            SCP_CmdResult aPoor = r.Run(("op", "box"), ("persona", "p"), ("account", "acct"),
                                        ("x1", "20"), ("x2", "39"), ("y1", "0"), ("y2", "9"), ("z1", "0"), ("z2", "0"), ("share", "0"));
            bool aRefused = aPoor.ExitCode == 3 && r.EventCount() == aEvBefore && aGate.Consumed == 2;

            // ④ carve：挖掉 ① 的 4 顆 ⇒ 收 1 張；share=0 ⇒ 略過原因寫明
            aGate.Vouchers = 5;
            SCP_CmdResult aCarve = r.Run(("op", "carve"), ("persona", "p"), ("account", "acct"),
                                         ("x1", "0"), ("x2", "1"), ("y1", "0"), ("y2", "1"), ("z1", "0"), ("z2", "0"), ("share", "0"));
            bool aCarved = aCarve.ExitCode == 0 && SculptRoom.V(aCarve, "placed") == "4" && SculptRoom.V(aCarve, "charged") == "1"
                           && SculptRoom.V(aCarve, "share_skipped").Contains("share=0") && aPosts.Count == 1;

            // ⑤ stampimg 對不上 expect_pixels ⇒ exit 5、沒扣、沒事件
            string aPng = r.Root + "/stamp.png";
            File.WriteAllBytes(aPng, SCP_CanvasPng.EncodeRgb(new byte[] { 224, 28, 3, 200 }, 0, 0, 2, 2, 2));
            int aConsumedBefore = aGate.Consumed;
            aEvBefore = r.EventCount();
            SCP_CmdResult aMis = r.Run(("op", "stampimg"), ("persona", "p"), ("account", "acct"), ("png", aPng),
                                       ("at", "50,50,50"), ("expect_pixels", "3"), ("share", "0"));
            SCP_CmdResult aImg = r.Run(("op", "stampimg"), ("persona", "p"), ("account", "acct"), ("png", aPng),
                                       ("at", "50,50,50"), ("expect_pixels", "4"), ("share", "0"));
            bool aStamp = aMis.ExitCode == 5 && aGate.Consumed == aConsumedBefore + 1 && r.EventCount() == aEvBefore + 1
                          && aImg.ExitCode == 0 && SculptRoom.V(aImg, "placed") == "4";
            // （+1 是 aImg 那一刀：mismatch 那一刀零扣零事件）

            bool aOk = aNoRenderer && aShared && aRefused && aCarved && aStamp && r.NoSharedViewPng();
            return new CheckRow(aName,
                $"沒渲染器⇒落子 exit {aB1.ExitCode}、分享略過有原因={aNoRenderer}〔{SculptRoom.V(aB1, "share_skipped")}〕"
                + $"／有渲染器⇒share png＋發文={aShared}（exit {aB2.ExitCode}）／🔴 不夠⇒exit {aPoor.ExitCode} 零事件={aRefused}"
                + $"／carve 收費＋share=0 原因={aCarved}／🔴 expect_pixels 對不上⇒exit {aMis.ExitCode} 零扣零事件，對得上⇒{aImg.ExitCode}={aStamp}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }
}
