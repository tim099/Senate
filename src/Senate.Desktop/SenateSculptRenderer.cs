// 區塊職責：雕刻場景的 **GPU（OpenGL 3.3 Core）渲染器** —— 實作 SCP_Core 的 <see cref="ISCP_SculptRenderer"/>（TASK-0377）。
// 物理意義：⭐ 引擎出圖（`senate cmd sculpture op=view`、落子分享圖）與 Senate 觀測頁的預覽走**這一個實作**
//           （觀測頁 spawn 同一支 CLI 指令，所以只有 CLI 這條路徑需要它）。
//           流程：voxel → CPU 建「只有外露面」的網格（逐頂點 AO）→ 陰影圖（光源正交）→ 4x MSAA 主畫 →
//           blit 到解析 FBO → ReadPixels → 翻成由上到下的 RGBA8。
// 數值影響：
//   ・座標系：世界 x、y 水平、z 朝上 —— ⚠ 但舊引擎的螢幕投影是 `sx=(x−y)`、`sy=(x+y)/2−z`，
//     換成右手系來看那是一張**鏡像**的圖（從 +x+y 往下看，右手系裡 +y 會在右邊，舊圖卻是 +x 在右邊）。
//     作品是照著舊圖雕的（字、朝向都以舊圖為準）⇒ 本檔把世界 (x,y,z) 映到 GL 的 (x,−y,z) 再用標準右手系算，
//     於是預設視角與舊圖**同一個朝向**。Yaw／Eye／Target／Light 全在世界座標給，映射由本檔處理。
//   ・預設鏡頭 yaw 45、pitch 30 ＝ 真正的正交投影：頂面是 2:1 菱形（與舊圖相同），
//     ⚠ 但垂直牆高是 cos30×(24/√2) ≈ 14.7 px／voxel（舊圖為了畫起來整齊壓成 12 px）—— 舊圖不是真投影，這裡是。
//   ・縮放（正交）：Zoom＝null ⇒ 投影外框填滿 92%、上限 24 px／voxel 寬（只縮不放；FitUpscale ⇒ 不設上限、一律填滿）；
//     有值 ⇒ 24×Zoom px。
//     「voxel 寬」＝ yaw 45 時一顆 voxel 的水平投影寬（√2 個世界單位）⇒ 每單位 24×Zoom/√2 px，換 yaw 刻度不變。
//   ・光照：多盞平行光（最多 8 盞；Dir 是光**行進**方向，色×強度）的 Lambert 總和 ＋ 半球環境光×Ambient ＋ AO；
//     投陰影的光最多 2 盞，各一張 2048² 陰影圖 ＋ 3×3 PCF。半球環境光色：有 skybox ⇒ 由全景上下半球平均色調出色偏。
//     沒有後製：只有一道 0.85 起的柔肩＋歸一（一盞白光強度 1、正對它且無遮蔽的面 ＝ 光量 1.0 ＝ 調色盤原色，實測逐位元）。
//   ・背景：Skybox＝null ⇒ 內建黃昏天空（程式生成）；路徑 ⇒ 等距柱狀全景；"none" ⇒ 純色 Bg ＋極淡垂直漸層。
//     透視 ⇒ 每像素視線查全景；正交 ⇒ 視線全平行，改用跟著 yaw 轉的「假透視」視窗（垂直 70°、俯角 1/4，再加 SkyboxTiltDeg 往上抬）。
//   ・地板（Floor≠null，Tim 2026-10-02）：z＝Floor.Z 的水平四邊形，畫序 天空 → 地板（預乘 alpha 淡出）→ voxel。
//     範圍：FullGrid ⇒ 0..256 方格；否則可見 voxel 外框（xy）外擴 Margin；⚠ 外框模式＋沒有 voxel ⇒ 不畫地板。
//     光照：同一組燈＋陰影（只有 voxel 投影，地板只接不投）＋半球環境光的天空色，沒有 AO；單面（鏡頭在地板下方 ⇒ 剔除）。
//     貼圖：Texture＝null ⇒ fragment shader 程式畫的量尺網格（每 1／16／64 格一條線，fwidth 反鋸齒，線落在整數世界座標
//     ＝ voxel 邊界）；路徑 ⇒ 重複鋪（每 TileSize 格一次，mipmap＋各向異性），以「路徑＋修改時間＋大小」快取。
//     邊緣淡出：距範圍邊 < Fade×邊長 的區域 alpha 平滑降到 0，蓋在已畫好的天空上 ⇒ 淡到「那個像素看到的背景」。
//     框景：⚠ **只框 voxel**（＋voxel 在地板上的投影腳印，僅當地板與 voxel 底部的距離 ≤ Margin）——
//     FullGrid 的 256 格地板不參與框景（不然作品會被縮成一個點），地板超出畫面就讓它出去；
//     例外：沒有 voxel 而有 FullGrid ⇒ 框整片地板（不然畫面上什麼都沒有）。深度範圍（近／遠平面）則一律涵蓋地板。
// 執行緒：GL context 在**第一次 TryRender 的執行緒**上建立（隱藏 GLFW 視窗）。之後每次呼叫都 MakeCurrent／
//         結束時 ClearContext，並以 lock 序列化 ⇒ 換執行緒呼叫也行，但建議固定在 CLI 主執行緒（GLFW 的慣例）。
// 失敗：建不出 context（沒有顯卡／遠端桌面沒有 GL 3.3…）⇒ TryRender 回 false 與原因，⛔ 不丟例外；
//       之後的呼叫直接回同一個原因，不重試（同一個行程裡驅動不會自己變好）。
// 決定性：網格依座標排序建立（與輸入順序無關；重複座標＝後者為準）；同機同驅動 ⇒ 同輸入同位元組。
#nullable enable
using System.Numerics;
using SCP.Core.Canvas;
using SCP.Core.Sculpture;
using Silk.NET.Core.Contexts;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace Senate.Desktop;

public sealed class SenateSculptRenderer : ISCP_SculptRenderer, IDisposable
{
    /// <summary>陰影圖邊長（texel）。</summary>
    public const int ShadowMapSize = 2048;
    /// <summary>舊引擎刻度：Zoom 1 時一顆 voxel（yaw 45 的水平投影）寬 24 px。</summary>
    const double VoxelWidthPx = 24.0;
    /// <summary>自動框住時投影外框佔畫面的比例（與舊引擎相同）。</summary>
    const double FitFill = 0.92;
    const int MaxSide = 8192;

    readonly object m_Lock = new();
    IWindow? m_Window;
    IGLContext? m_Context;
    GL? m_Gl;
    string? m_InitError;
    string m_GlInfo = "";
    int m_MaxSamples;
    int m_MaxRbSize;

    uint m_MainProg, m_ShadowProg, m_BgProg, m_SkyProg, m_FloorProg;
    /// <summary>各向異性過濾上限（0 ＝ 這張卡／驅動沒有那個擴充）。</summary>
    float m_MaxAniso;
    uint m_EmptyVao;
    readonly uint[] m_ShadowFbo = new uint[MaxShadowMaps], m_ShadowTex = new uint[MaxShadowMaps];
    const int MaxShadowMaps = SCP_SculptRenderParams.MaxShadowLights;
    const int MaxLightsGpu = SCP_SculptRenderParams.MaxLights;
    // 依尺寸快取的 MSAA／解析 FBO（同尺寸連畫多張不必重建）。
    uint m_MsFbo, m_MsColor, m_MsDepth, m_ResolveFbo, m_ResolveColor;
    int m_FboW, m_FboH, m_FboSamples;

    public string Name => m_GlInfo.Length == 0 ? "Senate.Desktop OpenGL 3.3（GPU，尚未初始化）"
                                               : "Senate.Desktop OpenGL（" + m_GlInfo + "）";

    // ════════════════════════════════════════════════════════════════════════════════════
    // 區塊職責：對外入口。參數驗證 → 建網格 → 算鏡頭 → GPU 畫 → 讀回。
    // ════════════════════════════════════════════════════════════════════════════════════
    public bool TryRender(IReadOnlyList<SCP_SculptVoxel> iVoxels, SCP_SculptRenderParams iParams,
                          out byte[] oRgba, out string oError)
    {
        oRgba = Array.Empty<byte>();
        oError = "";
        if (iVoxels == null || iParams == null) { oError = "voxel 清單或參數是 null"; return false; }
        if (!Validate(iParams, out oError)) return false;

        lock (m_Lock)
        {
            if (!EnsureContext(out oError)) return false;
            try
            {
                m_Context!.MakeCurrent();
                SkyTexture? aSky = null;
                if (!IsSkyNone(iParams.Skybox) && !ResolveSky(iParams.Skybox, out aSky, out oError)) return false;
                FloorTexture? aFloorTex = null;
                if (iParams.Floor?.Texture != null && !ResolveFloorTexture(iParams.Floor.Texture, out aFloorTex, out oError)) return false;
                var aMesh = BuildMesh(iVoxels, iParams.AmbientOcclusion);
                FloorSetup? aFloor = SolveFloor(aMesh, iParams, aFloorTex);
                if (!SolveCamera(aMesh, aFloor, iParams, out Matrix4x4 aViewProj, out SkyView aSkyView, out oError)) return false;
                var aLights = SolveLights(aMesh, aFloor, iParams);
                oRgba = RenderGpu(aMesh, aFloor, iParams, aViewProj, aLights, aSky, aSkyView);
                var aErr = m_Gl!.GetError();
                if (aErr != GLEnum.NoError) { oRgba = Array.Empty<byte>(); oError = "OpenGL 錯誤：" + aErr; return false; }
                return true;
            }
            catch (Exception e)
            {
                oRgba = Array.Empty<byte>();
                oError = "GPU 渲染失敗（" + e.GetType().Name + "）：" + e.Message;
                return false;
            }
            finally
            {
                try { m_Context?.Clear(); } catch (Exception) { /* 釋放 context 失敗不影響這張圖 */ }
            }
        }
    }

    static bool Finite(double iV) => !double.IsNaN(iV) && !double.IsInfinity(iV);

