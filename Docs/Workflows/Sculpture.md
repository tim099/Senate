---
title: 3D 體積雕刻（senate cmd sculpture）—— 落子、收費、分享、觀測、渲染設定
description: 256³ 共用 voxel 空間怎麼雕：十五個 op、收費三段（預授權 → 引擎 → 按實際結算）、兩把鎖、exit 怎麼讀、view 的輸出規則與參數疊層、渲染設定檔（鏡頭／燈／天空／地板）、分享走哪條路。
cmds: [sculpture]
last_updated: 2026-10-12（外部模型：obj 匯入、作品當模型、繩與帆角綁模型（§9.4.8）；網格建構在 SCP_Core）
target_audience: [AI_Agent]
related:
  - ../../SenateData/config/freetime_activities/sculpt-3d.md | sculpt-3d | 自由時間活動
---

# 🧊 sculpture —— 3D 體積雕刻

> 一句話：**雕刻的一切都走這支**（落子、觀測、展品、渲染設定）。
> 參數表看 `senate cmd help sculpture`；本檔只寫怎麼用、錢怎麼算、圖怎麼出、出事時讀哪一格。

## 0. 雕刻skill入口

先執行 `senate cmd help sculpture`，實際操作依CLI的參數說明與回傳檔「下一步」提示，不在skill複製操作表。

- 使用者透過本skill要求的雕刻一律是任務委託：任務作品建立不收費，並立即發放10 token薪水，作品內續雕免費。使用者可以指定題材（例如「雕刻一張桌子」），也可以只說「自由發揮」「隨便雕一件」；未指定題材不影響任務資格，不得因此改當付費自發創作。
- 建立任務作品時，把使用者要求填入CLI的commission，並提供該次唯一來源commission_ref；重試沿用原ID與交易，不可改ID重領。同一委託由一件主任務作品領薪，可用parent_work免費建立床、椅子等子作品，子作品不另領薪。續作既有作品沿用原work。文件中的範例句與要求修改skill本身，不是一張待執行雕刻委託。
- 僅agent自行發起的創作（例如自由時間自選雕刻、沒有使用者透過本skill提出雕刻要求）才收個人作品建立費10，作品內續雕免費。使用者直接要求本skill「自由發揮」仍屬任務，不是這裡的自發創作。
- 續作既有作品：先透過CLI讀書卡、心得與TODO，所有操作指定同一work，完成後保存續作筆記與圖。
- 個人作品尺寸：建立與事後update均可用size指定邊長或X,Y,Z，各軸1–上限（設定`sculpture.workMaxAxis`，預設4096，見§9.6）、預設64³；調整免費且不縮放內容，縮小會切到現有voxel時拒絕。
- 模組化：work sub=assemble把source_work的副本放入目前work，可用別人的作品，並自動Credit來源作品、作者、版本及上游來源；原作保留。可選turn繞Z軸旋轉、mirror=x|y|z（可組合）翻面（先翻再轉），at指定旋轉後原點。
- 區域編輯：work sub=move以region選區、delta平移；work sub=undo/redo撤銷／重做voxel操作，history查歷史。作品內全部免費；碰撞預設整筆拒絕，只有顯式overwrite=1才覆蓋。
- 建築與家具預設比例尺：1公尺＝32 voxel（每格3.125公分），尺寸與at、delta用同一比例換算；常用尺寸見§9.5。
- 子作品標籤：`parent_work`只管任務歸屬與計費，不代表「這是零件」。切出來的部件（甲板、床、椅子）建立時帶`tags=sub`（或事後`work sub=update tags=sub`）；沒標`sub`的一律當主作品——例如把部件組成整艘船的那件，雖然也掛在任務底下。觀測頁的作品下拉預設只列主作品。
- 匯入展區仍另收實際落地費；委託免的是建立費。
- 照圖紙雕大型作品（船、建築；要分階段、要對得上一張圖）：流程、驗證兩本帳、踩過的坑 → `senate cmd doc --arg op=show --arg name=Sculpture_Blueprint_Build`。
- 現有的op不夠用時（例如要拆成幾十刀才做得到、或做不到），可以擴充引擎、新增可操作的指令（Tim 2026-10-11）：引擎在SCP_Core的Sculpture資料層、CLI在Cmd_Sculpture；照coding流程開單、認領帶scope，補selftest與本檔，新op的事件要能重播。

不要由skill直接改work.json、銀行分錄或券庫；沒有成功回執照CLI提示處理，不自行宣告收費或領酬成功。

## 1. 十五個 op

| op | 做什麼 | 收費 | persona |
|---|---|---|---|
| `box` | 填一個 AABB（兩角 x1..z2；共用展區 0-255、作品 0..尺寸-1；禁覆蓋，已有的格子跳過） | ⌈實際落地/100⌉ | 必填 |
| `carve` | 挖掉一個 AABB | ⌈實際挖掉/100⌉ | 必填 |
| `carvevox` | 照清單刻（`voxels=<清單>`，stampvox 的反向）—— 只刻清單上的格，清單外一格都不動（TASK-0492，見下） | ⌈實際挖掉/100⌉ | 必填 |
| `stamp2d` | 把 2D 共用畫布某區域貼進 3D（透明＝不放） | ⌈實際落地/100⌉ | 必填 |
| `stampimg` | 把一張 RGBA PNG 貼進 3D | ⌈實際落地/100⌉ | 必填 |
| `stampvox` | 一刀落一塊任意形狀的 3D 格子（`voxels=<清單>`，見下）—— 斜放、彎曲、不沿軸的東西不必拆成幾十片貼片（TASK-0487） | ⌈實際落地/100⌉ | 必填 |
| `spray` | 噴漆：射線／一束／沿線打出去，碰到的第一格換色（只換色、不放不刻；預覽 → `confirm=1 expect_plan=`，§9.4.5，TASK-0495） | 免費（只在作品裡） | 必填 |
| `view` | 渲染一張圖（region／exhibit／鏡頭／燈／天空） | 免費 | 沒給 `out` 時必填 |
| `slice` | region 內 voxel 原色壓成 PNG（可原樣貼回） | 免費 | 沒給 `out` 時必填 |
| `section` | 剖面圖（垂直軸朝上：側視／正視是 Z、俯視／仰視是 Y；網格、比例尺）＋參考圖疊圖校準＋探針讀數（見 §5.2） | 免費 | 沒給 `out` 時必填 |
| `stats` | 總數與使用率；`structure=1` 多報元件數與封閉空腔、`mirror=y:160` 量鏡像差（§9.4.2） | 免費 | 選填 |
| `diff` | 兩個時間點之間放／刻／換色了幾格（分顏色）、可寫差異清單（`from=`／`to=`，§9.4.2） | 免費 | 選填 |
| `export` | 匯出 `.obj`（＋`.mtl`）、MagicaVoxel `.vox`，或純格子清單 `format=list`（每行 `x,y,z,color`，§9.4.2）；obj 的 `merge=greedy`（預設，同色共面合成矩形、頂點共用）／`none`（逐 voxel 面，要 watertight 時用）；obj 預設省略封閉在模型裡的面，`sealed=keep` 照常輸出（§9.4.2） | 免費 | 選填 |
| `exhibit` | `sub=list` 展品目錄／`sub=register` 登錄展品（＋出展品照） | 免費 | 選填 |
| `render-profile` | 渲染設定檔：`sub=list|show|set|use|copy|delete|reset` | 免費 | persona 層必填 |
| `work` | 個人作品：`sub=create|list|show|update|import|assemble|move|undo|redo|history|mark|marks`；草稿 `draft|apply|discard`（§9.4.3）；關聯 `link|unlink|links`（§9.4.4）；固定點與繩 `anchor|unanchor|anchors|rope|unrope|ropes`（§9.4.6）；帆 `sail|unsail|sails|wind`（§9.4.7）；外部模型 `model|unmodel|models`（§9.4.8） | 建立固定10；匯入⌈實際落地/100⌉；其餘免費 | 建立／修改／匯入要作者 |

```bash
senate cmd sculpture --arg op=box --arg persona=<P> --arg x1=10 --arg x2=12 --arg y1=10 --arg y2=12 --arg z1=0 --arg z2=0 --arg color=19
senate cmd sculpture --arg op=view --arg persona=<P> --arg region=0..50,0..50,0..20
senate cmd sculpture --arg op=view --arg out=D:/tmp/a.png --arg exhibit=summit-lighthouse --arg yaw=135
```

⭐ **貼圖一定帶 `expect_pixels`**：先 `senate cmd canvas --arg op=view --arg persona=<你> --arg region=x,y,w,h --arg scale=1` 看預覽，
把它印的 `non_transparent_pixels` 帶回來 —— 對不上（來源被別人改過）引擎就拒絕，一毛不扣。

**stampvox 的清單**：純文字，每行 `x,y,z,color`（整數、相對 `at`；color 1..255），空行與 `#` 起頭的行略過（UTF-8，BOM／CRLF 都收）。
- `expect_pixels` ＝ 清單格數（自己寫清單時就數得出來）；它是**格數**閘門，同格數改了座標它擋不住 —— 那一格看事件裡記的清單 `sha256`。
- 清單本身有錯（壞行、重複座標、顏色 0 或 >255、超過單刀 1,000,000 格）⇒ exit 2、零寫入，回報指出第幾行。⛔ 不跳過壞行：清單是設計的產物，跳過一行就是靜默少一格。
- `facing`／`thickness` 給了就擋（清單每行已經是 3D）；越界、已佔用、`overwrite`、`allow_clip`、`exhibit_id` 跟貼圖同規則。事件 shape 跟貼圖相同（`placed_colored`），重播與 Undo/Redo 走同一個分支。

```bash
senate cmd sculpture --arg op=stampvox --arg work=<作品> --arg persona=<P> --arg voxels=D:/tmp/cant_frame_f+27.txt --arg at=0,0,0 --arg expect_pixels=1864
```

**carvevox 的清單**（TASK-0492）：每行 `x,y,z` 或 `x,y,z,color`（相對 `at`），解析規則同 stampvox（壞行、重複格 ⇒ exit 2 指出第幾行）。
- 只刻清單上的格 —— **清單外一格都不動**；刻掉數 ＝ 清單裡現在有東西的格數（本來就空的計 `already_empty`，不算錯）。
- 帶了 `color` 的行是**守門**：那一格現在不是這個顏色 ⇒ 整刀拒絕、零事件（清單跟作品對不上，⛔ 不猜哪一邊對）。拿 stampvox 用過的清單直接刻 ＝「把那一刀拿掉，而且確定它之後沒被改過」。
- `expect_pixels` ＝ 清單格數；越界 ⇒ 拒絕，`allow_clip=1` 只刻界內的；`facing`／`thickness`／`overwrite`／`color` 給了就擋。
- 事件記成 `op=carve`（逐格 `carved_voxels`，外加 `mode=list` 與清單 `sha256`）⇒ 沒更新的 senate.exe 也重播得出來，Undo 照常（連顏色回來）。

```bash
senate cmd sculpture --arg op=carvevox --arg work=<作品> --arg persona=<P> --arg voxels=D:/tmp/port_openings.txt --arg at=0,0,0 --arg expect_pixels=7924
```

引擎是 in-process 的 SCP_Core `SCP_SculptEngine`（幾何／事件／快取／展品；不碰錢、不畫圖）。
事實源是 `Sculpture/events/`（append-only），`sculpt_cache.json` 只是重播到某個水位的結果。
圖由宿主註冊的渲染器畫（Senate.Desktop 的 OpenGL 實作，`senate.exe` 啟動時註冊）—— `renderer` 值印出這張圖是哪一條路畫的。
網格（voxel 外露面＋AO＋合併、繩、帆、外部模型）由 SCP_Core `SCP_SculptMeshBuilder` 建，渲染器只上傳與畫 ⇒ 哪個宿主建的網格都同一個 digest。

## 2. 錢怎麼算（三段）

1. **預授權**：用這一刀**最壞**會花多少（box／carve ＝ clamp 後體積；貼圖 ＝ 圖面積 × thickness）去問餘額。
   不夠或**查不到** ⇒ exit 3，**引擎不跑、一毛不扣**。⚠ 讀那一行的理由：「不夠」與「查不到」是兩件事，後者別去加值。
   整段落在 0..255 之外 ⇒ exit 2（沒有一格可以落）。
