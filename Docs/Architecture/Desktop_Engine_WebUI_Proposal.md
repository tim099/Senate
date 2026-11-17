---
title: 桌面遊戲引擎架構規劃（C# ＋ OpenGL ＋ C++ ＋ Web UI，提案）
description: Windows／Linux 雙平台桌面遊戲引擎之架構藍圖 —— 以 C# 為主導引擎層、Silk.NET／OpenGL 渲染、按需以 extern "C" 掛載 C++ 原生外掛，並透過離屏渲染（CEF OSR／Ultralight）將 Web HTML／CSS／JS 接成遊戲內 UI 與編輯器面板。
last_updated: 2026-11-16
target_audience: [Tim, AI_Agent, Backend_Programmer]
---

# 🧱 桌面遊戲引擎架構規劃（C# ＋ OpenGL ＋ C++ ＋ Web UI，提案）

> **狀態**：規劃提案（尚未建置專案、尚未開工實作）。
> **來源**：Tim 2026-11-16 架構討論整理。
> **關聯任務**：`TASK-1305`（桌面遊戲引擎架構定案與 Phase 0 垂直驗證）。
>
> 一句話：**完全可行，且適合作為獨立桌面引擎的長期架構。**
> 鎖定 **模式 A（Windows／Linux 桌面執行 ＋ 以 Web 作為遊戲內 UI 與編輯器面板）**：核心邏輯與管線由現代 C# 主導、效能/原生 SDK 按需以 C API 下沉 C++、跨平台 Web UI 透過離屏渲染（OSR）合成至 OpenGL 貼圖，兼顧開發效率與渲染一致性。

---

## 1. 範圍定位與選型結論

### 1.1 先劃清兩條路線：模式 A vs 模式 B

| 模式 | 定義 | 本規劃態度 |
|---|---|---|
| **模式 A（本案採用）** | 引擎跑在 **Windows／Linux 桌面**，使用 HTML／CSS／JS 實作**遊戲內 HUD 與編輯器面板** | ✅ **採用**：Web 擅長排版、文字、表格、動畫、輸入法與 DevTools，可省下自幹 OpenGL UI 系統的絕大部分工時 |
| **模式 B（暫不納入）** | 整顆 3D 引擎與 C#／C++ 核心都編進瀏覽器（WebAssembly ＋ WebGL） | ⏸ **暫不目標化**：會同時撞上桌面 OpenGL vs WebGL 差異、WASM 執行緒／檔案限制與 C#↔C++ 跨語言互通複雜度 |

### 1.2 Windows ＋ Linux 雙平台下的 Web UI 方案

因明確要求同時支援 **Windows 與 Linux**，不採用僅限 Windows 的 `WebView2`（亦避免維護「Win 用 WebView2 ＋ Linux 用 WebKitGTK」兩套透明子視窗與合成坑）。雙平台最一致的做法是 **Off-Screen Rendering（OSR，離屏渲染成 OpenGL Texture）**：

| 方案 | 原理 | 雙平台一致性 | 優點 | 代價 / 限制 |
|---|---|---|---|---|
| **CEF OSR**（Chromium Embedded Framework） | C++ 端藉 `libcef` 在背景繪製網頁像素緩衝並回傳髒區，OpenGL 合成為全螢幕透明 Quad | **極高**（Windows／Linux 同套貼圖管線） | 完整現代 Chromium、CSS Grid/Canvas/DevTools 齊全、BSD 授權無營收門檻 | 打包體積約 120–180 MB、多進程架構、需轉發鍵鼠與 IME 事件 |
| **Ultralight** | 專為遊戲 UI 設計的輕量 WebKit 衍生核心，直接輸出表面緩衝或對接 GPU Driver | **極高** | 體積與記憶體遠小於完整 Chromium、C/C++ API 簡潔、啟動快 | 商業授權受營收門檻限制；極新前端特性支援略少於最新版 Chrome |
| **Dear ImGui（ImGui.NET）** | 同幀由 OpenGL 直接繪製的即時 UI | **極高** | 零延遲、無跨進程與大記憶體開銷 | 不負責玩家正式視覺介面；**建議雙軌混用**：留給 FPS／線框／Gizmo 等開發除錯開關 |

---

## 2. 四層系統架構與職責邊界

