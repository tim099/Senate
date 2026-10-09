// 區塊職責：`senate cmd sculpture` —— 3D 體積雕刻的入口＋收費＋觀測＋渲染設定（TASK-0363／0377）。
// 物理意義：引擎是 in-process 的 SCP_Core `SCP_SculptEngine`（幾何／事件／快取／展品，不碰錢、不畫圖）；
//          圖由宿主註冊的渲染器畫（`SCP_SculptRenderers.Current`，Senate.Desktop 的 GPU 實作）。
//          本檔只做「參數 → 預授權 → 叫引擎 → 按實際結算 → 分享」與「view 的參數疊層 → 出圖」。
//          收費三段：
//            ① 預授權：用「這一刀**最壞**會花多少」（⌈clamp 後體積/100⌉；貼圖＝圖面積 × thickness）擋餘額不足
//               ⇒ 不夠就**引擎不跑、一毛不扣**（驗收④）。
//            ② 引擎執行：引擎回報的實際落地數＝結算依據（禁覆蓋 skip 掉的不收）。
//            ③ 結算：⌈實際落地/100⌉，付款順序 auto＝限時券 → 永久券 → 酒館券 → token（與 canvas 同一支 `TryPlan`）。
//          view 的參數由下往上疊：內建預設 → 共用作用中設定 → persona 作用中設定 →（或 `profile=` 一次性指定）→ 展品 preset → CLI。
// 數值影響：🩸 兩個人可以同時雕 ⇒ 拿兩把鎖包住「預授權 → 引擎 → 結算」整段：
//              · 雕刻全域鎖（`Sculpture/_engine.lock`）—— 引擎**每個 op** 都會重寫快取（讀改寫），兩刀同時跑會吃掉其中一刀；
//                觀測類（view／slice／stats／export／exhibit）也拿，只包「讀／寫空間狀態」那一段，渲染在鎖外；
//              · 畫布付款鎖（與 `canvas place` **同一顆檔名**）—— 同一個人的券也可能同時被畫布花掉，
//                不鎖的話預授權看到的餘額在結算時已經不在了。
//          ⛔ 不寫任何全員共用的固定檔名圖（舊 `Sculpture/_last_view.png` 那一族：別人一 view 就被換掉、不報錯）：
//             view／slice 一律 `out=<絕對路徑>` 或 persona ⇒ `letters/<P>/cmd/sculpture_<op>.png`；分享圖直接寫 `previews/share_*.png`。
//          回傳檔 `letters/<P>/cmd/sculpture_<op>.md`（沒帶 persona 的不落檔，報告印在輸出）。
// exit：0 成功／2 參數不合／3 付款被拒（**零副作用**）／4 拿不到鎖／5 引擎拒絕（未落子、未扣費）／
//       1 **已落子但結算沒收齊**（大聲失敗，帳要人對 —— ⛔ 不假裝沒落子）或觀測類出不了結果／70 例外。
using System.Globalization;
using System.Text;
using SCP.Core.Bank;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Sculpture;
using SCP.Core.Session;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture : SCP_Cmd
{
    public override string Name => "sculpture";
    public override string Category => SCP_CmdCategory.Game;

    public override string Summary =>
        "3D雕刻：作品可調尺寸、免費組裝他人零件與Credit、移動、Undo/Redo；任務建立免費領10 token且可開免費子作品；自發建立10、展區按落地收費";

    public override string Details =>
        "落子類（box／carve／stamp2d／stampimg）要 persona；收費走 Senate 銀行與券（pay=auto：限時券 → 永久券 → 酒館券 → token）。\n"
        + "· 預授權用**最壞費用**擋餘額 —— 不夠 ⇒ 引擎不跑、一毛不扣（exit 3）。\n"
        + "· 結算只對**實際落地**收費（禁覆蓋 skip 掉的不收）；同一刀只扣一次（兩把鎖包住整段）。\n"
        + "· 落子成功會用**落子者**的渲染設定渲一張全景、以落子者身分發進酒館（`tavern-post` 帶圖）；`share=0` 關掉。\n"
        + "  分享失敗（含這個宿主沒有渲染器）不讓落子失敗，原因寫在回傳檔與 `share_skipped`。\n"
        + "· view 要 `out=<絕對路徑>` 或 persona（⇒ `letters/<P>/cmd/sculpture_view.png`）；slice 同理（`sculpture_slice.png`）。\n"
        + "  參數疊層：內建預設 → 共用作用中設定 → persona 作用中設定（或 `profile=` 指定一份）→ 展品 preset → CLI；`layers` 印出用了哪幾層。\n"
        + "  給了 exhibit 或 region ⇒ 自動框住時主體填滿畫面（fit_upscale=1，可放大超過 24 px／voxel）；全景維持只縮不放；`fit_upscale=0|1` 顯式指定。\n"
        + "  地板：`floor=on|off` ＋ floor_z／floor_full_grid／floor_margin（上限）／floor_margin_ratio（外擴＝作品最長邊×比例，預設 0.5；0 ＝ 固定 margin）／floor_texture（相對 ⇒ Sculpture/floors/；builtin ＝ 量尺網格）／floor_tile／floor_color／floor_fade。\n"
        + "· render-profile：`sub=list|show|set|use|copy|delete|reset` 管長期保存的渲染設定（鏡頭／燈／天空／地板／尺寸）。\n"
        + "· 引擎是 in-process 的 SCP_Core `SCP_SculptEngine`；圖由宿主註冊的渲染器畫（`renderer` 值印出是哪一個）。\n"
        + "exit：0 成功／2 參數不合／3 付款被拒（零副作用）／4 拿不到鎖／5 引擎拒絕（未落子、未扣費）／1 已落子但結算沒收齊（要對帳）或觀測出不了結果。";

    public override string Example => SCP_CmdRegistry.Invoke("sculpture --arg op=view --arg persona=<你> --arg yaw=135");

    /// <summary>計費粒度：每 100 voxel 收 1 單位（Tim 拍板費率 ⌈實際落地數/100⌉）。</summary>
    public const int VoxelsPerUnit = 100;
    /// <summary>單次體積上限（＝ 引擎的 <see cref="SCP_SculptEngine.MaxVolume"/>）。</summary>
    public const int MaxVolume = (int)SCP_SculptEngine.MaxVolume;
    /// <summary>等雕刻全域鎖多久：要比一次引擎執行長（別人那一刀正在跑就等它）。</summary>
    const double EngineLockTimeoutSec = 150;
    const string SpendKind = "sculpture_place";
    public const string ViewPngName = "sculpture_view.png";
    public const string SlicePngName = "sculpture_slice.png";

    static readonly string[] s_Ops =
        { "box", "carve", "stamp2d", "stampimg", "view", "slice", "stats", "export", "exhibit", "render-profile", "work" };

    /// <summary>
    /// 分享的發文端（預設 `tavern-post`）。⚠ 只給自我對拍換成探針 —— 淨室裡不可以真的發進酒館。
    /// </summary>
    public static Func<IReadOnlyDictionary<string, string>, SCP_CmdResult> SharePoster =
        a => SCP_CmdRegistry.Dispatch("tavern-post", a);

    /// <summary>view 的渲染參數鍵（`layers` 的 CLI 那一層列出有給的是哪幾個）。</summary>
    static readonly string[] s_ViewRenderKeys =
    {
        "region", "exclude_color", "light_dir", "ambient", "shadow", "ao", "zoom", "projection", "yaw", "pitch", "roll",
        "target", "eye", "distance", "fov", "skybox", "skybox_yaw", "skybox_tilt", "width", "height", "lights", "light_add", "light_clear",
        "fit_upscale", "floor", "floor_z", "floor_full_grid", "floor_margin", "floor_margin_ratio", "floor_texture", "floor_tile", "floor_color", "floor_fade",
    };

    /// <summary>render-profile set 吃的設定鍵（＝ <see cref="SCP_SculptRenderProfiles.TryEdit"/> 認得的那幾個）。</summary>
    static readonly string[] s_ProfileEditKeys =
    {
        "projection", "yaw", "pitch", "roll", "target", "eye", "distance", "fov", "zoom", "ambient", "ao", "shadow",
        "skybox", "skybox_yaw", "skybox_tilt", "background", "width", "height", "lights", "light_add", "light_clear", "unset",
        "fit_upscale", "floor", "floor_z", "floor_full_grid", "floor_margin", "floor_margin_ratio", "floor_texture", "floor_tile", "floor_color", "floor_fade",
    };

    /// <summary>render-profile set 除了設定鍵以外還吃的（路由／身分）。其餘顯式給的參數 ⇒ 擋（⛔ 不靜默忽略）。</summary>
    static readonly string[] s_ProfileSetMetaKeys = { "op", "sub", "scope", "name", "persona", "data_root", "letters_root" };

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("op", "做什麼", iRequired: true, iChoices: s_Ops),
        new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（Senate CLI 從設定自動補）", iRequired: true),
        new SCP_CmdArgSpec("letters_root", "信件夾根（Senate CLI 從設定自動補；都沒有 ⇒ 資料根的慣例位置）"),
        new SCP_CmdArgSpec("persona", "誰（落子類**必填** —— 錢記在人頭上；view／slice 沒給 out 時必填；其餘選填）"),
        new SCP_CmdArgSpec("account", "付 token 的帳號；不給 ⇒ 由 persona 的權威綁定檔解（⛔ 解不出來不猜）"),
        new SCP_CmdArgSpec("pay", "付款方式", iDefault: "auto", iChoices: new[] { "auto", "freetime", "voucher", "token" }),
        new SCP_CmdArgSpec("work", "作品 ID；既有雕刻／觀測指定它即操作作品的獨立空間"),
        new SCP_CmdArgSpec("size", "work create/update：邊長或X,Y,Z，各軸1–256；建立預設64。調尺寸免費，縮小不可切掉現有voxel"),
        new SCP_CmdArgSpec("parent_work", "work create：任務作品或其子作品ID；同作者免費建立子作品，不另領薪"),
        new SCP_CmdArgSpec("source_work", "work assemble：零件來源作品ID，可用其他作者作品；自動Credit並保留原作"),
        new SCP_CmdArgSpec("delta", "work move：選區平移量dx,dy,dz；選區用region=x1..x2,y1..y2,z1..z2"),
        new SCP_CmdArgSpec("turn", "work assemble：繞Z軸旋轉0/90/180/270度，旋轉後原點放在at", iDefault: "0"),
        new SCP_CmdArgSpec("commission", "work create：使用者指定的委託內容；有委託才可填，免建立費並立即發10 token（可用 --arg-file）"),
        new SCP_CmdArgSpec("commission_ref", "work create：該次委託的唯一來源（task／訊息seq／對話來源），不可重複領酬"),
        new SCP_CmdArgSpec("notes", "work update：心得與續作筆記（可用 --arg-file）"),
        new SCP_CmdArgSpec("todo", "work update：待辦（可用 --arg-file）"),
        new SCP_CmdArgSpec("confirm", "work import：1 才落地並扣費；預設只預覽"),
        new SCP_CmdArgSpec("expect_revision", "work import：預覽的來源版本 SHA256"),
        new SCP_CmdArgSpec("expect_placed", "work import：預覽的實際落地 voxel 數"),
        new SCP_CmdArgSpec("x1", "box/carve：AABB 一角 x（0-255）"),
        new SCP_CmdArgSpec("x2", "box/carve：另一角 x"),
        new SCP_CmdArgSpec("y1", "box/carve：一角 y"),
        new SCP_CmdArgSpec("y2", "box/carve：另一角 y"),
        new SCP_CmdArgSpec("z1", "box/carve：一角 z"),
        new SCP_CmdArgSpec("z2", "box/carve：另一角 z"),
        new SCP_CmdArgSpec("color", "box：顏色 index（0-255，RGB332）", iDefault: "19"),
        new SCP_CmdArgSpec("src_x1", "stamp2d：2D 畫布來源區域一角 x（0 起算，上限是畫布實際寬度 −1，見 canvas op=size）"),
        new SCP_CmdArgSpec("src_y1", "stamp2d：一角 y"),
        new SCP_CmdArgSpec("src_x2", "stamp2d：另一角 x"),
        new SCP_CmdArgSpec("src_y2", "stamp2d：另一角 y"),
        new SCP_CmdArgSpec("png", "stampimg：RGBA PNG 路徑（透明像素不落地）"),
        new SCP_CmdArgSpec("resize", "stampimg：W,H（NEAREST）"),
        new SCP_CmdArgSpec("at", "stamp 類：圖左上角貼在哪（x,y,z）"),
        new SCP_CmdArgSpec("facing", "stamp 類：貼片法線 x+|x-|y+|y-|z+|z-（預設 z+）"),
        new SCP_CmdArgSpec("thickness", "stamp 類：層數", iDefault: "1"),
        new SCP_CmdArgSpec("overwrite", "work assemble/move：1允許覆蓋（預設碰撞拒絕）；stamp類預設跳過／render-profile copy：1覆蓋既有目標"),
        new SCP_CmdArgSpec("expect_pixels", "stamp 類：預覽印出的非透明像素數，對不上即拒絕（**強烈建議帶**）"),
        new SCP_CmdArgSpec("alpha_threshold", "stamp 類：alpha 門檻（預設 128）"),
        new SCP_CmdArgSpec("allow_clip", "stamp 類：1 ＝ 接受越界裁切（預設越界即拒絕）"),
        new SCP_CmdArgSpec("exhibit_id", "stamp 類：貼完自動登錄／擴充展品"),
        new SCP_CmdArgSpec("exhibit_title", "stamp 類：展品標題"),
        new SCP_CmdArgSpec("exhibit_desc", "stamp 類：展品描述"),
        new SCP_CmdArgSpec("exhibit_margin", "stamp 類：展品邊界格數（預設 2）"),
        new SCP_CmdArgSpec("region", "view／export／exhibit register：裁切範圍；slice **必填**（x1..x2,y1..y2,z1..z2）"),
        new SCP_CmdArgSpec("exclude_color", "view／export／exhibit register：不畫哪些顏色（c,c,…）"),
        new SCP_CmdArgSpec("exhibit", "view：展品 preset id"),
        new SCP_CmdArgSpec("light_dir", "view／exhibit register：一盞白光的行進方向 x,y,z（取代設定檔的燈）"),
        new SCP_CmdArgSpec("ambient", "view／exhibit register／render-profile set：環境光 0-1"),
        new SCP_CmdArgSpec("shadow", "view／exhibit register／render-profile set：陰影 1|0"),
        new SCP_CmdArgSpec("ao", "view／render-profile set：環境遮蔽 1|0"),
        new SCP_CmdArgSpec("zoom", "view／exhibit register／render-profile set：倍率（auto ＝ 自動框住）"),
        new SCP_CmdArgSpec("projection", "鏡頭：orthographic|perspective"),
        new SCP_CmdArgSpec("yaw", "鏡頭水平角（度；45 ＝ 預設等角視角）"),
        new SCP_CmdArgSpec("pitch", "鏡頭俯角（度）"),
        new SCP_CmdArgSpec("roll", "鏡頭滾轉（度）"),
        new SCP_CmdArgSpec("target", "鏡頭注視點 x,y,z（auto ＝ 可見 voxel 外框中心）"),
        new SCP_CmdArgSpec("eye", "鏡頭位置 x,y,z（auto ＝ 由 yaw／pitch／distance 決定）"),
        new SCP_CmdArgSpec("distance", "透視：鏡頭到注視點距離（auto ＝ 依 fov 框住）"),
        new SCP_CmdArgSpec("fov", "透視：垂直視角（度）"),
        new SCP_CmdArgSpec("skybox", "天空：路徑（相對 ⇒ Sculpture/skyboxes/）| builtin | none（純色背景）"),
        new SCP_CmdArgSpec("skybox_yaw", "天空水平旋轉（度）"),
        new SCP_CmdArgSpec("skybox_tilt", "正交時背景視窗往上抬（度，−89..89；透視忽略）"),
        new SCP_CmdArgSpec("fit_upscale", "view／render-profile set：自動框住可放大超過 24 px／voxel 1|0（view 給了 exhibit／region 時預設 1）"),
        new SCP_CmdArgSpec("floor", "view／render-profile set：地板 on|off（不給 ⇒ 沿用設定檔鏈；內建預設沒有地板）"),
        new SCP_CmdArgSpec("floor_z", "地板高度（世界 z，" + SCP_SculptRenderProfiles.FloorZMin + ".." + SCP_SculptRenderProfiles.FloorZMax + "）"),
        new SCP_CmdArgSpec("floor_full_grid", "地板範圍：1 ＝ 整個 0..256 空間；0 ＝ 可見 voxel 外框外擴 floor_margin"),
        new SCP_CmdArgSpec("floor_margin", "地板外框模式外擴的上限格數（0.." + SCP_SculptRenderProfiles.FloorMarginMax + "；margin_ratio=0 時就是固定外擴）"),
        new SCP_CmdArgSpec("floor_margin_ratio", "地板外擴 ＝ 作品 xy 最長邊 × 本值，封頂 floor_margin（0.." + SCP_SculptRenderProfiles.FloorMarginRatioMax + "，預設 0.5；0 ＝ 固定外擴）"),
        new SCP_CmdArgSpec("floor_texture", "地板貼圖：builtin（量尺網格）| 檔名（相對 ⇒ Sculpture/floors/）| 絕對路徑"),
        new SCP_CmdArgSpec("floor_tile", "地板貼圖每重複一次涵蓋幾格（" + SCP_SculptRenderProfiles.FloorTileMin + ".." + SCP_SculptRenderProfiles.FloorTileMax + "；網格忽略）"),
        new SCP_CmdArgSpec("floor_color", "地板色調 #RRGGBB（乘在貼圖／網格上）"),
        new SCP_CmdArgSpec("floor_fade", "地板邊緣淡出寬度（佔邊長比例 0.." + SCP_SculptRenderProfiles.FloorFadeMax + "；0 ＝ 硬邊）"),
        new SCP_CmdArgSpec("width", "出圖寬（16-8192）"),
        new SCP_CmdArgSpec("height", "出圖高（16-8192）"),
        new SCP_CmdArgSpec("smooth", "view／exhibit register：python 時代的旗標（只影響 summary 文字，GPU 渲染不吃）"),
        new SCP_CmdArgSpec("profile", "view：一次性指定一份渲染設定（不走作用中那條鏈）"),
        new SCP_CmdArgSpec("profile_scope", "view：profile= 在哪一層找", iChoices: new[] { "shared", "persona" }),
        new SCP_CmdArgSpec("axis", "slice：法線與近端方向（預設 z+）"),
        new SCP_CmdArgSpec("out", "view／slice：輸出 PNG **絕對路徑**（不給 ⇒ persona 的 cmd 夾）／export：輸出檔路徑"),
        new SCP_CmdArgSpec("format", "export：obj|vox", iChoices: new[] { "obj", "vox" }),
        new SCP_CmdArgSpec("out_dir", "export：輸出資料夾（預設 Sculpture/exports）"),
        new SCP_CmdArgSpec("merge", "export obj：同色共面合併 greedy（預設，面數大減、頂點共用）｜none（逐 voxel 面，與舊輸出相同；要 watertight 時用）", iChoices: new[] { "greedy", "none" }),
        new SCP_CmdArgSpec("sub", "work：create|list|show|update|import|assemble|move|undo|redo|history；exhibit：list|register；render-profile：list|show|set|use|copy|delete|reset",
                           iChoices: new[] { "list", "register", "show", "set", "use", "copy", "delete", "reset", "create", "update", "import", "assemble", "move", "undo", "redo", "history" }),
        new SCP_CmdArgSpec("id", "work create：全庫唯一作品ID；exhibit register：展品 id"),
        new SCP_CmdArgSpec("title", "work create/update：作品名稱；exhibit register：標題"),
        new SCP_CmdArgSpec("author", "exhibit register：創作者（不給 ⇒ persona）"),
        new SCP_CmdArgSpec("desc", "exhibit register：描述"),
        new SCP_CmdArgSpec("bg_color", "exhibit register：背景色（存進 preset）"),
        new SCP_CmdArgSpec("scope", "render-profile：哪一層（shared|persona；list 另有 all）", iChoices: new[] { "shared", "persona", "all" }),
        new SCP_CmdArgSpec("name", "render-profile：設定名（小寫英數 _ -；use 給 none ⇒ 不用）"),
        new SCP_CmdArgSpec("from_scope", "render-profile copy：來源層", iChoices: new[] { "shared", "persona" }),
        new SCP_CmdArgSpec("from", "render-profile copy：來源設定名"),
        new SCP_CmdArgSpec("to_scope", "render-profile copy：目標層", iChoices: new[] { "shared", "persona" }),
        new SCP_CmdArgSpec("to", "render-profile copy：目標設定名"),
        new SCP_CmdArgSpec("background", "render-profile set：純色背景 #RRGGBB（skybox=none 時用）"),
        new SCP_CmdArgSpec("lights", "view／render-profile set：整組燈（JSON 陣列，取代下層的燈）"),
        new SCP_CmdArgSpec("light_add", "view／render-profile set：加燈 x,y,z[;#rrggbb[;強度[;shadow 0|1]]]，多盞用 | 串（疊在下層的燈之後）"),
        new SCP_CmdArgSpec("light_clear", "view／render-profile set：1 ＝ 先清空燈（再接 light_add）"),
        new SCP_CmdArgSpec("unset", "render-profile set：拿掉哪些鍵（逗號分隔 ⇒ 回到沿用下層）"),
        new SCP_CmdArgSpec("share", "落子成功後發酒館帶圖（0 ＝ 不發）", iDefault: "1"),
    };

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        var aResult = new SCP_CmdResult();
        string aDataRoot = iArgs.Get("data_root").Trim().Replace('\\', '/');
        if (!Directory.Exists(aDataRoot))
        {
            Fail(aResult, 2, "✗ 資料根不存在：" + aDataRoot + "（這是「根給錯了」不是「空間是空的」）");
            return aResult;
        }
        string aLetters = iArgs.Get("letters_root").Trim().Replace('\\', '/');
        var aRoots = new SCP_MorningRoots
        {
            DataRoot = aDataRoot,
            LettersRoot = aLetters.Length > 0 ? aLetters : SCP_DataPaths.Letters(new SCP_DataRoot(aDataRoot)).Value,
        };
        aResult.Lines.Add("⤷ Senate 就地執行 @ data_root=" + aRoots.DataRoot);
        aResult.AddValue("delegate_host", "senate");

        string aOp = iArgs.Get("op").Trim().ToLowerInvariant();
        string aPersona = iArgs.Get("persona").Trim();
        if (aPersona.Length > 0 && !SCP_Cmd_FreeTimeActivity.IsSafePersona(aPersona))
        {
            Fail(aResult, 2, "✗ persona 不合法（不可含 `/` `\\` `:` 或是 `.` / `..`）：'" + aPersona + "'");
            return aResult;
        }
        var aCtx = new Ctx(aRoots, iArgs, aResult, aOp, aPersona);
        string? aPayload;
        try
        {
            if (aOp != "work" && !ConfigureWork(aCtx, out string aWorkError))
                aPayload = Blocked(aCtx, 2, aWorkError);
            else
            switch (aOp)
            {
                case "work": aPayload = OpWork(aCtx); break;
                case "box":
                case "carve":
                case "stamp2d":
                case "stampimg":
                    aPayload = OpPlace(aCtx); break;
                case "view": aPayload = OpView(aCtx); break;
                case "slice": aPayload = OpSlice(aCtx); break;
                case "stats": aPayload = OpStats(aCtx); break;
                case "export": aPayload = OpExport(aCtx); break;
                case "exhibit": aPayload = OpExhibit(aCtx); break;
                case "render-profile": aPayload = OpRenderProfile(aCtx); break;
                default: Fail(aResult, 2, "✗ 不認得的 op：" + aOp); return aResult;
            }
        }
        catch (Exception e)
        {
            aResult.ExitCode = 70;
            aResult.Lines.Add($"✗ {e.GetType().Name}: {e.Message}");
            return aResult;
        }
        if (aPayload != null) aResult.AddOutput(aPayload);   // 宿主會印「📄 回傳檔」
        return aResult;
    }

    sealed class Ctx
    {
        public readonly SCP_MorningRoots Roots;
        public readonly SCP_CmdArgs Args;
        public readonly SCP_CmdResult Result;
        public readonly string Op;
        public readonly string Persona;
        public readonly StringBuilder Report = new StringBuilder();
        public SCP_SculptWork? Work;

        public Ctx(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult iResult, string iOp, string iPersona)
        {
            Roots = iRoots; Args = iArgs; Result = iResult; Op = iOp; Persona = iPersona;
            Report.Append("# Sculpture op=").Append(iOp)
                  .Append(iPersona.Length > 0 ? " persona=" + iPersona : "")
                  .Append("  ts=`").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture))
                  .Append("`（本地時間）\n\n");
        }

        public SCP_DataRoot Data => new SCP_DataRoot(Roots.DataRoot);
        public SCP_LettersRoot Letters => Roots.Letters;
        public SCP_SculptWorks Works => new SCP_SculptWorks(Data);
        public int Size => Work == null ? 256 : Work.size;
        public string SculptDir => (Work == null ? new SCP_SculptPaths(Data).Root : Works.Folder(Work.id)).Replace('\\', '/');
        public string EngineLockTarget => Path.Combine(SculptDir, "_engine");
    }

    /// <summary>
    /// 新引擎實例。展品照（exhibit register／stamp 的 exhibit_id）用**共用作用中**的渲染設定當底
    /// （展品是全員的，不跟某個人的個人設定走）；共用設定壞掉 ⇒ 引擎把它轉成展品照的 warning（⛔ 不推翻已落的 voxel）。
    /// </summary>
    static SCP_SculptEngine NewEngine(Ctx c)
    {
        if (c.Work != null) c.Work = c.Works.Load(c.Work.id);
        var aEngine = c.Work == null ? new SCP_SculptEngine(c.Data) : c.Works.Engine(c.Work, c.Roots.DataRoot);
        SCP_DataRoot aData = c.Data;
        aEngine.PhotoBase = () =>
        {
            if (!SCP_SculptRenderProfiles.TryResolve(aData, null, null, out SCP_SculptRenderParams aP, out _, out string aErr))
                throw new InvalidOperationException("共用渲染設定：" + aErr);
            return aP;
        };
        return aEngine;
    }

    // ===========================================================
    // 區塊職責：落子四 op（box／carve／stamp2d／stampimg）—— 預授權 → 引擎 → 結算 → 分享。
    // 物理意義：四個 op 只差「最壞費用怎麼量」與「引擎參數怎麼組」，閘門與帳完全同型 ⇒ 共用本方法。
    //          ⚠ 最壞費用一律**本檔自己量**（stampimg 讀 PNG IHDR）—— 用呼叫端申報的數字守自己的門，門就是假的。
    // ===========================================================
    static string? OpPlace(Ctx c)
    {
        if (c.Work != null) return OpPlaceWork(c);
        if (c.Persona.Length == 0)
            return Blocked(c, 2, "落子需要 --arg persona=<名字>（錢認 persona 的券與帳戶，不能用猜的）");

        if (!TryBuildPlace(c, out Func<SCP_SculptEngine, SCP_SculptResult>? aRunEngine, out long aWorstVolume, out string aWhere, out string aBad))
            return Blocked(c, 2, aBad);
        if (aWorstVolume <= 0)
            return Blocked(c, 2, $"整段落在 0..255 之外（{aWhere}）—— 沒有任何一格可以落");
        if (aWorstVolume > MaxVolume)
            return Blocked(c, 2, $"最壞體積 {aWorstVolume:N0} 超過上限 {MaxVolume:N0}（{aWhere}）—— 拆多刀、縮圖或降 thickness");

        string aPay = PayOf(c);
        string aAccount = c.Args.Get("account").Trim();
        if (aAccount.Length == 0)
        {
            aAccount = SCP_BankAccountResolver.ResolvePersonaAccount(c.Roots.LettersRoot, c.Roots.DataRoot,
                                                                   c.Roots.Region, c.Persona, out string aTrace);
            c.Report.Append("- 帳戶：").Append(aAccount.Length > 0 ? "`" + aAccount + "`" : "（解不出來 ⇒ 只能用券）")
                    .Append("〔").Append(aTrace).Append("〕\n");
        }
        else c.Report.Append("- 帳戶：`").Append(aAccount).Append("`（顯式給的）\n");

        SCP_ICanvasGateway? aGate = SCP_CanvasGatewayHost.For(c.Roots.DataRoot);
        if (aGate == null)
            return Blocked(c, 1, "這個宿主沒有裝上付款閘 ⇒ 收不了錢，所以不落子（⛔ 不「先雕再說」—— 那等於免費 voxel）");
        c.Report.Append("- ").Append(aGate.HostQualifier).Append('\n');

        int aMaxUnits = CeilDiv(aWorstVolume, VoxelsPerUnit);
        SCP_SculptEngine aEngine = NewEngine(c);
        int aPlaced;
        int aCharge = 0;
        SCP_SculptViewPlan? aSharePlan = null;
        string aShareSkip = "";

        // ── 臨界區①：雕刻全域鎖（引擎快取是讀改寫）──
        SCP_FileLock aEngineLock;
        try { aEngineLock = SCP_FileLock.Acquire(c.EngineLockTarget, EngineLockTimeoutSec); }
        catch (Exception e) { return Blocked(c, 4, "拿不到雕刻鎖（別人那一刀還在跑？）：" + e.Message + " —— 未落子、未扣費"); }
        using (aEngineLock)
        {
            SCP_CanvasPayPlan aPlan = default;
            var aLedgerRefs = new List<string>();
            var aShortPaid = new List<string>();
            string aRef = "";

            // ── 臨界區②：畫布付款鎖（與 canvas place 同一顆檔 —— 同一個人的券不會在預授權與結算之間被花掉）──
            var aCanvasPaths = new SCP_CanvasPaths(c.Data);
            IDisposable? aPayLock = SCP_CanvasPlace.TryAcquireLock(aCanvasPaths, aAccount.Length > 0 ? aAccount : "noaccount",
                                                                  c.Persona, out string aLockWhy);
            if (aPayLock == null)
                return Blocked(c, 4, "拿不到付款鎖：" + aLockWhy + " —— 未落子、未扣費（⛔ 不強奪：對方可能還在扣款中）");
            try
            {
                // ① 預授權（最壞費用）—— 不夠就引擎不跑
                if (!SCP_CanvasPlace.TryPlan(aGate, c.Persona, aAccount, aMaxUnits, aPay, out _, out string aPlanWhy))
                    // ⚠ 不寫成「預授權不足」：TryPlan 失敗有兩種 —— 真的不夠，或**查不到**（-1）。
                    //   把「不知道」說成「不夠」，使用者會照著去加值（2026-10-01 實測：版本不符的 Server 問不到券，訊息卻說不足）。
                    return Blocked(c, 3, $"付款預驗沒過 —— 本刀最壞費用 {aMaxUnits} 單位（{aWhere}，⌈{aWorstVolume:N0}/{VoxelsPerUnit}⌉）："
                                         + aPlanWhy + "\n- 引擎未執行，未扣任何費用（不夠 ⇒ 縮小範圍／換 pay 模式；查不到 ⇒ 照上面那句的出口走）");

                // ② 引擎
                SCP_SculptResult aRes = aRunEngine!(aEngine);
                if (!aRes.Ok || aRes.Status != "success")
                {
                    string aStatus = aRes.Status;
                    string aReason = aRes is SCP_SculptStampResult aSr && aSr.Reason.Length > 0
                        ? aSr.Reason : (aRes.Lines.Count > 0 ? aRes.Lines[0].TrimStart('❌', ' ') : "");
                    c.Report.Append("## blocked\n- reason: 引擎未落子（engine exit=").Append(aRes.ExitCode)
                            .Append(aStatus.Length > 0 ? ", status=" + aStatus : "").Append("）—— 未扣任何費用\n");
                    if (aReason.Length > 0) c.Report.Append("- engine: ").Append(aReason).Append('\n');
                    if (aStatus == "mismatch")
                        c.Report.Append("- how: expect_pixels 是「你看的預覽」與「引擎吃的圖」的對帳閘門 —— 重跑預覽拿新數字。\n");
                    if (aStatus == "out_of_bounds")
                        c.Report.Append("- how: 改小 at、用 resize 縮圖，或顯式 allow_clip=1（別讓「只貼了一角」看起來像成功）。\n");
                    c.Report.Append("```\n").Append(aRes.Render()).Append("\n```\n");
                    return Finish(c, 5, "✗ 引擎未落子（engine exit " + aRes.ExitCode + (aStatus.Length > 0 ? "，" + aStatus : "") + "）—— 未扣任何費用");
                }

                // ③ 結算：只對實際落地收費
                int aActual = PlacedOf(aRes);
                aCharge = aActual > 0 ? CeilDiv(aActual, VoxelsPerUnit) : 0;
                string aEventFile = EventFileOf(aRes);
                aRef = "sculpture:" + Path.GetFileName(aEventFile);
                c.Result.AddValue("placed", aActual.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("charged", aCharge.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("event_file", aEventFile);

                if (aCharge > 0)
                {
                    // 鎖還握著 ⇒ 重新規劃一次（拿實際費用，讀的是此刻的餘額）
                    if (!SCP_CanvasPlace.TryPlan(aGate, c.Persona, aAccount, aCharge, aPay, out aPlan, out string aSettleWhy))
                        aShortPaid.Add("結算規劃失敗：" + aSettleWhy);
                    else
                        Settle(aGate, c.Persona, aAccount, aCharge, aPlan, aRef, aLedgerRefs, aShortPaid);
                }

                c.Report.Append("## result（引擎回報＝結算依據）\n");
                AppendPlaceFacts(c, aRes, aActual, aWhere);
                c.Report.Append("- charged: **").Append(aCharge).Append(" 單位**（⌈").Append(aActual).Append('/')
                        .Append(VoxelsPerUnit).Append("⌉；預授權上限 ").Append(aMaxUnits).Append("；帳單跟著事實走，不跟著意圖走）\n");
                c.Report.Append("- pay_breakdown: freetime(限時券)=").Append(aPlan.Expiring)
                        .Append(" voucher(永久券)=").Append(aPlan.Permanent)
                        .Append(" tavern(酒館券)=").Append(aPlan.Tavern)
                        .Append(" token=").Append(aPlan.Token).Append("（pay=").Append(aPay).Append("）\n");
                c.Report.Append("- ledger_refs: ").Append(aLedgerRefs.Count > 0 ? string.Join(", ", aLedgerRefs) : "（無）").Append('\n');
                c.Report.Append("- event: `").Append(aEventFile).Append("`\n");
                c.Result.AddValue("pay_freetime", aPlan.Expiring.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("pay_voucher", aPlan.Permanent.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("pay_tavern", aPlan.Tavern.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("pay_token", aPlan.Token.ToString(CultureInfo.InvariantCulture));
                aPlaced = aActual;
            }
            finally { aPayLock.Dispose(); }

            if (aShortPaid.Count > 0)
            {
                // 🩸 voxel 已經落了而錢沒收齊 ⇒ 大聲失敗，⛔ 不假裝沒落子、⛔ 不重跑（重跑是另一刀）
                c.Report.Append("## ✗ 結算沒收齊（voxel 已落，帳要對）\n");
                foreach (string s in aShortPaid) c.Report.Append("- ").Append(s).Append('\n');
                c.Report.Append("- 對帳用：ref `").Append(aRef).Append("`\n");
                return Finish(c, 1, "✗ 已落子但結算沒收齊（應收 " + aCharge + "）—— 見回傳檔，帳要人對");
            }

            // 分享的場景在鎖內準備（讀的是剛落子的那份快取）；渲染與發文在鎖外（GPU／酒館 Server 的等待不擋別人下刀）
            aSharePlan = PrepareShare(c, aEngine, aPlaced, out aShareSkip);
        }

        c.Report.Append("## next\n- 看成品：`").Append(SCP_CmdRegistry.Invoke("sculpture --arg op=view --arg persona=<你> [--arg region=…]"))
                .Append("`（免費）\n");
        if (c.Op == "stamp2d" || c.Op == "stampimg")
            c.Report.Append("- 下次貼圖：先 `").Append(SCP_CmdRegistry.Invoke("canvas --arg op=view --arg persona=<你> --arg region=x,y,w,h --arg scale=1"))
                    .Append("` 看預覽 → 把它印的 non_transparent_pixels 當 expect_pixels 帶回來。\n");
        SCP_FreeTimeHint.Append(c.Report, c.Data, c.Persona, out string aHintWarn);
        if (aHintWarn.Length > 0) c.Result.Lines.Add("⚠ " + aHintWarn);

        // ── 分享（best-effort）──
        if (aSharePlan != null) TrySharePreview(c, aSharePlan, aPlaced, aWhere, ref aShareSkip);
        if (aShareSkip.Length > 0)
        {
            c.Report.Append("## share\n- skipped: ").Append(aShareSkip).Append('\n');
            c.Result.AddValue("share_skipped", aShareSkip);
            c.Result.Lines.Add("⚠ 分享略過：" + aShareSkip + " —— 落子不受影響");
        }

        return Finish(c, 0, "✓ " + c.Op + " " + aPlaced + " voxels，收 " + aCharge + " 單位");
    }

    static int PlacedOf(SCP_SculptResult iRes) => iRes switch
    {
        SCP_SculptBoxResult b => b.PlacedCount,
        SCP_SculptCarveResult cv => cv.CarvedCount,
        SCP_SculptStampResult s => s.PlacedCount,
        _ => 0,
    };

    static string EventFileOf(SCP_SculptResult iRes) => iRes switch
    {
        SCP_SculptBoxResult b => b.EventFile,
        SCP_SculptCarveResult cv => cv.EventFile,
        SCP_SculptStampResult s => s.EventFile,
        _ => "",
    };

    static string PayOf(Ctx c)
    {
        string aPay = c.Args.Get("pay").Trim().ToLowerInvariant();
        return aPay.Length == 0 ? "auto" : aPay;
    }

    /// <summary>
    /// 照計畫逐通道扣；任何一段失敗記進 <paramref name="ioShort"/>（⛔ 不回滾前面已扣的 —— 那些是真的扣了，要留帳）。
    /// <para>⚠ 限時券與永久券走**同一支 consume**（ledger 內部先花快過期的）—— 分兩筆只為了帳面分得出哪幾張是限時券。</para>
    /// </summary>
    static void Settle(SCP_ICanvasGateway iGate, string iPersona, string iAccount, int iCharge, SCP_CanvasPayPlan iPlan,
                       string iRef, List<string> ioRefs, List<string> ioShort)
    {
        string aDesc = "3D sculpture " + iCharge + " unit(s) by " + iPersona;
        if (iPlan.Expiring > 0)
        {
            SCP_CanvasGateResult r = iGate.ConsumeVouchers(iPersona, iPlan.Expiring, iRef, aDesc + "（限時券）");
            if (r.Ok) ioRefs.Add("voucher-expiring:" + iRef); else ioShort.Add("限時券 " + iPlan.Expiring + "：" + r.Detail);
        }
        if (iPlan.Permanent > 0)
        {
            SCP_CanvasGateResult r = iGate.ConsumeVouchers(iPersona, iPlan.Permanent, iRef, aDesc);
            if (r.Ok) ioRefs.Add("voucher:" + iRef); else ioShort.Add("永久券 " + iPlan.Permanent + "：" + r.Detail);
        }
        if (iPlan.Tavern > 0)
        {
            SCP_CanvasGateResult r = iGate.ConsumeTavernVouchers(iPersona, iPlan.Tavern, iRef, aDesc + "（酒館券）");
            if (r.Ok) ioRefs.Add("voucher-tavern:" + iRef); else ioShort.Add("酒館券 " + iPlan.Tavern + "：" + r.Detail);
        }
        if (iPlan.Token > 0)
        {
            SCP_CanvasGateResult r = iGate.DebitTokens(iAccount, iPlan.Token, SpendKind, iRef, aDesc);
            if (r.Ok) ioRefs.Add("bank:" + iRef); else ioShort.Add("token " + iPlan.Token + "：" + r.Detail);
        }
    }

    /// <summary>組引擎呼叫＋量最壞體積。box／carve 的兩角先 clamp 0..255 再交給引擎（與預授權量的是同一個框）。</summary>
    static bool TryBuildPlace(Ctx c, out Func<SCP_SculptEngine, SCP_SculptResult>? oRun, out long oWorst, out string oWhere, out string oBad)
    {
        oRun = null;
        oWorst = 0; oWhere = ""; oBad = "";
        if (c.Op == "box" || c.Op == "carve")
        {
            if (!TryInt(c, "x1", out int x1) || !TryInt(c, "x2", out int x2) || !TryInt(c, "y1", out int y1)
                || !TryInt(c, "y2", out int y2) || !TryInt(c, "z1", out int z1) || !TryInt(c, "z2", out int z2))
            { oBad = c.Op + " 需要 x1 x2 y1 y2 z1 z2 六個整數（0-255）"; return false; }
            if (c.Work != null && (x1 < 0 || x1 >= c.Work.SizeX || x2 < 0 || x2 >= c.Work.SizeX || y1 < 0 || y1 >= c.Work.SizeY || y2 < 0 || y2 >= c.Work.SizeY || z1 < 0 || z1 >= c.Work.SizeZ || z2 < 0 || z2 >= c.Work.SizeZ))
            { oBad = "作品座標超出尺寸 " + c.Work.Dimensions + "；越界不會裁切或落子"; return false; }
            oWorst = ClampedVolume(ref x1, ref x2, ref y1, ref y2, ref z1, ref z2, c.Size);
            oWhere = $"({x1}..{x2},{y1}..{y2},{z1}..{z2})";
            string aPersona = c.Persona;
            if (c.Op == "box")
            {
                if (!TryInt(c, "color", out int aColor) || aColor < 0 || aColor > 255) { oBad = "color 要是 0-255 的整數"; return false; }
                var aBox = new SCP_SculptBoxArgs { X1 = x1, X2 = x2, Y1 = y1, Y2 = y2, Z1 = z1, Z2 = z2, Color = aColor, Persona = aPersona };
                oRun = e => e.Box(aBox);
            }
            else
            {
                var aCarve = new SCP_SculptCarveArgs { X1 = x1, X2 = x2, Y1 = y1, Y2 = y2, Z1 = z1, Z2 = z2, Persona = aPersona };
                oRun = e => e.Carve(aCarve);
            }
            return true;
        }

        string aAt = c.Args.Get("at").Trim();
        if (aAt.Length == 0) { oBad = "缺 --arg at=<x,y,z>（圖左上角要貼在 3D 的哪一點）"; return false; }
        int aThick = TryInt(c, "thickness", out int t) ? Math.Max(1, t) : 1;
        long aArea;
        SCP_SculptStampArgs aStamp;
        if (c.Op == "stamp2d")
        {
            if (!TryInt(c, "src_x1", out int sx1) || !TryInt(c, "src_y1", out int sy1)
                || !TryInt(c, "src_x2", out int sx2) || !TryInt(c, "src_y2", out int sy2))
            { oBad = "stamp2d 需要 src_x1 src_y1 src_x2 src_y2 四個整數（2D 畫布座標，範圍見 canvas op=size）"; return false; }
            int w = Math.Abs(sx2 - sx1) + 1, h = Math.Abs(sy2 - sy1) + 1;
            aArea = (long)w * h;
            oWhere = $"2D 畫布 ({sx1},{sy1})-({sx2},{sy2}) = {w}x{h} × thickness {aThick} @({aAt})";
            var a2d = new SCP_SculptStamp2dArgs { SrcX1 = sx1, SrcY1 = sy1, SrcX2 = sx2, SrcY2 = sy2, LettersRoot = c.Roots.LettersRoot };
            aStamp = a2d;
            oRun = e => e.Stamp2d(a2d);
        }
        else
        {
            string aPng = c.Args.Get("png").Trim();
            if (aPng.Length == 0 || !File.Exists(aPng)) { oBad = "stampimg 需要存在的 --arg png=<路徑>（got '" + aPng + "'）"; return false; }
            if (!TryReadPngSize(aPng, out int pw, out int ph)) { oBad = "讀不到 PNG 尺寸（非 PNG 或檔案損毀）：" + aPng; return false; }
            string aResize = c.Args.Get("resize").Trim();
            if (aResize.Length > 0)
            {
                if (!TryParseWh(aResize, out int rw, out int rh)) { oBad = "resize 要是 W,H（正整數）：'" + aResize + "'"; return false; }
                pw = rw; ph = rh;
                aResize = rw + "," + rh;
            }
            aArea = (long)pw * ph;
            oWhere = $"PNG {Path.GetFileName(aPng)} = {pw}x{ph} × thickness {aThick} @({aAt})";
            var aImg = new SCP_SculptStampImgArgs { Png = aPng, Resize = aResize };
            aStamp = aImg;
            oRun = e => e.StampImg(aImg);
        }
        oWorst = aArea * aThick;
        aStamp.At = aAt;
        aStamp.Persona = c.Persona;
        aStamp.Thickness = aThick;
        string aFacing = c.Args.Get("facing").Trim();
        if (aFacing.Length > 0) aStamp.Facing = aFacing;
        if (TryInt(c, "expect_pixels", out int aExpect)) aStamp.ExpectPixels = aExpect;
        if (TryInt(c, "alpha_threshold", out int aAlpha)) aStamp.AlphaThreshold = aAlpha;
        aStamp.Overwrite = Truthy(c.Args.Get("overwrite"));
        aStamp.AllowClip = Truthy(c.Args.Get("allow_clip"));
        string aExId = c.Args.Get("exhibit_id").Trim();
        if (aExId.Length > 0)
        {
            aStamp.ExhibitId = aExId;
            string aTitle = c.Args.Get("exhibit_title").Trim();
            string aDesc = c.Args.Get("exhibit_desc").Trim();
            if (aTitle.Length > 0) aStamp.ExhibitTitle = aTitle;
            if (aDesc.Length > 0) aStamp.ExhibitDesc = aDesc;
            if (TryInt(c, "exhibit_margin", out int aMargin)) aStamp.ExhibitMargin = aMargin;
        }
        return true;
    }

    static void AppendPlaceFacts(Ctx c, SCP_SculptResult iRes, int iActual, string iWhere)
    {
        if (iRes is SCP_SculptBoxResult aBox)
        {
            c.Report.Append("- placed: **").Append(iActual).Append("** @").Append(iWhere)
                    .Append(aBox.SkippedCount > 0 ? "（skip " + aBox.SkippedCount + " —— 禁覆蓋，不收費）" : "").Append('\n');
            return;
        }
        if (iRes is SCP_SculptCarveResult)
        {
            c.Report.Append("- carved: **").Append(iActual).Append("** @").Append(iWhere).Append('\n');
            return;
        }
        if (iRes is not SCP_SculptStampResult s) return;
        c.Report.Append("- source: ").Append(iWhere).Append(" → 非透明像素 **").Append(s.PaintedSourcePixels).Append("**（透明＝未繪製，不放 voxel）\n");
        c.Report.Append("- placed: **").Append(iActual).Append("** voxels")
                .Append(s.SkippedOccupied > 0 ? "；skip " + s.SkippedOccupied + "（禁覆蓋，不收費）" : "")
                .Append(s.OutOfBounds > 0 ? "；越界裁掉 " + s.OutOfBounds : "").Append('\n');
        if (s.RemappedBlack > 0)
            c.Report.Append("- remapped_black: ").Append(s.RemappedBlack).Append("（純黑 index 0 在 3D 代表「空」，重映到最近非零暗色 —— 不靜默改色）\n");
        if (s.Exhibit != null)
        {
            c.Report.Append("- exhibit: **").Append(PyField(s.Exhibit, "mode") == "created" ? "新建" : "擴充").Append("** `")
                    .Append(PyField(s.Exhibit, "id")).Append("`《").Append(PyField(s.Exhibit, "title")).Append("》 by ")
                    .Append(PyField(s.Exhibit, "author")).Append(" — region `").Append(PyField(s.Exhibit, "region")).Append("`\n");
            string aWarn = PyField(s.Exhibit, "warning");
            if (aWarn.Length > 0) c.Report.Append("- ⚠ ").Append(aWarn).Append('\n');
            string aPhoto = PyField(s.Exhibit, "photo");
            if (aPhoto.Length > 0 && File.Exists(aPhoto)) c.Result.AddOutput(aPhoto.Replace('\\', '/'));
        }
    }

    static string PyField(SCP_SculptPyObj iObj, string iKey)
    {
        if (!iObj.TryGet(iKey, out object? v) || v == null) return "";
        return v switch
        {
            string s => s,
            SCP_JsonData j => SCP_SculptPy.Str(j),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
    }

    // ===========================================================
    // 區塊職責：落子後的自動預覽分享（Tim 2026-08-20 拍板）—— 用**落子者**的渲染設定渲全景 → `previews/share_*.png` → `tavern-post` 帶圖。
    // 物理意義：分享圖直接寫獨立檔名（refs 才不會指著一張會變的圖）；⛔ 不經過任何共用的固定檔名。
    //          ⭐ 直接走 `tavern-post`，⛔ 不繞畫布閘的 `Share`。
    // 數值影響：任何失敗只是一個 `share_skipped` 原因 —— 錢已扣、voxel 已落，分享失敗不能讓主動作看起來失敗。
    //          ⚠ exit 7（不知道有沒有發）照實印出來，⛔ 不補發（同一則發兩次就是付兩次薪水）。
    // ===========================================================
    static SCP_SculptViewPlan? PrepareShare(Ctx c, SCP_SculptEngine iEngine, int iPlaced, out string oSkip)
    {
        oSkip = "";
        if (c.Args.Get("share").Trim() == "0" || (c.Work != null && !c.Args.IsExplicit("share"))) { oSkip = "share=0（作品預設不自動分享）"; return null; }
        if (iPlaced <= 0) { oSkip = "這一刀沒有落地任何 voxel"; return null; }
        if (SCP_SculptRenderers.Current == null)
        {
            oSkip = "這個宿主沒有註冊 3D 渲染器（例：Senate.Server 不引用 Desktop）⇒ 出不了預覽圖";
            return null;
        }
        if (!SCP_SculptRenderProfiles.TryResolve(c.Data, c.Letters, c.Persona, out SCP_SculptRenderParams aBase, out _, out string aErr))
        {
            oSkip = "渲染設定解不出來：" + aErr;
            return null;
        }
        SCP_SculptViewPlan aPlan = iEngine.PrepareView(new SCP_SculptViewArgs(), aBase);
        if (aPlan.ExitCode != 0) { oSkip = "預覽場景準備失敗：" + aPlan.Error; return null; }
        return aPlan;
    }

    static void TrySharePreview(Ctx c, SCP_SculptViewPlan iPlan, int iPlaced, string iWhere, ref string ioSkip)
    {
        try
        {
            if (!SCP_SculptEngine.TryRenderPng(iPlan, out byte[] aPng, out string aWhy)) { ioSkip = "預覽渲染失敗：" + aWhy; return; }
            string aDir = Path.Combine(c.SculptDir, "previews");
            Directory.CreateDirectory(aDir);
            string aShare = Path.Combine(aDir, "share_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture) + ".png")
                                .Replace('\\', '/');
            File.WriteAllBytes(aShare, aPng);
            c.Result.AddValue("share_png", aShare);

            string aBody = c.Op == "box" || c.Op == "carve"
                ? "🧊 " + c.Persona + " 雕刻落子（" + c.Op + "）：" + iPlaced + " voxels @" + iWhere
                : "🧊 " + c.Persona + " 貼圖入 3D（" + c.Op + "）：" + iPlaced + " voxels — " + iWhere;
            var aArgs = new Dictionary<string, string>
            {
                ["persona"] = c.Persona,
                ["body"] = aBody,
                ["tag"] = "sculpt-share",
                ["refs"] = aShare,
            };
            SCP_CmdResult aPost = SharePoster(aArgs);
            string aSeq = ValueOf(aPost, "post_seq");
            c.Report.Append("## share\n- preview: `").Append(aShare).Append("`\n- tavern-post exit: ").Append(aPost.ExitCode).Append('\n');
            if (aPost.ExitCode == 0)
            {
                c.Result.Lines.Add("📣 已分享到酒館" + (aSeq.Length > 0 ? "（seq " + aSeq + "）" : "") + "，附預覽 " + aShare);
                c.Result.AddValue("share_seq", aSeq);
            }
            else if (aPost.ExitCode == 7)
                c.Result.Lines.Add("⚠ 分享**不知道有沒有發**（tavern-post exit 7）—— ⛔ 別補發，先 `"
                                   + SCP_CmdRegistry.Invoke("tavern-query --arg kind=tail") + "` 回讀");
            else
                c.Result.Lines.Add("⚠ 分享沒發出去（tavern-post exit " + aPost.ExitCode + "："
                                   + (aPost.Lines.Count > 0 ? aPost.Lines[0] : "") + "）—— 落子不受影響");
        }
        catch (Exception e)
        {
            ioSkip = "分享例外（" + e.GetType().Name + ": " + e.Message + "）";
        }
    }

    // ===========================================================
    // 區塊職責：view —— 參數疊層 → 引擎準備場景（鎖內）→ 渲染器出圖（鎖外）→ 寫到呼叫端自己的檔。
    // 物理意義：疊層順序（下 → 上）：內建預設 → 共用作用中 → persona 作用中（或 `profile=` 一次性指定）→ 展品 preset → CLI。
    //          展品 preset 與「引擎認得的 CLI 鍵」由 <see cref="SCP_SculptEngine.PrepareView"/> 疊；
    //          ao／skybox／skybox_yaw／skybox_tilt／width／height／地板／fit_upscale 與 target／eye／distance／zoom 的 auto
    //          由本檔在它之後疊（CLI 是最上層）。
    //          fit_upscale：給了 exhibit 或 region ⇒ 預設 true（主體填滿畫面）；顯式 fit_upscale=0|1 最大；都沒有 ⇒ 沿用設定檔鏈。
    // 數值影響：⛔ 沒有渲染器 ⇒ exit 1 並說出來（不寫空白圖、不留舊圖）。輸出檔是呼叫端專屬的 ⇒ 不會被別人的 view 換掉。
    // ===========================================================
    static string? OpView(Ctx c)
    {
        if (!TryOutPath(c, ViewPngName, out string aOut, out string aOutBad)) return Blocked(c, 2, aOutBad);
        int aPrep = PrepareViewPlan(c, out SCP_SculptViewPlan aPlan, out string aLayersText, out string aPrepBad);
        if (aPrep != 0) return Blocked(c, aPrep, aPrepBad);

        ISCP_SculptRenderer? aRenderer = SCP_SculptRenderers.Current;
        foreach (string l in aPlan.Lines) c.Report.Append(l).Append('\n');
        c.Report.Append("- layers: ").Append(aLayersText).Append('\n');
        c.Result.AddValue("layers", aLayersText);
        if (!SCP_SculptEngine.TryRenderPng(aPlan, out byte[] aPng, out string aWhy))
            return Blocked(c, 1, aWhy + " —— 沒有寫出任何圖（⛔ 不寫空白圖、不留舊圖）");

        string? aDir = Path.GetDirectoryName(aOut);
        if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
        File.WriteAllBytes(aOut, aPng);
        string aSha = SCP_SculptPy.Sha256Hex(File.ReadAllBytes(aOut));

        c.Report.Append("```\n").Append(string.Join("\n", aPlan.SummaryLines(aOut))).Append("\n```\n");
        c.Report.Append("- renderer: ").Append(aRenderer!.Name).Append('\n');
        if (aPlan.OutOfRangeColors > 0)
            c.Report.Append("- ⚠ out_of_range_colors: ").Append(aPlan.OutOfRangeColors)
                    .Append("（顏色不在 1..255 的 voxel 畫成近黑色 index 4；資料原值不動）\n");
        c.Report.Append("- effective params:\n```json\n").Append(DescribeParams(aPlan.Params).ToJson(true)).Append("\n```\n");
        c.Result.AddValue("path", aOut);
        c.Result.AddValue("renderer", aRenderer.Name);
        c.Result.AddValue("total_voxels", aPlan.TotalVoxels.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("visible_voxels", aPlan.Voxels.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("out_of_range_colors", aPlan.OutOfRangeColors.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("width", aPlan.Params.Width.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("height", aPlan.Params.Height.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("sha256", aSha);
        c.Result.AddOutput(aOut);
        if (aPlan.OutOfRangeColors > 0)
            c.Result.Lines.Add("⚠ " + aPlan.OutOfRangeColors + " 顆 voxel 的顏色不在 1..255 —— 畫成近黑色（資料原值不動）");
        return Finish(c, 0, "✓ view " + aPlan.Voxels.Count + "/" + aPlan.TotalVoxels + " voxels → " + aOut);
    }

    /// <summary>
    /// view 的「場景＋參數」那一半（不畫圖）：參數疊層 → 引擎在鎖內準備 → CLI 尾段（auto、fit_upscale）→ layers 說明。
    /// 回 0 ＝ 成功；否則是 view 該回的 exit code（2 參數／4 拿不到鎖／1 引擎），原因在 <paramref name="oError"/>。
    /// <para>⭐ CLI 的 `op=view` 與觀測頁的即時預覽（<see cref="TryPrepareView"/>）**走這一支** —— 疊層規則只有一份。</para>
    /// </summary>
    static int PrepareViewPlan(Ctx c, out SCP_SculptViewPlan oPlan, out string oLayersText, out string oError)
    {
        oPlan = new SCP_SculptViewPlan(); oLayersText = ""; oError = "";
        if (!TryBase(c, out SCP_SculptRenderParams aBase, out List<string> aLayers, out string aBaseErr)) { oError = aBaseErr; return 2; }
        if (!TryViewArgs(c, out SCP_SculptViewArgs aViewArgs, out ViewAutos aAutos, out string aArgBad)) { oError = aArgBad; return 2; }

        try
        {
            using (SCP_FileLock.Acquire(c.EngineLockTarget, EngineLockTimeoutSec))
                oPlan = NewEngine(c).PrepareView(aViewArgs, aBase);
        }
        catch (SCP_FileLockTimeoutException e) { oError = "拿不到雕刻鎖：" + e.Message; return 4; }
        if (oPlan.ExitCode != 0) { oError = "引擎：" + oPlan.Error; return oPlan.ExitCode == 2 ? 2 : 1; }
        if (!TryApplyViewTail(c, oPlan.Params, aAutos, out string aTailBad)) { oError = aTailBad; return 2; }
        if (!c.Args.IsExplicit("fit_upscale") && (c.Work != null || aViewArgs.Exhibit.Length > 0 || aViewArgs.Region.Length > 0))
            oPlan.Params.FitUpscale = true;

        if (aViewArgs.Exhibit.Length > 0) aLayers.Add("展品 `" + aViewArgs.Exhibit + "`");
        var aCli = new List<string>();
        foreach (string k in s_ViewRenderKeys)
            if (c.Args.IsExplicit(k)) aCli.Add(k + "=" + c.Args.Get(k).Trim());
        if (aCli.Count > 0) aLayers.Add("CLI（" + string.Join(", ", aCli) + "）");
        oLayersText = string.Join(" → ", aLayers);
        return 0;
    }

    /// <summary>
    /// **行程內**準備 view 的場景（TASK-0472，觀測頁 GPU 即時預覽用）：同一組參數規格與驗證、同一支 <see cref="PrepareViewPlan"/>，
    /// 只是不畫圖、不寫檔。<paramref name="iRaw"/> 跟呼叫 `cmd sculpture` 一樣（data_root 等宿主參數由呼叫端放進來）；`op` 一律當 view。
    /// </summary>
    public static bool TryPrepareView(IReadOnlyDictionary<string, string> iRaw, out SCP_SculptViewPlan oPlan, out string oLayers, out string oError)
    {
        oPlan = new SCP_SculptViewPlan(); oLayers = ""; oError = "";
        var aCmd = new Cmd_Sculpture();
        var aRaw = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in iRaw) aRaw[kv.Key] = kv.Value;
        aRaw["op"] = "view";
        (SCP_CmdArgs? aArgs, List<string> aErrors) = SCP_CmdArgs.Bind(aCmd.ArgSpecs, aRaw);
        if (aArgs == null) { oError = "參數不合：" + string.Join("；", aErrors); return false; }
        string aDataRoot = aArgs.Get("data_root").Trim().Replace('\\', '/');
        if (!Directory.Exists(aDataRoot)) { oError = "資料根不存在：" + aDataRoot; return false; }
        string aLetters = aArgs.Get("letters_root").Trim().Replace('\\', '/');
        var aRoots = new SCP_MorningRoots
        {
            DataRoot = aDataRoot,
            LettersRoot = aLetters.Length > 0 ? aLetters : SCP_DataPaths.Letters(new SCP_DataRoot(aDataRoot)).Value,
        };
        string aPersona = aArgs.Get("persona").Trim();
        if (aPersona.Length > 0 && !SCP_Cmd_FreeTimeActivity.IsSafePersona(aPersona)) { oError = "persona 不合法：" + aPersona; return false; }
        var c = new Ctx(aRoots, aArgs, new SCP_CmdResult(), "view", aPersona);
        if (!ConfigureWork(c, out string aWorkError)) { oError = aWorkError; return false; }
        return PrepareViewPlan(c, out oPlan, out oLayers, out oError) == 0;
    }

    /// <summary>view／slice 的輸出：`out=` 必須是絕對路徑；沒給 ⇒ persona 的 cmd 夾；兩者都沒有 ⇒ 擋（⛔ 不退回共用檔名）。</summary>
    static bool TryOutPath(Ctx c, string iFileName, out string oPath, out string oBad)
    {
        oBad = "";
        oPath = c.Args.Get("out").Trim();
        if (oPath.Length > 0)
        {
            if (!Path.IsPathRooted(oPath)) { oBad = "out 要是絕對路徑（相對路徑會隨工作目錄漂）：'" + oPath + "'"; return false; }
            oPath = Path.GetFullPath(oPath).Replace('\\', '/');
            return true;
        }
        if (c.Persona.Length > 0)
        {
            oPath = SCP_LettersPaths.CmdDir(c.Letters, c.Persona) + "/" + iFileName;
            return true;
        }
        oBad = c.Op + " 要 `--arg out=<絕對路徑>` 或 `--arg persona=<你>`（⇒ letters/<你>/cmd/" + iFileName + "）"
               + " —— 共用的固定檔名會被別人的 " + c.Op + " 換掉而不報錯，所以不提供";
        return false;
    }

    /// <summary>渲染底：`profile=` 指定一份 ⇒ 只疊那一份；否則走作用中那條鏈（共用 → persona）。</summary>
    static bool TryBase(Ctx c, out SCP_SculptRenderParams oBase, out List<string> oLayers, out string oErr)
    {
        string aName = c.Args.Get("profile").Trim();
        string aScope = c.Args.Get("profile_scope").Trim();
        if (aName.Length == 0)
        {
            if (aScope.Length > 0)
            {
                oBase = new SCP_SculptRenderParams(); oLayers = new List<string>();
                oErr = "profile_scope 要跟 profile=<名> 一起給";
                return false;
            }
            return SCP_SculptRenderProfiles.TryResolve(c.Data, c.Letters, c.Persona.Length > 0 ? c.Persona : null,
                                                       out oBase, out oLayers, out oErr);
        }
        oBase = new SCP_SculptRenderParams();
        oLayers = new List<string> { "內建預設" };
        if (!TryScopeDir(c, aScope.Length > 0 ? aScope : "shared", out string aDir, out string aWho, out oErr)) return false;
        if (!SCP_SculptRenderProfiles.TryLoad(aDir, aName, out SCP_JsonData aProfile, out oErr))
        {
            if (oErr.Length == 0) oErr = aWho + "設定 `" + aName + "` 不存在（" + SCP_SculptRenderProfiles.ProfilePath(aDir, aName) + "）";
            return false;
        }
        if (!SCP_SculptRenderProfiles.TryApply(aProfile, oBase, SCP_SculptRenderProfiles.SkyboxesDir(c.Data), out oErr))
        { oErr = aWho + "設定 `" + aName + "`：" + oErr; return false; }
        oLayers.Add(aWho + "設定 `" + aName + "`（profile= 指定，不走作用中）");
        return true;
    }

    /// <summary>CLI 的 auto（＝ 明確改回自動）—— 引擎的鏡頭解析不吃 auto，由本檔在 PrepareView 之後清掉。</summary>
    struct ViewAutos { public bool Target, Eye, Distance, Zoom; }

    static bool IsAuto(string iValue) => iValue.Equals("auto", StringComparison.OrdinalIgnoreCase);

    static bool TryViewArgs(Ctx c, out SCP_SculptViewArgs oArgs, out ViewAutos oAutos, out string oBad)
    {
        oBad = "";
        oAutos = default;
        string Arg(string k) => c.Args.Get(k).Trim();
        string aZoom = Arg("zoom"), aTarget = Arg("target"), aEye = Arg("eye"), aDistance = Arg("distance");
        oAutos.Zoom = IsAuto(aZoom); oAutos.Target = IsAuto(aTarget); oAutos.Eye = IsAuto(aEye); oAutos.Distance = IsAuto(aDistance);
        oArgs = new SCP_SculptViewArgs
        {
            Region = Arg("region"),
            ExcludeColor = Arg("exclude_color"),
            LightDir = Arg("light_dir"),
            Ambient = Arg("ambient"),
            Smooth = Truthy(Arg("smooth")),
            Shadow = Arg("shadow"),
            Zoom = oAutos.Zoom ? "" : aZoom,
            Exhibit = Arg("exhibit"),
            Camera = new SCP_SculptCamera
            {
                Projection = Arg("projection"), Yaw = Arg("yaw"), Pitch = Arg("pitch"), Roll = Arg("roll"),
                Target = oAutos.Target ? "" : aTarget, Eye = oAutos.Eye ? "" : aEye,
                Distance = oAutos.Distance ? "" : aDistance, Fov = Arg("fov"),
            },
        };
        string aAmbient = oArgs.Ambient;
        if (aAmbient.Length > 0 && (!double.TryParse(aAmbient, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) || a < 0 || a > 1))
        { oBad = "ambient 要在 0..1：'" + aAmbient + "'"; return false; }
        return true;
    }

    /// <summary>CLI 最上層、引擎 PrepareView 不管的那幾格：多燈（lights／light_add／light_clear）／ao／skybox／skybox_yaw／
    /// skybox_tilt／width／height／fit_upscale／地板 ＋ auto。</summary>
    static bool TryApplyViewTail(Ctx c, SCP_SculptRenderParams p, ViewAutos iAutos, out string oBad)
    {
        oBad = "";
        if (!TryApplyViewLights(c, p, out oBad)) return false;
        if (!TryApplyViewFloor(c, p, out oBad)) return false;
        string aFit = c.Args.Get("fit_upscale").Trim();
        if (aFit.Length > 0)
        {
            if (!TryBool01(aFit, out bool aOn)) { oBad = "fit_upscale 要 1|0：'" + aFit + "'"; return false; }
            p.FitUpscale = aOn;
        }
        string aTilt = c.Args.Get("skybox_tilt").Trim();
        if (aTilt.Length > 0)
        {
            if (!TryFinite(aTilt, out double t) || t < -89 || t > 89) { oBad = "skybox_tilt 要在 −89..89：'" + aTilt + "'"; return false; }
            p.SkyboxTiltDeg = t;
        }
        if (iAutos.Target) p.TargetX = p.TargetY = p.TargetZ = null;
        if (iAutos.Eye) p.EyeX = p.EyeY = p.EyeZ = null;
        if (iAutos.Distance) p.Distance = null;
        if (iAutos.Zoom) p.Zoom = null;
        string aAo = c.Args.Get("ao").Trim();
        if (aAo.Length > 0)
        {
            if (!TryBool01(aAo, out bool aOn)) { oBad = "ao 要 1|0：'" + aAo + "'"; return false; }
            p.AmbientOcclusion = aOn;
        }
        string aSky = c.Args.Get("skybox").Trim();
        if (aSky.Length > 0)
        {
            if (!SCP_SculptRenderProfiles.TryResolveSkybox(aSky, SCP_SculptRenderProfiles.SkyboxesDir(c.Data), out string? aPath, out oBad))
                return false;
            p.Skybox = aPath;
        }
        string aSkyYaw = c.Args.Get("skybox_yaw").Trim();
        if (aSkyYaw.Length > 0)
        {
            if (!TryFinite(aSkyYaw, out double d)) { oBad = "skybox_yaw 不是數字：'" + aSkyYaw + "'"; return false; }
            p.SkyboxYawDeg = d;
        }
        foreach (string k in new[] { "width", "height" })
        {
            string v = c.Args.Get(k).Trim();
            if (v.Length == 0) continue;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 16 || n > 8192)
            { oBad = k + " 要是 16..8192 的整數：'" + v + "'"; return false; }
            if (k == "width") p.Width = n; else p.Height = n;
        }
        return true;
    }

    /// <summary>
    /// view 的一次性多燈：語法與 `render-profile set` 相同（同一支 <see cref="SCP_SculptRenderProfiles.TryEdit"/> 解析、
    /// 同一支 <see cref="SCP_SculptRenderProfiles.TryApply"/> 驗證）—— 臨時設定檔先放「疊到目前為止的燈」，
    /// 所以 light_add 是**接在下層的燈後面**、lights 是整組取代、light_clear 是先清空。
    /// <para>⛔ 與 light_dir 一起給 ⇒ 擋：light_dir 的語意是「換成一盞白光」，兩個都給時哪一個算數沒有答案。</para>
    /// </summary>
    static bool TryApplyViewLights(Ctx c, SCP_SculptRenderParams p, out string oBad)
    {
        oBad = "";
        var aEdit = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string k in new[] { "lights", "light_clear", "light_add" })
        {
            string v = c.Args.Get(k).Trim();
            if (v.Length > 0) aEdit[k] = v;
        }
        if (aEdit.Count == 0) return true;
        if (c.Args.Get("light_dir").Trim().Length > 0)
        {
            oBad = "light_dir 與 lights／light_add／light_clear 只能擇一（light_dir ＝ 換成一盞白光；多燈請全用 light_add）";
            return false;
        }
        var aTemp = SCP_JsonData.NewObject();
        aTemp.Set("lights", DescribeParams(p)["lights"]);
        if (!SCP_SculptRenderProfiles.TryEdit(aTemp, aEdit, new List<string>(), out oBad)) return false;
        if (!SCP_SculptRenderProfiles.TryApply(aTemp, p, SCP_SculptRenderProfiles.SkyboxesDir(c.Data), out oBad))
        { oBad = "燈：" + oBad; return false; }
        return true;
    }

    /// <summary>
    /// view 的一次性地板：與 `render-profile set` 同一支 TryEdit 解析、同一支 TryApply 驗證（臨時設定檔只放 floor 物件）——
    /// 疊在設定檔鏈的地板上（沒給 floor=on|off ⇒ 開關沿用下層；只給 floor_texture ⇒ 只換貼圖）。貼圖不存在 ⇒ 擋。
    /// </summary>
    static bool TryApplyViewFloor(Ctx c, SCP_SculptRenderParams p, out string oBad)
    {
        oBad = "";
        var aEdit = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string k in SCP_SculptRenderProfiles.FloorFlatKeys)
        {
            string v = c.Args.Get(k).Trim();
            if (v.Length > 0) aEdit[k] = v;
        }
        if (aEdit.Count == 0) return true;
        var aTemp = SCP_JsonData.NewObject();
        if (!SCP_SculptRenderProfiles.TryEdit(aTemp, aEdit, new List<string>(), out oBad)) return false;
        if (!SCP_SculptRenderProfiles.TryApply(aTemp, p, SCP_SculptRenderProfiles.SkyboxesDir(c.Data),
                                               SCP_SculptRenderProfiles.FloorsDir(c.Data), out oBad))
        { oBad = "地板：" + oBad; return false; }
        return true;
    }

    /// <summary>渲染參數 → 設定檔同形的 JSON（`show` 的「實際生效值」與 view 回傳檔用）。</summary>
    public static SCP_JsonData DescribeParams(SCP_SculptRenderParams p)
    {
        SCP_JsonData Vec(double? x, double? y, double? z)
        {
            if (!x.HasValue || !y.HasValue || !z.HasValue) return SCP_JsonData.NewNull();
            var a = SCP_JsonData.NewArray();
            a.Add(SCP_JsonData.NewNumber(x.Value)); a.Add(SCP_JsonData.NewNumber(y.Value)); a.Add(SCP_JsonData.NewNumber(z.Value));
            return a;
        }
        SCP_JsonData Opt(double? v) => v.HasValue ? SCP_JsonData.NewNumber(v.Value) : SCP_JsonData.NewNull();
        string Hex(byte r, byte g, byte b) => "#" + r.ToString("x2") + g.ToString("x2") + b.ToString("x2");

        var aOut = SCP_JsonData.NewObject();
        var aCam = SCP_JsonData.NewObject();
        aCam.Set("projection", p.Projection == SCP_SculptProjection.Perspective ? "perspective" : "orthographic");
        aCam.Set("yaw", p.YawDeg);
        aCam.Set("pitch", p.PitchDeg);
        aCam.Set("roll", p.RollDeg);
        aCam.Set("target", Vec(p.TargetX, p.TargetY, p.TargetZ));
        aCam.Set("eye", Vec(p.EyeX, p.EyeY, p.EyeZ));
        aCam.Set("distance", Opt(p.Distance));
        aCam.Set("fov", p.FovDeg);
        aCam.Set("zoom", Opt(p.Zoom));
        aCam.Set("fit_upscale", p.FitUpscale);
        aOut.Set("camera", aCam);
        var aLights = SCP_JsonData.NewArray();
        foreach (SCP_SculptLight l in p.Lights)
        {
            var aL = SCP_JsonData.NewObject();
            aL.Set("dir", Vec(l.DirX, l.DirY, l.DirZ));
            aL.Set("color", Hex(l.R, l.G, l.B));
            aL.Set("intensity", l.Intensity);
            aL.Set("shadow", l.CastShadow);
            aLights.Add(aL);
        }
        aOut.Set("lights", aLights);
        aOut.Set("ambient", p.Ambient);
        aOut.Set("ao", p.AmbientOcclusion);
        aOut.Set("shadow", p.Shadow);
        var aSky = SCP_JsonData.NewObject();
        aSky.Set("path", p.Skybox ?? SCP_SculptRenderProfiles.SkyboxBuiltin);
        aSky.Set("yaw", p.SkyboxYawDeg);
        aSky.Set("tilt", p.SkyboxTiltDeg);
        aOut.Set("skybox", aSky);
        aOut.Set("floor", SCP_SculptRenderProfiles.DescribeFloor(p));
        aOut.Set("background", Hex(p.BgR, p.BgG, p.BgB));
        aOut.Set("width", (long)p.Width);
        aOut.Set("height", (long)p.Height);
        return aOut;
    }

    // ===========================================================
    // 區塊職責：slice／stats／export／exhibit —— 免費的引擎 op，結果物件直接映到回報與 values。
    // 數值影響：都拿雕刻鎖 —— 引擎每個 op 進來都重寫快取（讀改寫），與落子撞在一起會吃掉一刀。
    // ===========================================================
    static string? OpSlice(Ctx c)
    {
        string aRegion = c.Args.Get("region").Trim();
        if (aRegion.Length == 0)
            return Blocked(c, 2, "slice 需要 --arg region=<x1..x2,y1..y2,z1..z2>（法線軸跨度＝厚度）");
        if (!TryOutPath(c, SlicePngName, out string aOut, out string aOutBad)) return Blocked(c, 2, aOutBad);
        var aArgs = new SCP_SculptSliceArgs { Region = aRegion, Out = aOut };
        string aAxis = c.Args.Get("axis").Trim();
        if (aAxis.Length > 0) aArgs.Axis = aAxis;

        if (!TryLocked(c, e => e.Slice(aArgs), out SCP_SculptResult aRes, out string? aBlocked)) return aBlocked;
        c.Report.Append("```\n").Append(aRes.Render()).Append("\n```\n");
        if (aRes is SCP_SculptSliceResult s)
            c.Result.AddValue("non_transparent_pixels", s.NonTransparentPixels.ToString(CultureInfo.InvariantCulture));
        if (!aRes.Ok) return EngineFail(c, aRes);
        var aSlice = (SCP_SculptSliceResult)aRes;
        string aPath = aSlice.OutputPath.Replace('\\', '/');
        c.Result.AddValue("path", aPath);
        c.Result.AddValue("sha256", aSlice.Sha256);
        c.Result.AddOutput(aPath);
        return Finish(c, 0, "✓ slice " + aSlice.Size + "（非透明 " + aSlice.NonTransparentPixels + "）→ " + aPath);
    }

    static string? OpStats(Ctx c)
    {
        if (!TryLocked(c, e => e.Stats(), out SCP_SculptResult aRes, out string? aBlocked)) return aBlocked;
        c.Report.Append("```\n").Append(aRes.Render()).Append("\n```\n");
        if (!aRes.Ok) return EngineFail(c, aRes);
        c.Result.Lines.AddRange(aRes.Lines);
        c.Result.AddValue("total_voxels", ((SCP_SculptStatsResult)aRes).TotalVoxels.ToString(CultureInfo.InvariantCulture));
        return Finish(c, 0, "✓ stats 完成");
    }

    static string? OpExport(Ctx c)
    {
        string aFormat = c.Args.Get("format").Trim();
        if (aFormat.Length == 0) return Blocked(c, 2, "export 需要 --arg format=obj|vox");
        var aArgs = new SCP_SculptExportArgs
        {
            Format = aFormat,
            Region = c.Args.Get("region").Trim(),
            ExcludeColor = c.Args.Get("exclude_color").Trim(),
            Out = c.Args.Get("out").Trim(),
            OutDir = c.Args.Get("out_dir").Trim(),
        };
        string aMerge = c.Args.Get("merge").Trim();
        if (aMerge.Length > 0) aArgs.Merge = aMerge;
        if (!TryLocked(c, e => e.Export(aArgs), out SCP_SculptResult aRes, out string? aBlocked)) return aBlocked;
        c.Report.Append("```\n").Append(aRes.Render()).Append("\n```\n");
        if (!aRes.Ok) return EngineFail(c, aRes);
        var aEx = (SCP_SculptExportResult)aRes;
        string aPath = aEx.OutputPath.Replace('\\', '/');
        c.Result.Lines.AddRange(aRes.Lines);
        c.Result.AddValue("path", aPath);
        if (aEx.MtlPath.Length > 0) c.Result.AddValue("mtl_path", aEx.MtlPath.Replace('\\', '/'));
        c.Result.AddValue("voxels", aEx.VoxelCount.ToString(CultureInfo.InvariantCulture));
        if (aEx.Merge.Length > 0)
        {
            c.Result.AddValue("merge", aEx.Merge);
            c.Result.AddValue("faces", aEx.FaceCount.ToString(CultureInfo.InvariantCulture));
            c.Result.AddValue("vertices", aEx.VertexCount.ToString(CultureInfo.InvariantCulture));
        }
        c.Result.AddOutput(aPath);
        return Finish(c, 0, "✓ export " + aFormat + " " + aEx.VoxelCount + " voxels → " + aPath);
    }

    static string? OpExhibit(Ctx c)
    {
        string aSub = c.Args.Get("sub").Trim();
        if (aSub == "list")
        {
            if (!TryLocked(c, e => e.ExhibitList(), out SCP_SculptResult aList, out string? aListBlocked)) return aListBlocked;
            c.Report.Append("```\n").Append(aList.Render()).Append("\n```\n");
            c.Result.Lines.AddRange(aList.Lines);
            if (!aList.Ok) return EngineFail(c, aList);
            c.Result.AddValue("count", ((SCP_SculptExhibitResult)aList).Exhibits.Count.ToString(CultureInfo.InvariantCulture));
            return Finish(c, 0, "✓ exhibit list");
        }
        if (aSub != "register") return Blocked(c, 2, "exhibit 要 --arg sub=list|register（got '" + aSub + "'）");

        string aId = c.Args.Get("id").Trim(), aTitle = c.Args.Get("title").Trim();
        string aAuthor = c.Args.Get("author").Trim();
        if (aAuthor.Length == 0) aAuthor = c.Persona;
        if (aId.Length == 0 || aTitle.Length == 0 || aAuthor.Length == 0)
            return Blocked(c, 2, "exhibit register 需要 id、title、author（author 不給 ⇒ 用 persona）");
        if (aId.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || aId == "." || aId == "..")
            return Blocked(c, 2, "展品 id 不可含路徑字元：'" + aId + "'");
        var aArgs = new SCP_SculptExhibitRegisterArgs
        {
            Id = aId, Title = aTitle, Author = aAuthor,
            Desc = c.Args.Get("desc").Trim(),
            Region = c.Args.Get("region").Trim(),
            ExcludeColor = c.Args.Get("exclude_color").Trim(),
            BgColor = c.Args.Get("bg_color").Trim(),
            Skybox = c.Args.Get("skybox").Trim(),
            LightDir = c.Args.Get("light_dir").Trim(),
            Smooth = Truthy(c.Args.Get("smooth")),
            Shadow = Truthy(c.Args.Get("shadow")),
            Camera = new SCP_SculptCamera
            {
                Projection = c.Args.Get("projection").Trim(), Yaw = c.Args.Get("yaw").Trim(), Pitch = c.Args.Get("pitch").Trim(),
                Roll = c.Args.Get("roll").Trim(), Target = c.Args.Get("target").Trim(), Eye = c.Args.Get("eye").Trim(),
                Distance = c.Args.Get("distance").Trim(), Fov = c.Args.Get("fov").Trim(),
            },
        };
        string aAmb = c.Args.Get("ambient").Trim();
        if (aAmb.Length > 0)
        {
            if (!TryFinite(aAmb, out double a) || a < 0 || a > 1) return Blocked(c, 2, "ambient 要在 0..1：'" + aAmb + "'");
            aArgs.Ambient = a;
        }
        string aZoom = c.Args.Get("zoom").Trim();
        if (aZoom.Length > 0)
        {
            if (!TryFinite(aZoom, out double z) || z <= 0) return Blocked(c, 2, "zoom 要 > 0：'" + aZoom + "'");
            aArgs.Zoom = z;
        }
        if (!TryLocked(c, e => e.ExhibitRegister(aArgs), out SCP_SculptResult aRes, out string? aBlocked)) return aBlocked;
        c.Report.Append("```\n").Append(aRes.Render()).Append("\n```\n");
        if (!aRes.Ok) return EngineFail(c, aRes);
        var aEx = (SCP_SculptExhibitResult)aRes;
        c.Result.Lines.AddRange(aRes.Lines);
        c.Result.AddValue("exhibit_file", aEx.ExhibitFile.Replace('\\', '/'));
        if (aEx.PhotoPath.Length > 0)
        {
            c.Result.AddValue("photo", aEx.PhotoPath.Replace('\\', '/'));
            c.Result.AddOutput(aEx.PhotoPath.Replace('\\', '/'));
        }
        else c.Result.AddValue("photo_skipped", aEx.Warning);
        return Finish(c, 0, "✓ exhibit register `" + aId + "`" + (aEx.PhotoPath.Length > 0 ? "" : "（展品照沒出：" + aEx.Warning + "）"));
    }

    /// <summary>拿雕刻鎖跑一個引擎 op。拿不到 ⇒ false ＋ <paramref name="oBlocked"/>（已 Finish 過的回傳檔路徑）。</summary>
    static bool TryLocked(Ctx c, Func<SCP_SculptEngine, SCP_SculptResult> iRun, out SCP_SculptResult oRes, out string? oBlocked)
    {
        oBlocked = null;
        try
        {
            using (SCP_FileLock.Acquire(c.EngineLockTarget, EngineLockTimeoutSec))
                oRes = iRun(NewEngine(c));
            return true;
        }
        catch (SCP_FileLockTimeoutException e)
        {
            oRes = SCP_SculptResult.Text(1);
            oBlocked = Blocked(c, 4, "拿不到雕刻鎖：" + e.Message);
            return false;
        }
    }

    /// <summary>引擎回非 0：參數類（2）照 2；其餘（empty／沒東西可匯出／讀不了狀態…）＝ 出不了結果 ⇒ 1。</summary>
    static string? EngineFail(Ctx c, SCP_SculptResult iRes)
    {
        string aFirst = iRes.Lines.Count > 0 ? iRes.Lines[0] : (iRes.Status.Length > 0 ? "status=" + iRes.Status : "");
        c.Result.AddValue("engine_exit", iRes.ExitCode.ToString(CultureInfo.InvariantCulture));
        return Finish(c, iRes.ExitCode == 2 ? 2 : 1, "✗ " + c.Op + " 沒有結果（engine exit " + iRes.ExitCode + "）" + (aFirst.Length > 0 ? "：" + aFirst : ""));
    }

    // ===========================================================
    // 區塊職責：render-profile —— 長期保存的渲染設定（共用／個人兩層，各自多組、各自記住作用中那組）。
    // 物理意義：設定檔格式與疊加規則在 SCP_Core `SCP_SculptRenderProfiles`（本檔只做 CLI 的 sub 分派）。
    //          寫入前先驗（套到一份新的參數上）—— ⛔ 不讓一份套不上去的設定落盤，否則下一次 view 才炸、而且炸在別人身上。
    // 數值影響：寫入一律原子（WriteAtomic）；⛔ 不刪作用中的那一份（刪了等於讓作用中指向不存在 ⇒ 每個人的 view 都炸）。
    // ===========================================================
    static string? OpRenderProfile(Ctx c)
    {
        string aSub = c.Args.Get("sub").Trim();
        switch (aSub)
        {
            case "list": return ProfileList(c);
            case "show": return ProfileShow(c);
            case "set": return ProfileSet(c);
            case "use": return ProfileUse(c);
            case "copy": return ProfileCopy(c);
            case "delete": return ProfileDelete(c);
            case "reset": return ProfileReset(c);
            default: return Blocked(c, 2, "render-profile 要 --arg sub=list|show|set|use|copy|delete|reset（got '" + aSub + "'）");
        }
    }

    /// <summary>層名 → 目錄。persona 層要 persona（⛔ 不猜是誰的）。</summary>
    static bool TryScopeDir(Ctx c, string iScope, out string oDir, out string oWho, out string oErr)
    {
        oDir = ""; oWho = ""; oErr = "";
        string s = iScope.Length == 0 ? "shared" : iScope;
        if (s == "shared")
        {
            oDir = SCP_SculptRenderProfiles.ScopeDir(SCP_SculptProfileScope.Shared, c.Data, null, null);
            oWho = "共用";
            return true;
        }
        if (s == "persona")
        {
            if (!SCP_RegisteredMail.IsValidPersonaName(c.Persona)) { oErr = "scope=persona 要 --arg persona=<你>（個人設定記在你的信件夾）"; return false; }
            oDir = SCP_SculptRenderProfiles.ScopeDir(SCP_SculptProfileScope.Persona, c.Data, c.Letters, c.Persona);
            oWho = c.Persona + " 個人";
            return true;
        }
        oErr = "scope 只能是 shared|persona：'" + iScope + "'";
        return false;
    }

    static bool TryProfileTarget(Ctx c, string iScopeKey, string iNameKey, out string oDir, out string oWho, out string oName, out string oErr)
    {
        oName = c.Args.Get(iNameKey).Trim();
        if (!TryScopeDir(c, c.Args.Get(iScopeKey).Trim(), out oDir, out oWho, out oErr)) return false;
        if (!SCP_SculptRenderProfiles.IsValidName(oName))
        {
            oErr = iNameKey + " 要是 1..40 字的小寫英數／_／-：'" + oName + "'";
            return false;
        }
        return true;
    }

    /// <summary>寫完之後的「現在實際生效的是哪幾層」（每個寫入 sub 都附 —— 改了什麼要看得到結果）。</summary>
    static void AppendChain(Ctx c)
    {
        bool aOk = SCP_SculptRenderProfiles.TryResolve(c.Data, c.Letters, c.Persona.Length > 0 ? c.Persona : null,
                                                       out _, out List<string> aLayers, out string aErr);
        string aText = aOk ? string.Join(" → ", aLayers) : "✗ " + aErr;
        c.Report.Append("- effective chain").Append(c.Persona.Length > 0 ? "（" + c.Persona + "）" : "（只看共用層）")
                .Append(": ").Append(aText).Append('\n');
        c.Result.AddValue("layers", aText);
        c.Result.Lines.Add("  生效鏈：" + aText);
    }

    static string? ProfileList(Ctx c)
    {
        string aScope = c.Args.Get("scope").Trim();
        if (aScope.Length == 0) aScope = "all";
        var aScopes = new List<string>();
        if (aScope == "all") { aScopes.Add("shared"); if (c.Persona.Length > 0) aScopes.Add("persona"); }
        else aScopes.Add(aScope);
        int aCount = 0;
        foreach (string s in aScopes)
        {
            if (!TryScopeDir(c, s, out string aDir, out string aWho, out string aErr)) return Blocked(c, 2, aErr);
            string? aActive = SCP_SculptRenderProfiles.GetActive(aDir);
            List<string> aNames = SCP_SculptRenderProfiles.List(aDir);
            string aHead = "## " + aWho + "（" + aDir + "/" + SCP_SculptRenderProfiles.ProfilesDirName + "）—— 作用中："
                           + (aActive ?? (s == "persona" ? "（沒設 ⇒ 跟共用走）" : "（沒設 ⇒ 內建預設）"));
            c.Report.Append(aHead).Append('\n');
            c.Result.Lines.Add(aHead);
            foreach (string n in aNames)
            {
                string aLine = "  " + (n == aActive ? "* " : "- ") + n + (n == aActive ? "（作用中）" : "");
                c.Report.Append(aLine).Append('\n');
                c.Result.Lines.Add(aLine);
            }
            if (aActive != null && !aNames.Contains(aActive))
            {
                string aWarn = "  ⚠ 作用中的 `" + aActive + "` 不存在 ⇒ view 會擋（先 sub=use 換一組或 sub=set 建它）";
                c.Report.Append(aWarn).Append('\n');
                c.Result.Lines.Add(aWarn);
            }
            c.Result.AddValue(s + "_active", aActive ?? "");
            aCount += aNames.Count;
        }
        c.Result.AddValue("count", aCount.ToString(CultureInfo.InvariantCulture));
        return Finish(c, 0, "✓ render-profile list（" + aCount + " 份）");
    }

    static string? ProfileShow(Ctx c)
    {
        string aName = c.Args.Get("name").Trim();
        if (aName.Length > 0)
        {
            if (!TryProfileTarget(c, "scope", "name", out string aDir, out string aWho, out _, out string aErr)) return Blocked(c, 2, aErr);
            if (!SCP_SculptRenderProfiles.TryLoad(aDir, aName, out SCP_JsonData aProfile, out aErr))
                return Blocked(c, 2, aErr.Length > 0 ? aErr : aWho + "設定 `" + aName + "` 不存在");
            string aJson = aProfile.ToJson(true);
            c.Report.Append("## ").Append(aWho).Append("設定 `").Append(aName).Append("`\n```json\n").Append(aJson).Append("\n```\n");
            c.Result.Lines.Add(aJson);
            c.Result.AddValue("path", SCP_SculptRenderProfiles.ProfilePath(aDir, aName));
            bool aActive = SCP_SculptRenderProfiles.GetActive(aDir) == aName;
            c.Result.AddValue("active", aActive ? "1" : "0");
            return Finish(c, 0, "✓ " + aWho + "設定 `" + aName + "`" + (aActive ? "（作用中）" : ""));
        }
        // 沒給 name ⇒ 這個人此刻 view 會用的完整參數（疊完的結果）
        if (!SCP_SculptRenderProfiles.TryResolve(c.Data, c.Letters, c.Persona.Length > 0 ? c.Persona : null,
                                                 out SCP_SculptRenderParams aP, out List<string> aLayers, out string aResolveErr))
            return Blocked(c, 2, aResolveErr);
        string aLayersText = string.Join(" → ", aLayers);
        string aEff = DescribeParams(aP).ToJson(true);
        c.Report.Append("- layers: ").Append(aLayersText).Append("\n```json\n").Append(aEff).Append("\n```\n");
        c.Result.Lines.Add("  生效鏈：" + aLayersText);
        c.Result.Lines.Add(aEff);
        c.Result.AddValue("layers", aLayersText);
        return Finish(c, 0, "✓ 實際生效的渲染參數" + (c.Persona.Length > 0 ? "（" + c.Persona + "）" : "（只看共用層）"));
    }

    static string? ProfileSet(Ctx c)
    {
        if (!TryProfileTarget(c, "scope", "name", out string aDir, out string aWho, out string aName, out string aErr)) return Blocked(c, 2, aErr);
        // ⛔ 顯式給了、而 set 不吃的參數 ⇒ 擋（例：light_dir 是 view 的旗標，set 要用 light_add／lights）。
        //   靜默忽略的話，「我設了」與「沒設上」在畫面上完全同形。
        var aEdit = new Dictionary<string, string>(StringComparer.Ordinal);
        var aBadKeys = new List<string>();
        foreach (SCP_CmdArgSpec aSpec in ArgSpecsStatic)
        {
            if (!c.Args.IsExplicit(aSpec.Name)) continue;
            if (Array.IndexOf(s_ProfileEditKeys, aSpec.Name) >= 0) aEdit[aSpec.Name] = c.Args.Get(aSpec.Name);
            else if (Array.IndexOf(s_ProfileSetMetaKeys, aSpec.Name) < 0) aBadKeys.Add(aSpec.Name);
        }
        if (aBadKeys.Count > 0)
            return Blocked(c, 2, "render-profile set 不吃：" + string.Join(", ", aBadKeys) + "（設定鍵：" + string.Join(" ", s_ProfileEditKeys)
                                 + "；燈用 light_add／lights／light_clear）—— 什麼都沒寫");
        if (aEdit.Count == 0) return Blocked(c, 2, "render-profile set 沒給任何設定鍵（" + string.Join(" ", s_ProfileEditKeys) + "）");

        bool aExisted = SCP_SculptRenderProfiles.TryLoad(aDir, aName, out SCP_JsonData aProfile, out aErr);
        if (!aExisted && aErr.Length > 0) return Blocked(c, 2, aErr + " —— 先 sub=reset 重來或手修");
        if (!aExisted) aProfile = SCP_JsonData.NewObject();
        var aChanged = new List<string>();
        if (!SCP_SculptRenderProfiles.TryEdit(aProfile, aEdit, aChanged, out aErr)) return Blocked(c, 2, aErr + " —— 什麼都沒寫");
        if (!SCP_SculptRenderProfiles.TryApply(aProfile, new SCP_SculptRenderParams(), SCP_SculptRenderProfiles.SkyboxesDir(c.Data), out aErr))
            return Blocked(c, 2, "改完套不上去：" + aErr + " —— 什麼都沒寫");
        SCP_SculptRenderProfiles.Save(aDir, aName, aProfile);
        string aPath = SCP_SculptRenderProfiles.ProfilePath(aDir, aName);
        c.Report.Append("## ").Append(aExisted ? "改" : "新建").Append(' ').Append(aWho).Append("設定 `").Append(aName).Append("`\n")
                .Append("- changed: ").Append(string.Join(", ", aChanged)).Append('\n')
                .Append("- path: `").Append(aPath).Append("`\n```json\n").Append(aProfile.ToJson(true)).Append("\n```\n");
        c.Result.AddValue("path", aPath);
        c.Result.AddValue("changed", string.Join(",", aChanged));
        if (SCP_SculptRenderProfiles.GetActive(aDir) != aName)
            c.Result.Lines.Add("  ⚠ `" + aName + "` 不是" + aWho + "層作用中的那一份 ⇒ 要生效請 `sub=use`");
        AppendChain(c);
        return Finish(c, 0, "✓ " + (aExisted ? "改了" : "新建") + aWho + "設定 `" + aName + "`：" + string.Join(", ", aChanged));
    }

    static string? ProfileUse(Ctx c)
    {
        string aName = c.Args.Get("name").Trim();
        if (aName == "none")
        {
            if (!TryScopeDir(c, c.Args.Get("scope").Trim(), out string aNoneDir, out string aNoneWho, out string aNoneErr)) return Blocked(c, 2, aNoneErr);
            SCP_SculptRenderProfiles.SetActive(aNoneDir, null);
            c.Report.Append("- ").Append(aNoneWho).Append("層：不用任何設定（")
                    .Append(aNoneWho == "共用" ? "⇒ 只剩內建預設" : "⇒ 跟共用走").Append("）\n");
            AppendChain(c);
            return Finish(c, 0, "✓ " + aNoneWho + "層不再指定設定");
        }
        if (!TryProfileTarget(c, "scope", "name", out string aDir, out string aWho, out aName, out string aErr)) return Blocked(c, 2, aErr);
        if (!SCP_SculptRenderProfiles.TryLoad(aDir, aName, out SCP_JsonData aProfile, out aErr))
            return Blocked(c, 2, aErr.Length > 0 ? aErr : aWho + "設定 `" + aName + "` 不存在（先 sub=set 建它）");
        if (!SCP_SculptRenderProfiles.TryApply(aProfile, new SCP_SculptRenderParams(), SCP_SculptRenderProfiles.SkyboxesDir(c.Data), out aErr))
            return Blocked(c, 2, aWho + "設定 `" + aName + "` 套不上去：" + aErr + " —— 作用中沒換");
        string? aOld = SCP_SculptRenderProfiles.GetActive(aDir);
        SCP_SculptRenderProfiles.SetActive(aDir, aName);
        c.Report.Append("- ").Append(aWho).Append("層作用中：").Append(aOld ?? "（沒設）").Append(" → `").Append(aName).Append("`\n");
        c.Result.AddValue("active", aName);
        AppendChain(c);
        return Finish(c, 0, "✓ " + aWho + "層改用 `" + aName + "`");
    }

    static string? ProfileCopy(Ctx c)
    {
        if (!TryProfileTarget(c, "from_scope", "from", out string aFromDir, out string aFromWho, out string aFrom, out string aErr)) return Blocked(c, 2, aErr);
        if (!TryProfileTarget(c, "to_scope", "to", out string aToDir, out string aToWho, out string aTo, out aErr)) return Blocked(c, 2, aErr);
        if (!SCP_SculptRenderProfiles.TryLoad(aFromDir, aFrom, out SCP_JsonData aProfile, out aErr))
            return Blocked(c, 2, aErr.Length > 0 ? aErr : aFromWho + "設定 `" + aFrom + "` 不存在");
        if (aFromDir == aToDir && aFrom == aTo) return Blocked(c, 2, "來源與目標是同一份");
        bool aExists = File.Exists(SCP_SculptRenderProfiles.ProfilePath(aToDir, aTo));
        if (aExists && !Truthy(c.Args.Get("overwrite")))
            return Blocked(c, 2, aToWho + "已經有 `" + aTo + "` —— 要覆蓋請加 overwrite=1（⛔ 不默默蓋掉）");
        SCP_SculptRenderProfiles.Save(aToDir, aTo, aProfile);
        string aPath = SCP_SculptRenderProfiles.ProfilePath(aToDir, aTo);
        c.Report.Append("- copy: ").Append(aFromWho).Append(" `").Append(aFrom).Append("` → ").Append(aToWho).Append(" `").Append(aTo).Append('`')
                .Append(aExists ? "（覆蓋）" : "").Append('\n');
        c.Result.AddValue("path", aPath);
        AppendChain(c);
        return Finish(c, 0, "✓ 複製 " + aFromWho + " `" + aFrom + "` → " + aToWho + " `" + aTo + "`");
    }

    static string? ProfileDelete(Ctx c)
    {
        if (!TryProfileTarget(c, "scope", "name", out string aDir, out string aWho, out string aName, out string aErr)) return Blocked(c, 2, aErr);
        if (SCP_SculptRenderProfiles.GetActive(aDir) == aName)
            return Blocked(c, 2, "`" + aName + "` 是" + aWho + "層作用中的那一份 —— 先 `sub=use` 換一組（或 name=none）再刪");
        if (!SCP_SculptRenderProfiles.Delete(aDir, aName)) return Blocked(c, 2, aWho + "設定 `" + aName + "` 不存在");
        c.Report.Append("- deleted: ").Append(aWho).Append(" `").Append(aName).Append("`\n");
        AppendChain(c);
        return Finish(c, 0, "✓ 刪了" + aWho + "設定 `" + aName + "`");
    }

    static string? ProfileReset(Ctx c)
    {
        if (!TryProfileTarget(c, "scope", "name", out string aDir, out string aWho, out string aName, out string aErr)) return Blocked(c, 2, aErr);
        SCP_SculptRenderProfiles.Save(aDir, aName, SCP_JsonData.NewObject());
        string aPath = SCP_SculptRenderProfiles.ProfilePath(aDir, aName);
        c.Report.Append("- reset: ").Append(aWho).Append(" `").Append(aName).Append("` ⇒ `{}`（每一格都沿用下層）\n");
        c.Result.AddValue("path", aPath);
        AppendChain(c);
        return Finish(c, 0, "✓ " + aWho + "設定 `" + aName + "` 清成空（全部沿用下層）");
    }

    /// <summary>ArgSpecs 的實例（ProfileSet 要列舉「哪些是顯式給的」—— 規格只有一份，就是 <see cref="ArgSpecs"/>）。</summary>
    static IReadOnlyList<SCP_CmdArgSpec> ArgSpecsStatic => new Cmd_Sculpture().ArgSpecs;

    // ───────────────────────────── 回傳 ─────────────────────────────

    static string? Blocked(Ctx c, int iExit, string iReason)
    {
        c.Report.Append("## blocked\n- reason: ").Append(iReason).Append('\n');
        return Finish(c, iExit, "✗ blocked：" + iReason.Split('\n')[0]);
    }

    /// <summary>落回傳檔（有 persona 才落）並設定 exit。沒有 persona ⇒ 報告印在輸出。</summary>
    static string? Finish(Ctx c, int iExit, string iHeadline)
    {
        c.Result.ExitCode = iExit;
        c.Result.Lines.Add(iHeadline);
        if (c.Persona.Length == 0) { c.Result.Lines.Add(c.Report.ToString().TrimEnd()); return null; }
        string aPath = SCP_LettersPaths.CmdPayload(c.Letters, c.Persona, "sculpture", c.Op);
        try { SCP_CmdPayload.Write(aPath, c.Report.ToString()); return aPath; }
        catch (Exception e)
        {
            c.Result.Lines.Add("⚠ 回傳檔寫不進去（報告附在下面）：" + e.Message);
            c.Result.Lines.Add(c.Report.ToString().TrimEnd());
            return null;
        }
    }

    static void Fail(SCP_CmdResult ioResult, int iExit, string iLine)
    {
        ioResult.ExitCode = iExit;
        ioResult.Lines.Add(iLine);
    }

    // ───────────────────────────── 小工具 ─────────────────────────────

    static bool TryInt(Ctx c, string iKey, out int oVal)
        => int.TryParse(c.Args.Get(iKey).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out oVal);

    static bool TryFinite(string iText, out double oVal)
        => double.TryParse(iText, NumberStyles.Float, CultureInfo.InvariantCulture, out oVal) && !double.IsNaN(oVal) && !double.IsInfinity(oVal);

    static bool TryBool01(string iText, out bool oVal)
    {
        string v = iText.Trim().ToLowerInvariant();
        oVal = v == "1" || v == "true" || v == "yes" || v == "on";
        return oVal || v == "0" || v == "false" || v == "no" || v == "off";
    }

    static bool Truthy(string iVal)
    {
        string v = (iVal ?? "").Trim().ToLowerInvariant();
        return v == "1" || v == "true" || v == "yes";
    }

    static string ValueOf(SCP_CmdResult iResult, string iKey)
    {
        foreach (KeyValuePair<string, string> kv in iResult.Values)
            if (string.Equals(kv.Key, iKey, StringComparison.Ordinal)) return kv.Value;
        return "";
    }

    public static int CeilDiv(long a, int b) => (int)((a + b - 1) / b);

    /// <summary>兩角任意順序、clamp 0..255（與引擎 box 同語意）。回傳 clamp 後體積。</summary>
    public static int ClampedVolume(ref int x1, ref int x2, ref int y1, ref int y2, ref int z1, ref int z2, int iSize = 256)
    {
        void Norm(ref int a, ref int b) { if (a > b) (a, b) = (b, a); a = Math.Max(0, a); b = Math.Min(iSize - 1, b); }
        Norm(ref x1, ref x2); Norm(ref y1, ref y2); Norm(ref z1, ref z2);
        if (x2 < x1 || y2 < y1 || z2 < z1) return 0;
        return (x2 - x1 + 1) * (y2 - y1 + 1) * (z2 - z1 + 1);
    }

    /// <summary>
    /// 讀 PNG 寬高（不解碼本體）：8 bytes 簽章 ＋ IHDR 後 big-endian 的寬、高。
    /// 這個尺寸是預授權的分母 —— 由本檔自己量，不接受呼叫端申報。失敗回 false，⛔ 不猜預設值。
    /// </summary>
    public static bool TryReadPngSize(string iPath, out int oWidth, out int oHeight)
    {
        oWidth = oHeight = 0;
        try
        {
            var aHead = new byte[24];
            using (FileStream aFs = File.OpenRead(iPath))
            {
                int aRead = 0;
                while (aRead < aHead.Length)
                {
                    int n = aFs.Read(aHead, aRead, aHead.Length - aRead);
                    if (n <= 0) return false;
                    aRead += n;
                }
            }
            if (aHead[0] != 0x89 || aHead[1] != 0x50 || aHead[2] != 0x4E || aHead[3] != 0x47) return false;
            if (aHead[12] != 'I' || aHead[13] != 'H' || aHead[14] != 'D' || aHead[15] != 'R') return false;
            oWidth = (aHead[16] << 24) | (aHead[17] << 16) | (aHead[18] << 8) | aHead[19];
            oHeight = (aHead[20] << 24) | (aHead[21] << 16) | (aHead[22] << 8) | aHead[23];
            return oWidth > 0 && oHeight > 0;
        }
        catch (Exception) { return false; }
    }

    static bool TryParseWh(string iVal, out int oW, out int oH)
    {
        oW = oH = 0;
        string[] p = iVal.Split(',');
        return p.Length == 2
               && int.TryParse(p[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out oW)
               && int.TryParse(p[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out oH)
               && oW > 0 && oH > 0;
    }
}