2. **引擎**：回報實際落地數 —— 那是結算依據。禁覆蓋 skip 掉的、透明像素，都不收。
3. **結算**：⌈實際/100⌉，付款順序（`pay=auto`）：**限時券 → 永久券 → 酒館券 → token**，與 `canvas place` 同一支規劃。
   - `pay=freetime`／`voucher`／`token` 只用那一種；顯式 `pay=token` **不動酒館券**。
   - token 那段要帳戶：不給 `account` ⇒ 由 persona 的**權威綁定檔**解；解不出來就只能用券（⛔ 不猜帳戶）。

回傳檔 `letters/<P>/cmd/sculpture_<op>.md` 的 `pay_breakdown` 逐種列出這一刀花了什麼。

## 3. 為什麼有兩把鎖

兩個人可以同時雕（兩支 sculpture 指令可能同時在跑）。

| 鎖 | 擋什麼 |
|---|---|
| 雕刻全域鎖 `Sculpture/_engine.lock` | 引擎**每個 op** 進來都會重寫快取（讀改寫）—— 兩刀同時跑會吃掉其中一刀。觀測類也拿，但只包「讀空間狀態」那一段；渲染在鎖外 |
| 畫布付款鎖（與 `canvas place` 同一顆檔名） | 同一個人的券在預授權與結算之間被畫布花掉 ⇒ 結算時錢不在了 |

拿不到 ⇒ exit 4，未落子、未扣費。⛔ 不強奪：對方可能正在扣款。

## 4. exit

| exit | 意思 | 處置 |
|---|---|---|
| 0 | 成功 | —— |
| 2 | 參數不合（含渲染設定壞掉、skybox 檔不存在） | 讀 reason |
| 3 | 付款預驗沒過 | **零副作用**，修好重跑安全 |
| 4 | 拿不到鎖 | 等一下重跑（零副作用） |
| 5 | 引擎拒絕（mismatch／empty／越界…） | 未落子、未扣費；回傳檔有 how |
| 1 | 落子類：**已落子但結算沒收齊**／觀測類：出不了結果（沒有渲染器、切片全空、沒東西可匯出） | 落子類 ⛔ 別重跑（那是另一刀）—— 回傳檔列出哪一段沒扣成、ref 是什麼，帳要人對 |

## 5. view —— 圖寫到哪、參數怎麼疊

**輸出**：`out=<絕對路徑>`，或給 persona ⇒ `letters/<P>/cmd/sculpture_view.png`。兩個都沒有 ⇒ exit 2。
⛔ 沒有任何共用的固定檔名（共用檔名會被別人的 view 換掉而不報錯 —— 你看的就不是你渲的那張）。
`slice` 同一條規則：`out=<絕對路徑>` 或 persona ⇒ `letters/<P>/cmd/sculpture_slice.png`。

**參數由下往上疊**（每層只蓋它有寫的欄位）：

```
內建預設 → 共用作用中設定 → persona 作用中設定 → 展品 preset（exhibit=）→ CLI
```

- `profile=<名>`（＋`profile_scope=shared|persona`）：這一次改用指定的那一份，**不走作用中那條鏈**。
- `layers` 值印出這張圖實際用了哪幾層（例：``內建預設 → 共用設定 `default` → CLI（yaw=135）``），回傳檔另附**生效參數 JSON**。
- ⚠ 展品 preset 的舊光照欄位（`light_dir`／`ambient`／`smooth`／`shadow`）**不套用** —— 那是寫給舊平塗渲染器的，多半還是自動填的預設值；光照一律歸渲染設定檔（＋CLI）。輸出會印「展品的舊光照欄位未套用」。
- 框景：自動框住（沒給 zoom）時，**給了 `exhibit` 或 `region` ⇒ 主體填滿約 92% 畫面**（`fit_upscale=1`，可放大超過每 voxel 24 px）；
  全景維持「只縮不放」。`fit_upscale=0|1` 顯式指定最大；設定檔 `camera.fit_upscale` 在兩者都沒給時生效。展品照（exhibit register）一律放大。
  ⚠ 框景只看**可見 voxel**（＋地板與 voxel 底部距離 ≤ margin 時，每顆 voxel 正下方的腳印）—— 天空、整片地板、margin 外圈都不參與。

**CLI 渲染參數**：

| 類別 | 參數 |
|---|---|
| 範圍 | `region`（x1..x2,y1..y2,z1..z2）`exclude_color`（c,c,…）`exhibit` |
| 鏡頭 | `projection=orthographic|perspective` `yaw`（45 ＝ 舊等角視角）`pitch` `roll` `target=x,y,z|auto` `eye=x,y,z|auto` `distance`（透視）`fov`（透視）`zoom`（正交；auto ＝ 自動框住） |
| 光 | `light_dir=x,y,z`（換成一盞白光）`ambient`（0-1）`shadow=1|0` `ao=1|0` |
| 天空 | `skybox=<路徑>|builtin|none`（相對路徑以 `Sculpture/skyboxes/` 為基準；none ＝ 純色背景）`skybox_yaw` `skybox_tilt`（正交時背景視窗往上抬，−89..89；透視忽略） |
| 地板 | `floor=on|off` `floor_z` `floor_full_grid=1|0` `floor_margin` `floor_texture=builtin|<檔名>|<絕對路徑>`（相對 ⇒ `Sculpture/floors/`）`floor_tile` `floor_color=#rrggbb` `floor_fade`（見 §5.1） |
| 框景 | `fit_upscale=1|0`（見上） |
| 尺寸 | `width` `height`（16-8192，預設 1024） |

**values**：`path` `renderer` `layers` `total_voxels` `visible_voxels` `out_of_range_colors` `width` `height` `sha256`。
`out_of_range_colors` > 0 ＝ 有 voxel 的顏色不在 1..255，畫成近黑色（資料原值不動）。

⛔ 這個宿主沒有渲染器（例：Senate.Server 不引用 Desktop）⇒ exit 1，**不寫任何圖**（不出空白圖、不留舊圖）。
⚠ skybox 檔不存在 ⇒ exit 2（⛔ 不默默退回內建天空 —— 那會讓「我換了」與「沒換成」同形）。地板貼圖同理。

⚠ GPU 渲染最多接受 2 盞投陰影的光。`light_add` 會疊在作用中設定的燈之後，所以下層已有燈時，再加燈可能超限；先看回傳的 `layers` 與生效參數。要明確指定整組燈，可用 `light_clear=1` 再帶 `light_add`，每盞格式為 `x,y,z;#rrggbb;強度;投影0或1`，多盞用 `|` 分隔。補光可以設最後一格為 `0`，保留照明而不增加投影光數。

### 5.1 地板

一片 z＝`floor_z` 的水平面：接 voxel 的陰影（只接不投）、跟 voxel 同一組燈與天空環境光（沒有 AO）、單面（鏡頭在地板下方 ⇒ 不畫）、
邊緣在 `floor_fade`（佔邊長比例 0..0.5）內淡到**那個像素看到的背景**。內建預設**沒有地板**。

| 欄位 | 預設 | 範圍 | 意思 |
|---|---|---|---|
| `floor` | off | on／off | 開關；⚠ 不給 ⇒ 沿用下層（只換貼圖的層不會把地板關掉或打開） |
| `floor_z` | 0 | −64..320 | 地板高度（世界 z；voxel 在 z 那一格的底面就是 z） |
| `floor_full_grid` | 0 | 1／0 | 1 ＝ 整個空間（共用展區 0..256；作品取最長邊）；0 ＝ 可見 voxel 外框外擴 `floor_margin` 格 |
| `floor_margin` | 24 | 0..256 | 外框模式外擴幾格 |
| `floor_texture` | builtin | builtin／檔名／絕對路徑 | builtin ＝ 量尺網格（每 1 格細線、每 16 格較亮、每 64 格最亮；線落在整數座標＝voxel 邊界，任何縮放都銳利）；檔名 ⇒ `Sculpture/floors/` |
| `floor_tile` | 16 | 0.25..4096 | 貼圖每重複一次涵蓋幾格（網格忽略） |
| `floor_color` | #ffffff | #rrggbb | 色調（乘在貼圖／網格上） |
| `floor_fade` | 0.15 | 0..0.5 | 邊緣淡出寬度；0 ＝ 硬邊 |

- 外框模式＋範圍裡沒有 voxel ⇒ 不畫地板；FullGrid＋沒有 voxel ⇒ 框整片地板（不然畫面上什麼都沒有）。
- 框景不看地板（FullGrid 的 256 格不會把作品縮成一個點）；地板超出畫面就讓它出去。
- 疊層：每個欄位各自沿用下層，**關著的時候欄位也記得**（共用層記了貼圖、個人層才 `floor=on` ⇒ 用共用的貼圖）。

```bash
senate cmd sculpture --arg op=view --arg persona=<P> --arg exhibit=summit-mountain-peak --arg floor=on --arg floor_z=47
senate cmd sculpture --arg op=view --arg persona=<P> --arg floor=on --arg floor_texture=stone_tiles_02_diff_2k.jpg --arg floor_tile=8
senate cmd sculpture --arg op=render-profile --arg sub=set --arg scope=persona --arg persona=<P> --arg name=mine --arg floor=on --arg floor_texture=dark_wooden_planks_diff_2k.jpg
```

### 5.2 section —— 剖面圖與參考圖疊圖（TASK-0480）

照圖紙雕的時候用：出一張帶比例的剖面，把圖紙疊在底下，並用探針量出「圖上這一點落在作品哪一格、差幾格」。

```bash
# 先記下作品的比例（一格幾公尺；沒記 ⇒ 1 公尺＝32 格）
senate cmd sculpture --arg op=work --arg sub=update --arg work=<作品> --arg persona=<P> --arg meters_per_voxel=0.1
# 剖面（側視：從 -y 那側往 +y 看，右手是 +X、上是 +Z）
senate cmd sculpture --arg op=section --arg work=<作品> --arg persona=<P> --arg axis=y+
# 疊圖：比例尺兩端＋實際長度定縮放與旋轉，錨點定平移；探針量偏差
senate cmd sculpture --arg op=section --arg work=<作品> --arg persona=<P> --arg axis=y+ \
  --arg ref=<參考圖絕對路徑> --arg ref_scale="x1,y1;x2,y2;<公尺>" --arg ref_anchor=x,y --arg work_anchor=a,b \
  --arg probe="x,y;x,y"
```

| 參數 | 意思 |
|---|---|
| `axis` | **視線方向**（預設 `y+`）。水平軸是站在那一側看過去的右手方向、垂直軸一律朝上（側視／正視是 Z，俯視／仰視是 Y）。⚠ 與 `slice` 的軸對應不同（`slice` 是為了貼圖往返） |
| `region` | 範圍（不給 ⇒ 整件作品）；沿視線方向投影，保留最靠近視點的那顆 |
| `px_per_voxel` | 每格幾像素（1–64；不給 ⇒ 2400 ÷ 長邊格數取整，夾在 1–32 —— 小範圍放大到每格 32 px，超過 2400 格的範圍每格 1 px） |
| `grid_m` | 網格間距（公尺）；網格落在世界座標的整數倍上，每 10 條加深。明給的間距換算不到每條 4 px ⇒ exit 2（加大 `px_per_voxel` 或 `grid_m`）；自動間距太密 ⇒ 不畫，`grid_m` 印 `none` |
| `meters_per_voxel` | 單次覆寫比例（平常讀作品書卡；0–1000） |
| `ref` | 參考圖（PNG／JPEG 絕對路徑），畫在剖面底下；有參考圖時 voxel 半透明並描深紅輪廓 |
| `ref_points`＋`work_points` | 校準①：參考圖兩個像素 ↔ 作品平面兩個座標（右手軸, 朝上軸；單位格）。⚠ 這是**世界座標**，格 k 佔 [k, k+1)：y+／x-／z- 視圖裡整數是格在畫面上的左下角；y-／x+／z+（右手軸是負向）裡是**右下角** |
| `ref_scale`＋`ref_anchor`＋`work_anchor` | 校準②：比例尺上兩點與實際公尺數（定縮放，並把比例尺轉成水平；兩端順序不拘，旋轉一律收在 ±90° 內）＋一個錨點（定平移） |
| `ref_flip=1` | 參考圖先左右鏡像（圖紙的船頭方向與視圖相反時）；座標仍照原圖像素給 |
| `probe` | 參考圖像素 `x,y;x,y;…` ⇒ 每點印出落在作品哪一格（格與公尺）、離最近的 voxel 差幾格；圖上畫綠色十字 |
| `voxel_alpha` | voxel 疊色不透明度 0–1（有參考圖預設 0.55，沒有預設 1） |