    /// <summary>
    /// 參數驗證。⚠ 給錯就回 false 說清楚，⛔ 不夾成「看起來合理」的值（夾了的那張圖會被當成對的）。
    /// </summary>
    bool Validate(SCP_SculptRenderParams iP, out string oError)
    {
        oError = "";
        if (iP.Width <= 0 || iP.Height <= 0 || iP.Width > MaxSide || iP.Height > MaxSide)
        { oError = $"圖片尺寸不合法：{iP.Width}×{iP.Height}（需 1..{MaxSide}）"; return false; }
        int aEyeCount = (iP.EyeX.HasValue ? 1 : 0) + (iP.EyeY.HasValue ? 1 : 0) + (iP.EyeZ.HasValue ? 1 : 0);
        if (aEyeCount != 0 && aEyeCount != 3)
        { oError = "鏡頭位置 EyeX/EyeY/EyeZ 只給了 " + aEyeCount + " 軸 —— 要嘛三軸都給、要嘛都不給（不猜缺的那一軸）"; return false; }
        double[] aAll = { iP.Ambient, iP.YawDeg, iP.PitchDeg, iP.RollDeg, iP.FovDeg,
                          iP.TargetX ?? 0, iP.TargetY ?? 0, iP.TargetZ ?? 0, iP.EyeX ?? 0, iP.EyeY ?? 0, iP.EyeZ ?? 0,
                          iP.Distance ?? 1, iP.Zoom ?? 1 };
        foreach (double v in aAll) if (!Finite(v)) { oError = "參數含 NaN／無限大"; return false; }
        if (iP.Lights == null) { oError = "Lights 是 null（只要環境光請給空清單）"; return false; }
        if (iP.Lights.Count > SCP_SculptRenderParams.MaxLights)
        { oError = $"光源 {iP.Lights.Count} 盞，超過上限 {SCP_SculptRenderParams.MaxLights}"; return false; }
        int aCasters = 0;
        for (int i = 0; i < iP.Lights.Count; i++)
        {
            var aL = iP.Lights[i];
            if (aL == null) { oError = $"第 {i + 1} 盞光是 null"; return false; }
            if (!Finite(aL.DirX) || !Finite(aL.DirY) || !Finite(aL.DirZ) || !Finite(aL.Intensity))
            { oError = $"第 {i + 1} 盞光含 NaN／無限大"; return false; }
            if (aL.DirX * aL.DirX + aL.DirY * aL.DirY + aL.DirZ * aL.DirZ < 1e-12)
            { oError = $"第 {i + 1} 盞光的方向是零向量"; return false; }
            if (aL.Intensity < 0) { oError = $"第 {i + 1} 盞光的強度是負的：{aL.Intensity}"; return false; }
            if (aL.CastShadow) aCasters++;
        }
        // ⚠ 投影光數量不看 Shadow 總開關也照算 —— 同一份設定開關一切換就壞，比一開始就說清楚糟。
        if (aCasters > SCP_SculptRenderParams.MaxShadowLights)
        { oError = $"投陰影的光 {aCasters} 盞，超過上限 {SCP_SculptRenderParams.MaxShadowLights}（⛔ 不默默只取前幾盞）"; return false; }
        if (iP.Ambient < 0 || iP.Ambient > 1) { oError = "Ambient 需在 0..1：" + iP.Ambient; return false; }
        if (iP.Projection == SCP_SculptProjection.Perspective)
        {
            if (iP.FovDeg <= 1 || iP.FovDeg >= 170) { oError = "FovDeg 需在 (1,170)：" + iP.FovDeg; return false; }
            if (iP.Distance.HasValue && iP.Distance.Value <= 0) { oError = "Distance 需 > 0：" + iP.Distance; return false; }
        }
        else if (iP.Zoom.HasValue && iP.Zoom.Value <= 0) { oError = "Zoom 需 > 0（自動框住請給 null）：" + iP.Zoom; return false; }
        if (iP.Skybox != null && !IsSkyNone(iP.Skybox) && !Path.IsPathRooted(iP.Skybox))
        { oError = "Skybox 需是絕對路徑（或 null＝內建天空、\"none\"＝純色背景）：" + iP.Skybox; return false; }
        if (!Finite(iP.SkyboxYawDeg)) { oError = "SkyboxYawDeg 是 NaN／無限大"; return false; }
        if (!Finite(iP.SkyboxTiltDeg) || iP.SkyboxTiltDeg < -89 || iP.SkyboxTiltDeg > 89)
        { oError = "SkyboxTiltDeg 需在 −89..89：" + iP.SkyboxTiltDeg; return false; }
        if (iP.Projection != SCP_SculptProjection.Orthographic && iP.Projection != SCP_SculptProjection.Perspective)
        { oError = "認不得的投影模式：" + iP.Projection; return false; }
        var aF = iP.Floor;
        if (aF != null)
        {
            if (!Finite(aF.Z) || !Finite(aF.Margin) || !Finite(aF.TileSize) || !Finite(aF.Fade))
            { oError = "Floor 參數含 NaN／無限大"; return false; }
            if (aF.Margin < 0) { oError = "Floor.Margin 需 ≥ 0：" + aF.Margin; return false; }
            if (aF.TileSize <= 0) { oError = "Floor.TileSize 需 > 0：" + aF.TileSize; return false; }
            if (aF.Fade < 0 || aF.Fade > 0.5) { oError = "Floor.Fade 需在 0..0.5：" + aF.Fade; return false; }
            if (aF.Texture != null && !Path.IsPathRooted(aF.Texture))
            { oError = "Floor.Texture 需是絕對路徑（或 null＝內建量尺網格）：" + aF.Texture; return false; }
        }
        return true;
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // 區塊職責：GL context（隱藏 GLFW 視窗）與常駐資源（shader、陰影圖）。第一次呼叫時才建。
    // ════════════════════════════════════════════════════════════════════════════════════
    bool EnsureContext(out string oError)
    {
        oError = "";
        if (m_Gl != null) return true;
        if (m_InitError != null) { oError = m_InitError; return false; }
        try
        {
            var aOpt = WindowOptions.Default;
            aOpt.Size = new Vector2D<int>(16, 16);
            aOpt.Title = "senate-sculpt-offscreen";
            aOpt.IsVisible = false;
            aOpt.VSync = false;
            aOpt.ShouldSwapAutomatically = false;
            aOpt.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3));
            m_Window = Window.Create(aOpt);
            m_Window.Initialize();
            m_Context = m_Window.GLContext ?? throw new InvalidOperationException("視窗沒有 GL context");
            m_Context.MakeCurrent();
            m_Gl = GL.GetApi(m_Context);
            m_GlInfo = (m_Gl.GetStringS(StringName.Version) ?? "?") + "｜" + (m_Gl.GetStringS(StringName.Renderer) ?? "?");
            m_MaxSamples = m_Gl.GetInteger((GetPName)GLEnum.MaxSamples);
            m_MaxRbSize = m_Gl.GetInteger(GetPName.MaxRenderbufferSize);
            m_MaxAniso = QueryMaxAnisotropy(m_Gl);
            CreateStaticResources();
            m_Context.Clear();
            return true;
        }
        catch (Exception e)
        {
            m_InitError = "建不出 OpenGL 3.3 context（" + e.GetType().Name + "：" + e.Message + "）⇒ 這台機器／這個工作階段沒有 GPU 雕刻渲染";
            try { m_Gl?.Dispose(); m_Window?.Dispose(); } catch (Exception) { /* 已經在失敗路徑上 */ }
            m_Gl = null; m_Window = null; m_Context = null;
            oError = m_InitError;
            return false;
        }
    }

    const string MainVs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec4 aColorAo;
uniform mat4 uViewProj;
uniform mat4 uLightVP0;
uniform mat4 uLightVP1;
uniform float uNormalOffset;
out vec3 vNormal;
out vec3 vColor;
out float vAo;
out vec4 vLightPos0;
out vec4 vLightPos1;
void main() {
    gl_Position = uViewProj * vec4(aPos, 1.0);
    vNormal = aNormal;
    vColor = aColorAo.rgb;
    vAo = aColorAo.a;
    vec4 aOff = vec4(aPos + aNormal * uNormalOffset, 1.0);
    vLightPos0 = uLightVP0 * aOff;
    vLightPos1 = uLightVP1 * aOff;
}";

    // 光量 = Ambient×半球×AO ＋ (1−Ambient)×Sun×Σ(光色×強度×N·L×陰影)×(AO 的一部分)，逐通道過 0.85 起的柔肩，
    // 再乘 uToneScale ＝ 1／柔肩(Ambient＋(1−Ambient)×Sun) ⇒ 一盞白光強度 1、正對它的頂面 ＝ 光量 1.0 ＝ 調色盤原色。
    // Sun 1.3：預設斜光（N·L≈0.577）下頂面約 0.87 —— 顏色貼近調色盤、不過曝。
    const string MainFs = @"#version 330 core
in vec3 vNormal;
in vec3 vColor;
in float vAo;
in vec4 vLightPos0;
in vec4 vLightPos1;
uniform int uLightCount;
uniform vec3 uLightDir[8];
uniform vec3 uLightCol[8];
uniform int uLightShadow[8];
uniform float uAmbient;
uniform float uToneScale;
uniform sampler2DShadow uShadowMap0;
uniform sampler2DShadow uShadowMap1;
uniform float uTexel;
uniform float uBias;
uniform float uAoMin;
uniform float uSun;
uniform vec3 uHemiSky;
uniform vec3 uHemiGround;
out vec4 oColor;
float shoulder(float x) { if (x > 0.85) x = 0.85 + 0.15 * (1.0 - exp(-(x - 0.85) / 0.15)); return x; }
float shadowFactor(sampler2DShadow iMap, vec4 iLightPos) {
    vec3 p = iLightPos.xyz / iLightPos.w * 0.5 + 0.5;
    if (p.z >= 1.0) return 1.0;
    float s = 0.0;
    for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
            s += texture(iMap, vec3(p.xy + vec2(x, y) * uTexel, p.z - uBias));
    return s / 9.0;
}
void main() {
    vec3 n = normalize(vNormal);
    vec3 direct = vec3(0.0);
    for (int i = 0; i < uLightCount; i++) {
        float ndl = max(dot(n, uLightDir[i]), 0.0);
        if (ndl <= 0.0) continue;
        float sh = 1.0;
        if (uLightShadow[i] == 0) sh = shadowFactor(uShadowMap0, vLightPos0);
        else if (uLightShadow[i] == 1) sh = shadowFactor(uShadowMap1, vLightPos1);
        direct += uLightCol[i] * (ndl * sh);
    }
    float ao = mix(uAoMin, 1.0, vAo);
    vec3 hemi = mix(uHemiGround, uHemiSky, n.z * 0.5 + 0.5);
    vec3 light = uAmbient * hemi * ao + (1.0 - uAmbient) * uSun * direct * mix(1.0, ao, 0.4);
    // 肩部：0.85 以上平滑收（逐通道：有色光／色偏時三通道各自收），再乘 uToneScale（見上方說明）。
    light = vec3(shoulder(light.r), shoulder(light.g), shoulder(light.b)) * uToneScale;
    vec3 c = vColor * light;
    oColor = vec4(clamp(c, 0.0, 1.0), 1.0);
}";

    const string ShadowVs = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uLightVP;
void main() { gl_Position = uLightVP * vec4(aPos, 1.0); }";

    const string ShadowFs = @"#version 330 core
void main() { }";

    // 背景：全畫面三角形，極淡的垂直漸層（上方略亮 3.5%、下方略暗 6%）。
    const string BgVs = @"#version 330 core
out float vT;
void main() {
    vec2 p = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
    vT = p.y * 0.5 + 0.5;
    gl_Position = vec4(p, 0.0, 1.0);
}";

    const string BgFs = @"#version 330 core
in float vT;
uniform vec3 uBg;
out vec4 oColor;
void main() {
    vec3 top = mix(uBg, vec3(1.0), 0.035);
    vec3 bottom = uBg * 0.94;
    oColor = vec4(mix(bottom, top, clamp(vT, 0.0, 1.0)), 1.0);
}";

    // Skybox：每個像素一條視線 → 等距柱狀全景查表（世界 z 朝上；GL→世界時 y 取負）。
    // 經度 φ＝atan2(y,x)（世界）− SkyboxYaw，u＝0.5＋φ/2π（u＝0.5 ＝ 世界 +x 方向）；v＝0.5−asin(z)/π（圖的第 0 列＝天頂）。
    const string SkyVs = @"#version 330 core
out vec2 vNdc;
void main() {
    vec2 p = vec2((gl_VertexID == 1) ? 3.0 : -1.0, (gl_VertexID == 2) ? 3.0 : -1.0);
    vNdc = p;
    gl_Position = vec4(p, 0.0, 1.0);
}";

    const string SkyFs = @"#version 330 core
in vec2 vNdc;
uniform vec3 uRight;
uniform vec3 uUp;
uniform vec3 uForward;
uniform float uTanX;
uniform float uTanY;
uniform float uSkyYaw;
uniform float uLod;
uniform sampler2D uSky;
out vec4 oColor;
const float PI = 3.14159265358979;
void main() {
    vec3 d = normalize(uForward + uRight * (vNdc.x * uTanX) + uUp * (vNdc.y * uTanY));
    vec3 w = vec3(d.x, -d.y, d.z);
    float phi = atan(w.y, w.x) - uSkyYaw;
    vec2 uv = vec2(0.5 + phi / (2.0 * PI), 0.5 - asin(clamp(w.z, -1.0, 1.0)) / PI);
    oColor = vec4(textureLod(uSky, uv, uLod).rgb, 1.0);
}";

    // 地板：一片 z＝Floor.Z 的四邊形（GL 座標；vWorld 把 y 取負還原成世界 xy ⇒ 網格線與 voxel 邊界同一套整數座標）。
    // 光 ＝ 與 voxel 同式（法線 +z、半球取天空那一端、沒有 AO），陰影用同兩張陰影圖（地板只接不投、不做法線偏移）。
    const string FloorVs = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uViewProj;
uniform mat4 uLightVP0;
uniform mat4 uLightVP1;
out vec2 vWorld;
out vec4 vLightPos0;
out vec4 vLightPos1;
void main() {
    gl_Position = uViewProj * vec4(aPos, 1.0);
    vWorld = vec2(aPos.x, -aPos.y);
    vLightPos0 = uLightVP0 * vec4(aPos, 1.0);
    vLightPos1 = uLightVP1 * vec4(aPos, 1.0);
}";

    // 量尺網格：底色深石板；每 1 格一條細線（淡）、每 16 格（較亮、較粗）、每 64 格（最亮）。
    // 線寬以「螢幕像素」計（fwidth）⇒ 任何縮放都一樣銳利；線距小於約 3 px 時那一級線淡掉（不然糊成一片灰／摩爾紋）。
    // 輸出是**預乘 alpha**（rgb×a, a）—— 混色式 ONE, ONE_MINUS_SRC_ALPHA 蓋在已畫好的天空上 ⇒ 邊緣淡到該像素看到的背景。
    const string FloorFs = @"#version 330 core