```text
+-------------------------------------------------------------------+
|  Web UI 層 (HTML / CSS / JS)                                      |
|  - 遊戲內 HUD、背包/商店/地圖面板、編輯器屬性表、DevTools 遠端除錯  |
+-------------------▲---------------------------▲-------------------+
   Snapshot / Events│(低頻節流)                  │ Commands (RPC)
+-------------------┴---------------------------┴-------------------+
|  C# 引擎主導層 (.NET 8/9 + Silk.NET)                              |
|  - 主迴圈 (FixedUpdate / Render)                                  |
|  - ECS / 場景管理 / 資源虛擬檔案系統 / Input Router                |
|  - UI Bridge 抽象層 (IUiHost：可抽換 OSR 或外部 WebSocket 除錯)    |
+-------------------┬---------------------------┬-------------------+
   extern "C" 批次呼叫│                          │ Silk.NET / GL 呼叫
+-------------------▼-------+           +-------▼-------------------+
|  C++ 原生外掛層 (.dll/.so)|           |  OpenGL 渲染管線          |
|  - CEF / Ultralight 宿主  |──Texture─▶|  1. 3D 世界場景 Pass      |
|  - Dirty Rect 上傳管理    |           |  2. 後處理 (Bloom/Tone)   |
|  - 物理(Jolt)/尋路(Recast)|           |  3. Web UI Alpha 合成 Pass|
+---------------------------+           +---------------------------+
```

### 各層分工原則

1. **C# 引擎層（主導）**：
   - `.NET 8/9` 搭配 `Span<T>`、`ArrayPool`、連續值型別 `struct` 已具備高水準運算效能，**場景、ECS、遊戲邏輯、資源索引、事件路由一律優先用 C# 開發**，不為了分層而過早拆進 C++。
   - 透過 **Silk.NET**（或 OpenTK）綁定 OpenGL 4.5+ 與視窗系統（GLFW / SDL）。
   - OpenGL Context 具嚴格**執行緒親和性**：背景執行緒只負責讀檔與解壓，所有 `gl*` 呼叫統一排隊在單一 Render Thread 執行。
2. **C++ 原生層（按需下沉）**：
   - 只放兩類東西：① 原生僅提供 C/C++ SDK 的函式庫（物理如 JoltPhysics、尋路如 Recast Navigation、CEF/Ultralight 宿主）；② 經效能分析證實為 CPU 瓶頸的緊密計算或零 GC 停頓熱路徑。
3. **OpenGL 渲染層**：
   - 3D 幾何與後處理先畫進 Scene Target，最後才執行 Web UI 全螢幕透明 Quad 疊加 Pass（若有需要，亦可將特定 WebView 貼圖作為 3D 物件表面材質）。
4. **Web UI 層**：
   - 純標準前端（原生 HTML/CSS/ES Modules，或搭配輕量前端建置工具），透過薄薄的 `bridge.js` 與引擎溝通，保持在普通瀏覽器中也能預覽靜態版型。

---

## 3. 三大關鍵工程機制

### 3.1 OSR 貼圖同步與「預乘透明」規則

1. **Dirty Rect 局部上傳（靜止零頻寬）**：
   - 瀏覽器 `OnPaint` 回傳完整 `BGRA` 像素緩衝與變更矩形清單 `dirtyRects`。
   - C++ 端保留一份固定大小的 CPU Pixel Buffer 與 `dirty` 矩形列表；Render Thread 每幀繪製前僅在有變動時以 `glTexSubImage2D` 上傳變更矩形。畫面靜止時每幀消耗接近零頻寬。
2. **Premultiplied Alpha（防止黑邊）**：
   - 離屏瀏覽器輸出為預乘透明度格式，OpenGL 合成 UI Quad 時**必須**使用：
     `glBlendFunc(GL_ONE, GL_ONE_MINUS_SRC_ALPHA)`
   - 若誤用 `GL_SRC_ALPHA, GL_ONE_MINUS_SRC_ALPHA`，半透明陰影與圓角邊緣會出現黑邊。

### 3.2 零延遲「點擊穿透」與輸入焦點鎖定（Input Router）

全螢幕透明 Web UI 蓋在 3D 畫面上時，必須精確區分「點在 UI 上」與「點穿到 3D 世界」：

1. **CPU 像素 Alpha 快查表（Hit-Testing）**：
   - 直接查詢 C++ 本地保留的最後一次 `OnPaint` 像素緩衝中座標 `(x, y)` 的 Alpha 值：
     - `alpha > 10`：命中 UI（由瀏覽器接收點擊／Hover／滾輪）。
     - `alpha <= 10`：空白透明區，**直接穿透**給 3D 攝影機旋轉與世界物件射線選取。
   - 優點：完全不需在 `mousemove` 跨越語言邊界呼叫 JS 的 `elementFromPoint`，支援任意圓角、CSS 動畫與縮放。