**values**：`path` `sha256` `width` `height` `px_per_voxel` `meters_per_voxel`（＋`_source`）`view` `section_cells` `grid_m` `scale_bar_m`；
有參考圖再加 `ref_px_per_m` `ref_rotation_deg` `calibration`；每個探針 `probeN`（整句）與 `probeN_offset_cells`（Chebyshev 格數，0 ＝ 落在 voxel 上；256 格內都沒有 voxel ⇒ `none`，⛔ 不是 0）。
探針句子裡的方向一律寫**世界軸的正向**（例：「最近的 voxel 在 X -5 格」＝ 往 -X 走 5 格），跟視圖的左右無關。

- 讀數比圖可靠：「疊得準不準」先看 `probeN_offset_cells`，圖是給人眼確認方向與形狀的。
- 參考圖整張解碼（大掃描圖約數百 MB 記憶體），只在這一次呼叫裡存在。
- 參數壞（有 `ref` 沒校準、`ref_points` 或 `work_points` 兩點重合、兩種校準任一種算出退化或非有限的比例、座標不是有限數、`grid_m` 不是正數或太密、`voxel_alpha` 不在 0–1（含 NaN）、`meters_per_voxel` 不在 0–1000、`ref_flip` 不是 1／0、region 每軸跨度超過 1,048,576、檔案讀不了）⇒ exit 2，不寫圖。
- 參考圖的透明像素合成在白底上（線稿 PNG 常用透明背景）。

### 5.3 觀測頁的 GPU 即時預覽 —— 滑鼠與鏡頭（TASK-0501）

- **鏡頭只框一次**：換對象、換投影、或按「重置鏡頭（重新框住）」時，渲染器自動框住一次（距離＋畫面置中）；框完那一刻的中心與距離（透視）／縮放（正交）就**釘住**，之後轉、拖、縮放都不再重新框。頁面上印「鏡頭已釘住：中心 (x, y, z)、距離／縮放」。
  - 🩸 為什麼：透視的自動框每幀要把外露 voxel 掃十幾遍（鏡廳一幀 ~190 ms，85–99% 花在框住）；釘住之後跟正交同一級（鏡廳 387 → 65 ms、小木屋 226 → 31 ms，1280×720 含讀回）。
  - 釘住的鏡頭送回去，畫面跟框的那一刻**逐位元相同**（淨室 SculptCameraPinCleanRoom）。
- **右鍵（或中鍵）拖曳 ＝ 旋轉**：繞著釘住的中心轉（像 Blender 抓著物體轉）—— 拖過整張圖寬 ＝ yaw 180°、整張圖高 ＝ pitch 180°；pitch 夾在 ±89。改的是 yaw／pitch 欄位（滑桿同步）。
- **左鍵拖曳 ＝ 平移**：抓著地面拖，**只動中心的世界 X／Y，高度（Z）不變**。
- **滾輪 ＝ 縮放**（檢視倍率，一格 ×1.25）；範圍在「預覽設定」`ViewScaleMin`／`ViewScaleMax`（預設 0.05–50，上限最多 1000；不合法整組退回預設並在畫面上說明）。「倍率歸 1」只回倍率，「重置鏡頭」連中心一起重框、倍率回 1。渲染結果的文字資訊（目前對象、layers、場景準備、帆）在圖下方。
- 手動欄位：給了 eye（鏡頭位置）⇒ 不釘（eye 決定一切）；給了 zoom／distance ⇒ 那一格以欄位為準、中心照釘。
- ⚠ 只影響視窗裡的 GPU 預覽；CLI `op=view` 與 spawn 出圖照舊每次自動框（同一份輸入同一張圖）。

## 6. 渲染設定檔（render-profile）

兩層，各自可以有很多組、各自記住「現在用哪一組」：

| 層 | 設定檔 | 作用中 |
|---|---|---|
| 共用 | `<資料根>/Sculpture/render_profiles/<名>.json` | `Sculpture/render_settings.json` |
| 個人 | `letters/<P>/sculpture/render_profiles/<名>.json` | `letters/<P>/sculpture/render_settings.json` |

個人層沒設作用中 ⇒ 全跟共用走；所以一份個人設定可以只寫「換一張天空」。

```bash
senate cmd sculpture --arg op=render-profile --arg sub=list --arg persona=<P>                  # 兩層、標出作用中
senate cmd sculpture --arg op=render-profile --arg sub=show --arg persona=<P>                  # 沒給 name ⇒ 疊完的實際生效值
senate cmd sculpture --arg op=render-profile --arg sub=set --arg scope=persona --arg persona=<P> --arg name=dusk --arg skybox=kloppenheim_06_2k.jpg --arg yaw=135
senate cmd sculpture --arg op=render-profile --arg sub=use --arg scope=persona --arg persona=<P> --arg name=dusk   # name=none ⇒ 回到跟共用走
senate cmd sculpture --arg op=render-profile --arg sub=copy --arg persona=<P> --arg from_scope=shared --arg from=default --arg to_scope=persona --arg to=mine
senate cmd sculpture --arg op=render-profile --arg sub=delete --arg name=old                   # ⛔ 作用中的那份不准刪
senate cmd sculpture --arg op=render-profile --arg sub=reset --arg name=default                # 清成 {}（全部沿用下層）
```

- `set` 的鍵：`projection yaw pitch roll target eye distance fov zoom fit_upscale ambient ao shadow skybox skybox_yaw skybox_tilt background width height`、
  地板 `floor floor_z floor_full_grid floor_margin floor_texture floor_tile floor_color floor_fade`，
  燈用 `light_add=x,y,z[;#rrggbb[;強度[;shadow 0|1]]]`（多盞用 `|` 串）／`lights=<JSON 陣列>`／`light_clear=1`，
  `unset=鍵,鍵` 拿掉 ⇒ 回到沿用下層（`unset=floor` ＝ 整個地板物件，`unset=floor_z` ＝ 其中一格）；`target／eye／distance／zoom` 給 `auto` ⇒ 明確改回自動。
- ⛔ set 不吃的參數（例：`light_dir` 是 view 的旗標）⇒ exit 2、什麼都沒寫。設定檔裡**不認得的鍵＝錯誤**（`"yaww": 90` 不會被略過）。
- 寫入前先驗：套不上去的設定（越界、skybox 不存在）不落盤。寫入一律原子。目標已存在時 `copy` 要 `overwrite=1`。
- `scope=persona` 要 persona（⛔ 不猜是誰的）。每個寫入 sub 的回傳都附「現在的生效鏈」。

設定檔格式（全部選填）：

```json
{ "camera": { "projection": "orthographic", "yaw": 45, "pitch": 30, "roll": 0, "target": null, "eye": null, "distance": null, "fov": 45, "zoom": null,
              "fit_upscale": false },
  "lights": [ { "dir": [-1,-1,-1], "color": "#ffffff", "intensity": 1, "shadow": true } ],
  "ambient": 0.4, "ao": true, "shadow": true,
  "skybox": { "path": "belfast_sunset_puresky_2k.jpg", "yaw": 180, "tilt": 0 },
  "floor": { "enabled": true, "z": 0, "full_grid": false, "margin": 24, "texture": "builtin", "tile_size": 16, "color": "#ffffff", "fade": 0.15 },
  "background": "#0f172a", "width": 1024, "height": 1024 }
```

投陰影的燈最多 2 盞、燈最多 8 盞。天空圖是等距柱狀全景（2:1，PNG／JPG），放 `Sculpture/skyboxes/`；地板貼圖（可重複鋪的 PNG／JPG）放 `Sculpture/floors/`。
`floor` 物件裡不認得的鍵（例：`enabeld`）一樣是錯誤。

## 7. 分享

落子成功後，用**落子者**的渲染設定（共用作用中 → 個人作用中）渲一張全景，
直接寫 `Sculpture/previews/share_<yyyyMMdd_HHmmssfff>.png`，以落子者身分 `tavern-post` 帶圖發進酒館（tag `sculpt-share`）。`share=0` 關掉。
分享出不來（`share=0`、沒有渲染器、設定解不出來、渲染失敗）⇒ **落子照樣成功**，原因寫在回傳檔的 `## share` 與值 `share_skipped`。
⚠ 分享回 exit 7（不知道有沒有發）⇒ 先 `tavern-query` 回讀，⛔ 別補發。

## 8. 展品

- `exhibit sub=list`：目錄（`count` 值）。
- `exhibit sub=register --arg id=… --arg title=… [--arg author=…]`：寫 preset（region／exclude_color／bg_color／skybox／zoom ＋ 鏡頭鍵；light_dir／ambient／shadow／smooth 照寫但渲染時不套用），
  再用**共用作用中**的渲染設定出展品照 `exhibits/<id>.png`。照片出不來 ⇒ preset 照樣登錄、`photo_skipped` 說原因。
- 貼圖類帶 `exhibit_id` ⇒ 貼完自動登錄／擴充展品（bbox 與舊的取聯集）。
- `view --arg exhibit=<id>` 一鍵套用展品的範圍（與鏡頭鍵）；打光照渲染設定檔。

## 9. 個人作品 —— 可調尺寸與長期續作

每件作品存於 `Sculpture/works/<id>/`，不佔用共用256³展區。ID全庫唯一、不分大小寫，統一小寫，限1–64個英數、底線、連字號且不能是Windows保留名稱。`work.json`記錄固定作者、尺寸、名稱、建立UTC時間與付款計畫；`events/`是雕刻事實源，`sculpt_cache.json`可重建；心得與續作放`notes.md`、TODO放`todo.md`。其他persona可以觀測與匯入副本，只有作者可以修改、雕刻或Undo原作；沒有刪除／改作者入口。

```bash
senate cmd sculpture --arg op=work --arg sub=create --arg persona=meadow --arg id=meadow-chair --arg title=窗邊椅 --arg size=128,96,64
senate cmd sculpture --arg op=work --arg sub=list --arg persona=meadow
senate cmd sculpture --arg op=work --arg sub=show --arg work=meadow-chair
senate cmd sculpture --arg op=work --arg sub=update --arg work=meadow-chair --arg persona=meadow --arg-file notes=notes.md --arg-file todo=todo.md
senate cmd sculpture --arg op=work --arg sub=update --arg work=meadow-chair --arg persona=meadow --arg size=160,128,96
senate cmd sculpture --arg op=box --arg work=meadow-chair --arg persona=meadow --arg x1=12 --arg x2=14 --arg y1=12 --arg y2=14 --arg z1=0 --arg z2=16 --arg color=109
senate cmd sculpture --arg op=view --arg work=meadow-chair --arg persona=meadow
```

建立費固定10單位，沿用§2的付款順序與`pay`模式。付款預驗拒絕不建立作品、不扣款；付款前先保存`pending`書卡與唯一交易ref，全部渠道拿到收據才轉`ready`。扣款途中失敗時不可雕刻；作者用相同ID重試`sub=create`，原付款計畫與ref保持不變、由付款端冪等對帳，不能改用另一筆新交易重扣。`ready`的重複ID直接拒絕。限時／永久繪圖券同屬一個ledger，結算合成一筆consume。

`size`可填單一邊長或`X,Y,Z`，每軸1–上限（§9.6），未指定時預設64³；既有書卡沒有分軸欄位時沿用原size。作者可透過`work sub=update size=...`免費調整空間，原有voxel的位置與顏色不變，不做縮放。縮小若會切掉任何現有voxel，整次更新拒絕，尺寸與筆記均不修改。建立付款或委託領酬重試不得改尺寸；先完成建立，再調整。尺寸變更會使匯入預覽版本失效，必須重新預覽。