in vec2 vWorld;
in vec4 vLightPos0;
in vec4 vLightPos1;
uniform int uLightCount;
uniform vec3 uLightDir[8];
uniform vec3 uLightCol[8];
uniform int uLightShadow[8];
uniform float uAmbient;
uniform float uToneScale;
uniform sampler2DShadow uShadowMap0;
uniform sampler2DShadow uShadowMap1;
uniform float uTexel;
uniform float uBias;
uniform float uSun;
uniform vec3 uHemiSky;
uniform vec4 uRect;
uniform vec2 uFadeW;
uniform vec3 uTint;
uniform int uMode;
uniform sampler2D uTex;
uniform float uTile;
out vec4 oColor;
float shoulder(float x) { if (x > 0.85) x = 0.85 + 0.15 * (1.0 - exp(-(x - 0.85) / 0.15)); return x; }
float shadowFactor(sampler2DShadow iMap, vec4 iLightPos) {
    vec3 p = iLightPos.xyz / iLightPos.w * 0.5 + 0.5;
    if (p.z >= 1.0) return 1.0;
    float s = 0.0;
    for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++)
            s += texture(iMap, vec3(p.xy + vec2(x, y) * uTexel, p.z - uBias));
    return s / 9.0;
}
float lineCov(vec2 iP, float iPeriod, float iHalfPx) {
    vec2 q = iP / iPeriod;
    vec2 fw = max(fwidth(q), vec2(1e-6));
    vec2 d = abs(fract(q + 0.5) - 0.5) / fw;
    float cov = clamp(iHalfPx + 0.5 - min(d.x, d.y), 0.0, 1.0);
    float cellPx = 1.0 / max(fw.x, fw.y);
    return cov * clamp((cellPx - 3.0) / 6.0, 0.0, 1.0);
}
void main() {
    vec2 dEdge = min(vWorld - uRect.xy, uRect.zw - vWorld);
    float a = 1.0;
    if (uFadeW.x > 0.0) a *= smoothstep(0.0, uFadeW.x, dEdge.x);
    if (uFadeW.y > 0.0) a *= smoothstep(0.0, uFadeW.y, dEdge.y);
    if (a < 1.0 / 512.0) discard;
    vec3 albedo;
    if (uMode == 1) albedo = texture(uTex, vWorld / uTile).rgb;
    else {
        albedo = vec3(0.150, 0.170, 0.205);
        albedo = mix(albedo, vec3(0.245, 0.270, 0.315), lineCov(vWorld, 1.0, 0.5) * 0.60);
        albedo = mix(albedo, vec3(0.360, 0.395, 0.455), lineCov(vWorld, 16.0, 0.8) * 0.85);
        albedo = mix(albedo, vec3(0.500, 0.545, 0.610), lineCov(vWorld, 64.0, 1.1) * 0.95);
    }
    albedo *= uTint;
    vec3 direct = vec3(0.0);
    for (int i = 0; i < uLightCount; i++) {
        float ndl = max(uLightDir[i].z, 0.0);
        if (ndl <= 0.0) continue;
        float sh = 1.0;
        if (uLightShadow[i] == 0) sh = shadowFactor(uShadowMap0, vLightPos0);
        else if (uLightShadow[i] == 1) sh = shadowFactor(uShadowMap1, vLightPos1);
        direct += uLightCol[i] * (ndl * sh);
    }
    vec3 light = uAmbient * uHemiSky + (1.0 - uAmbient) * uSun * direct;
    light = vec3(shoulder(light.r), shoulder(light.g), shoulder(light.b)) * uToneScale;
    vec3 c = clamp(albedo * light, 0.0, 1.0);
    oColor = vec4(c * a, a);
}";

    unsafe void CreateStaticResources()
    {
        var gl = m_Gl!;
        m_MainProg = Link(MainVs, MainFs);
        m_ShadowProg = Link(ShadowVs, ShadowFs);
        m_BgProg = Link(BgVs, BgFs);
        m_SkyProg = Link(SkyVs, SkyFs);
        m_FloorProg = Link(FloorVs, FloorFs);
        m_EmptyVao = gl.GenVertexArray();

        float* aBorder = stackalloc float[4] { 1, 1, 1, 1 };
        for (int m = 0; m < MaxShadowMaps; m++)
        {
            m_ShadowTex[m] = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, m_ShadowTex[m]);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, ShadowMapSize, ShadowMapSize, 0,
                          PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, aBorder);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);

            m_ShadowFbo[m] = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, m_ShadowFbo[m]);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, m_ShadowTex[m], 0);
            gl.DrawBuffer(DrawBufferMode.None);
            gl.ReadBuffer(ReadBufferMode.None);
            CheckFbo("陰影圖");
        }
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    uint Link(string iVs, string iFs)
    {
        var gl = m_Gl!;
        uint aVs = Compile(ShaderType.VertexShader, iVs);
        uint aFs = Compile(ShaderType.FragmentShader, iFs);
        uint aProg = gl.CreateProgram();
        gl.AttachShader(aProg, aVs);
        gl.AttachShader(aProg, aFs);
        gl.LinkProgram(aProg);
        gl.GetProgram(aProg, ProgramPropertyARB.LinkStatus, out int aOk);
        gl.DeleteShader(aVs);
        gl.DeleteShader(aFs);
        if (aOk == 0) throw new InvalidOperationException("shader 連結失敗：" + gl.GetProgramInfoLog(aProg));
        return aProg;
    }

    uint Compile(ShaderType iType, string iSrc)
    {
        var gl = m_Gl!;
        uint aId = gl.CreateShader(iType);
        gl.ShaderSource(aId, iSrc);
        gl.CompileShader(aId);
        gl.GetShader(aId, ShaderParameterName.CompileStatus, out int aOk);
        if (aOk == 0) throw new InvalidOperationException(iType + " 編譯失敗：" + gl.GetShaderInfoLog(aId));
        return aId;
    }

    void CheckFbo(string iWhat)
    {
        var aStatus = m_Gl!.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (aStatus != GLEnum.FramebufferComplete) throw new InvalidOperationException(iWhat + " FBO 不完整：" + aStatus);
    }

    /// <summary>MSAA 與解析 FBO（尺寸變了才重建）。</summary>
    void EnsureTargets(int iW, int iH)
    {
        var gl = m_Gl!;
        if (iW > m_MaxRbSize || iH > m_MaxRbSize)
            throw new InvalidOperationException($"圖片 {iW}×{iH} 超過這張顯卡的 renderbuffer 上限 {m_MaxRbSize}");
        int aSamples = Math.Max(0, Math.Min(4, m_MaxSamples));
        if (m_MsFbo != 0 && m_FboW == iW && m_FboH == iH && m_FboSamples == aSamples) return;
        DeleteTargets();

        m_MsFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, m_MsFbo);
        m_MsColor = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, m_MsColor);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)aSamples, InternalFormat.Rgba8, (uint)iW, (uint)iH);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, m_MsColor);
        m_MsDepth = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, m_MsDepth);
        gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)aSamples, InternalFormat.DepthComponent24, (uint)iW, (uint)iH);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, m_MsDepth);
        CheckFbo("MSAA");

        m_ResolveFbo = gl.GenFramebuffer();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, m_ResolveFbo);
        m_ResolveColor = gl.GenRenderbuffer();
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, m_ResolveColor);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)iW, (uint)iH);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, m_ResolveColor);
        CheckFbo("解析");
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        m_FboW = iW; m_FboH = iH; m_FboSamples = aSamples;
    }

    void DeleteTargets()
    {
        var gl = m_Gl!;
        if (m_MsFbo != 0) gl.DeleteFramebuffer(m_MsFbo);
        if (m_ResolveFbo != 0) gl.DeleteFramebuffer(m_ResolveFbo);
        if (m_MsColor != 0) gl.DeleteRenderbuffer(m_MsColor);
        if (m_MsDepth != 0) gl.DeleteRenderbuffer(m_MsDepth);
        if (m_ResolveColor != 0) gl.DeleteRenderbuffer(m_ResolveColor);
        m_MsFbo = m_ResolveFbo = m_MsColor = m_MsDepth = m_ResolveColor = 0;
        m_FboW = m_FboH = 0;
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // 區塊職責：Skybox —— 等距柱狀全景（2:1）的讀取、貼圖快取、內建預設天空、半球環境光色。
    // 物理意義：Skybox＝null ⇒ 內建天空（程式生成、決定性）；"none" ⇒ 純色 Bg（與沒有 skybox 的舊版逐位元相同）；
    //           絕對路徑 ⇒ StbImageSharp 解碼（PNG／JPG），以「路徑＋修改時間＋大小」快取。
    //           ⛔ 路徑給了但讀不了／不是 2:1 ⇒ TryRender 回 false —— 不默默退回預設天空（「換了」與「沒換成」不能同形）。
    // 數值影響：全景上下半球的平均色（依緯度 cos 加權＝立體角）調成**色偏**（亮度歸一、只保留 60% 色相），
    //           乘進半球環境光 ⇒ 光照跟著天空染色；亮度仍由 Ambient 決定。
    // 方位慣例：u＝0.5 ＝ 世界 +x 方向、u 往右 ＝ 經度 atan2(y,x) 增加（往 +y 轉）；第 0 列＝天頂。SkyboxYawDeg 正值 ＝ 天空往 +經度轉。
    // ════════════════════════════════════════════════════════════════════════════════════
    sealed class SkyTexture
    {
        public uint Tex;
        public int Width, Height;
        public DateTime WriteTimeUtc;
        public long Length;
        public Vector3 HemiSky, HemiGround;
    }

    /// <summary>背景視線的鏡頭基底（GL 座標）。正交時是「假透視」視窗，見 <see cref="OrthoSkyView"/>。</summary>
    struct SkyView
    {
        public Vector3 Right, Up, Forward;
        public float TanX, TanY;
    }

    const string BuiltinSkyKey = "<builtin>";
    /// <summary>內建天空的尺寸。</summary>
    public const int DefaultSkyWidth = 2048, DefaultSkyHeight = 1024;
    readonly Dictionary<string, SkyTexture> m_SkyCache = new(StringComparer.OrdinalIgnoreCase);
    static byte[]? s_DefaultSky;
    static readonly object s_DefaultSkyLock = new();

    static bool IsSkyNone(string? iSky) =>
        iSky != null && string.Equals(iSky.Trim(), SCP_SculptRenderParams.SkyboxNone, StringComparison.OrdinalIgnoreCase);

    bool ResolveSky(string? iPath, out SkyTexture? oSky, out string oError)
    {
        oSky = null;
        oError = "";
        if (iPath == null)
        {
            if (m_SkyCache.TryGetValue(BuiltinSkyKey, out var aBuiltin)) { oSky = aBuiltin; return true; }
            byte[] aPx = GenerateDefaultSky(out int aW, out int aH);
            oSky = UploadSky(aPx, aW, aH);
            m_SkyCache[BuiltinSkyKey] = oSky;
            return true;
        }

        DateTime aMtime;
        long aLen;
        try
        {
            var aInfo = new FileInfo(iPath);
            if (!aInfo.Exists) { oError = "Skybox 檔案不存在：" + iPath; return false; }
            aMtime = aInfo.LastWriteTimeUtc;
            aLen = aInfo.Length;
        }
        catch (Exception e) { oError = "Skybox 讀不了（" + e.GetType().Name + "）：" + iPath; return false; }

        if (m_SkyCache.TryGetValue(iPath, out var aOld))
        {
            if (aOld.WriteTimeUtc == aMtime && aOld.Length == aLen) { oSky = aOld; return true; }
            m_Gl!.DeleteTexture(aOld.Tex);
            m_SkyCache.Remove(iPath);
        }

        StbImageSharp.ImageResult aImg;
        try
        {
            byte[] aBytes = File.ReadAllBytes(iPath);
            aImg = StbImageSharp.ImageResult.FromMemory(aBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception e) { oError = "Skybox 解碼失敗（" + e.GetType().Name + "：" + e.Message + "）：" + iPath; return false; }
        if (aImg == null || aImg.Data == null || aImg.Width <= 0 || aImg.Height <= 0)
        { oError = "Skybox 解碼失敗（空影像）：" + iPath; return false; }
        double aRatio = (double)aImg.Width / aImg.Height;
        if (Math.Abs(aRatio - 2.0) > 0.05)
        { oError = $"Skybox 不是 2:1 的等距柱狀全景（{aImg.Width}×{aImg.Height}）：" + iPath; return false; }
        int aMaxTex = m_Gl!.GetInteger(GetPName.MaxTextureSize);
        if (aImg.Width > aMaxTex)
        { oError = $"Skybox 寬 {aImg.Width} 超過這張顯卡的貼圖上限 {aMaxTex}：" + iPath; return false; }

        oSky = UploadSky(aImg.Data, aImg.Width, aImg.Height);
        oSky.WriteTimeUtc = aMtime;
        oSky.Length = aLen;
        m_SkyCache[iPath] = oSky;
        return true;
    }

    unsafe SkyTexture UploadSky(byte[] iRgba, int iW, int iH)
    {
        var gl = m_Gl!;
        var aSky = new SkyTexture { Width = iW, Height = iH };
        aSky.Tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, aSky.Tex);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        // ⚠ 由上到下的列直接上傳 ⇒ 第 0 列落在 t＝0；shader 的 v＝0 ＝ 天頂 ⇒ 不必翻。
        fixed (byte* p = iRgba)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)iW, (uint)iH, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        HemisphereTints(iRgba, iW, iH, out aSky.HemiSky, out aSky.HemiGround);
        return aSky;
    }

    /// <summary>上／下半球平均色（cos 緯度加權）→ 環境光色偏。天 ≈ 亮度 1、地 ≈ 亮度 0.62（與無 skybox 時同刻度）。</summary>
    static void HemisphereTints(byte[] iRgba, int iW, int iH, out Vector3 oSky, out Vector3 oGround)
    {
        double sr = 0, sg = 0, sb = 0, sw = 0, gr = 0, gg = 0, gb = 0, gw = 0;
        for (int y = 0; y < iH; y += 2)
        {
            double aLat = (0.5 - (y + 0.5) / iH) * Math.PI;
            double aWt = Math.Cos(aLat);
            double r = 0, g = 0, b = 0;
            int aRow = y * iW * 4;
            for (int x = 0; x < iW; x += 2)
            {
                int k = aRow + x * 4;
                r += iRgba[k]; g += iRgba[k + 1]; b += iRgba[k + 2];
            }
            if (aLat >= 0) { sr += r * aWt; sg += g * aWt; sb += b * aWt; sw += aWt; }
            else { gr += r * aWt; gg += g * aWt; gb += b * aWt; gw += aWt; }
        }
        oSky = Tint(sr, sg, sb, sw) * 1.0f;
        oGround = Tint(gr, gg, gb, gw) * 0.62f;
    }

    static Vector3 Tint(double iR, double iG, double iB, double iW)
    {
        if (iW <= 0) return Vector3.One;
        var c = new Vector3((float)iR, (float)iG, (float)iB) / (float)iW / 255f;
        float aLum = 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        if (aLum < 1e-4f) return Vector3.One;
        Vector3 t = Vector3.Lerp(Vector3.One, c / aLum, 0.6f);
        return Vector3.Clamp(t, Vector3.Zero, new Vector3(1.6f));
    }

    /// <summary>
    /// 正交鏡頭的背景：所有視線平行 ⇒ 查全景只會得到一個顏色。改用「假透視」視窗：
    /// 水平方向跟鏡頭（yaw 轉天空就轉），俯角只取 1/4（30° 俯視 ⇒ 背景看向 −7.5°，地平線落在畫面上半），垂直視角 70°。
    /// <para>iTiltDeg（SkyboxTiltDeg）：在那個俯角上再往上抬（正值 ＝ 看更高的天空、地面變少），結果夾在 ±89°。</para>
    /// </summary>
    static SkyView OrthoSkyView(Vector3 iForward, Vector3 iUp, double iRollDeg, float iAspect, double iTiltDeg)
    {
        Vector2 aH = new(iForward.X, iForward.Y);
        if (aH.LengthSquared() < 1e-6f) aH = new Vector2(iUp.X, iUp.Y) * (iForward.Z < 0 ? 1 : -1);
        aH = aH.LengthSquared() < 1e-12f ? Vector2.UnitX : Vector2.Normalize(aH);
        double aEl = Math.Asin(Math.Clamp(iForward.Z, -1f, 1f)) * 0.25 + iTiltDeg * Math.PI / 180;
        aEl = Math.Clamp(aEl, -89 * Math.PI / 180, 89 * Math.PI / 180);
        var aF = new Vector3(aH.X * (float)Math.Cos(aEl), aH.Y * (float)Math.Cos(aEl), (float)Math.Sin(aEl));
        Vector3 aR = Vector3.Normalize(Vector3.Cross(aF, Vector3.UnitZ));
        Vector3 aU = Vector3.Cross(aR, aF);
        if (iRollDeg != 0)
        {
            float aCr = (float)Math.Cos(iRollDeg * Math.PI / 180), aSr = (float)Math.Sin(iRollDeg * Math.PI / 180);
            Vector3 aR2 = aR * aCr + aU * aSr;
            Vector3 aU2 = aU * aCr - aR * aSr;
            aR = aR2; aU = aU2;
        }
        float aTanY = (float)Math.Tan(35 * Math.PI / 180);
        return new SkyView { Right = aR, Up = aU, Forward = aF, TanX = aTanY * iAspect, TanY = aTanY };
    }

    /// <summary>
    /// 內建預設天空（等距柱狀 2048×1024，RGBA8 由上到下）—— 安靜的黃昏：天頂深靛、地平線暖光、
    /// 地平線以上淡淡的星、地平線以下略暗的地面。純程式生成、**決定性**（同一支程式 ⇒ 同一份位元組）。
    /// <para>給 Cmd 落一份參考圖用（<c>SCP_CanvasPng.EncodeRgbaRows</c>）；回傳的陣列是共用快取，⚠ 呼叫端不要改它。</para>
    /// <para>暖光最亮的方位是經度 −135°（世界 −x−y）＝ 預設鏡頭（yaw 45）看過去的那一側，讓作品背後有光。</para>
    /// </summary>
    public static byte[] GenerateDefaultSky(out int oWidth, out int oHeight)
    {
        oWidth = DefaultSkyWidth;
        oHeight = DefaultSkyHeight;
        lock (s_DefaultSkyLock)
        {
            if (s_DefaultSky != null) return s_DefaultSky;
            const int W = DefaultSkyWidth, H = DefaultSkyHeight;
            var aPx = new byte[W * H * 4];
            var aWarm = new double[W];
            const double aGlowPhi = -0.75 * Math.PI;
            for (int x = 0; x < W; x++)
            {
                double aPhi = ((x + 0.5) / W - 0.5) * 2 * Math.PI;
                double c = Math.Cos(aPhi - aGlowPhi);
                aWarm[x] = 0.35 + 0.65 * Math.Pow(0.5 + 0.5 * c, 2.0);
            }
            // 色票（0..1）
            Vector3 aZenith = new(14 / 255f, 16 / 255f, 44 / 255f);
            Vector3 aMid = new(48 / 255f, 44 / 255f, 100 / 255f);
            Vector3 aHorizCool = new(92 / 255f, 70 / 255f, 118 / 255f);
            Vector3 aHorizWarm = new(226 / 255f, 138 / 255f, 96 / 255f);
            Vector3 aGroundNear = new(40 / 255f, 33 / 255f, 48 / 255f);
            Vector3 aGroundDeep = new(12 / 255f, 13 / 255f, 22 / 255f);
            for (int y = 0; y < H; y++)
            {
                double aEl = (0.5 - (y + 0.5) / H) * Math.PI;   // +π/2 ＝ 天頂
                double aDeg = aEl * 180 / Math.PI;
                for (int x = 0; x < W; x++)
                {
                    float aW = (float)aWarm[x];
                    Vector3 aHoriz = Vector3.Lerp(aHorizCool, aHorizWarm, aW);
                    Vector3 aSky;
                    {
                        double t = Math.Max(0, aEl) / (Math.PI / 2);
                        // 暖光貼著地平線（約 0..15°），往上進中段紫靛、再進天頂深靛。
                        double aLow = Math.Exp(-t / (0.10 + 0.08 * aW));
                        Vector3 aUpper = Vector3.Lerp(aMid, aZenith, (float)Math.Pow(t, 0.7));
                        aSky = Vector3.Lerp(aUpper, aHoriz, (float)aLow);
                        // 星：地平線 8° 以上漸現，越往天頂越多；位置與亮度由座標雜湊決定（決定性）。
                        if (aDeg > 8)
                        {
                            uint h = Hash((uint)x, (uint)y);
                            double aDensity = 0.0016 * SmoothStep(8, 45, aDeg);
                            if ((h & 0xFFFFFF) / 16777216.0 < aDensity)
                            {
                                float aB = (float)(0.25 + 0.55 * ((h >> 24) / 255.0)) * (float)(1 - 0.6 * aLow);
                                aSky += new Vector3(aB, aB, aB * 1.05f);
                            }
                        }
                    }
                    Vector3 aGround;
                    {
                        double g = Math.Max(0, -aEl) / (Math.PI / 2);
                        Vector3 aNear = Vector3.Lerp(aGroundNear, aHoriz * 0.45f, 0.35f * aW);
                        aGround = Vector3.Lerp(aNear, aGroundDeep, (float)Math.Pow(g, 0.4));
                    }
                    // 地平線柔化：±1.2° 內平滑過渡（不畫硬線）。
                    float aMix = (float)SmoothStep(-1.2, 1.2, aDeg);
                    Vector3 c = Vector3.Lerp(aGround, aSky, aMix);
                    int k = (y * W + x) * 4;
                    aPx[k] = ToByte(c.X); aPx[k + 1] = ToByte(c.Y); aPx[k + 2] = ToByte(c.Z); aPx[k + 3] = 255;
                }
            }
            s_DefaultSky = aPx;
            return aPx;
        }
    }

    static byte ToByte(float iV) => (byte)Math.Clamp((int)Math.Round(iV * 255f), 0, 255);

    static double SmoothStep(double iA, double iB, double iX)
    {
        double t = Math.Clamp((iX - iA) / (iB - iA), 0, 1);
        return t * t * (3 - 2 * t);
    }

    static uint Hash(uint iX, uint iY)
    {
        uint h = iX * 0x8DA6B343u ^ iY * 0xD8163841u;
        h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
        return h;
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // 區塊職責：地板 —— 範圍解析（FullGrid／外框＋Margin）與貼圖讀取快取。
    // 物理意義：範圍在**世界** xy（voxel 邊界＝整數座標）；外框模式的範圍 ＝ 可見 voxel 外緣（Min..Max，已是格的外緣）± Margin。
    //           ⛔ 貼圖路徑給了但讀不了 ⇒ TryRender 回 false（不默默換成內建網格）。
    // 數值影響：貼圖 RGBA8、Repeat 雙軸、三線性 mipmap、各向異性取驅動上限與 16 的小者（地板常是斜看，沒有它遠處會糊）。
    // ════════════════════════════════════════════════════════════════════════════════════
    sealed class FloorTexture
    {
        public uint Tex;
        public DateTime WriteTimeUtc;
        public long Length;
    }

    /// <summary>這一張圖要畫的地板（世界座標的矩形 ＋ 高度）。</summary>
    sealed class FloorSetup
    {
        public float X0, Y0, X1, Y1, Z;
        public SCP_SculptFloor Src = new();
        public FloorTexture? Tex;

        /// <summary>四個角（世界座標）。</summary>
        public Vector3[] Corners => new[]
        {
            new Vector3(X0, Y0, Z), new Vector3(X1, Y0, Z), new Vector3(X1, Y1, Z), new Vector3(X0, Y1, Z),
        };
    }

    readonly Dictionary<string, FloorTexture> m_FloorCache = new(StringComparer.OrdinalIgnoreCase);

    static FloorSetup? SolveFloor(Mesh iMesh, SCP_SculptRenderParams iP, FloorTexture? iTex)
    {
        var f = iP.Floor;
        if (f == null) return null;
        if (f.FullGrid) return new FloorSetup { X0 = 0, Y0 = 0, X1 = iP.SpaceSize, Y1 = iP.SpaceSize, Z = (float)f.Z, Src = f, Tex = iTex };
        if (iMesh.VoxelCount == 0) return null;   // 外框模式沒有外框 ⇒ 不畫（見檔頭）
        float m = (float)f.EffectiveMargin(Math.Max(iMesh.Max.X - iMesh.Min.X, iMesh.Max.Y - iMesh.Min.Y));
        return new FloorSetup
        {
            X0 = iMesh.Min.X - m, Y0 = iMesh.Min.Y - m, X1 = iMesh.Max.X + m, Y1 = iMesh.Max.Y + m,
            Z = (float)f.Z, Src = f, Tex = iTex,
        };
    }

    static float QueryMaxAnisotropy(GL iGl)
    {
        int aCount = iGl.GetInteger(GetPName.NumExtensions);
        bool aHas = false;
        for (int i = 0; i < aCount && !aHas; i++)
        {
            string? e = iGl.GetStringS(StringName.Extensions, (uint)i);
            aHas = e == "GL_EXT_texture_filter_anisotropic" || e == "GL_ARB_texture_filter_anisotropic";
        }
        if (!aHas) return 0;
        iGl.GetFloat((GetPName)0x84FF, out float aMax);   // GL_MAX_TEXTURE_MAX_ANISOTROPY
        return iGl.GetError() == GLEnum.NoError ? aMax : 0;
    }

    unsafe bool ResolveFloorTexture(string iPath, out FloorTexture? oTex, out string oError)
    {
        oTex = null;
        oError = "";
        DateTime aMtime;
        long aLen;
        try
        {
            var aInfo = new FileInfo(iPath);
            if (!aInfo.Exists) { oError = "地板貼圖不存在：" + iPath; return false; }
            aMtime = aInfo.LastWriteTimeUtc;
            aLen = aInfo.Length;
        }
        catch (Exception e) { oError = "地板貼圖讀不了（" + e.GetType().Name + "）：" + iPath; return false; }

        if (m_FloorCache.TryGetValue(iPath, out var aOld))
        {
            if (aOld.WriteTimeUtc == aMtime && aOld.Length == aLen) { oTex = aOld; return true; }
            m_Gl!.DeleteTexture(aOld.Tex);
            m_FloorCache.Remove(iPath);
        }

        StbImageSharp.ImageResult aImg;
        try
        {
            byte[] aBytes = File.ReadAllBytes(iPath);
            aImg = StbImageSharp.ImageResult.FromMemory(aBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        }
        catch (Exception e) { oError = "地板貼圖解碼失敗（" + e.GetType().Name + "：" + e.Message + "）：" + iPath; return false; }
        if (aImg == null || aImg.Data == null || aImg.Width <= 0 || aImg.Height <= 0)
        { oError = "地板貼圖解碼失敗（空影像）：" + iPath; return false; }
        var gl = m_Gl!;
        int aMaxTex = gl.GetInteger(GetPName.MaxTextureSize);
        if (aImg.Width > aMaxTex || aImg.Height > aMaxTex)
        { oError = $"地板貼圖 {aImg.Width}×{aImg.Height} 超過這張顯卡的貼圖上限 {aMaxTex}：" + iPath; return false; }

        var aTex = new FloorTexture { WriteTimeUtc = aMtime, Length = aLen, Tex = gl.GenTexture() };
        gl.BindTexture(TextureTarget.Texture2D, aTex.Tex);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = aImg.Data)
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)aImg.Width, (uint)aImg.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        if (m_MaxAniso > 1f)
            gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, Math.Min(16f, m_MaxAniso));   // GL_TEXTURE_MAX_ANISOTROPY
        m_FloorCache[iPath] = aTex;
        oTex = aTex;
        return true;
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // 區塊職責：CPU 端網格 —— 只出外露面（鄰格有 voxel 就不出），逐頂點 AO（每角看 3 個鄰格）。
    // 物理意義：頂點已轉進 GL 座標（y 取負，見檔頭鏡像說明）；三角形依 AO 選對角線，避免 AO 漸層的方向性條紋。
    // 數值影響：頂點 28 bytes（位置 3f、法線 3f、RGB＋AO 4ub）；6079 顆的全場景約數萬頂點，上傳成本可忽略。
    // ════════════════════════════════════════════════════════════════════════════════════
    sealed class Mesh
    {
        public float[] Vertices = Array.Empty<float>();   // 每頂點 7 個 float 槽（最後一槽是 4ub 的位元重解）
        public uint[] Indices = Array.Empty<uint>();
        public int VertexCount;
        public int IndexCount;
        public int VoxelCount;
        /// <summary>voxel 中心（世界座標，已去重、排序）—— 鏡頭框景用。</summary>
        public Vector3[] Centers = Array.Empty<Vector3>();
        public Vector3 Min, Max;   // 世界座標 AABB（voxel 格的外緣）
    }

    // 六個面：法線 n、切向 u、v（u×v＝n，世界右手系；轉到 GL 鏡像後繞序反轉，建三角形時處理）。
    static readonly int[,] s_Faces =
    {
        //  nx ny nz   ux uy uz   vx vy vz
        {  1, 0, 0,   0, 1, 0,   0, 0, 1 },
        { -1, 0, 0,   0, 0, 1,   0, 1, 0 },
        {  0, 1, 0,   0, 0, 1,   1, 0, 0 },
        {  0,-1, 0,   1, 0, 0,   0, 0, 1 },
        {  0, 0, 1,   1, 0, 0,   0, 1, 0 },
        {  0, 0,-1,   0, 1, 0,   1, 0, 0 },
    };

    static Mesh BuildMesh(IReadOnlyList<SCP_SculptVoxel> iVoxels, bool iAo)
    {
        var aMesh = new Mesh();
        if (iVoxels.Count == 0) return aMesh;

        int aMinX = int.MaxValue, aMinY = int.MaxValue, aMinZ = int.MaxValue;
        int aMaxX = int.MinValue, aMaxY = int.MinValue, aMaxZ = int.MinValue;
        for (int i = 0; i < iVoxels.Count; i++)
        {
            var v = iVoxels[i];
            if (v.Color == 0) continue;   // 0 ＝ 空（契約說不會出現；出現了就當沒有）
            if (v.X < aMinX) aMinX = v.X; if (v.X > aMaxX) aMaxX = v.X;
            if (v.Y < aMinY) aMinY = v.Y; if (v.Y > aMaxY) aMaxY = v.Y;
            if (v.Z < aMinZ) aMinZ = v.Z; if (v.Z > aMaxZ) aMaxZ = v.Z;
        }
        if (aMinX == int.MaxValue) return aMesh;

        // 外圍各墊一格 ⇒ 鄰格查詢（含 AO 的斜角）不必判邊界。
        int aOx = aMinX - 1, aOy = aMinY - 1, aOz = aMinZ - 1;
        int aSx = aMaxX - aMinX + 3, aSy = aMaxY - aMinY + 3, aSz = aMaxZ - aMinZ + 3;
        long aCells = (long)aSx * aSy * aSz;
        if (aCells > 64L * 1024 * 1024) throw new InvalidOperationException($"場景外框太大（{aSx}×{aSy}×{aSz}）");
        var aGrid = new byte[aCells];
        int aStrideY = aSx, aStrideZ = aSx * aSy;
        int Idx(int x, int y, int z) => (x - aOx) + (y - aOy) * aStrideY + (z - aOz) * aStrideZ;

        var aCoords = new List<long>(iVoxels.Count);
        for (int i = 0; i < iVoxels.Count; i++)
        {
            var v = iVoxels[i];
            if (v.Color == 0) continue;
            int k = Idx(v.X, v.Y, v.Z);
            if (aGrid[k] == 0) aCoords.Add(k);
            aGrid[k] = v.Color;   // 重複座標：後者為準
        }
        aCoords.Sort();   // 網格索引的排序 ＝ (z, y, x) 字典序 ⇒ 與輸入順序無關

        aMesh.VoxelCount = aCoords.Count;
        aMesh.Centers = new Vector3[aCoords.Count];
        aMesh.Min = new Vector3(aMinX, aMinY, aMinZ);
        aMesh.Max = new Vector3(aMaxX + 1, aMaxY + 1, aMaxZ + 1);

        var aVerts = new List<float>(aCoords.Count * 6 * 4 * 7 / 3);
        var aIdx = new List<uint>(aCoords.Count * 6 * 6 / 3);
        Span<int> aAo = stackalloc int[4];
        for (int c = 0; c < aCoords.Count; c++)
        {
            int k = (int)aCoords[c];
            int x = k % aStrideY + aOx;
            int y = k / aStrideY % aSy + aOy;
            int z = k / aStrideZ + aOz;
            aMesh.Centers[c] = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            byte aColor = aGrid[k];
            SCP_CanvasPalette.IndexToRgb(aColor, out byte aR, out byte aG, out byte aB);

            for (int f = 0; f < 6; f++)
            {
                int nx = s_Faces[f, 0], ny = s_Faces[f, 1], nz = s_Faces[f, 2];
                if (aGrid[Idx(x + nx, y + ny, z + nz)] != 0) continue;   // 鄰格有 voxel ⇒ 這面看不到
                int ux = s_Faces[f, 3], uy = s_Faces[f, 4], uz = s_Faces[f, 5];
                int vx = s_Faces[f, 6], vy = s_Faces[f, 7], vz = s_Faces[f, 8];
                // 面所在平面的原點：法線朝正向的面在 +1 那一側。
                int px = x + Math.Max(nx, 0), py = y + Math.Max(ny, 0), pz = z + Math.Max(nz, 0);

                uint aBase = (uint)(aVerts.Count / 7);
                for (int corner = 0; corner < 4; corner++)
                {
                    // 角的順序 (0,0) (1,0) (1,1) (0,1) —— 從面外看是逆時針（世界右手系）。
                    int a = (corner == 1 || corner == 2) ? 1 : 0;
                    int b = (corner >= 2) ? 1 : 0;
                    int aLevel = 3;
                    if (iAo)
                    {
                        int su = a == 1 ? 1 : -1, sv = b == 1 ? 1 : -1;
                        int bx = x + nx, by = y + ny, bz = z + nz;
                        bool s1 = aGrid[Idx(bx + ux * su, by + uy * su, bz + uz * su)] != 0;
                        bool s2 = aGrid[Idx(bx + vx * sv, by + vy * sv, bz + vz * sv)] != 0;
                        bool cc = aGrid[Idx(bx + ux * su + vx * sv, by + uy * su + vy * sv, bz + uz * su + vz * sv)] != 0;
                        aLevel = (s1 && s2) ? 0 : 3 - ((s1 ? 1 : 0) + (s2 ? 1 : 0) + (cc ? 1 : 0));
                    }
                    aAo[corner] = aLevel;
                    float wx = px + ux * a + vx * b;
                    float wy = py + uy * a + vy * b;
                    float wz = pz + uz * a + vz * b;
                    aVerts.Add(wx); aVerts.Add(-wy); aVerts.Add(wz);      // 世界 → GL（y 鏡像）
                    aVerts.Add(nx); aVerts.Add(-ny); aVerts.Add(nz);
                    uint aPacked = aR | ((uint)aG << 8) | ((uint)aB << 16) | ((uint)(aLevel * 85) << 24);
                    aVerts.Add(BitConverter.UInt32BitsToSingle(aPacked));
                }
                // y 鏡像讓繞序反轉 ⇒ 下面的三角形都是「反過來」寫，GL 裡從面外看才是逆時針。
                // 對角線：沿 AO 總和較大（較亮）的那一對切，讓暗角留在自己那個三角形裡（不拉出斜條紋）。
                if (aAo[0] + aAo[2] >= aAo[1] + aAo[3])
                {
                    aIdx.Add(aBase + 0); aIdx.Add(aBase + 2); aIdx.Add(aBase + 1);
                    aIdx.Add(aBase + 0); aIdx.Add(aBase + 3); aIdx.Add(aBase + 2);
                }
                else
                {
                    aIdx.Add(aBase + 0); aIdx.Add(aBase + 3); aIdx.Add(aBase + 1);
                    aIdx.Add(aBase + 1); aIdx.Add(aBase + 3); aIdx.Add(aBase + 2);
                }
            }
        }
        aMesh.Vertices = aVerts.ToArray();
        aMesh.Indices = aIdx.ToArray();
        aMesh.VertexCount = aMesh.Vertices.Length / 7;
        aMesh.IndexCount = aMesh.Indices.Length;
        return aMesh;
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // 區塊職責：鏡頭 —— 正交／透視、yaw／pitch／roll、注視點、指定鏡頭位置、自動框景。
    // 物理意義：全部在 GL 座標（世界 y 取負）裡算；使用者給的角度與座標都是世界座標。
    // ════════════════════════════════════════════════════════════════════════════════════
    static Vector3 ToGl(double iX, double iY, double iZ) => new((float)iX, (float)-iY, (float)iZ);

    /// <summary>
    /// 框景用的點（世界座標的「格中心」，框景時每點各帶 ±0.5 的半寬）：voxel 中心；有地板且地板與 voxel 底部距離 ≤ Margin ⇒
    /// 再加每顆 voxel 在地板上的腳印（接觸面與近處陰影要在畫面裡）；沒有 voxel 但有 FullGrid ⇒ 整片地板的四角。
    /// <para>⚠ FullGrid／Margin 外圈**不**參與框景（見檔頭）。</para>
    /// </summary>
    static Vector3[] FramePoints(Mesh iMesh, FloorSetup? iFloor, out Vector3 oMin, out Vector3 oMax)
    {
        if (iMesh.VoxelCount == 0)
        {
            if (iFloor != null)
            {
                oMin = new Vector3(iFloor.X0, iFloor.Y0, iFloor.Z - 1);
                oMax = new Vector3(iFloor.X1, iFloor.Y1, iFloor.Z);
                return new[]
                {
                    new Vector3(iFloor.X0 + 0.5f, iFloor.Y0 + 0.5f, iFloor.Z - 0.5f), new Vector3(iFloor.X1 - 0.5f, iFloor.Y0 + 0.5f, iFloor.Z - 0.5f),
                    new Vector3(iFloor.X1 - 0.5f, iFloor.Y1 - 0.5f, iFloor.Z - 0.5f), new Vector3(iFloor.X0 + 0.5f, iFloor.Y1 - 0.5f, iFloor.Z - 0.5f),
                };
            }
            oMin = Vector3.Zero; oMax = Vector3.One;
            return iMesh.Centers;
        }
        oMin = iMesh.Min; oMax = iMesh.Max;
        if (iFloor == null || Math.Abs(iMesh.Min.Z - iFloor.Z)
            > iFloor.Src.EffectiveMargin(Math.Max(iMesh.Max.X - iMesh.Min.X, iMesh.Max.Y - iMesh.Min.Y))) return iMesh.Centers;
        // 每顆 voxel 正下方在地板上的那一格（格中心 z 在地板下半格 ⇒ 加回 ±0.5 剛好是地板面）—— 只框「作品實際踩著的地方」，
        // ⛔ 不用外框四角：細長斜放的作品外框比剪影寬很多，框四角會讓作品縮小（2026-10-02 燈塔實測）。
        int n = iMesh.Centers.Length;
        var aPts = new Vector3[n * 2];
        Array.Copy(iMesh.Centers, aPts, n);
        float aZ = iFloor.Z - 0.5f;
        for (int i = 0; i < n; i++) aPts[n + i] = new Vector3(iMesh.Centers[i].X, iMesh.Centers[i].Y, aZ);
        oMin = Vector3.Min(oMin, new Vector3(iMesh.Min.X, iMesh.Min.Y, iFloor.Z));
        oMax = Vector3.Max(oMax, new Vector3(iMesh.Max.X, iMesh.Max.Y, iFloor.Z));
        return aPts;
    }

    static bool SolveCamera(Mesh iMesh, FloorSetup? iFloor, SCP_SculptRenderParams iP, out Matrix4x4 oViewProj, out SkyView oSky, out string oError)
    {
        oViewProj = Matrix4x4.Identity;
        oSky = default;
        oError = "";
        Vector3[] aPts = FramePoints(iMesh, iFloor, out Vector3 aMinW, out Vector3 aMaxW);
        // 深度範圍（近／遠平面）額外涵蓋的點：地板四角（框景不看它們）
        Vector3[] aDepthOnly = iFloor?.Corners ?? Array.Empty<Vector3>();
        Vector3 aCenterW = (aMinW + aMaxW) * 0.5f;
        bool aTargetGiven = iP.TargetX.HasValue || iP.TargetY.HasValue || iP.TargetZ.HasValue;
        // 注視點：逐軸 —— 沒給的軸用可見 voxel 外框中心。
        Vector3 aTarget = ToGl(iP.TargetX ?? aCenterW.X, iP.TargetY ?? aCenterW.Y, iP.TargetZ ?? aCenterW.Z);

        Vector3 aForward;   // 鏡頭 → 注視點
        Vector3 aHoriz;     // 鏡頭的水平「後方」（俯視到 90° 時拿來當 up 的備援）
        Vector3? aEye = null;
        if (iP.EyeX.HasValue)
        {
            Vector3 aE = ToGl(iP.EyeX!.Value, iP.EyeY!.Value, iP.EyeZ!.Value);
            Vector3 aD = aTarget - aE;
            if (aD.Length() < 1e-4f) { oError = "鏡頭位置與注視點重合 —— 看不出方向"; return false; }
            aForward = Vector3.Normalize(aD);
            aHoriz = new Vector3(-aForward.X, -aForward.Y, 0);
            aEye = aE;
        }
        else
        {
            double aYaw = iP.YawDeg * Math.PI / 180, aPitch = iP.PitchDeg * Math.PI / 180;
            // 世界裡「注視點 → 鏡頭」的方向是 (cosP·cosY, cosP·sinY, sinP)；轉 GL 時 y 取負。
            var aToEye = new Vector3((float)(Math.Cos(aPitch) * Math.Cos(aYaw)), (float)(-Math.Cos(aPitch) * Math.Sin(aYaw)), (float)Math.Sin(aPitch));
            aForward = Vector3.Normalize(-aToEye);
            aHoriz = new Vector3((float)Math.Cos(aYaw), (float)-Math.Sin(aYaw), 0);
        }

        Vector3 aUpRef = Vector3.UnitZ;
        if (Math.Abs(Vector3.Dot(aForward, aUpRef)) > 0.9999f)
            aUpRef = aHoriz.LengthSquared() > 1e-8f ? Vector3.Normalize(-aHoriz * Math.Sign(aForward.Z)) : Vector3.UnitY;
        Vector3 aRight = Vector3.Normalize(Vector3.Cross(aForward, aUpRef));
        Vector3 aUp = Vector3.Cross(aRight, aForward);
        if (iP.RollDeg != 0)
        {
            // 正值 ＝ 鏡頭繞視線逆時針滾（畫面內容看起來順時針轉）。
            float aCr = (float)Math.Cos(iP.RollDeg * Math.PI / 180), aSr = (float)Math.Sin(iP.RollDeg * Math.PI / 180);
            Vector3 aR2 = aRight * aCr + aUp * aSr;
            Vector3 aU2 = aUp * aCr - aRight * aSr;
            aRight = aR2; aUp = aU2;
        }

        // 每顆 voxel 投影在 right／up／forward 軸上的半寬（單位立方體的支撐函數）。
        float HalfExtent(Vector3 iAxis) => 0.5f * (Math.Abs(iAxis.X) + Math.Abs(iAxis.Y) + Math.Abs(iAxis.Z));
        float aHr = HalfExtent(aRight), aHu = HalfExtent(aUp), aHf = HalfExtent(aForward);
        int aN = aPts.Length;
        float aAspect = (float)iP.Width / iP.Height;

        if (iP.Projection == SCP_SculptProjection.Orthographic)
        {
            // 投影外框（相對注視點）。
            float aMinR = float.MaxValue, aMaxR = float.MinValue, aMinU = float.MaxValue, aMaxU = float.MinValue;
            float aMinF = float.MaxValue, aMaxF = float.MinValue;
            for (int i = 0; i < aN; i++)
            {
                Vector3 d = ToGl(aPts[i].X, aPts[i].Y, aPts[i].Z) - aTarget;
                float r = Vector3.Dot(d, aRight), u = Vector3.Dot(d, aUp), f = Vector3.Dot(d, aForward);
                aMinR = Math.Min(aMinR, r - aHr); aMaxR = Math.Max(aMaxR, r + aHr);
                aMinU = Math.Min(aMinU, u - aHu); aMaxU = Math.Max(aMaxU, u + aHu);
                aMinF = Math.Min(aMinF, f - aHf); aMaxF = Math.Max(aMaxF, f + aHf);
            }
            if (aN == 0) { aMinR = aMinU = aMinF = -1; aMaxR = aMaxU = aMaxF = 1; }
            foreach (Vector3 w in aDepthOnly)
            {
                float f = Vector3.Dot(ToGl(w.X, w.Y, w.Z) - aTarget, aForward);
                aMinF = Math.Min(aMinF, f); aMaxF = Math.Max(aMaxF, f);
            }

            double aBasePx = VoxelWidthPx / Math.Sqrt(2.0);   // Zoom 1：每世界單位的像素數
            // 畫面中心：有注視點 ⇒ 注視點在正中；沒有 ⇒ 投影外框中心（與舊引擎相同）。
            double aCr2 = aTargetGiven ? 0 : (aMinR + aMaxR) * 0.5;
            double aCu2 = aTargetGiven ? 0 : (aMinU + aMaxU) * 0.5;
            double aScale;
            if (iP.Zoom.HasValue) aScale = iP.Zoom.Value;
            else
            {
                double aExtR = aTargetGiven ? 2 * Math.Max(Math.Abs(aMinR), Math.Abs(aMaxR)) : aMaxR - aMinR;
                double aExtU = aTargetGiven ? 2 * Math.Max(Math.Abs(aMinU), Math.Abs(aMaxU)) : aMaxU - aMinU;
                aExtR = Math.Max(1e-3, aExtR * aBasePx);
                aExtU = Math.Max(1e-3, aExtU * aBasePx);
                aScale = Math.Min(iP.Width * FitFill / aExtR, iP.Height * FitFill / aExtU);
                if (!iP.FitUpscale) aScale = Math.Min(1.0, aScale);   // 舊行為：只縮不放（見 FitUpscale）
            }
            double aPx = aBasePx * aScale;
            float aHalfW = (float)(iP.Width / 2.0 / aPx), aHalfH = (float)(iP.Height / 2.0 / aPx);

            // 鏡頭放在注視點後方、所有 voxel 之前；指定 Eye 時只取它的方向（正交沒有「距離」）。
            Vector3 aCenter = aTarget + aRight * (float)aCr2 + aUp * (float)aCu2;
            float aBack = Math.Max(0, -aMinF) + 2f;
            Vector3 aEyePos = aCenter - aForward * aBack;
            float aNear = 0.5f, aFar = aBack + Math.Max(0, aMaxF) + 2f;
            var aView = LookAt(aEyePos, aRight, aUp, aForward);
            oViewProj = aView * Ortho(-aHalfW, aHalfW, -aHalfH, aHalfH, aNear, aFar);
            oSky = OrthoSkyView(aForward, aUp, iP.RollDeg, aAspect, iP.SkyboxTiltDeg);
            return true;
        }

        // ── 透視 ──
        float aTanY = (float)Math.Tan(iP.FovDeg * Math.PI / 360);
        float aTanX = aTanY * aAspect;
        Vector3 aEyeP;
        if (aEye.HasValue) aEyeP = aEye.Value;
        else
        {
            float aDist;
            if (iP.Distance.HasValue) aDist = (float)iP.Distance.Value;
            else
            {
                aDist = FitDistance(aPts, aTarget, aRight, aUp, aForward, aTanX, aTanY);
                // 沒指定注視點 ⇒ 像正交那樣把**投影外框**擺正中（外框中心 ≠ AABB 中心：場景常是斜的一長條）。
                // 做法：把注視點沿畫面平面平移、重算距離；幾輪就收斂（透視下平移會微改投影，所以要迭代）。
                if (!aTargetGiven)
                {
                    for (int aIter = 0; aIter < 6; aIter++)
                    {
                        float aMinX = float.MaxValue, aMaxX = float.MinValue, aMinY = float.MaxValue, aMaxY = float.MinValue;
                        for (int i = 0; i < aN; i++)
                        {
                            Vector3 d = ToGl(aPts[i].X, aPts[i].Y, aPts[i].Z) - aTarget;
                            float z = aDist + Vector3.Dot(d, aForward);
                            if (z <= 0.1f) continue;
                            float x = Vector3.Dot(d, aRight) / z, y = Vector3.Dot(d, aUp) / z;
                            aMinX = Math.Min(aMinX, x); aMaxX = Math.Max(aMaxX, x);
                            aMinY = Math.Min(aMinY, y); aMaxY = Math.Max(aMaxY, y);
                        }
                        if (aMinX > aMaxX) break;
                        float aCx = (aMinX + aMaxX) * 0.5f, aCy = (aMinY + aMaxY) * 0.5f;
                        if (Math.Abs(aCx) < 1e-4f && Math.Abs(aCy) < 1e-4f) break;
                        aTarget += (aRight * aCx + aUp * aCy) * aDist;
                        aDist = FitDistance(aPts, aTarget, aRight, aUp, aForward, aTanX, aTanY);
                    }
                }
            }
            aEyeP = aTarget - aForward * aDist;
        }
        float aMinDepth = float.MaxValue, aMaxDepth = float.MinValue;
        for (int i = 0; i < aN; i++)
        {
            float f = Vector3.Dot(ToGl(aPts[i].X, aPts[i].Y, aPts[i].Z) - aEyeP, aForward);
            aMinDepth = Math.Min(aMinDepth, f - 0.9f); aMaxDepth = Math.Max(aMaxDepth, f + 0.9f);
        }
        if (aN == 0) { aMinDepth = 1; aMaxDepth = 10; }
        float aZNear = Math.Max(0.05f, aMinDepth - 1f);
        if (aDepthOnly.Length > 0)
        {
            foreach (Vector3 w in aDepthOnly)
                aMaxDepth = Math.Max(aMaxDepth, Vector3.Dot(ToGl(w.X, w.Y, w.Z) - aEyeP, aForward) + 1f);
            // 地板離鏡頭最近的可見點：鏡頭到地板平面的距離 h × 視錐半對角的 cos（視錐裡的點離鏡頭至少 h，沿視線的深度至少再乘 cos）
            float aH = Math.Abs(aEyeP.Z - aDepthOnly[0].Z);
            float aCos = 1f / MathF.Sqrt(1f + aTanX * aTanX + aTanY * aTanY);
            if (aH > 1e-3f) aZNear = Math.Max(0.05f, Math.Min(aZNear, aH * aCos * 0.9f));
            else aZNear = 0.05f;
        }
        float aZFar = Math.Max(aZNear + 1f, aMaxDepth + 1f);
        // 深度精度：近平面太貼（鏡頭在場景裡面）時夾住遠近比，免得 24-bit 深度失真。
        aZNear = Math.Max(aZNear, aZFar / 100000f);
        var aViewP = LookAt(aEyeP, aRight, aUp, aForward);
        oViewProj = aViewP * Perspective(aTanY, aAspect, aZNear, aZFar);
        oSky = new SkyView { Right = aRight, Up = aUp, Forward = aForward, TanX = aTanX, TanY = aTanY };
        return true;
    }

    /// <summary>
    /// 透視自動距離：每顆 voxel（以外接球 √3/2 保守估）都落在 92% 視錐裡所需的最小鏡頭距離（相對注視點）。
    /// </summary>
    static float FitDistance(Vector3[] iPts, Vector3 iTarget, Vector3 iRight, Vector3 iUp, Vector3 iForward, float iTanX, float iTanY)
    {
        const float aRad = 0.8660254f;
        float aFillY = iTanY * (float)FitFill, aFillX = iTanX * (float)FitFill;
        float aDist = 1f;
        for (int i = 0; i < iPts.Length; i++)
        {
            Vector3 d = ToGl(iPts[i].X, iPts[i].Y, iPts[i].Z) - iTarget;
            float r = Math.Abs(Vector3.Dot(d, iRight)) + aRad, u = Math.Abs(Vector3.Dot(d, iUp)) + aRad;
            float f = Vector3.Dot(d, iForward);   // >0 ＝ 在注視點後方（離鏡頭更遠）
            aDist = Math.Max(aDist, r / aFillX - f);
            aDist = Math.Max(aDist, u / aFillY - f);
            aDist = Math.Max(aDist, -f + aRad + 0.5f);
        }
        return aDist;
    }

    /// <summary>System.Numerics 是列向量慣例（p·M）；原樣上傳給 GL（不轉置）剛好等於 GL 的 M·p。</summary>
    static Matrix4x4 LookAt(Vector3 iEye, Vector3 iRight, Vector3 iUp, Vector3 iForward)
    {
        return new Matrix4x4(
            iRight.X, iUp.X, -iForward.X, 0,
            iRight.Y, iUp.Y, -iForward.Y, 0,
            iRight.Z, iUp.Z, -iForward.Z, 0,
            -Vector3.Dot(iRight, iEye), -Vector3.Dot(iUp, iEye), Vector3.Dot(iForward, iEye), 1);
    }

    /// <summary>GL 慣例的正交投影（深度映到 −1..1）。</summary>
    static Matrix4x4 Ortho(float l, float r, float b, float t, float n, float f)
    {
        return new Matrix4x4(
            2 / (r - l), 0, 0, 0,
            0, 2 / (t - b), 0, 0,
            0, 0, -2 / (f - n), 0,
            -(r + l) / (r - l), -(t + b) / (t - b), -(f + n) / (f - n), 1);
    }

    static Matrix4x4 Perspective(float iTanHalfY, float iAspect, float n, float f)
    {
        float aF = 1 / iTanHalfY;
        return new Matrix4x4(
            aF / iAspect, 0, 0, 0,
            0, aF, 0, 0,
            0, 0, (f + n) / (n - f), -1,
            0, 0, 2 * f * n / (n - f), 0);
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // 區塊職責：光源 —— 每盞光轉進 GL 座標；投影光各配一張陰影圖（正交視錐框住**全部** voxel 的 AABB ⇒ 正交與透視都被涵蓋）。
    // 數值影響：全場景（約 250 單位）時每 voxel ≈ 7 texel；裁切的小展品則精細得多。
    // 地板：陰影圖的 xy（光的橫向）**只框投影者（voxel）** —— 平行光下一顆 voxel 與它落在地板上的影子在光空間是同一個 xy，
    //       所以框住 voxel 就框住了所有落在地板上的影子；框外的地板取樣落在邊框色（深度 1）⇒ 照亮，本來就沒有東西擋。
    //       ⚠ 但**深度**範圍必須延伸到地板（否則地板像素的光空間深度 ≥ 1 ⇒ 一律當成照亮，影子整片消失 —— 而且不會報錯）。
    //       這樣陰影圖解析度不會因為 FullGrid 的 256 格地板被稀釋。
    // ════════════════════════════════════════════════════════════════════════════════════
    sealed class LightSetup
    {
        public int Count;
        public readonly Vector3[] ToLight = new Vector3[MaxLightsGpu];   // GL 座標、指向光源
        public readonly Vector3[] Color = new Vector3[MaxLightsGpu];     // 光色×強度（0..1 色 × 強度）
        public readonly int[] ShadowMap = new int[MaxLightsGpu];         // −1 ＝ 不投影；0／1 ＝ 第幾張陰影圖
        public readonly Matrix4x4[] ShadowVp = { Matrix4x4.Identity, Matrix4x4.Identity };
        public int ShadowMaps;
    }

    static LightSetup SolveLights(Mesh iMesh, FloorSetup? iFloor, SCP_SculptRenderParams iP)
    {
        var aSet = new LightSetup();
        bool aShadows = iP.Shadow && iMesh.IndexCount > 0;
        foreach (var aL in iP.Lights)
        {
            int i = aSet.Count++;
            Vector3 aTravel = Vector3.Normalize(ToGl(aL.DirX, aL.DirY, aL.DirZ));
            aSet.ToLight[i] = -aTravel;
            float aK = (float)aL.Intensity / 255f;
            aSet.Color[i] = new Vector3(aL.R * aK, aL.G * aK, aL.B * aK);
            aSet.ShadowMap[i] = -1;
            if (aShadows && aL.CastShadow && aSet.ShadowMaps < MaxShadowMaps)   // 上限已在 Validate 擋過
            {
                aSet.ShadowMap[i] = aSet.ShadowMaps;
                aSet.ShadowVp[aSet.ShadowMaps++] = LightViewProj(iMesh, iFloor, aTravel);
            }
        }
        return aSet;
    }

    static Matrix4x4 LightViewProj(Mesh iMesh, FloorSetup? iFloor, Vector3 iTravel)
    {
        Vector3 aUpRef = Math.Abs(iTravel.Z) > 0.999f ? Vector3.UnitY : Vector3.UnitZ;
        Vector3 aRight = Vector3.Normalize(Vector3.Cross(iTravel, aUpRef));
        Vector3 aUp = Vector3.Cross(aRight, iTravel);
        Vector3 aMin = iMesh.VoxelCount > 0 ? iMesh.Min : Vector3.Zero, aMax = iMesh.VoxelCount > 0 ? iMesh.Max : Vector3.One;
        float aMinR = float.MaxValue, aMaxR = float.MinValue, aMinU = float.MaxValue, aMaxU = float.MinValue;
        float aMinF = float.MaxValue, aMaxF = float.MinValue;
        for (int i = 0; i < 8; i++)
        {
            Vector3 c = ToGl((i & 1) != 0 ? aMax.X : aMin.X, (i & 2) != 0 ? aMax.Y : aMin.Y, (i & 4) != 0 ? aMax.Z : aMin.Z);
            float r = Vector3.Dot(c, aRight), u = Vector3.Dot(c, aUp), f = Vector3.Dot(c, iTravel);
            aMinR = Math.Min(aMinR, r); aMaxR = Math.Max(aMaxR, r);
            aMinU = Math.Min(aMinU, u); aMaxU = Math.Max(aMaxU, u);
            aMinF = Math.Min(aMinF, f); aMaxF = Math.Max(aMaxF, f);
        }
        if (iFloor != null)
            foreach (Vector3 w in iFloor.Corners)
            {
                float f = Vector3.Dot(ToGl(w.X, w.Y, w.Z), iTravel);
                aMinF = Math.Min(aMinF, f); aMaxF = Math.Max(aMaxF, f);
            }
        const float aPad = 1f;
        var aView = LookAt(Vector3.Zero, aRight, aUp, iTravel);
        return aView * Ortho(aMinR - aPad, aMaxR + aPad, aMinU - aPad, aMaxU + aPad, aMinF - aPad, aMaxF + aPad);
    }

    // ════════════════════════════════════════════════════════════════════════════════════
    // 區塊職責：GPU 三趟 —— 陰影圖 → MSAA 主畫（背景＋網格）→ blit 解析 → ReadPixels（翻成由上到下）。
    // ════════════════════════════════════════════════════════════════════════════════════
    unsafe byte[] RenderGpu(Mesh iMesh, FloorSetup? iFloor, SCP_SculptRenderParams iP, Matrix4x4 iViewProj, LightSetup iLights,
                            SkyTexture? iSky, SkyView iSkyView)
    {
        var gl = m_Gl!;
        int aW = iP.Width, aH = iP.Height;
        EnsureTargets(aW, aH);

        uint aVao = 0, aVbo = 0, aIbo = 0, aFloorVao = 0, aFloorVbo = 0;
        bool aHasMesh = iMesh.IndexCount > 0;
        // 半球環境光：沒有 skybox ⇒ 天 1.0／地 0.62（灰階，與無 skybox 的舊版逐位元相同）；
        // 有 ⇒ 由全景上下半球的平均色調出色偏（亮度仍由 Ambient 決定）。
        Vector3 aHs = iSky?.HemiSky ?? Vector3.One, aHg = iSky?.HemiGround ?? new Vector3(0.62f);
        try
        {
            if (aHasMesh)
            {
                aVao = gl.GenVertexArray();
                gl.BindVertexArray(aVao);
                aVbo = gl.GenBuffer();
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, aVbo);
                gl.BufferData<float>(BufferTargetARB.ArrayBuffer, iMesh.Vertices.AsSpan(), BufferUsageARB.StaticDraw);
                aIbo = gl.GenBuffer();
                gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, aIbo);
                gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, iMesh.Indices.AsSpan(), BufferUsageARB.StaticDraw);
                const uint aStride = 7 * sizeof(float);
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, aStride, (void*)0);
                gl.EnableVertexAttribArray(1);
                gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, aStride, (void*)(3 * sizeof(float)));
                gl.EnableVertexAttribArray(2);
                gl.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, aStride, (void*)(6 * sizeof(float)));
            }

            // ① 陰影圖：只畫背面（正面剔除）⇒ 受光面與存下的深度差一整顆 voxel，幾乎不會自遮蔽（shadow acne）。
            for (int m = 0; m < iLights.ShadowMaps; m++)
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, m_ShadowFbo[m]);
                gl.Viewport(0, 0, ShadowMapSize, ShadowMapSize);
                gl.Enable(EnableCap.DepthTest);
                gl.DepthFunc(DepthFunction.Less);
                gl.DepthMask(true);
                gl.Clear(ClearBufferMask.DepthBufferBit);
                gl.Enable(EnableCap.CullFace);
                gl.CullFace(TriangleFace.Front);
                gl.UseProgram(m_ShadowProg);
                SetMat(m_ShadowProg, "uLightVP", iLights.ShadowVp[m]);
                gl.BindVertexArray(aVao);
                gl.DrawElements(PrimitiveType.Triangles, (uint)iMesh.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }

            // ② 主畫：MSAA FBO。
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, m_MsFbo);
            gl.Viewport(0, 0, (uint)aW, (uint)aH);
            gl.ClearColor(iP.BgR / 255f, iP.BgG / 255f, iP.BgB / 255f, 1f);
            gl.DepthMask(true);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            gl.Disable(EnableCap.DepthTest);
            gl.Disable(EnableCap.CullFace);
            if (iSky == null)
            {
                gl.UseProgram(m_BgProg);
                gl.Uniform3(gl.GetUniformLocation(m_BgProg, "uBg"), iP.BgR / 255f, iP.BgG / 255f, iP.BgB / 255f);
            }
            else
            {
                gl.UseProgram(m_SkyProg);
                gl.Uniform3(gl.GetUniformLocation(m_SkyProg, "uRight"), iSkyView.Right.X, iSkyView.Right.Y, iSkyView.Right.Z);
                gl.Uniform3(gl.GetUniformLocation(m_SkyProg, "uUp"), iSkyView.Up.X, iSkyView.Up.Y, iSkyView.Up.Z);
                gl.Uniform3(gl.GetUniformLocation(m_SkyProg, "uForward"), iSkyView.Forward.X, iSkyView.Forward.Y, iSkyView.Forward.Z);
                gl.Uniform1(gl.GetUniformLocation(m_SkyProg, "uTanX"), iSkyView.TanX);
                gl.Uniform1(gl.GetUniformLocation(m_SkyProg, "uTanY"), iSkyView.TanY);
                gl.Uniform1(gl.GetUniformLocation(m_SkyProg, "uSkyYaw"), (float)(iP.SkyboxYawDeg * Math.PI / 180));
                // mip 等級：畫面中心一個像素涵蓋幾個 texel（全景寬 ＝ 2π 弧度）。
                double aTexelsPerPx = iSky.Width / (2 * Math.PI) * (2 * iSkyView.TanY / aH);
                gl.Uniform1(gl.GetUniformLocation(m_SkyProg, "uLod"), (float)Math.Max(0, Math.Log2(Math.Max(1e-6, aTexelsPerPx))));
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.BindTexture(TextureTarget.Texture2D, iSky.Tex);
                gl.Uniform1(gl.GetUniformLocation(m_SkyProg, "uSky"), 0);
            }
            gl.BindVertexArray(m_EmptyVao);
            gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            // ②b 地板：天空之後、voxel 之前；預乘 alpha 蓋在天空上（目標 alpha 保持 1）。寫深度 ⇒ 地板下方的 voxel 被擋住。
            if (iFloor != null)
            {
                // 世界 (x,y) → GL (x,−y)：y 鏡像讓繞序反轉 ⇒ 依世界 (x0,y0)→(x0,y1)→(x1,y1)→(x1,y0) 排，從 +z 看在 GL 裡才是逆時針。
                float x0 = iFloor.X0, x1 = iFloor.X1, y0 = -iFloor.Y0, y1 = -iFloor.Y1, z = iFloor.Z;
                float[] aQuad = { x0, y0, z, x0, y1, z, x1, y1, z, x0, y0, z, x1, y1, z, x1, y0, z };
                aFloorVao = gl.GenVertexArray();
                gl.BindVertexArray(aFloorVao);
                aFloorVbo = gl.GenBuffer();
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, aFloorVbo);
                gl.BufferData<float>(BufferTargetARB.ArrayBuffer, aQuad.AsSpan(), BufferUsageARB.StaticDraw);
                gl.EnableVertexAttribArray(0);
                gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), (void*)0);

                gl.Enable(EnableCap.DepthTest);
                gl.DepthFunc(DepthFunction.Less);
                gl.DepthMask(true);
                gl.Enable(EnableCap.CullFace);      // 單面：鏡頭在地板下方 ⇒ 不畫（從下面看一片不透明的地板只會擋住作品）
                gl.CullFace(TriangleFace.Back);
                gl.Enable(EnableCap.Blend);
                gl.BlendFuncSeparate(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.Zero, BlendingFactor.One);
                gl.UseProgram(m_FloorProg);
                SetLitUniforms(m_FloorProg, iViewProj, iLights, iP, aHs);
                // 偏移沿用 voxel 的 0.0005（SetLitUniforms 設的）：地板只接不投、不會自遮蔽，但貼地 voxel 的底面與地板同平面 ——
                // 偏移太小時 PCF 的鄰格比到底面深度，迎光的貼地邊緣會描出一圈假黑邊（2026-10-02 放大實測）。
                var f = iFloor.Src;
                gl.Uniform4(gl.GetUniformLocation(m_FloorProg, "uRect"), iFloor.X0, iFloor.Y0, iFloor.X1, iFloor.Y1);
                gl.Uniform2(gl.GetUniformLocation(m_FloorProg, "uFadeW"),
                            (float)(f.Fade * (iFloor.X1 - iFloor.X0)), (float)(f.Fade * (iFloor.Y1 - iFloor.Y0)));
                gl.Uniform3(gl.GetUniformLocation(m_FloorProg, "uTint"), f.R / 255f, f.G / 255f, f.B / 255f);
                gl.Uniform1(gl.GetUniformLocation(m_FloorProg, "uMode"), iFloor.Tex != null ? 1 : 0);
                gl.Uniform1(gl.GetUniformLocation(m_FloorProg, "uTile"), (float)f.TileSize);
                // ⚠ uTex 一定要指到自己的單元：預設 0 會跟 sampler2DShadow 的單元 0 撞型別 ⇒ draw 時 INVALID_OPERATION
                gl.ActiveTexture(TextureUnit.Texture2);
                gl.BindTexture(TextureTarget.Texture2D, iFloor.Tex?.Tex ?? 0);
                gl.Uniform1(gl.GetUniformLocation(m_FloorProg, "uTex"), 2);
                gl.ActiveTexture(TextureUnit.Texture0);
                gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
                gl.Disable(EnableCap.Blend);
            }

            if (aHasMesh)
            {
                gl.Enable(EnableCap.DepthTest);
                gl.DepthFunc(DepthFunction.Less);
                gl.Enable(EnableCap.CullFace);
                gl.CullFace(TriangleFace.Back);
                gl.UseProgram(m_MainProg);
                SetLitUniforms(m_MainProg, iViewProj, iLights, iP, aHs);
                gl.Uniform1(gl.GetUniformLocation(m_MainProg, "uNormalOffset"), 0.08f);
                gl.Uniform1(gl.GetUniformLocation(m_MainProg, "uAoMin"), 0.5f);
                gl.Uniform3(gl.GetUniformLocation(m_MainProg, "uHemiGround"), aHg.X, aHg.Y, aHg.Z);
                gl.BindVertexArray(aVao);
                gl.DrawElements(PrimitiveType.Triangles, (uint)iMesh.IndexCount, DrawElementsType.UnsignedInt, (void*)0);
            }

            // ③ 解析 MSAA → 單樣本，讀回。
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, m_MsFbo);
            gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, m_ResolveFbo);
            gl.BlitFramebuffer(0, 0, aW, aH, 0, 0, aW, aH, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, m_ResolveFbo);
            gl.ReadBuffer(ReadBufferMode.ColorAttachment0);

            var aPixels = new byte[aW * aH * 4];
            fixed (byte* p = aPixels)
            {
                gl.PixelStore(PixelStoreParameter.PackAlignment, 1);
                gl.ReadPixels(0, 0, (uint)aW, (uint)aH, PixelFormat.Rgba, PixelType.UnsignedByte, p);
            }
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

            // GL 左下原點 → 由上到下（⚠ 不翻就是一張上下顛倒的圖，而那不會報錯）。
            int aRow = aW * 4;
            var aTopDown = new byte[aPixels.Length];
            for (int y = 0; y < aH; y++)
                System.Buffer.BlockCopy(aPixels, (aH - 1 - y) * aRow, aTopDown, y * aRow, aRow);
            return aTopDown;
        }
        finally
        {
            gl.BindVertexArray(0);
            if (aVbo != 0) gl.DeleteBuffer(aVbo);
            if (aIbo != 0) gl.DeleteBuffer(aIbo);
            if (aVao != 0) gl.DeleteVertexArray(aVao);
            if (aFloorVbo != 0) gl.DeleteBuffer(aFloorVbo);
            if (aFloorVao != 0) gl.DeleteVertexArray(aFloorVao);
        }
    }

    /// <summary>
    /// voxel 與地板共用的光照 uniform（鏡頭、兩張陰影圖、燈、環境光、色調刻度）—— 兩者同一套光才會「站在同一個場景裡」。
    /// 各自不同的（voxel：法線偏移／AO／地面半球色；地板：偏移量、範圍、貼圖）由呼叫端另設。
    /// </summary>
    void SetLitUniforms(uint iProg, Matrix4x4 iViewProj, LightSetup iLights, SCP_SculptRenderParams iP, Vector3 iHemiSky)
    {
        var gl = m_Gl!;
        SetMat(iProg, "uViewProj", iViewProj);
        SetMat(iProg, "uLightVP0", iLights.ShadowVp[0]);
        SetMat(iProg, "uLightVP1", iLights.ShadowVp[1]);
        gl.Uniform1(gl.GetUniformLocation(iProg, "uLightCount"), iLights.Count);
        for (int i = 0; i < iLights.Count; i++)
        {
            Vector3 d = iLights.ToLight[i], c = iLights.Color[i];
            gl.Uniform3(gl.GetUniformLocation(iProg, $"uLightDir[{i}]"), d.X, d.Y, d.Z);
            gl.Uniform3(gl.GetUniformLocation(iProg, $"uLightCol[{i}]"), c.X, c.Y, c.Z);
            gl.Uniform1(gl.GetUniformLocation(iProg, $"uLightShadow[{i}]"), iLights.ShadowMap[i]);
        }
        float aAmb = (float)iP.Ambient;
        gl.Uniform1(gl.GetUniformLocation(iProg, "uAmbient"), aAmb);
        gl.Uniform1(gl.GetUniformLocation(iProg, "uToneScale"), 1f / Shoulder(aAmb + (1f - aAmb) * SunGain));
        gl.Uniform1(gl.GetUniformLocation(iProg, "uTexel"), 1f / ShadowMapSize);
        gl.Uniform1(gl.GetUniformLocation(iProg, "uBias"), 0.0005f);
        gl.Uniform1(gl.GetUniformLocation(iProg, "uSun"), SunGain);
        gl.Uniform3(gl.GetUniformLocation(iProg, "uHemiSky"), iHemiSky.X, iHemiSky.Y, iHemiSky.Z);
        for (int m = 0; m < MaxShadowMaps; m++)
        {
            gl.ActiveTexture(TextureUnit.Texture0 + m);
            gl.BindTexture(TextureTarget.Texture2D, m_ShadowTex[m]);
            gl.Uniform1(gl.GetUniformLocation(iProg, "uShadowMap" + m), m);
        }
        gl.ActiveTexture(TextureUnit.Texture0);
    }

    /// <summary>直射光增益（見主 shader 說明）。</summary>
    const float SunGain = 1.3f;

    /// <summary>與 shader 的 shoulder() 同式（CPU 端算 uToneScale 用）。</summary>
    static float Shoulder(float x) => x > 0.85f ? 0.85f + 0.15f * (1f - MathF.Exp(-(x - 0.85f) / 0.15f)) : x;

    unsafe void SetMat(uint iProg, string iName, Matrix4x4 iM)
    {
        m_Gl!.UniformMatrix4(m_Gl.GetUniformLocation(iProg, iName), 1, false, (float*)&iM);
    }

    /// <summary>
    /// 釋放 GL 資源與隱藏視窗。⚠ 要在能 MakeCurrent 的狀態下呼叫（沒有其他執行緒正握著 context）；
    /// 不呼叫也無妨 —— 行程結束時作業系統會回收。
    /// </summary>
    public void Dispose()
    {
        lock (m_Lock)
        {
            if (m_Gl == null) return;
            try
            {
                m_Context?.MakeCurrent();
                DeleteTargets();
                for (int m = 0; m < MaxShadowMaps; m++) { m_Gl.DeleteFramebuffer(m_ShadowFbo[m]); m_Gl.DeleteTexture(m_ShadowTex[m]); }
                m_Gl.DeleteVertexArray(m_EmptyVao);
                m_Gl.DeleteProgram(m_MainProg);
                m_Gl.DeleteProgram(m_ShadowProg);
                m_Gl.DeleteProgram(m_BgProg);
                m_Gl.DeleteProgram(m_SkyProg);
                m_Gl.DeleteProgram(m_FloorProg);
                foreach (var aSky in m_SkyCache.Values) m_Gl.DeleteTexture(aSky.Tex);
                m_SkyCache.Clear();
                foreach (var aTex in m_FloorCache.Values) m_Gl.DeleteTexture(aTex.Tex);
                m_FloorCache.Clear();
                m_Gl.Dispose();
                m_Window?.Dispose();
            }
            catch (Exception) { /* 收尾失敗不影響已經交出去的圖 */ }
            m_Gl = null; m_Window = null; m_Context = null;
        }
    }
}