2. **文字輸入焦點與 IME 閘道（Text / IME Gate）**：
   - 使用者點擊網頁內的 `<input>` / `<textarea>` 時，`bridge.js` 發送 `ui:textFocus = true` 給 C# Input Router：
     - 鎖住遊戲快捷鍵（如 `WASD`、空白鍵、`E`），將鍵盤與系統中文輸入法（IME）組字／候選詞座標交由瀏覽器處理。
     - 按 `Esc` 或點擊穿透區時觸發 `blur()` 並送回 `ui:textFocus = false`，控制權歸還遊戲。

### 3.3 C# ↔ JS 雙向通訊與資源讀取

1. **低頻推送＋非同步 RPC**：
   - **C# → JS**：事件驅動或低頻節流快照（例如數值狀態 15–30 Hz 推送差分），**禁止每幀序列化整顆世界 JSON 塞給 JS**。
   - **JS → C#**：前端透過 `await engine.invoke(cmd, payload)` 送出帶 `requestId` 的結構化指令，由 C# `IUiHost` 非同步回覆。
2. **自訂 URL Scheme 取代 Base64**：
   - 在瀏覽器端註冊自訂 Scheme（如 `engine://assets/...`、`engine://runtime/...`），直接從引擎資源系統餵二進位圖片／JSON 串流給 `<img src="...">` 與 `fetch()`，避免大量圖片轉成 Base64 字串造成序列化阻塞。

---

## 4. C# ↔ C++ Interop 規範與介面草圖

### 4.1 跨語言邊界三條鐵律

1. **平坦 `extern "C"` ABI**：C# 不直接呼叫 C++ 類別成員或傳遞 STL 容器（`std::vector` / `std::string`），統一包裝成 C 函式與原始指標＋長度，C# 端採 `.NET 7+` `[LibraryImport]`。
2. **粗粒度批次呼叫（Batched Calls）**：不允許「每幀每個實體／每顆體素」跨邊界，應將批次資料置於固定（Pinned）或 Unmanaged 連續陣列中，一幀僅跨一兩次邊界處理。
3. **配置與釋放對稱**：由 C++ 配置的記憶體必由同一個模組提供的 `Destroy*` 釋放，絕不跨執行階段（CRT vs .NET）混用釋放器。

### 4.2 原生外掛介面草圖（示意）

```c
// Engine.Native (extern "C" ABI 示意，實作時獨立建置)
typedef void* EngineWebViewHandle;

EngineWebViewHandle Engine_WebView_Create(int width, int height, const char* initial_url, EngineAssetFetchFn asset_cb);
void Engine_WebView_Destroy(EngineWebViewHandle handle);
void Engine_WebView_Resize(EngineWebViewHandle handle, int width, int height);
void Engine_WebView_Tick(void); // 推進瀏覽器訊息迴圈

// Hit-testing 與輸入轉送
int  Engine_WebView_HitTestAlpha(EngineWebViewHandle handle, int x, int y, uint8_t threshold);
void Engine_WebView_SendMouseMove(EngineWebViewHandle handle, int x, int y, int modifiers);
void Engine_WebView_SendMouseButton(EngineWebViewHandle handle, int x, int y, int button, int down, int modifiers);
void Engine_WebView_SendKey(EngineWebViewHandle handle, int key_code, int scan_code, int down, int modifiers);
void Engine_WebView_SendChar(EngineWebViewHandle handle, uint32_t utf16_char);

// Render Thread 上傳至 OpenGL 貼圖（內部跑 glTexSubImage2D 處理 dirtyRects）
void Engine_WebView_UploadIfDirty(EngineWebViewHandle handle, uint32_t gl_texture_id);

// 雙向通訊
void Engine_WebView_PostMessage(EngineWebViewHandle handle, const char* utf8_json, int byte_len);
```

---

## 5. 性能對比與 Voxel 核心切分（參考 Blockside）

### 5.1 純 C# vs C#＋部分 C++ 的實戰性能評估

同一套演算法在現代 `.NET 8/9`（Tiered PGO ＋ 零配置紀律）與 C++ 之間的差距，重點不在單一語言快慢，而在**工作型態、記憶體布局與邊界粒度**：