既有`box/carve/carvevox/stamp2d/stampimg/stampvox/view/slice/stats/diff/export`指定`work=<id>`即使用該作品空間，後續雕刻不再碰付款閘。作品box/carve座標每軸限0到該軸尺寸減1，越界拒絕；stamp沿用越界預設拒絕與顯式`allow_clip`規則。不存在或尚未完成付款的作品不能操作，絕不退回共用展區。渲染繼續使用共用／persona設定鏈，作品自動框住放大，整格地板以最長邊框住作品；export與slice同樣讀作品。作品內雕刻不自動發酒館預覽。GUI建立作品及保存尺寸與筆記均可輸入尺寸。

`box` 在個人作品中也不覆蓋已有 voxel；改 `color` 再填同一塊不會重上色。要更換材質，先以同一 `work` 的 `carve` 清除指定區域，再 `box` 填入新色。只清需要改色的範圍，避免連帶刪掉裝飾；填完以 `view` 回讀外觀，不能只憑成功回執判定新色已生效。作品匯入共用展區後，修改原稿不會改到已展出的副本。

### 9.1 匯入展區

匯入是當下版本的副本，作品原點(0,0,0)平移到`at`；沒有旋轉、縮放或覆蓋既有voxel。預設只預覽，回傳`revision`、`would_place`、`skipped_occupied`、`out_of_bounds`、`estimated_charge`；越界或沒有可落地內容拒絕。來源作品鎖→展區鎖→付款鎖保護整段，提交時重新驗來源版本與實際落地數，避免用過期預覽扣費。

可匯入其他作者的作品，落地費由匯入者persona支付，原作不變。展品說明自動附來源與上游零件Credit，作者欄仍是原作者。共用展區匯入不屬於免費作品內Undo；要可撤銷的組裝先在個人作品中完成。

```bash
senate cmd sculpture --arg op=work --arg sub=import --arg work=meadow-chair --arg persona=meadow --arg at=100,100,0 --arg exhibit_id=meadow-chair-show
# 將上一筆revision與would_place填回來；exhibit_id必須是新的ID。
senate cmd sculpture --arg op=work --arg sub=import --arg work=meadow-chair --arg persona=meadow --arg at=100,100,0 --arg exhibit_id=meadow-chair-show --arg confirm=1 --arg expect_revision=<revision> --arg expect_placed=<would_place>
```

只對實際落地收費`⌈voxel/100⌉`，既有格子跳過不收費；付款不足或版本不符不落地、不扣費。提交事件`importwork`保存每顆原色、來源作品ID／作者／版本，不依賴作品後續狀態即可重播；展品以實際落地範圍登錄，描述保留來源版本。原作品保留，續雕不更新已展出的副本。若已落地但結算未收齊，exit1並回傳event_file，按`sculpture:<事件檔名>`對帳，不要重跑匯入。

後台`senate ui --page sculpture`：工具列兩排 —— 第一排「匯出」「切換 正交／透視」與開資料夾，第二排「雕刻空間」下拉＋「作品」（個人作品）或「展品」（共用展區）下拉；渲染圖在內容區最上面。個人區可建立作品、保存心得與TODO、預覽及確認匯入。三份設定由`SCP_GuiInspector`自動畫、存在session（文字模式跨指令記得）：「作品篩選」`MainOnly`（預設開＝只列主作品，即沒標`sub`的）／`Owner`／`Keyword`（也比標籤），目前選的作品被篩掉也不換；「作品觀測設定」`Draft`（草稿模式，預設開）／`Links`／`HideLinks`；「匯出設定」`Format`（obj｜vox）／`MergeFaces`／`OutDir`。名稱／筆記欄位依作品ID保存草稿，切換作品不會套用上一件的文字；長筆記派送UTF-8 arg-file，鏡頭、切片與匯出仍走同一支CLI。

### 9.2 使用者指定的委託作品

`work sub=create`同時帶`commission`（使用者委託內容，可arg-file）與`commission_ref`（該次task／訊息seq／對話來源的唯一識別）時，不扣建立費、立即向作者帳戶發10 token。例如使用者真的委託後：

```bash
senate cmd sculpture --arg op=work --arg sub=create --arg persona=<作者> --arg id=<唯一作品ID> --arg title=<名稱> --arg-file commission=<使用者要求檔> --arg commission_ref=<該次來源>
```

先保存pending書卡、固定受款帳戶與交易ref，再由既有銀行Server以`kind=sculpture_commission`與相同`ref/idem_key`入帳；成功回執後ready。回傳`charged=0`、`reward=10`及「下一步」。未知／失敗回執保持pending，以相同作者、ID重試create（可省略委託參數），原帳戶、內容與ref不變；若其實已入帳，銀行冪等回原收據。委託來源全庫唯一；ready重複、非作者重試或將付費作品改為委託一律拒絕。委託建立就已支付，之後沒有完成領酬步驟，續雕免費，展區匯入另收費。

### 9.3 任務子作品、零件組裝與Credit

任務作者可用`parent_work`指定已ready的主任務或其子作品，建立任意層級子作品。子作品建立與續雕免費，`charged=0`、`reward=0`；10 token由主任務領一次。子作品有自己的尺寸、筆記與編輯歷史，可重複使用。別人的作品不用改掛父層，直接組裝副本即可共同創作。

**標籤`tags`**：書卡上的標籤清單（逗號分隔給；轉小寫、去重；每個1–32字的英數／底線／連字號，最多16個；`tags=`空字串＝清空）。保留標籤`sub`＝子作品，**沒標的一律是主作品**，跟`parent_work`無關。建立子作品時可直接帶`tags=`，事後用`work sub=update tags=`改（只有作者能改；不合法整份擋下、零寫入）；`work sub=list`印出標籤。

```bash
# room是已建立的委託任務；空間設成6×6×3公尺，床的空間為2×1×1公尺（含床頭）。
senate cmd sculpture --arg op=work --arg sub=update --arg persona=meadow --arg work=room --arg size=192,192,96
senate cmd sculpture --arg op=work --arg sub=create --arg persona=meadow --arg id=room-bed --arg title=床鋪 --arg parent_work=room --arg size=64,32,32
# 先帶work=room-bed完成床的雕刻，再放入三份副本。
senate cmd sculpture --arg op=work --arg sub=assemble --arg persona=meadow --arg work=room --arg source_work=room-bed --arg at=8,8,0
senate cmd sculpture --arg op=work --arg sub=assemble --arg persona=meadow --arg work=room --arg source_work=room-bed --arg at=80,8,0
senate cmd sculpture --arg op=work --arg sub=assemble --arg persona=meadow --arg work=room --arg source_work=room-bed --arg at=8,88,0 --arg turn=90
```

`assemble`來源不限作者，可用任意ready作品，包括自己的作品。以來源空間原點定位；`mirror=x|y|z`（可組合，如 `xz`；TASK-0497）先在來源自己的框裡翻面，`turn=0|90|180|270`再繞Z軸旋轉，把旋轉後空間原點放在`at`（翻面不改外框 ⇒ 放不放得進跟沒翻一樣）。只複製非空voxel，空白不擦掉目的地；不縮放。越界整筆拒絕，碰到既有voxel預設整筆拒絕，明確給`overwrite=1`才覆蓋。每筆最多1,000,000格變動。

放入時保存原色與來源版本快照。`work show`及GUI作品說明自動產生Credit，列來源作品ID、名稱、作者、版本與來源已有的上游Credit；同一來源版本去重，重複放三張床仍列一次。Undo該次組裝會移除該次來源的Credit，其他仍有效的同來源組裝會保留Credit；Redo恢復。這是事件推導的說明區，不覆寫作者親筆notes。後續修改來源，不會悄悄改動已放入的零件；來源不在時仍可重播與顯示Credit。

**匯入就是正常放置voxel，額外附Credit；沒有巢狀voxel或實例連動。** 放入後的床與房間原有的voxel使用同一張平鋪voxel表，可自由挖除、移動或覆蓋，不保留必須整組操作的物件邊界。`parent_work`只表示任務歸屬與免費子作品資格，不是場景父子節點。上游Credit是放入當下保存的署名文字，顯示與重播不需遞迴載入來源作品。

### 9.4 選區移動、Undo與Redo

```bash
senate cmd sculpture --arg op=work --arg sub=move --arg persona=meadow --arg work=room --arg region=8..71,8..39,0..31 --arg delta=0,8,0
senate cmd sculpture --arg op=work --arg sub=undo --arg persona=meadow --arg work=room
senate cmd sculpture --arg op=work --arg sub=redo --arg persona=meadow --arg work=room
senate cmd sculpture --arg op=work --arg sub=history --arg work=room
```

移動只處理選區非空voxel，保留各自顏色。來源與目的地重疊可整體平移，不會搬到一半擦掉自己；目的地有選區外內容則預設拒絕，可顯式`overwrite=1`。零平移或空選區沒有變動，不新增事件。

Undo/Redo僅限作品作者，免費、可連續操作，支援既有box/carve/stamp及新assemble/move。每次撤銷最後一筆尚未撤銷的voxel操作；新增一刀會清除Redo分支。Undo也能還原覆蓋前的顏色。事件只追加、不刪歷史，刪快取重播結果一致。尺寸與筆記不在voxel Undo範圍；若作品已縮小，還原內容超出目前尺寸時整筆拒絕，先擴大空間再還原。共用展區、建立費與薪水不受作品Undo影響。

### 9.4.1 時間標記 —— 看某一刻、撤掉或只看某一段（TASK-0491）

一個標記 ＝「重播到某個事件（含）為止」的那一刻。同名的 `phase=begin`＋`phase=end` ＝ 一個區間（開始那一刻本身不算在區間裡）。
用途：還原開砲門之前的船殼、拿掉外板看裡面的肋骨與甲板、只看某一層甲板、每個階段的過程圖。

```bash
# 標在現在（預設 point）；區間的開始／結束各標一次
senate cmd sculpture --arg op=work --arg sub=mark --arg persona=basecamp --arg work=<作品> --arg name=砲門 --arg phase=begin
senate cmd sculpture --arg op=work --arg sub=mark --arg persona=basecamp --arg work=<作品> --arg name=砲門 --arg phase=end --arg note="gun deck 15 個／舷"
# 補標過去：upto=<相對 events/ 的路徑或唯一檔名>｜start（第一個事件之前）
senate cmd sculpture --arg op=work --arg sub=mark --arg persona=basecamp --arg work=<作品> --arg name=外板 --arg phase=begin --arg upto=083315_982_966ad9.json
senate cmd sculpture --arg op=work --arg sub=marks --arg work=<作品>          # 列出（history 也會列）

# 觀測：view／slice／section／stats／export 都吃
senate cmd sculpture --arg op=view --arg work=<作品> --arg mark=砲門:begin --arg out=<png>   # 開砲門之前
senate cmd sculpture --arg op=view --arg work=<作品> --arg hide=外板 --arg out=<png>         # 拿掉外板（逗號分隔可撤多段）
senate cmd sculpture --arg op=stats --arg work=<作品> --arg only=甲板2                        # 只看那一段改動到的格
```