| 工作型態 | 典型差距（純 C# vs C++） | 關鍵原因 |
|---|---|---|
| **遊戲邏輯 / 場景圖 / UI 橋接 / IO** | **幾乎無感（< 5%–15%）** | 多受制於分支、資源等待與事件頻率；現代 JIT 表現已相當接近 C++。 |
| **OpenGL 繪製命令送出（Render Submission）** | **幾乎無感（通常 < 10%）** | C#（Silk.NET）亦直接呼叫原生驅動函式指標；瓶頸在 GPU Driver 與批次數量（Instancing / Indirect Draw），不在 CPU 送出微秒差。 |
| **緊密數值迴圈（大規模體素 Meshing / 物理 / 尋路）** | **C++ 常快 1.5×～3×** | C++ 編譯器在自動 SIMD 向量化（AVX2）、嚴格別名優化（`__restrict`）、跨模組內聯（LTO）與零邊界檢查上仍具優勢；成熟庫本身亦為手調 SIMD 成品。 |
| **幀時間穩定度（1% Low / Frame Pacing）** | **寫法錯誤會落差極大** | 差距常體現在突發停頓而非平均 FPS：C# 熱路徑若不慎配置（`new class`、`LINQ`、字串拼接、閉包）會觸發 GC 掉幀；嚴守 `struct`＋`ArrayPool` 零配置則可穩定 0 GC 停頓。 |
| **反直覺陷阱：細碎跨邊界呼叫** | **混用 C++ 反而更慢** | `P/Invoke` 無法跨 DLL 內聯且有固定轉換成本；在迴圈內針對單顆粒子或單格體素跨界呼叫，累積成本遠高於留在純 C#。 |

> **記憶體局部性優先於語言切換**：把連續 `struct` 陣列排在一起掃描的 C# 程式，往往快過大量 `std::shared_ptr` 跳躍配置的 OOP C++。

### 5.2 主打 Voxel 核心時的切法（以 Blockside 為參考）

對照《Blockside》既有模組架構（`05_hull.js` 船殼、`15_smooth.js` 平滑網格、`20_shatter.js` 斷裂連通、`33_wrsim.js` 艙室進水、`60_rbatch.js` 零配置繪製批處理、與離線烘焙 `VoxelKit`），**Voxel 引擎最忌諱將權威網格資料本體塞進 C++、卻把玩法邏輯留在 C#**：否則彈道（`21_gunnery`）、進水欄（`33_wrsim`）、甲板尋路（`40_crew`）每幀會觸發數萬次跨界 `GetVoxel(x, y, z)` 查詢，吞噬所有算力紅利。

建議採用 **「C# 握住所有狀態，C++ 僅作無狀態純函數加速算子（Stateless Kernel）」**：

| 模組類別（對照 Blockside） | 建議落點 | 設計與效能理由 |
|---|---|---|
| **權威 Voxel Grid 本體、屬性與狀態表** | **C#（一維平坦連續陣列）** | **一律用一維 `byte[]`／`ushort[]`／`struct[]`（⛔ 不用 `Voxel[,,]` 多維陣列或每格物件）**。所有玩法邏輯在 C# 內直接零成本讀寫；需要交給 C++ 時直接 `fixed` 傳指標，**零拷貝**。 |
| **進水模擬 (`33_wrsim`)、帆索物理 (`30_rig_physics`)、船員 AI (`40_crew`)、離線轉即時的 `VoxelKit` 烘焙** | **C#（多執行緒＋SIMD 結構）** | 由單執行緒 JS 搬至 `.NET 8/9` 後，靠 `Parallel.For` 多核平行化、`System.Numerics` 硬體向量與 `BitOperations` 位元遮罩，即可獲得遠超網頁版的吞吐量，並大幅加速規則迭代；Python 離線烘焙亦能轉為編輯器內毫秒級即時烘焙。 |
| **批處理整理 (`60_rbatch`) 與 OpenGL 提交 (`01_gl`)** | **C#（Silk.NET）** | 結合 OpenGL 4.5 Persistent Mapped Buffer 或 `MultiDrawElementsIndirect`，C# 可直接將世界矩陣寫入 GPU 可見記憶體。 |
| **無狀態重算閉環（平滑船殼 `15_smooth`、殉爆大面積 6-連通洪水填充 `20_shatter`、城鎮巨量 Meshing）** | **先寫 C#；實測瓶頸才抽入 C++** | 輸入整塊連續體素指標、輸出整段頂點 Buffer，每次事件或每幀僅跨 1 次 `extern "C"` 邊界（如 `GenerateSmoothMesh(const uint8_t* voxels, ...)`），因從第一天就維持平坦一維陣列，未來抽換不需改動任何上層資料結構。 |
| **海面波浪、水體光影、大型環境光遮蔽（AO）** | **OpenGL Compute Shader（GPU）** | 高度並行的格點與光柵運算直接下放 GPU Compute，加速幅度遠勝在 CPU 上從 C# 搬往 C++。 |

---

## 6. 建議專案結構與開發期工作流

```text
MyEngine/
├── src/
│   ├── Engine.Core/            # 純 C# (.NET 8/9)：主迴圈、ECS、InputRouter、IUiHost
│   ├── Engine.Render.GL/       # C# + Silk.NET：Shader、Mesh、Pipeline、WebUI Overlay Pass
│   ├── Engine.Native/          # C++ (CMake)：CEF OSR / Ultralight 宿主、extern "C" ABI、原生套件
│   └── Engine.Editor/          # 桌面啟動器、視窗生命週期與編輯器殼層
├── webui/                      # 前端專案（純 HTML/ES Modules，或 Vite）
│   ├── index.html
│   ├── bridge.js               # RPC 封裝與開發模式 fallback
│   ├── hud/                    # 遊戲內 HUD 元件
│   └── editor/                 # 場景樹、屬性 Inspector、資源瀏覽器
└── build/                      # Windows (.dll) / Linux (.so) 建置與 CEF 執行檔部署腳本
```

### 開發期熱重載工作流

- 啟動旗標支援 `--ui-url=http://localhost:5173` 與 `--remote-debugging-port=9222`：
  - **正式發佈**：載入打包後的 `engine://ui/index.html`。
  - **開發期間**：OSR 瀏覽器直接連本地靜態／熱重載伺服器，**修改 HTML／CSS／JS 一秒即時反映在遊戲視窗內**，並可用外部 Chrome 連上 `localhost:9222` 使用完整 DevTools 除錯，不必重編 C# 或 C++。

---

## 7. 分階段驗證與實施路線

### Phase 0：最小垂直驗證原型（Vertical Slice，優先於正式功能開發）
1. **視窗與 OpenGL 最小核心**：C# + Silk.NET 在 Windows 與 Linux 皆能開窗並繪製旋轉 3D 幾何。
2. **原生 Web OSR 合成**：C++ CMake 編出 `.dll` / `.so`，在背景渲染一張含透明背景、按鈕與 `<input>` 的測試頁並透過 `glTexSubImage2D` 疊合（驗證預乘透明無黑邊）。
3. **輸入雙通道驗證**：
   - 點擊網頁按鈕可回傳訊號給 C#；
   - 滑鼠於透明區拖曳可直接控制 3D 相機（點擊穿透正常）；
   - 聚焦輸入框時能正常使用中文輸入法（IME）而不誤觸遊戲按鍵。
4. **雙平台打包檢查**：Windows 與 Linux 各自編譯、載入原生動態庫與瀏覽器子進程皆能正常啟動與無遺留殭屍程序結束。

### Phase 1：通訊協定與除錯雙軌
- 確立 `bridge.js` ↔ `IUiHost` RPC 規格、自訂 `engine://` Scheme 資源載入、`--ui-url` 熱重載，並掛入 `ImGui.NET` 供引擎底層高頻除錯面板使用。

### Phase 2：按需下沉原生模組
- 視實際遊戲專案需求，再透過同一條 `extern "C"` CMake 管線納入物理（JoltPhysics）、尋路（Recast）或高負載網格處理。

---

## 8. 已知風險與待拍板事項

1. **Web OSR 核心拍板**：首版原型要直接上 **CEF OSR**（自由商用無營收門檻、體積較大），還是先用 **Ultralight**（輕量快速、需留意商業授權條款）？
2. **視窗套件選擇（特別關係 Linux IME 與事件轉發）**：C# 主視窗底層選 **GLFW** 還是 **SDL3**？（SDL3 原生 IME 候選窗與多平台輸入事件映射通常比 GLFW 更完整。）
3. **Linux 沙箱與部署差異**：CEF 在 Linux 下的 `chrome-sandbox` 權限／AppImage 打包，以及 X11 與 Wayland 環境下的視窗與輸入法相容性需在 Phase 0 實機驗證。
4. **原生 Crash 防護**：C# ↔ C++ 邊界一旦寫壞記憶體無法被 C# `try/catch` 接住，Phase 0 即需建立 C++ 邊界斷言檢查與跨平台 Mixed-mode 除錯設定。