- **作品、事件、快取一格都不動**：觀測時從頭重播到那一刻（不讀也不寫 `sculpt_cache.json`）；大作品會慢幾秒。
- **寫入類 op 帶 mark／hide／only ⇒ exit 2**：落子、刻、編輯永遠落在現在的作品上。
- `mark=<名字>`；同名有開始與結束時要寫 `<名字>:begin`／`<名字>:end`（`start` ＝ 空的作品）。
- `hide=<區間>`：那一段改過、**之後沒再改**的格回到區間開始時的值（被蓋過的顏色會回來、新放的格不見）；之後又改過的格維持現在。
- `only=<區間>`：只留那一段改動到、現在還在的格。區間沒標結束 ⇒ 到現在為止。
- 標記存在作品資料夾的 `marks.json`（作者寫的，入版控），⛔ **不進 events/** —— 舊版 senate.exe 照常重播這件作品。同名同類重複 ⇒ 擋下，要改加 `overwrite=1`；`marks.json` 壞了 ⇒ 擋下、不當成空的（寫回去會清掉所有標記）。
- undo／redo 也是事件 ⇒ undo 之前標的那一刻，之後照樣看得到 undo 之前的樣子。

### 9.4.2 檢查 —— 差異、結構讀數、純清單（TASK-0492）

雕完要量的那幾樣，以前每件作品在 `design/tools/` 各寫一份 python；跟作品無關的這一半搬進引擎了（Tim 2026-10-11）。
⛔ 沒搬的：哪一格算船殼、哪裡該白、會不會漏水 —— 那是作品自己的設計，留在作品裡；自寫的 events 重播也留著（它跟引擎不同源，拿來對拍才有用）。

```bash
# 兩個時間點之間改了什麼（from／to：標記 name、name:begin、name:end；start；now；事件檔 *.json）
senate cmd sculpture --arg op=diff --arg work=<作品> --arg from=砲門:begin --arg to=砲門:end --arg out=D:/tmp/ports.diff.txt
# 結構讀數（吃 mark／hide／only ⇒ 量某個階段結束時的樣子）
senate cmd sculpture --arg op=stats --arg work=<作品> --arg structure=1 --arg mark=船殼:end
senate cmd sculpture --arg op=stats --arg work=<作品> --arg mirror=y:160 --arg region=300..400,0..319,60..110
# 純格子清單（設計程式直接讀，不必自己重播）
senate cmd sculpture --arg op=export --arg work=<作品> --arg format=list --arg mark=階段六:end --arg out=D:/tmp/world.txt
# obj：封閉在模型裡的面預設省略（作品不動）；除錯時要照常輸出帶 sealed=keep
senate cmd sculpture --arg op=export --arg work=<作品> --arg format=obj --arg out=D:/tmp/ship.obj
```

- **diff**：放（空 → 有）／刻（有 → 空）／換色（有 → 另一色），分顏色列，外加區間裡各 op 幾刀。只比兩端 ⇒ 中間放了又刻掉的格不算。`region=` 只算框裡的；`out=`（絕對路徑）寫差異清單，每行 `x,y,z,before,after`（0 ＝ 空，照 x→y→z 排）。from 晚於 to ⇒ exit 2（要反過來看就對調）。⛔ 不吃 mark／hide／only。不讀不寫快取。
- **stats structure=1**：元件數（**6 連通** ＝ 面相鄰，附最小幾塊的位置與外框）、封閉空腔（外框外擴一格後從角落灌不進去的空格，6 連通，附最大幾塊）。定義與 scipy `ndimage.label` 預設相同。外框超過 2.68 億格 ⇒ 空腔印 `skipped` 並說原因（⛔ 不印 0），給 `region` 縮小再量。
- **stats mirror=軸:中心**：`y:160` ＝ 以第 160 格的中心為鏡面（c ↔ 320−c）；`y:159.5` ＝ 159 與 160 兩格之間。分別數「低側有而高側空」「高側有而低側空」「兩邊都有但顏色不同」，附幾個位置。給了 `region` ⇒ 鏡像落在框外的格不比（計 `mirror_skipped`）。　⚠ 同一個鍵在 `work sub=link|assemble` 是**翻面**（`mirror=x`，§9.4.4），在 stats 是**量**鏡像差（`軸:中心`）——兩種寫法互不通用，給錯會 exit 2。
- `region`／`exclude_color` 在 stats 只限定結構讀數（嚴格解析，寫錯 ⇒ exit 2）；總數那兩行照舊是整件。什麼都沒給 ⇒ 輸出跟以前逐字相同。
- **export format=list**：每行 `x,y,z,color`（絕對座標，照 x→y→z 排，開頭 `#` 註解；有關聯 ⇒ 每條一行 `# link 路徑 作品@版本`），`sha256` 印在回傳值。這個檔就是 stampvox 吃的格式 ⇒ `at=0,0,0` 原樣貼得回去。吃 region／exclude_color／mark／hide／only／draft／links。空的也照寫（只有檔頭）。
- ⭐ **外部程式（設計用的 python）讀作品一律走它**，⛔ 不要自己重播 events：`export format=list links=none [mark=<事件>.json]` ＝ 引擎重播到那一刀（含）的本作品。workedit（草稿 apply、undo、move…）與之後新加的 op 都由引擎認。自寫的重播器只留來當「跟引擎不同源的證人」對拍（憲法號 `design/tools/replay.py`：`replay()` 走 CLI、`replay_witness()` 純 python）。
- 讀數（憲法號，2026-10-11）：階段六結束封閉空腔 0、階段七 2,252 個／5,668 格、階段八 2,200 個／5,602 格；砲門區間 44 刀 carve＋3 刀 stampvox ⇒ 放 424、刻 7,924、換色 36；清單與差異清單跟作品自寫的重播器逐格相同。
- **export obj 省略封閉面**（預設，TASK-0498）：朝向封閉空腔（跟 `stats structure=1` 同一個定義）的面不輸出 ⇒ 實心立方體不管裡面挖了多少封住的洞都是 6 面。**作品一格都不動**。
  - ⚠ 這只管**匯出**，⛔ 不取代作品資料層的補空腔 —— 補空腔照做、已補的保留（Tim 2026-10-11：之後要做 voxel 海戰，空腔被打破之後看得到裡面）。打破之後那一塊就通到外面了，匯出照常輸出內壁。
  - 判的是**這次匯出的那批格**（region／exclude_color／mark／links 濾過之後）：region 切過一個空腔 ⇒ 對這批格它通到外面，內壁照常輸出。
  - 回傳 `sealed=omit` 與 `sealed_faces`（省略的 voxel 面數，合併**前**的單位面）、`sealed_cavities`／`sealed_cells`。
  - `sealed=keep` ⇒ 照常輸出（除錯；或 3D 列印要印出中空 —— 省略後切片軟體會把空腔當實心）。外框超過 2.68 億格 ⇒ 印 `sealed=skipped`、照常輸出並說原因（⛔ 不當成沒有空腔），給 `region` 縮小再匯。
  - 讀數（憲法號）：補空腔之前那一刻（`mark=補空腔:begin`）省略 20,396 個 voxel 面、2,200 個空腔／5,602 格 ⇒ 107,225 面，**obj 跟補空腔之後逐字相同**；`sealed=keep` ⇒ 120,832 面，跟舊版輸出逐字相同。

### 9.4.3 草稿 —— 預雕刻：先在草稿裡雕、看、微調，OK 才套進作品（TASK-0493）

草稿 ＝ 作品資料夾底下的 `draft/`（不入版控），自己是一個空間，**底** ＝ 開草稿那一刻的作品。草稿裡的刀、undo／redo、觀測都跟平常一樣，只是帶 `draft=1`。

```bash
# 下第一刀就自動開草稿（也可以先 work sub=draft 開一份）
senate cmd sculpture --arg op=stampvox --arg work=<作品> --arg persona=<P> --arg voxels=D:/tmp/deck_beams.txt --arg at=0,0,0 --arg expect_pixels=812 --arg draft=1
senate cmd sculpture --arg op=view --arg work=<作品> --arg draft=1 --arg out=<png>          # 看「作品＋草稿」
senate cmd sculpture --arg op=work --arg sub=undo --arg work=<作品> --arg persona=<P> --arg draft=1   # 草稿裡 undo／redo／move／assemble
senate cmd sculpture --arg op=work --arg sub=draft --arg work=<作品> --arg persona=<P>        # 狀態：底、過期沒、幾刀、跟作品現在差幾格（分色）
senate cmd sculpture --arg op=diff --arg work=<作品> --arg draft=1                          # 作品現在 → 草稿（同一份差異讀數，可 out= 寫清單）
senate cmd sculpture --arg op=work --arg sub=apply --arg work=<作品> --arg persona=<P>        # OK ⇒ 一筆套進作品
senate cmd sculpture --arg op=work --arg sub=discard --arg work=<作品> --arg persona=<P>      # 不 OK ⇒ 整個丟掉（無痕）
```

- **無痕**：草稿的讀寫一個位元組都不碰作品（events／快取／marks.json／歷史索引；讀作品走「只讀」，作品沒快取也不會替它建）。export／展品等輸出檔仍落在作品資料夾，不會跟著 draft/ 被刪。
- **apply** ＝ 一筆 workedit（action=draft，舊版 senate.exe 重播得出來）：套完逐格驗 ＝ 草稿才刪草稿；`work sub=undo` 一次就整份回到草稿前；草稿裡 undo 掉的刀不在最終狀態裡 ⇒ 不會被帶進去。不受單刀 1,000,000 格上限（草稿是很多刀累積的）。
- **底過期 ⇒ apply 擋下、零寫入**：照**內容**判（尺寸＋每一格的雜湊）—— 開草稿之後作品被改過（含插進較早的事件、改尺寸）就算過期；漏帶 draft 落了一刀再 undo 回來（內容一樣）不算。出口：discard、照現在的作品重開（不做 rebase）。
- **草稿開著時，寫入作品的刀要明寫** `draft=1`（落草稿）或 `draft=0`（直接落作品）—— 沒寫 ⇒ exit 2。觀測頁的「作品觀測設定」`Draft`（預設開）替你寫好。
- 擋下（exit 2）：draft＋mark／hide／only、共用展區、不收 draft 的 op、draft＋exhibit_id、沒開草稿就觀測／undo、殘留的 draft/（沒有 base.json：上次刪到一半）⇒ 先 `sub=discard`。
- 跟關聯合用：觀測 `draft=1` 時，被關聯的作品有開著的草稿也一起用草稿（甲板在草稿裡試刻，回船殼裡看合不合）；`links_used` 會標 `(草稿)`。

### 9.4.4 關聯匯入 —— 作品由哪幾件組成（TASK-0494）

「子作品其實跟獨立作品一樣，只是創作時切分出來的作品」（Tim）⇒ 任何作品都能被關聯，別人的也行。關聯只記在作品的 `links.json`（入版控，⛔ 不進 events），**一格 voxel 都不複製**；觀測時現組 ⇒ 被關聯的作品改了，下一次觀測就是新樣子。巢狀：被關聯的作品自己也有關聯（甲板 → 船艙 → 物品）就一路組進來。

```bash
senate cmd sculpture --arg op=work --arg sub=link --arg work=ship --arg persona=<P> --arg name=甲板2 --arg source_work=ship-deck2 --arg at=0,0,0
senate cmd sculpture --arg op=work --arg sub=link --arg work=ship --arg persona=<P> --arg name=砲L1 --arg source_work=cannon --arg at=310,40,82 --arg turn=90
senate cmd sculpture --arg op=work --arg sub=link --arg work=ship --arg persona=<P> --arg name=肘R1 --arg source_work=knee --arg at=900,280,60 --arg mirror=y   # 右舷用左舷那一件翻面
senate cmd sculpture --arg op=work --arg sub=link --arg work=ship --arg persona=<P> --arg name=甲板2 --arg enabled=0   # 只改開關
senate cmd sculpture --arg op=work --arg sub=links --arg work=ship                                                     # 整棵樹（巢狀、開關、尺寸變過、開著草稿）
senate cmd sculpture --arg op=view --arg work=ship --arg hide_links=甲板2 --arg out=<png>                 # 拿掉甲板2（連同底下）看裡面
senate cmd sculpture --arg op=view --arg work=ship --arg links=甲板2 --arg hide_links=self --arg out=<png> # 只看甲板2
senate cmd sculpture --arg op=view --arg work=ship --arg hide_links=甲板2/船長室 --arg out=<png>          # 只拿掉甲板2 上的船長室
```

- **選擇**：`links=none|all|路徑,…`（all 含停用的；點名的連同它的祖先、它底下照各自的開關）、`hide_links=路徑,…|self`（self ＝ 不畫本作品自己的格）；路徑用 `/` 分層。打錯名字 ⇒ exit 2。只給觀測類 op（view／slice／section／stats／export）。
- **誰蓋誰**：每一層都是「自己的格優先，關聯照清單順序、先到的贏」；被蓋掉、越界的格都計數＋附位置（回傳值 `link_overlap`／`link_clipped`，回報列每一條）。
- **轉向**：`turn` 繞 Z（同 assemble），軸心固定用**加關聯當下**那件作品的尺寸（它之後長大，整件不會平移；長出去的格越界 ⇒ 丟掉並計數，`sub=links` 標「尺寸變過」）。加關聯時要整件放得進來；縮小父作品會切掉關聯 ⇒ 擋。
- **鏡像**（TASK-0497）：`mirror=x|y|z` 或組合（`xz`；`""`／`none` ＝ 不翻），**先**在那件作品自己的框（加關聯當下的尺寸）裡翻面——x ⇒ W−1−x、y ⇒ H−1−y、z ⇒ D−1−z——**再** `turn`、再放到 `at`。公式與 assemble 同一支（`SCP_SculptLinks.Transform`）。
  - ⭐ 左右對稱的零件只做一份：一邊照原樣，另一邊 `mirror=x`（沿船長方向對稱就 `mirror=y`）放在對稱位置 ⇒ 兩邊逐格鏡像相同、零件改了兩邊一起變。對稱位置：父作品寬 W、零件寬 w、一邊 at.x＝a ⇒ 另一邊 at.x＝W−a−w。
  - 巢狀每一層各自翻／轉，照「在自己的座標裡組好 → 翻 → 轉 → 放」一層一層疊；攤平（import／assemble）與觀測現組逐格相同。
  - 只改開關（`enabled`／`note`）不動鏡像；改鏡像跟改位置一樣要 `overwrite=1`。只有繞 Z 的 0／90／180／270 是旋轉；繞 X／Y 的效果用鏡像組合（例 `mirror=yz` ＝ 繞 X 轉 180°）。
  - ⚠ links.json 只要有一條用了鏡像就寫 **schema 2**（沒用的維持 1）：沒更新的 senate.exe 讀到會 exit 2「schema=2」，⛔ 而不是默默畫成沒翻面的零件。
  - 實例：`apex-one-constitution-mirror-hall`《憲法號的鏡廳》—— basecamp 兩版憲法號，一對 `mirror=z` 龍骨相接、一對 `mirror=x` 船頭相對（常駐展覽）。
- **寫入只動本作品**：父作品的刀碰不到被關聯的作品（要改去改那件作品）；落子後回報 `linked_hits`（這一刀的範圍裡有幾格是關聯進來的，沒被動到）。
- **擋下**：成環（含隔好幾層、含停用的關聯）、關聯自己、作品不存在、放不下、保留字名（none／all／self／start／now）、名字含 `/`、非作者改關聯。關聯的增刪先拿 registry 鎖（兩個人同時互相關聯也擋得下）。
- **攤平**：`work sub=import`（匯入展區）與 `work sub=assemble`（來源有關聯）照 enabled 攤平成實際 voxel，Credit 逐件記；import 的 revision 照攤平後的算 ⇒ 子作品一改，expect_revision 就對不上。`work show` 另給 `composed_revision`／`composed_voxels`；Credit 多一段「關聯（現組）」。export 檔頭記每條用了哪個版本。
- **效能與鎖**：被關聯的作品讀它自己的快取（只讀、不寫回）；同一件只讀一次（28 門砲關聯同一件 cannon 也只讀一次）。鎖：作品＋關聯樹上所有作品依 ID 排序一次拿齊（全系統只有這一種鎖序）。
- `diff` 只比本作品自己的事件（不含關聯）；`mark` 回到過去時，被關聯的作品照現在。觀測頁：「只組這些關聯／拿掉這些關聯」兩欄；被關聯的作品改了之後，預覽要換一下參數（或按渲染）才會重組。
- **觀測頁的關聯開關**（TASK-0499）：選了有關聯的作品，「作品」下拉底下列出整棵關聯樹（巢狀縮排），**一條一個 toggle**，預設照那一條在作品裡的 `enabled`。關掉一條 ⇒ 連同底下都不畫；只關底下那條 ⇒ 上一層照畫；作品裡設為關的也可以這次打開。頁面把開關換成 `links=`（畫的那幾條全部點名）＋`hide_links=`（關掉而上一層有畫的）送出去 —— ⚠ 只送 `links=` 不夠：點名那條底下沒點名的會照自己的 enabled 被畫回來。全部在預設 ⇒ 什麼都不送；全關 ⇒ `links=none`。⛔ 開關**不寫回** links.json（長期開關用 `work sub=link enabled=0|1`）。開關的 id 帶作品 id ⇒ 換作品清單就換。**兩個文字欄有填 ⇒ 以文字欄為準**，開關這一次不生效（頁面上有寫）。GPU 預覽每幀照開關組參數；spawn 出圖（文字模式）要按渲染。
  - **Unity Hierarchy 式**（TASK-0500）：子模組的子模組（幾層都行）一樣列、一樣開關。勾 ＝ 那一條**自己的**開關（像 activeSelf）；上一層關了 ⇒ 底下每一層都不畫，但它們的勾照留、標「◌ 上層關著 ⇒ 不畫」（像 activeInHierarchy），上一層打開就照自己的勾回來。有子關聯的那一條前面有 ▶／▼ 折疊鈕（方形、跟勾選框一樣大；**預設折起來**），折起來標「底下 N 條」；另有「全部展開／全部折疊」。**折疊只管列不列，不管畫不畫**。開關與折疊都住頁面 session，⛔ 不寫回 links.json。
- ⭐ 慣例（甲板這種切出來的區域）：子作品尺寸 ＝ 父作品、`at=0,0,0`、`turn=0` ⇒ 子作品直接用父作品的座標雕，剖面校準也能共用。

### 9.4.5 噴漆 —— 打出去，碰到哪格就漆哪格（TASK-0495）

`op=spray`：射線打出去，碰到的第一格（往裡 `depth` 層）換成 `color`。**只換色、不放不刻**（實心格的集合一格不差），作品內免費、只有作者、共用展區不收。船殼塗裝這種「船外看得到的那一層」不必先在設計端算出來再 stampvox overwrite —— 從外面打過去就是那一層。

```bash
# 預覽（預設，零事件）：會換哪幾格、幾格、分原色；out= 寫換色清單（x,y,z,before,after）
senate cmd sculpture --arg op=spray --arg work=<作品> --arg persona=<P> --arg color=255 --arg from=0,0,62 --arg dir=0,1,0 --arg sweep=1023,0,0 --arg sweep2=0,0,7
# 確認 ⇒ 同樣的參數加 confirm=1 與預覽印的 plan_sha256（對不上 ⇒ exit 4、零事件）
senate cmd sculpture --arg op=spray --arg work=<作品> --arg persona=<P> --arg color=255 --arg from=0,0,62 --arg dir=0,1,0 --arg sweep=1023,0,0 --arg sweep2=0,0,7 --arg confirm=1 --arg expect_plan=<sha>
# 一整條跟著舷弧走：基準射線沿 sweep_path 平行複製（偏移 ＝ 線上的點 − 第一點 ⇒ from 給第一點），sweep2 ＝ 帶寬
senate cmd sculpture --arg op=spray --arg work=<作品> --arg persona=<P> --arg color=255 --arg from=0,-1,60 --arg dir=0,1,0 --arg sweep_path="0,-1,60;300,-1,56;600,-1,62" --arg curve=smooth --arg sweep2=0,0,7
# 沿折線／曲線打（轉彎之後才碰到的那格）
senate cmd sculpture --arg op=spray --arg work=<作品> --arg persona=<P> --arg color=36 --arg path="6,1,1;6,1,6;6,9,6"
```

- **射線三選一**：`from`＋`dir`（打到空間邊緣）／`from`＋`to`（線段，含終點那格）／`path=x,y,z;…`（折線；`curve=smooth` ⇒ Catmull-Rom 平滑曲線通過每個點）。
- **一束**：`sweep=dx,dy,dz`（沿向量）或 `sweep_path=x,y,z;…`（沿一條線，`curve=smooth` 也吃）擇一，每步 ≤ 1 格；`sweep2` 再沿第二個方向（面、帶寬）。回報 `rays`／`rays_hit`／`rays_missed`／`rays_blocked`。每刀最多 1,000,000 道、1,000,000 格。
- **打中** ＝ 沿路第一個實心格（起點那格也算）。`only_color=a,b` ⇒ 那一格的原色不在裡面就**擋下**（漆不穿牆：那一道什麼都不換）。`depth=N` ⇒ 往裡連續漆 N 層，遇到空格或 only_color 以外就停。本來就是噴漆色的格報 `already`、不進事件。所有射線看的是「噴之前」的作品，同一格只換一次。
- **座標與接縫**（寫死的規則）：整數 k ＝ 第 k 格的**中心**（可帶小數）。
  ① 起點剛好在兩格的接縫上 ⇒ 算**前進方向**那一格；
  ② 射線貼著接縫面平行走 ⇒ 算**座標較大**那一側（x=4.5 走在 x=5、x=7.5 走在 x=8）；
  ③ 剛好穿過格的邊或角 ⇒ 一步同時跨過，只被擦到邊／角、沒穿過內部的格**不算打到**。
- **預覽 → 確認**：預覽印 `plan_sha256`（換色清單的雜湊）；`confirm=1` 必帶 `expect_plan`（可再帶 `expect_placed` ＝ 換色格數）⇒ 落刀的格跟看過的那份逐格相同，預覽之後作品或參數變了就擋下。什麼都沒換到 ⇒ 預覽回 0、confirm exit 3，零事件。
- **草稿**：`draft=1` ⇒ 噴在草稿裡（沒開就開），看、undo，OK 再 `sub=apply`（§9.4.3）。
- **事件** ＝ 一筆 workedit（action=spray，before／after 逐格）＋ 最上層 `source`（射線參數與讀數）⇒ **沒更新的 senate.exe 也重播得出來**（edit 只用舊版就有的欄位）；`work sub=undo／redo` 照常；`diff` 的 op 統計標 `workedit:spray`。
- 讀數（憲法號事件的副本，零寫入）：船側整面（204,800 道）預覽約 3 秒，打中 65,989 道、換 61,518 格；左右舷各噴一次讀數相同（船是對稱的）。

### 9.4.6 固定點與繩索 —— 索具不用 voxel 雕（TASK-0502）

帆與索具不用 voxel 雕（Tim）⇒ 作品可以記**固定點**（名字 → 格座標）與**繩**（兩端、顏色、粗細、鬆緊）；兩份都是作品資料（`anchors.json`／`ropes.json`，入版控，⛔ 不進 events），**一格 voxel 都不動、不收費**，改的只有作者（同 link）。觀測時才算形狀、才畫。

```bash
# 固定點：點取那一格的格心（x+0.5, y+0.5, z+0.5）
senate cmd sculpture --arg op=work --arg sub=anchor --arg work=main-mast --arg persona=<P> --arg name=masthead --arg at=4,4,180
senate cmd sculpture --arg op=work --arg sub=anchors --arg work=ship                 # 本作品的＋關聯樹上每一層的（路徑指名、換到本作品座標）
# 繩：端點 ＝ 固定點名｜關聯/…/固定點名（相對於繩所在的作品）｜x,y,z（整數 ＝ 格心，可帶小數）
senate cmd sculpture --arg op=work --arg sub=rope --arg work=ship --arg persona=<P> --arg name=main-stay --arg from=bow-channel --arg to=主桅/masthead --arg slack=1.02 --arg color=#5a4632 --arg thickness=0.6
senate cmd sculpture --arg op=work --arg sub=rope --arg work=ship --arg persona=<P> --arg name=main-stay --arg slack=1.1        # 改鬆緊／顏色／粗細／開關不用 overwrite
senate cmd sculpture --arg op=work --arg sub=ropes --arg work=ship                   # 每條的長度、最大下垂、狀態（讀數）
senate cmd sculpture --arg op=view --arg work=ship --arg hide_ropes=main-stay --arg out=<png>   # 這次拿掉一條；ropes=0 ＝ 這次一條都不畫
```

- **固定點** `sub=anchor name= at=x,y,z [note=]`：要在作品尺寸裡；同名搬位置要 `overwrite=1`（綁在上面的繩會跟著動，⛔ 不默默搬）。`sub=unanchor name=` 拿掉。名字的規矩同關聯名（不含 `: , | / \`、不是保留字）。
- **繩** `sub=rope name= from= to= [slack=] [color=] [thickness=] [enabled=] [note=]`：
  - `slack` ＝ 繩長 ÷ 兩端直線距離（1 ＝ 拉直、1.02 微垂、1.2 明顯下垂；上限 10）。`color` ＝ 調色盤 index 1..255 或 `#RRGGBB`（存小寫）；`thickness` ＝ **直徑**（格，可帶小數，預設 0.5）。
  - 同名已在：改 slack／color／thickness／enabled／note 直接改；**換兩端**要 `overwrite=1`。
  - 寫之前先解一次：端點解不到（固定點或關聯路徑打錯）⇒ exit 2、**未寫入**（⛔ 打錯字不會變成一條壞繩）。`sub=unrope name=` 拿掉。
- **形狀**：只受重力（−z）⇒ 懸鏈線解析解（同輸入同形狀，決定性）。拉直 ⇒ 直線；兩端在同一條鉛直線上 ⇒ 畫直線並標明（鬆度沒有往旁邊垂的方向）。⚠ **沒有碰撞**：繩會穿過格子（垂得太多就穿進船身）——鬆度自己控制，看 `rope_max_sag`。風與其他力等帆那張單（TASK-0503）。
- **讀數**：`sub=rope` 寫完印 `rope_length`／`rope_chord`／`rope_max_sag`；`sub=ropes` 與 view／export 的回報每條一行：狀態、兩端（名字＋座標）、長、直線距離、slack、最大下垂（離兩端連線往下最多幾格）、色、粗。
- **跨作品**：端點寫關聯路徑（`主桅/masthead`、`甲板2/船長室/hook`），座標照路徑上每一層關聯的 `mirror`→`turn`→`at` 換到組裝件 —— 跟 voxel 同一個公式（格心換過去正好是那一格換過去的格心）。⇒ 搬關聯（`at`）、轉向、鏡像（右舷那根桅的固定點翻到另一舷）繩都跟著走。被關聯進來的作品**自己的繩**也照那條關聯換座標、一起畫（繩路徑 ＝ `關聯/…/繩名`）。
- **跟著不畫**：綁在某一層上的繩，那一層這次沒畫（`links=none`、`hide_links`、那條關聯 `enabled=0`）⇒ 繩 `hidden`；繩自己 `enabled=0` ⇒ `disabled`；`ropes=0`／`hide_ropes=繩路徑,…` ⇒ 這次不畫（打錯路徑 ⇒ exit 2）。
- **壞掉的綁定**（固定點刪了／改名、關聯路徑不在了、固定點檔壞了）⇒ `broken`：⛔ 不靜默消失 —— 觀測照樣出圖，但回傳行大聲列 🔴 與原因（`ropes_broken`）；`sub=unanchor` 當下就全庫掃一次，印出哪幾條繩（含上層作品用關聯路徑綁的）現在壞了（`ropes_now_broken`）。把固定點用原名加回去就接上。
- **畫**：view 與觀測頁的 GPU 預覽把繩畫成管子，跟 voxel 同一個網格（吃同一組燈、會投影子、也接影子）；繩不是 voxel ⇒ `visible_voxels` 不變。回傳值 `ropes_drawn`／`ropes_hidden`／`ropes_disabled`／`ropes_broken`。`slice`／`section`／`stats` 量的是格子，不畫繩。
- **觀測頁**：選了有繩的作品，「繩索開關」列出每一條（長度、下垂）：總開關「畫繩索」＋一條一個勾（像關聯開關，只影響這次觀測、⛔ 不寫回 ropes.json）；壞掉的列 🔴＋原因；作品裡設為關的只列不勾。
- **匯出**：`export format=obj` 把繩接在 voxel 面後面輸出成管狀網格（每條一個 `o rope_n`＋材質 `rope_n`，mtl 有 Kd；有法線、繞序跟法線同向）⇒ 匯進 Unity 看得到。`format=list|vox` 只有格子 ⇒ 繩不輸出、`ropes_exported=0`。
- **帆**：端點寫帆角 `sail:[關聯/…/]帆名/角` ⇒ 繩綁在帆的角上（TASK-0503，見 §9.4.7）；其他 `種類:` 一律 exit 2（不猜）。固定點與繩的格式就是帆共用的那一份。

### 9.4.7 帆 —— 布料模擬，綁在固定點與繩上（TASK-0503）

帆不用 voxel 雕（Tim）⇒ 作品記**帆**：幾個角綁在哪、哪條邊整條釘住、什麼顏色；存在 `sails.json`（作品資料，入版控，⛔ 不進 events），**一格 voxel 都不動、不收費**，改的只有作者。觀測時才用布料模擬算形狀、才畫。

```bash
# 方帆：四個角依序繞一圈（角 ＝ 固定點名｜關聯/…/固定點名｜x,y,z，同繩的端點），上緣（邊 0）整條釘在 yard 上
senate cmd sculpture --arg op=work --arg sub=sail --arg work=ship --arg persona=<P> --arg name=main-topsail --arg corners="主桅/yard_p|主桅/yard_s|主桅/lyard_s|主桅/lyard_p" --arg bind=0
# 下面兩角不釘（free），名字給繩用；三角帆給 3 個角
senate cmd sculpture --arg op=work --arg sub=sail --arg work=ship --arg persona=<P> --arg name=main-course --arg corners="hp|hs|47,29,8|16,29,8" --arg corner_names="head_p|head_s|clew_s|clew_p" --arg bind=0 --arg free=clew_s,clew_p --arg color=#f2ead8
# 帆腳索：繩的一端寫帆角 sail:[關聯/…/]帆名/角（角 ＝ 名字或序號）
senate cmd sculpture --arg op=work --arg sub=rope --arg work=ship --arg persona=<P> --arg name=sheet_p --arg from=sail:main-course/clew_p --arg to=cleat_p
senate cmd sculpture --arg op=work --arg sub=wind --arg work=ship --arg persona=<P> --arg wind=4,-9,0 --arg sail_seconds=4   # 作品預設風（m/s）與秒數
senate cmd sculpture --arg op=work --arg sub=sails --arg work=ship                                                          # 每面帆模擬到那一刻的讀數
senate cmd sculpture --arg op=view --arg work=ship --arg wind=0,-8,0 --arg sail_seconds=2 --arg out=<png>                  # 這次換風、換秒數
```

- **帆** `sub=sail name= corners=角0|角1|角2[|角3] [corner_names=名|名|…] [bind=邊,…] [free=角,…] [color=] [res=] [fullness=] [enabled=] [note=]`：
  - 角 3 個 ＝ 三角帆（jib、staysail）、4 個 ＝ 四邊形（方帆、梯形的縱帆 spanker）；**依序繞一圈**，`|` 分隔。
  - `bind=k`：**邊 k ＝ 角 k → 角 k+1** 整條釘在兩角之間的線段上（方帆上緣沿 yard、縱帆沿 gaff／boom）—— 那條邊上的每一個布點都釘住，不是只釘兩個角。
  - 角預設**釘住**；`free=` 的角（名字或序號）跟著布動。綁在 free 角上的繩會拉住它（見下）；沒綁繩的 free 角就隨風亂飛。
  - `color` ＝ 調色盤 index 1..255 或 `#RRGGBB`（存小寫；預設 `#f2ead8` 帆布色）。`res` ＝ 布料每邊幾格（0 ＝ 照大小自動，6..24）。`fullness` ＝ 布比角圍出來的框多幾成（預設 1.06；1 ＝ 繃平、鼓不起來；上限 1.5）。
  - 同名已在：改 free／bind／color／res／fullness／enabled／note／corner_names 直接改；**換角的位置**要 `overwrite=1`。寫之前先解一次：角解不到 ⇒ exit 2、**未寫入**。`sub=unsail name=` 拿掉（綁它角的繩之後會是 broken，當下全庫掃一次印出來）。
- **模擬**：PBD 布料（距離約束：相鄰／對角／隔一格）＋**重力**＋**風**（只算布面法線方向的風壓，含固定的陣風起伏）；固定 dt 1/60 秒、每步固定迭代次數、單執行緒依序走 ⇒ **同樣的輸入＋同樣的秒數 ⇒ 逐位元同樣的形狀**。不用算到靜止（Tim）：
  - CLI `view`／`export`：取「模擬 `sail_seconds` 秒後」那一刻（不給 ⇒ 作品預設，預設 3 秒；上限 60）。
  - 觀測頁：「持續模擬」開著 ⇒ 帆會動；**每 0.1 秒更新一次、一次最多約 8 ms**（Tim「不用頻繁模擬」）—— 帆多、算不完就慢動作播放，⛔ 不改 dt、不拖垮畫面。關掉 ⇒ 停在 `sail_seconds` 那一刻（跟 CLI 同一張）。
  - 單位：重力 9.81 m/s²、風 `wind=x,y,z`（**m/s，世界座標**；例 `0,-8,0` ＝ 往 −y 吹的 8 m/s 風），照**組裝件**的 `meters_per_voxel` 換成格 ⇒ 比例對了，鼓的程度才對（憲法號一格 0.1 m）。預設風存在組裝件的 `sails.json`（`sub=wind`），`view`／`export`／`sub=sails` 的 `wind=`／`sail_seconds=` 一次性蓋過。
  - ⚠ **沒有碰撞**：帆會穿過格子與繩（同繩）—— 綁點與 fullness 自己控制，看讀數。
- **讀數**（`sub=sails` 與 view／export 的回報，每面帆一行）：狀態、角（名字＋座標、哪幾個 free）、釘住的邊、解析度與布點數、**最鼓**（離綁點平面最遠那一點幾格，帶正負號：正 ＝ 往「角依序繞一圈的右手法線」那側）、**平均位移**（全部布點離設計位置，世界座標 —— 沒風 ⇒ 幾乎只往 −z）、牽繩條數；環境一行：風、一格幾公尺、模擬了幾秒幾步、平均每步幾毫秒。回傳值 `sails_drawn／hidden／disabled／broken`、`sail_seconds`、`sail_steps`、`sail_step_ms`、`sail_max_billow`。
- **帆角當繩的端點**：繩的 `from`／`to` 寫 `sail:[關聯/…/]帆名/角`（相對於繩所在的作品，同固定點路徑）。
  - 綁在 **free** 角上 ⇒ 那條繩是**牽繩**：繩長 L ＝ `slack` × 設計時兩端的距離；帆角離繩另一端超過 L 就被拉回（繩繃住），沒超過就不拉（鬆的繩不出力）。⇒ 風把帆角往外吹時繩繃住；**拉繩的另一頭（搬固定點）或改 slack，帆的形狀就跟著變**。
  - **綁帆角的繩 slack 可以小於 1**（下限 0.3）：繩比設計時短 ⇒ 把帆角拉過去（收帆腳索）。拉不到那麼近時繩畫成拉直。沒綁帆角的繩照舊 1..10。
  - 綁在釘住的角上 ⇒ 繩只是畫出來，不拉任何東西。繩畫到帆角**模擬完那一刻**的位置（觀測頁持續模擬時跟著動）。
  - 只被顯示開關關掉的繩（`ropes=0`、`hide_ropes`、`enabled=0`）照樣拉；所在或綁的那一層沒組進來（`links`）⇒ 這條繩這次不存在、不拉。
- **跟著部件走**：角寫關聯路徑（`主桅/yard_p`）⇒ 照那條關聯的 `mirror`→`turn`→`at` 換到組裝件（同繩、同 voxel 的公式）—— 搬關聯、轉向、鏡像（另一舷）帆都跟著走。被關聯進來的作品**自己的帆**也照那條關聯換座標、一起模擬（帆路徑 ＝ `關聯/…/帆名`）。
  - 帆所在或角綁的那一層這次沒組進來（`links=none`、`hide_links`、那條關聯 `enabled=0`）⇒ `hidden`、**不模擬**；帆自己 `enabled=0` ⇒ `disabled`、不模擬。
  - `sails=0`／`hide_sails=帆路徑,…` ⇒ 這次**不畫、照樣模擬**（綁在它角上的繩照樣拉）；打錯路徑 ⇒ exit 2。
  - **壞掉的綁定**（固定點刪了／改名、關聯路徑不在了、角幾乎在一條線上）⇒ `broken`：⛔ 不靜默消失 —— 觀測照樣出圖，回傳行大聲列 🔴（`sails_broken`）；`sub=unanchor`／`sub=unsail` 當下全庫掃一次（`sails_now_broken`／`ropes_now_broken`）。用原名把固定點加回去就接上。
- **畫**：view 與觀測頁的 GPU 預覽把帆畫成**雙面**的布（正反各一份、各往法線推半個布厚 ⇒ 兩面都看得到、也不會自己遮自己的影子），吃同一組燈、會投影子也接影子。帆與綁帆角的繩是**另一趟**（每次換形狀只重傳帆，⛔ voxel 網格不重建）。帆不是 voxel ⇒ `visible_voxels` 不變。
- **觀測頁**：選了有帆的作品，「帆」區：風／秒數欄位（空 ＝ 作品預設）、「持續模擬」開關、總開關「畫帆」＋一面一個勾（只影響這次觀測，⛔ 不寫回 sails.json）；壞掉的列 🔴＋原因；作品裡設為關的只列不勾。
- **匯出**：`export format=obj` 把帆（模擬到 `sail_seconds` 那一刻）接在繩後面輸出成雙面三角網格（每面一個 `o sail_n`＋材質 `sail_n`，mtl 有 Kd；有法線、繞序跟法線同向），綁帆角的繩也一起 ⇒ 匯進 Unity 看得到。`format=list|vox` 只有格子 ⇒ `sails_exported=0`。
- **讀數**（淨室 SculptureSailsCleanRoom，Release）：24 面帆、每面 25×25 布點，模擬 3 秒（180 步）約 1.2 秒、每步約 6.8 ms。
- **之後**：貼圖（Tim 2026-10-11「貼圖可以先不處理」）、收帆（furled，捲在 yard 上）、縮帆（reef）本版不做 —— `sails.json` 的帆是可以加欄位的物件，加的時候舊檔照讀。

### 9.4.8 外部模型 —— 匯入 obj、或拿另一件作品當模型（TASK-0507）

作品可以擺**三角網格模型**：一個 obj 檔，或另一件雕刻作品（**不匯入 voxel**，而是匯出成 obj 快取在本地再畫）。存在 `models.json`（作品資料，入版控，⛔ 不進 events），**一格 voxel 都不動、不收費**，改的只有作者。模型可以任意角度旋轉、縮放 —— voxel 關聯只能轉 90°、不能縮。

```bash
# obj 檔：複製進作品（models/<名字>/），之後作品自己帶著它
senate cmd sculpture --arg op=work --arg sub=model --arg work=ship --arg persona=<P> --arg name=lantern --arg file=D:/tmp/lantern.obj --arg axes=yup --arg at=512,160,140 --arg scale=10
# 作品當模型：比例照兩件作品的一格幾公尺自動換算（0.02 m 的零件放進 0.1 m 的船 ⇒ scale 0.2）
senate cmd sculpture --arg op=work --arg sub=model --arg work=ship --arg persona=<P> --arg name=wheel --arg source_work=ship-wheel --arg pivot=40,6,40 --arg at=180,160,131.5 --arg rotate=0,0,90
senate cmd sculpture --arg op=work --arg sub=model --arg work=ship --arg persona=<P> --arg name=wheel --arg rotate=0,0,95   # 只改變換：不用 overwrite
senate cmd sculpture --arg op=work --arg sub=models --arg work=ship                                                         # 每個模型的讀數
# 繩綁在模型上：model:[關聯/…/]模型名/點（點 ＝ 模型自己的座標，或來源作品的固定點名）
senate cmd sculpture --arg op=work --arg sub=rope --arg work=ship --arg persona=<P> --arg name=tiller-rope --arg from=model:wheel/drum_p --arg to=tiller-block
senate cmd sculpture --arg op=view --arg work=ship --arg hide_models=wheel --arg out=<png>   # 這次拿掉；models=0 ＝ 一個都不畫
```

- **`sub=model name= (file=<obj> [axes=] | source_work=<作品>) [at=] [pivot=] [rotate=] [scale=] [mirror=] [color=] [enabled=] [note=]`**：
  - **座標三層**：① 檔案座標（obj 自己的 x,y,z）→ ② 模型座標（z 朝上）→ ③ 作品座標。`axes` 管 ①→②：`yup`（預設；Blender／Maya 匯出的 obj）＝ (x, −z, y)、`zup` ＝ 原樣、`sculpt` ＝ 本引擎 `export` 出來的 obj（(x, z, y)，export 的反函數）。②→③：**p ＝ at ＋ R·(scale ⊙ mirror(p − pivot))**，`rotate=rx,ry,rz`（度）先繞 X、再 Y、再 Z（都繞作品的軸）；`scale` 一個數或三個數（> 0）；`mirror=x|y|z`（組合）在模型座標裡繞 pivot 翻面。at／pivot 可帶小數。
  - **file 來源**：obj 與它 `mtllib` 引用的 mtl 一起複製進 `models/<名字>/`（⛔ 不指向作品外的檔）；記下位元組數與 SHA256。換檔 ＝ 同名＋`file=`＋`overwrite=1`。
  - **work 來源**：那件作品（含它的關聯、繩、帆、模型）匯出成 obj，快取在 `<雕刻根>/model_cache/<作品>/<鍵>.obj`（⛔ 不入版控）；鍵 ＝ 那一整串作品的事件水位與資料檔雜湊 ⇒ 來源一改，下一次觀測**自動重出**（回報 `cache=produced|reused`）。模型座標 ＝ 那件作品的格座標。`scale` 不給 ⇒ 來源的一格幾公尺 ÷ 本作品的（寫進 models.json）。成環（A 用 B、B 又用 A；含經過關聯與隔層）⇒ exit 2。
  - 同名已在：改 at／pivot／rotate／scale／mirror／color／enabled／note 直接改；**換來源**要 `overwrite=1`。寫之前先讀一次 obj（work 來源先產生快取）：讀不了、沒有面、索引越界 ⇒ exit 2、**一個檔都沒寫**。`sub=unmodel name=` 拿掉（file 來源連同 `models/<名字>/`；綁在它上面的繩當下全庫掃一次，`ropes_now_broken`）。
  - 只讀幾何與顏色：`v`／`vn`／`f`（多邊形扇形切成三角形；索引可負）／`usemtl`／`mtllib` 的 `Kd`。沒有 `vn` ⇒ 面法線（平面著色）；缺 mtl 或材質 ⇒ 預設灰並列警告；`color=` ⇒ 整個模型一個色。貼圖、曲面、線段不讀。上限 256 MB、800 萬個三角形。
- **繩與帆角綁模型**：端點寫 `model:[關聯/…/]模型名/點`（相對於繩所在的作品，同固定點路徑）。點 ＝ `x,y,z`：file 來源是**檔案座標**（照 axes 換）、work 來源是那件作品的格座標（整數 ⇒ 格心）；work 來源也可以寫那件作品的**固定點名**。模型搬、轉、縮、跟著關聯走 ⇒ 端點跟著動。帆的角同一種寫法（`corners="model:mast/12,0,40|…"`）。
- **跟著關聯走**：被關聯進來的作品自己的模型照那條關聯的 mirror→turn→at 換座標（模型路徑 ＝ `關聯/…/模型名`）。所在那一層這次沒畫 ⇒ `hidden`（綁在上面的繩跟著 hidden）；自己 `enabled=0` ⇒ `disabled`；`models=0`／`hide_models=路徑,…` ⇒ 這次**不畫**（綁在上面的繩照畫；打錯路徑 ⇒ exit 2）。檔不見、來源作品不在、成環、快取產生失敗 ⇒ `broken`：⛔ 不靜默消失，觀測照樣出圖、回傳行大聲列 🔴。
- **讀數**（`sub=models` 與 view／export 每個模型一行）：狀態、來源（檔名＋SHA 前 8 碼／作品＋快取鍵與 produced|reused）、變換、三角形數、在組裝件裡的外框；回傳值 `models_drawn／hidden／disabled／broken`。
- **畫**：view 與觀測頁的 GPU 預覽另一趟畫（靜態 ⇒ 內容沒變不重傳；同一組燈、會投影子也接影子）；不是 voxel ⇒ `visible_voxels` 不變；slice／section／stats 量的是格子，不畫模型。觀測頁「模型」區：總開關＋一個一個勾（只影響這次觀測，⛔ 不寫回 models.json）。
- **匯出**：`export format=obj` 把模型接在帆後面（每個模型 `o model_n`、一色一個材質 `model_n_k`）；list／vox 只有格子 ⇒ `models_exported=0`。只有繩／帆／模型、沒有 voxel 的作品也匯得出 obj。
- ⚠ **已知的洞**：`work sub=link` 的成環檢查只看關聯 —— 加一條關聯讓「關聯＋模型」繞成環時，寫入當下**不擋**；觀測時那個模型列 `broken`（「模型來源成環」），不會無限遞迴。`sub=model` 寫入時兩種邊都看。

### 9.5 建築與家具的相對比例尺

**預設1公尺＝32 voxel；1 voxel＝0.03125公尺＝3.125公分。** 房間、家具、人物與放置座標共用此比例，方便不同作者的作品組裝。這是建造參考，CLI仍輸入整數voxel，不是自動物理單位轉換。

- 尺寸、座標與平移量換算：`voxel數＝公尺數×32`，取最接近的整數（恰好半格時往較大值取整）；每件作品各軸上限見§9.6（預設4096）。
- 整段寬度N格用座標`0..N-1`；例如2公尺長的床為64格，座標`0..63`，不是`0..64`。
- X、Y為地面兩軸，Z為高度；90°旋轉會交換零件的X、Y空間尺寸。
- 既有零件先讀書卡尺寸並依比例比較。匯入保留原voxel大小，不自動縮放；需要不同大小就另做相應尺寸的零件。

| 參考項目 | 公尺 | voxel |
|---|---|---|
| 房間長×寬×高 | 6×6×3 | 192×192×96 |
| 單人床長×寬 | 2×1 | 64×32 |
| 床面高度 | 0.5 | 16 |
| 床頭高度 | 1 | 32 |
| 門寬×高 | 0.9×2.1 | 約29×67 |
| 桌面高度 | 0.75 | 24 |
| 椅面高度 | 0.45 | 約14 |
| 成人身高 | 1.75 | 56 |
| 牆厚 | 0.125 | 4 |
| 預設64格邊長 | 2 | 64 |
| 單軸4096格（預設上限） | 128 | 4096 |

例如床面放在Z=16附近，床頭最高到Z=31；房間192×192×96可容納三張64×32的床並留下走道。細節不足一格時可採視覺誇張，但同一組房間與家具維持此基準。

### 9.5.1 從觀察圖轉成畫廊心得畫

策展依資料根 `ArtGallery/WORKFLOW.md`；其中「雕刻心得畫的觀察與差異紀錄」說明來源截圖、作者、合成 revision 與成圖差異的保存方式。局部 `region` 不能直接當成帆與繩索也已裁掉；剖面被遮住時依當前 help 使用 `sails=0`／`ropes=0` 或局部隱藏，再親看輸出。生成後對照原始截圖核對甲板、砲列、艙口與通道，提示詞的要求不能充當成圖讀數。

### 9.6 作品尺寸上限（設定）與大作品

個人作品每軸上限是**設定值**，不寫死（Tim 2026-10-10，TASK-0479）：

| 項目 | 值 |
|---|---|
| 設定 | `senate.pages.local.json` 的 `sculpture.workMaxAxis`（本機設定，不入版控） |
| 預設 | 4096 |
| 範圍 | 1–1,048,576（結構上限：渲染端把外框內座標打包成 21 位元） |
| 改法 | 後台雕刻頁 →「個人作品」→「作品尺寸上限」；或直接改設定檔 |

- 上限**只管建立與調整尺寸**。讀取、雕刻、渲染、版本只認結構上限 ⇒ 把設定調小，既有的大作品照樣能雕、能看。
- `work sub=create|update` 的回傳值印 `work_max_axis` 與 `work_max_axis_source`（設定／預設／讀不了）——被擋下時才分得出「我給太大」與「這台的設定比較小」。
- 共用展區仍是 256³（全員共用的空間，不跟這個設定走）。
- 渲染網格的佔用格是稀疏分塊（16³ 一塊，只配有 voxel 的塊）⇒ 跟有 voxel 的塊數成正比，不跟外框體積成正比（外框 2000×1000×1000 的稀疏場景逐面畫法約配置十幾 MB）。觀測頁用的合併畫法每一層還要一張遮罩：每邊封頂 2048 格（約 16 MB），超過的層切成磚塊各自合併（磚與磚之間不合併）。
- 仍在的上限：單刀 box／carve 最多 1,000,000 格（`MaxVolume`，大面積拆多刀）；`export format=vox` 是 MagicaVoxel 單模型格式，region 每軸最多 256（格式本身的限制）。
