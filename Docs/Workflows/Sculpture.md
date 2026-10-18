---
title: 3D 體積雕刻（senate cmd sculpture）—— 落子、收費、分享、觀測、渲染設定
description: 256³ 共用 voxel 空間怎麼雕：十五個 op、收費三段（預授權 → 引擎 → 按實際結算）、兩把鎖、exit 怎麼讀、view 的輸出規則與參數疊層、渲染設定檔（鏡頭／燈／天空／地板）、分享走哪條路。
cmds: [sculpture]
last_updated: 2026-10-18（§9.4.6 繩端綁定檢查 sub=ropecheck；ame TASK-0716）；2026-10-17（橫繩組 sub=ratlines／unratlines、繩梯 climb（§9.4.6））
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
- 造船（風帆戰艦這一類）：要讀哪幾份文件 → skill `scp-shipbuilding`（`senate cmd skill --arg op=show --arg name=scp-shipbuilding`）。
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
| `work` | 個人作品：`sub=create|list|show|update|import|assemble|move|undo|redo|history|mark|marks`；草稿 `draft|apply|discard`（§9.4.3）；關聯 `link|unlink|links`（§9.4.4）；固定點與繩 `anchor|unanchor|anchors|rope|unrope|ropes`（§9.4.6）；帆 `sail|unsail|sails|wind`（§9.4.7）；外部模型 `model|unmodel|models`（§9.4.8）；舊大事件拆檔 `split-events`（§9.4 大事件拆檔） | 建立固定10；匯入⌈實際落地/100⌉；其餘免費 | 建立／修改／匯入要作者 |

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
事件檔是**單行 JSON**（分隔符不帶空白，＝ python `separators=(",", ":")`；TASK-0528）—— 之前寫的是 indent=2（同一刀約大 2.7 倍），舊檔照讀、不回頭轉；讀事件一律用 JSON 解析，⛔ 不要逐行讀。
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
| 光 | `light_dir=x,y,z`（換成一盞白光）`ambient`（0-1）`sun`（0-1，太陽；見 §5.7）`night=on|off`（夜景）`shadow=1|0` `ao=1|0` |
| 天空 | `skybox=<路徑>|builtin|none`（相對路徑以 `Sculpture/skyboxes/` 為基準；none ＝ 純色背景）`skybox_yaw` `skybox_tilt`（正交時背景視窗往上抬，−89..89；透視忽略） |
| 地板 | `floor=on|off` `floor_z` `floor_full_grid=1|0` `floor_margin` `floor_texture=builtin|<檔名>|<絕對路徑>`（相對 ⇒ `Sculpture/floors/`）`floor_tile` `floor_color=#rrggbb` `floor_fade`（見 §5.1） |
| 海面 | `sea=on|off` `sea_z` `sea_color=#rrggbb` `sea_opacity`（0..1）（見 §5.2；作品預設 `work sub=sea`） |
| 點光源 | `lamps=on|off` `lamp_intensity`（0..8）`lamp_radius`（公尺）`lamp_color=#rrggbb`（見 §5.6；作品預設 `work sub=lamps`） |
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

### 5.2 海面（TASK-0585）

Tim 2026-10-14：「預覽模式是否可以開海水（可以設定高度）因為不希望實際建模海水 但是想要港口建築能在水中（同時可以引用船當子模型）」。
一片 z＝`sea_z` 的水平面，**是渲染出來的、⛔ 不是 voxel**（不進格子清單、不影響讀數、匯出 list／obj／vxk 都沒有它）。

- 畫在所有不透明的東西之後：深度測試、不寫深度、alpha 混色 ⇒ 海面以上照常；以下被蓋住（`sea_opacity` 1）或帶海色淡淡看得到（< 1）。
- 鋪到遠方（不跟作品外框；正交、透視都到地平線）；⛔ 不算進框景（鏡頭不會為了海退到天邊）、不進陰影圖；接 voxel 的影子；雙面（鏡頭在水下也看得到海面）。
- 跟地板並存：例：地板當海床、海面在上面。

| 欄位 | 預設 | 範圍 | 意思 |
|---|---|---|---|
| `sea` | off | on／off | 開關；⚠ 不給 ⇒ 沿用下層（只換高度的層不改開關） |
| `sea_z` | 0 | −64..4096 | 海面高度（世界 z；格 k 佔 [k, k+1)，`sea_z=60` ＝ 第 60 格的底面） |
| `sea_color` | #1e5a78 | #rrggbb | 海色 |
| `sea_opacity` | 0.85 | 0..1 | 不透明度；1 ＝ 水下全部蓋住 |

**疊層**：內建預設 → 共用 → persona →（`profile=`）→ **作品海面**（主作品的 `sea.json`；關聯進來的子作品自己的海不算 —— 一張圖只有一片海）→ 展品 → CLI。`layers` 會印「作品海面 `<id>`」。
**作品預設**：`work sub=sea`（只有作者能改；只給一部分 ⇒ 其他欄位沿用檔裡的；第一次只給高度 ⇒ 當成要開；都不給 ⇒ 印現在的值）。寫在作品資料夾的 `sea.json`；壞掉的檔 ⇒ view 擋下（⛔ 不默默當成沒有海）。
觀測頁「相機與光影（手動）」最後一段：海水（沿用／開／關）＋高度、顏色、不透明度；空白 ＝ 照作品預設。

```bash
senate cmd sculpture --arg op=work --arg sub=sea --arg work=<港口> --arg persona=<P> --arg sea_z=60 --arg sea_opacity=0.8   # 作品預設
senate cmd sculpture --arg op=view --arg work=<港口> --arg persona=<P>                    # 照作品預設開海
senate cmd sculpture --arg op=view --arg work=<港口> --arg persona=<P> --arg sea=off      # 這次不畫
senate cmd sculpture --arg op=view --arg work=<船> --arg persona=<P> --arg sea=on --arg sea_z=50 --arg sea_opacity=1   # 一次性：海面放到船的水線
```

- 引用船當子作品（關聯）：⚠ 關聯不能放到負座標（越界的格會被切掉）⇒ 港口的海面要高過船的吃水（船底 z 0、水線 z 50 ⇒ 港口海面 ≥ 50）。帆的風是作品自己的設定 ⇒ 港口作品要自己 `work sub=wind`，不然帆是垂的。

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

- **沒有開關**：宿主能畫就用 GPU 即時預覽；「渲染結果」**收起來 ⇒ 不準備場景、不畫、也不 spawn 出圖**，展開才照最新參數畫（收著時改的參數，展開那一輪補畫）。宿主不能畫（文字模式、`SENATE_SCULPT_GPU=off`）或這一幀畫失敗 ⇒ 退回 spawn 出圖，原因印在頁面上。

- **鏡頭只框一次**：換對象、換投影、或按工具列的「重置鏡頭」時，渲染器自動框住一次（距離＋畫面置中）；框完那一刻的中心與距離（透視）／縮放（正交）就**釘住**，之後轉、拖、縮放都不再重新框。頁面上印「鏡頭已釘住：中心 (x, y, z)、距離／縮放」。
  - 🩸 為什麼：透視的自動框每幀要把外露 voxel 掃十幾遍（鏡廳一幀 ~190 ms，85–99% 花在框住）；釘住之後跟正交同一級（鏡廳 387 → 65 ms、小木屋 226 → 31 ms，1280×720 含讀回）。
  - 釘住的鏡頭送回去，畫面跟框的那一刻**逐位元相同**（淨室 SculptCameraPinCleanRoom）。
- **右鍵（或中鍵）拖曳 ＝ 旋轉**：繞著釘住的中心轉（像 Blender 抓著物體轉）—— 拖過整張圖寬 ＝ yaw 180°、整張圖高 ＝ pitch 180°；pitch 夾在 ±89。改的是 yaw／pitch 欄位（滑桿同步）。
- **左鍵拖曳 ＝ 平移**：抓著地面拖，**只動中心的世界 X／Y，高度（Z）不變**。
- **滾輪 ＝ 縮放**（檢視倍率，一格 ×1.25）；範圍在「預覽設定」`ViewScaleMin`／`ViewScaleMax`（預設 0.05–50，上限最多 1000；不合法整組退回預設並在畫面上說明）。「倍率歸 1」只回倍率，「重置鏡頭」連中心一起重框、倍率回 1。渲染結果的文字資訊（目前對象、layers、場景準備、帆）在圖下方。
- 手動欄位：給了 eye（鏡頭位置）⇒ 不釘（eye 決定一切）；給了 zoom／distance ⇒ 那一格以欄位為準、中心照釘。
- 切換正交／透視（工具列鈕或手動區下拉）＝ 按一次「重置鏡頭」：等新投影的場景準備好才重框、倍率回 1。
- 「預覽設定」的 `ViewSide` ＝ 渲染畫面在頁面上的顯示邊長（160–4096，預設 720）；只管顯示，輸出解析度是手動區的 `width`／`height`。預覽設定與上次選的空間／作品存在 `senate.pages.local.json` 的 `sculpture.*`，重開視窗照用。
- ⚠ 只影響視窗裡的 GPU 預覽；CLI `op=view` 與 spawn 出圖照舊每次自動框（同一份輸入同一張圖）。

### 5.4 子模型分開畫（TASK-0509）

觀測頁的 GPU 預覽看個人作品時，請求一律帶 `parts=1`：

- 主作品只帶**自己的格**；每個關聯節點（含作品裡設為關的）、每個外部模型各是一個**實例**，指向一份共用網格＋自己的矩陣（關聯：鏡像→轉→平移，巢狀逐層；模型：at／pivot／rotate／scale／mirror）。
- **同件共用**：關聯 ＝ 同一件作品同一個版本（＋草稿）（＋同一張有效替換表，§9.4.4 替換色）一份；外部模型 ＝ 同一個 obj（file 來源比 sha256＋大小，所以同一個檔用兩個名字也共用）＋同軸向＋同改色一份。渲染器以這個鍵快取網格，鍵沒變就不重建、不重傳。
- **各自完整顯示**：子模型畫自己的全部格子；跟主作品貼合處不互相剔除、重疊處兩份都在（合併版「自己的格優先、先到的贏、越界丟掉」不適用）。
- **開關只切顯示**：關聯開關、`hide_links=self`、模型開關每幀換成「畫哪幾個實例」，⛔ 不重建任何網格。框景、地板、陰影範圍照**全部**實例算（切開關畫面不跳）。
- 繩在分開畫時是自己一趟（不併進 voxel 網格）。繩與帆仍照關聯選取算 ⇒ 作品有繩或帆時，切關聯會在背景重新準備場景（子模型當下切換，繩稍後跟上）；沒有繩也沒有帆 ⇒ 不重新準備。
- 不適用（照舊合併）：共用展區、手動填了 `region`／`exclude_color`。CLI 也可以 `op=view parts=1`；`export` 的 obj／vox／list／vxk 照舊合併；分開匯出走 `format=glb`（§9.4.11，同一份場景）。
- 讀數：渲染結果下方「子模型分開畫：N 份網格、M 個實例（顯示 k）」，GPU 讀數「這一幀新建 b」。

### 5.5 平滑渲染與材質反光（TASK-0645）

Tim 2026-10-16：「地形可以設定為柔和平面」「是否可讓 SculptureViewerPage 也支援（平滑渲染）」「然後還有材質（可以參考兩艘鐵甲艦）」。
觀測頁（GPU 即時預覽）與 CLI `op=view` 照**關聯的 meta** 畫，跟遊戲（Blockside 15_smooth.js）同一套規則、同一個算法 —— 標法見造船規格 §9.5，這裡只寫預覽這側。

- `render=smooth` 的格畫成平滑面（surface nets：格角往表面拉、周圍同一面平均、夾回格角 ±0.5；跟一般格相鄰的格角釘住 ⇒ 交界不露縫）。巢狀照最近一層，子關聯寫 `render=voxel` 蓋回方塊。
  - 平滑格自己的方塊面不畫，但照樣擋住旁邊格子的面、照樣進框景與陰影。
  - 只改畫面：格子清單、讀數、匯出 list／obj 照舊是方塊（vxk 檔頭照舊多 `render`）。
- `material=<ID>` 的**平滑面**照雕刻的材質表上金屬反光（天空／地面的反射、菲涅耳、每盞燈的鏡面高光，照陰影）；方塊面不反光（跟遊戲一樣）。
  - 材質表：`Sculpture/materials.json`（雕刻自己一份，Tim 2026-10-16 拍板；⛔ 不讀遊戲的 `game.json` —— 兩邊各管各的，數值要對齊時手動抄）。
    格式 `{"schema":1,"list":{"iron":{"name":"鐵甲","metal":1,"rough":0.35}}}`：`metal`、`rough` 都是 0..1，沒寫 `rough` ⇒ 0.5；ID 小寫英數、底線、連字號。
  - 沒有檔 ⇒ 空表（平滑面一律不反光）；壞檔（不是 JSON、schema 不是 1、值越界、不認得的鍵）⇒ view 擋下 exit 2（⛔ 不默默當成沒有表）。
  - 表裡沒有的 ID ⇒ 照不反光畫，輸出那一行列出來。
- 環境反射取半球光的顏色（有 skybox ⇒ 全景上下半球的平均色），⛔ 不是照著 skybox 逐像素反射。
- CLI `op=view` 有平滑格才多印一行「· 平滑渲染：平滑 N 格｜材質 iron N｜…」與 value `smooth_voxels`；沒有平滑格的作品輸出、圖都跟以前逐位元相同（新舊 exe 的 sha256 對過：風暴角號、酒館房屋）。
- 子模型分開畫（觀測頁、`parts=1`）：render／material 沿關聯樹往下繼承，平滑的節點整份畫成平滑面（同一件作品、平滑與否不同 ⇒ 各一份網格）。⚠ 分開畫本來就不跨件剔除 ⇒ 平滑子作品跟旁邊的方塊件之間不釘格角（合併畫的 CLI view 會釘）。
- ⚠ 很緩的坡（每好幾格才高一格）：格角最多挪半格 ⇒ 階梯變成圓角的小坡，遠看還有淡淡的等高線條紋；近看才是連續的面。
- 範例：維吉尼亞號（`ame-virginia-01m-assembly`，平滑 99,571 格）、拿破崙號（`kotoko-napoleon-01m-assembly`，煙囪 28,900 格）、岩堡港（`summit-rocher-01m`，地形 2,062,102 格，CLI view 約 13 秒）。

### 5.6 點光源（TASK-0650）

Tim 2026-10-16：岩堡港的燈籠「可以有光源」。燈籠、營火在遊戲裡會放點光源（造船規格 §9 `light`），觀測頁與 CLI `op=view` 照同一套慣例畫（Blockside 60_render.js `isLamp`／`lampAt`）：

- **誰是燈**：外部模型或關聯的 meta `kind=light`（或 `lantern`）。不另外登記 —— 標了 meta 就是燈。
- **燈心**：來源作品的固定點 `wick`（格心），照模型的 at／pivot／rotate／scale／mirror 與關聯路徑一層層換到組裝件座標；
  沒有 `wick`（obj 模型、或來源作品沒登記）⇒ 外框中心（模型 ＝ obj 外框中心；關聯 ＝ 那件作品的空間中心）。
- **光色**：設定的 `lamp_color`；單盞的 meta `light_color=#rrggbb` 優先（例：風暴角號船尾的綠燈）。寫壞 ⇒ 照設定的光色、回傳檔記一行。⚠ 只有觀測讀它：遊戲的模型燈一律暖橘，船殼上的燈照 game.json 的 `rgb`。
- **照法**（跟遊戲 01_gl.js `lampK` 同式）：照到的面加上 底色 × 光色 × 強度 × a² × (0.35 ＋ 0.65 × N·L)，a ＝ 1 − 距離／半徑。⛔ 不投影子（光會穿過牆，跟遊戲一樣）；
  照 voxel、平滑面、外部模型、繩、帆；地板與海面不接。一張圖最多點 64 盞，多了取離鏡頭近的（正交取離注視點近的）。
- **光暈**（跟遊戲 `lanternHalo` 同一個意思）：每盞一片面向鏡頭的光暈，半徑 ＝ 照明半徑 × 0.25，往鏡頭挪半個光暈（燈心在燈罩裡）；加色、被前面的東西擋住、不擋別人。
- **天越暗燈越亮**（遊戲 `lampsUpdate` 的 1.6 − 天光）：`sun` < 1 時強度 × (1.6 − sun)／0.6 —— 白天 1 倍、`night=on`（sun 0.1）2.5 倍；光暈一起。
- **只改畫面**：不動格子、讀數、匯出。

| 欄位 | 預設 | 範圍 | 意思 |
|---|---|---|---|
| `lamps` | off | on／off | 開關；⚠ 不給 ⇒ 沿用下層（只換強度的層不改開關） |
| `lamp_intensity` | 0.6 | 0..8 | 強度（燈心旁正對它的面加上 底色 × 光色 × 本值） |
| `lamp_radius` | 6 | 0.01..1000 | 照多遠（**公尺**，Blockside `LAMP_R`；照主作品的一格幾公尺換成格：0.1 m／格 ⇒ 60 格） |
| `lamp_color` | #ff9e40 | #rrggbb | 光色（遊戲模型燈的暖橘） |

**疊層**：同海面 —— 內建預設 → 共用 → persona →（`profile=`）→ 作品海面 → **作品點光源**（主作品的 `lamps.json`）→ 展品 → CLI。`layers` 會印「作品點光源 `<id>`」。設定檔的 `lamps` 物件同形（`{"enabled":true,"intensity":0.6,"radius":6,"color":"#ff9e40"}`）。
**作品預設**：`work sub=lamps`（只有作者能改；只給一部分 ⇒ 其他欄位沿用檔裡的；第一次只給強度之類 ⇒ 當成要開；都不給 ⇒ 印現在的值）。寫在作品資料夾的 `lamps.json`；壞掉的檔 ⇒ view 擋下。
觀測頁「相機與光影（手動）」最後一段：點光源（沿用／開／關）＋強度、半徑、顏色；空白 ＝ 照作品預設。分開畫（`parts=1`）時燈跟著它的實例：關掉那個模型或關聯 ⇒ 那盞燈也熄。

```bash
senate cmd sculpture --arg op=work --arg sub=lamps --arg work=<港口> --arg persona=<P> --arg lamps=on            # 作品預設：點燈
senate cmd sculpture --arg op=view --arg work=<港口> --arg persona=<P> --arg lamps=on --arg lamp_intensity=1.2 --arg ambient=0.25   # 一次性：暗一點、燈亮一點
```

- 開燈時回傳多一行「· 點光源：N 盞（固定點 wick a、外框中心 b）」與 value `lamps`；回傳檔逐盞列出燈心座標（拿來核 wick 擺得對不對）。⚠ 固定點檔讀不了 ⇒ 那盞放外框中心、回傳檔記一行。
- **沒開燈 ⇒ 圖、輸出跟以前逐位元相同**（岩堡港新舊 exe 的 sha256 對過）。
- ⚠ 白天預設光下燈很淡（6 m 只照到旁邊一圈）；要看效果開夜景（§5.7 `night=on`），或拉近鏡頭。
- 落子後的分享圖（§7）只在共用展區出，那裡沒有外部模型與關聯 ⇒ 沒有燈可點（設定檔的 `lamps` 照樣疊，只是場景裡沒有燈）。
- 範例：
  - 岩堡港（`summit-rocher-01m`）：燈籠 22 盞＋營火 2 個，全部有 `wick`。
  - 風暴角號（`summit-stormkaap-01m-assembly`）：船尾三盞綠燈從 `-stern-deco` 拆成 `-stern-lamp-top／-port／-stbd`。
    - 用 `work sub=split` 拆，外觀逐格相同；三條雙生關聯 `船尾裝飾-燈-上／左舷／右舷`，meta 都是 `kind=light`、`light_color=#59ff8c`。
    - 遊戲端的綠燈照舊：關聯在 vxk 裡照樣攤成船殼的格，game.json 的 `lamps` 也照舊。

### 5.7 夜景 —— 壓暗或關掉太陽（TASK-0650）

Tim 2026-10-16：「請加夜景：可以壓暗或關掉太陽光（觀測頁跟 CLI 都要），開燈時燈籠周圍要明顯亮」。

- `sun`（0..1，預設 1）：平行光全部乘上它；天空、背景、環境光（含金屬反射的環境色）乘上 0.25 ＋ 0.75 × sun ⇒ 0 ＝ 沒有太陽、只剩一點夜空。設定檔頂層 `sun` 同義（`render-profile set sun=`、`unset=sun`）。
  - 色調刻度照白天算（⛔ 不跟著放大）⇒ 太陽暗了畫面就是暗。
- `night=on`（view／觀測頁）＝ `sun=0.1`＋`lamps=on`（強度、半徑、光色沿用下層）；同時給的 `sun=`、`lamps=` 以那兩格為準。`night=off` ＝ `sun=1`。
- 燈在夜裡會自己變亮（§5.6「天越暗燈越亮」），所以夜景不必另外調強度。
- 觀測頁「相機與光影（手動）」最後一段：夜景（沿用／開／關）＋ `sun` 欄。
- **白天（`sun` 1、沒給 `night`）⇒ 圖跟以前逐位元相同**（岩堡港 sha256 對過）。

```bash
senate cmd sculpture --arg op=view --arg work=summit-rocher-01m --arg persona=<P> --arg night=on                  # 夜景＋點燈
senate cmd sculpture --arg op=view --arg work=<船> --arg persona=<P> --arg sun=0.4 --arg lamps=on                 # 黃昏：太陽壓暗一半多
```

- 讀數（岩堡港 B3 房燈籠旁，透視 800×600，`night=on` 燈開／關）：
  - 平均亮度 14.2 → 20.8，亮了 > 30 的像素 6.1%、最多 +177。
  - 對照白天：只開燈、沒有夜景，近看平均差 +2.1。
- 讀數（風暴角號船尾近看）：平均亮度 11.3 → 26.7，亮了 > 30 的像素 17%。

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
  地板 `floor floor_z floor_full_grid floor_margin floor_texture floor_tile floor_color floor_fade`，海面 `sea sea_z sea_color sea_opacity`，點光源 `lamps lamp_intensity lamp_radius lamp_color`（§5.6），
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

每件作品存於 `Sculpture/works/<id>/`，不佔用共用256³展區。ID全庫唯一、不分大小寫，統一小寫，限1–64個英數、底線、連字號且不能是Windows保留名稱。`work.json`記錄固定作者、尺寸、名稱、建立UTC時間與付款計畫；`events/`是雕刻事實源，`sculpt_cache.json`可重建；心得與續作放`notes.md`、TODO放`todo.md`。其他persona可以觀測與匯入副本，只有作者可以修改、雕刻或Undo原作；沒有刪除／改作者入口。⭐ 例外（規則由 Tim 定，2026-10-16）：**不影響模型外觀的修改不限作者** —— meta、外部模型、關聯、固定點，以及拆件（§9.4.12）；不是作者改的會記進那件作品的 `meta_log.jsonl`（TASK-0646）。

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

後台`senate ui --page sculpture`：工具列兩排 —— 第一排「匯出」「切換 正交／透視」「重置鏡頭」與開資料夾，第二排「雕刻空間」下拉＋「類型」「作品」（個人作品）或「展品」（共用展區）下拉。「類型」：全部／船／房屋／城鎮／其他（選項上的件數照其他篩選條件算，每一類各算一次）；**房屋 ＝ 標籤 `house`、城鎮 ＝ 標籤 `town`（兩個都有算城鎮；船優先）—— 這兩類子作品也算、也不看 `MainOnly`（房屋本來就是城鎮的零件，TASK-0582）**；**船 ＝ 主作品自己的 links.json／models.json 有 meta `kind` 是造船規格 §9 那幾種（gun、magazine、powder、yard、ladders、wheel、binnacle、capstan、boat），或船殼關聯的 `ship`**（§9 表加了新 kind 要跟著改 `SculptureViewerPage.ShipKinds`），⛔ 子作品（標 `sub`）一律不算船 —— 它們是船的部件；作品卡沒有類型欄位，類型是推出來的（讀不了的件在工具列說出來、算「其他」）；渲染圖在內容區最上面。個人區可建立作品、保存心得與TODO、預覽及確認匯入。三份設定由`SCP_GuiInspector`自動畫、存在session（文字模式跨指令記得）：「作品篩選」`MainOnly`（預設開＝只列主作品，即沒標`sub`的）／`Owner`／`Keyword`（也比標籤），目前選的作品被篩掉也不換；「作品觀測設定」`Draft`（草稿模式，預設開）／`Links`／`HideLinks`；「匯出設定」`Format`（obj｜vox）／`MergeFaces`／`OutDir`。「開啟匯出資料夾」：`OutDir` 有填 ⇒ 開它；選了個人作品 ⇒ 那件作品的 `Sculpture/works/<id>/exports`（引擎預設寫這裡；匯出中、還沒匯出過也一樣）；共用展區 ⇒ 上一次匯出檔那一層，沒有就 `Sculpture/exports`。名稱／筆記欄位依作品ID保存草稿，切換作品不會套用上一件的文字；長筆記派送UTF-8 arg-file，鏡頭、切片與匯出仍走同一支CLI。

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

#### 大事件拆檔（TASK-0521）

一個事件檔寫到磁碟最多 **40 MB**（GitHub 單檔 50 MB 起警告、100 MB 拒收）。整件前後快照的刀（assemble、move、undo／redo、草稿 apply、噴漆）超過就自動拆成**頭檔＋續檔**：

- 名字：頭檔 `X.json`，續檔 `X_p2.json`、`X_p3.json`…（排序緊跟在頭檔後面）。每一格只落在其中一段 ⇒ 依序重播各段 ＝ 重播整刀。
- 頭檔：這一刀的 `action`／`target_event`／`credits`／`source`，最上層多 `parts`（共幾段）。續檔：`edit.action` 是 `part`，最上層帶 `part_of`（頭檔）與 `part`（第幾段）。
- **一刀還是一刀**：history 只數頭檔；undo／redo 整刀來回（undo 本身大也照樣拆）；時間點（`mark`、`upto`、`from`／`to`、草稿的底）指到頭檔或中間的續檔 ⇒ 都延伸到最後一段，不會停在半刀上。回傳值 `event_parts` ＞ 1 ＝ 這一刀拆過，`event_file` 是最後一段。
- 舊版 senate.exe 重播得出拆過的刀（只讀 `edit.after`）；它的 undo 會把每一段當一刀 ⇒ 撤拆過的刀之前先更新 exe。

拆檔之前就寫下的大檔用 `split-events` 就地拆（頭檔留原名 ⇒ undo 目標、標記照舊指得到；格式跟原檔一樣，縮排的照縮排、單行的照單行）：

```bash
senate cmd sculpture --arg op=work --arg sub=split-events --arg work=<作品> --arg persona=<作者>               # 試算：列出超過上限的、會拆成幾檔，一個位元組都不寫
senate cmd sculpture --arg op=work --arg sub=split-events --arg work=<作品> --arg persona=<作者> --arg confirm=1  # 改寫
```

回傳 `voxels`／`checksum`（同 `.vxk` 的 checksum）是**從頭重播**（不讀快取）的格子，並跟快取逐格比 —— 不同 ⇒ exit 1，先別提交。不是 workedit 的大檔不拆、照實列出。

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
- **替換色**（TASK-0584）：`recolor=舊色:新色,…`（例 `218:213,213:218` ＝ 互換）—— 同一件零件換個配色再用一次，⛔ 不用複製一件只改色的子作品。
  ```bash
  senate cmd sculpture --arg op=work --arg sub=link --arg work=town --arg persona=<P> --arg name=房A2 --arg source_work=house-a1 --arg at=120,40,0 --arg recolor=218:213,168:109,132:73,49:42
  senate cmd sculpture --arg op=work --arg sub=link --arg work=town --arg persona=<P> --arg name=房A2 --arg recolor=none   # 清掉（不用 overwrite）
  ```
  - 換的是那條關聯底下**整棵子樹組好的格**；巢狀時內層先換、外層看到的是**換完的色**（內層 40→50、外層寫 50:60 ⇒ 60；外層寫 40:70 用不到）。
  - **同時換、不連鎖**（一次查表）；表外的色不動；色 1..255（換成 0 等於挖掉 ⇒ 擋）；同一個舊色寫兩次、舊色＝新色、格式錯 ⇒ exit 2、links.json 不動。
  - 不動幾何 ⇒ 跟 `enabled`／`note`／`meta` 一樣直接改，不用 `overwrite`；`overwrite=1` 搬位置時替換表照留。`sub=links` 每條印 `recolor …`。
  - 觀測、`export`（list／obj／vxk）、攤平（import／assemble）、觀測頁 GPU 的分開畫（§5.4：同一件作品、有效替換表不同 ⇒ 各一份網格）看到的都是換過的色。
  - ⚠ links.json 只要有一條用了替換色就寫 **schema 4**（清掉就回到原本的號）：沒更新的 senate.exe 讀到會 exit 2「schema=4」，⛔ 而不是默默畫成原色。schema 3 以下的檔卻帶 `recolor` ⇒ 大聲讀不了（手改的，或舊版寫回去的）。
  - 只能換「一個舊色 → 一個新色」：同一個色在兩處要換成不同的色 ⇒ 先在零件裡把那兩群分成兩個色。
  - 樣本（量的，2026-10-16）：summit 岩堡港 `summit-rocher-01m-house-a2` ＝ `house-a1` 加 `recolor=218:213,168:109,132:73,49:42`（101,542 格，形狀相同、每個舊色只對到一個新色）⇒ 換成關聯可省一件約 1.47 MB 的雕刻庫檔。`house-a3` 形狀也相同，但色 218 有 12,324 格留著、22,626 格換成 246 ⇒ 單靠替換色做不到。
  - ⚠ vxk 裡關聯是展開成格子的 ⇒ 省的是雕刻庫，不是遊戲檔；遊戲檔要省得靠外部模型的替換色（TASK-0584 另一半，等跟 ame 對好 vxk）。
- **覆寫：改裝船**（TASK-0656；Tim 2026-10-16：「可以把一艘船當成子作品概念去做改裝船」「修改可以包含挖掉原本船體上的格子 移除原有的子模組 概念類似 Unity 的 nested prefab」「只要記錄 diff 的部分」）：
  一條關聯除了替換色，還能存兩種覆寫 —— 改的是**這條關聯看到的樣子**，被關聯的作品本身一格都不動（另一條關聯、另一艘船照舊看到原樣）。
  ```bash
  # 原船整艘關聯進來，拿掉它裡面的駕駛塔（關聯）、前艙蓋（甲板-main 這一層的外部模型），並照遮罩挖格子
  senate cmd sculpture --arg op=work --arg sub=link --arg work=<改裝船> --arg persona=<作者> --arg name=原船 --arg source_work=<原船組裝件> --arg at=0,0,0 \
      --arg link_hide=駕駛塔,甲板-main/艙蓋-前 --arg link_carve=<遮罩子作品>
  senate cmd sculpture --arg op=work --arg sub=link --arg work=<改裝船> --arg persona=<作者> --arg name=原船 --arg link_hide=none   # 清掉（不用 overwrite）
  ```
  - `link_hide=路徑,…`：被關聯的作品**裡面**的關聯或外部模型，路徑相對這條關聯（`/` 分層）。整條都是關聯 ⇒ 連同底下剪掉；最後一段是那一層作品的外部模型 ⇒ 那個模型不在。
    讀關聯樹的時候就剪（`SCP_SculptLinks.Tree`）⇒ 觀測、`export`（list／obj／vxk：部件外框、固定點、模型都少它）、攤平、繩、帆、觀測頁 GPU 預覽都當它不在；`hide_links=` 點不到被剪掉的路徑。
  - `link_carve=<遮罩作品>`：遮罩是一件普通的子作品，**尺寸跟被關聯的作品一樣、用它自己的座標**；遮罩上有格（顏色不管）的地方，這條關聯底下**整棵組好的格**（含巢狀進來的）就少那一格。
    在遮罩上 `stampvox`／`box` ＝ 挖、`carvevox` ＝ 補回來 —— 就是一般的雕刻，版本、undo、草稿照舊。只挖格子；外部模型要拿掉用 `link_hide`。挖在轉向之前 ⇒ 關聯轉了、翻了，遮罩照樣對得上。
  - 讀數：`link_hide_applied`（每條拿掉的是關聯還是模型）；觀測與匯出的關聯列「照遮罩 <作品> 挖掉 N」、`link_carved`；export 檔頭 `link …` 那一行多 `carve <遮罩>@<版本> carved N`（沒用 carve ⇒ 跟以前一樣）。`sub=links` 每條印「拿掉 …」「挖格子照遮罩 …」。
  - 🔴 擋下（links.json 不動）：路徑找不到（⛔ 打錯字不會變成「什麼都沒拿掉」）、遮罩尺寸跟被關聯的作品不一樣、遮罩是被關聯的作品自己或它的上層、**不是作者**（覆寫會改外觀 ⇒ 不走 TASK-0646 的「不影響外觀不限作者」）。換了被關聯的作品（overwrite 改 source_work）⇒ 舊的覆寫對不上，沒重給就清掉。
  - ⚠ links.json 只要有一條用了覆寫就寫 **schema 5**（清掉回到原本的號）：沒更新的 senate.exe 讀到會 exit 2「schema=5」，⛔ 而不是默默畫成原船。schema 4 以下的檔卻帶 `hide`／`carve` ⇒ 大聲讀不了。
  - 鎖：遮罩作品也在關聯樹的鎖裡（`Closure`）。
  - 實例：哨兵號 `meadow-sentinel-01m`（2026-10-16）：`原船` → `meadow-monitor-01m-assembly`，`link_hide=駕駛塔`、`link_carve=meadow-sentinel-01m-carve`。造船那一側的做法見 Sculpture_Shipbuilding §11。
- **寫入只動本作品**：父作品的刀碰不到被關聯的作品（要改去改那件作品）；落子後回報 `linked_hits`（這一刀的範圍裡有幾格是關聯進來的，沒被動到）。
- **擋下**：成環（含隔好幾層、含停用的關聯；跟「作品當模型」一起繞成的也算，見 §9.4.8）、關聯自己、作品不存在、放不下、保留字名（none／all／self／start／now）、名字含 `/`。關聯的增刪先拿 registry 鎖（兩個人同時互相關聯也擋得下）。
- **攤平**：`work sub=import`（匯入展區）與 `work sub=assemble`（來源有關聯）照 enabled 攤平成實際 voxel，Credit 逐件記；import 的 revision 照攤平後的算 ⇒ 子作品一改，expect_revision 就對不上。`work show` 另給 `composed_revision`／`composed_voxels`；Credit 多一段「關聯（現組）」。export 檔頭記每條用了哪個版本。
- **效能與鎖**：被關聯的作品讀它自己的快取（只讀、不寫回）；同一件只讀一次（28 門砲關聯同一件 cannon 也只讀一次）。鎖：作品＋關聯樹上所有作品依 ID 排序一次拿齊（全系統只有這一種鎖序）。
- `diff` 只比本作品自己的事件（不含關聯）；`mark` 回到過去時，被關聯的作品照現在。觀測頁：「只組這些關聯／拿掉這些關聯」兩欄；被關聯的作品改了之後，預覽要換一下參數（或按渲染）才會重組。
- **觀測頁的關聯開關**（TASK-0499）：選了有關聯的作品，「作品」下拉底下列出整棵關聯樹（巢狀縮排），**一條一個 toggle**，預設照那一條在作品裡的 `enabled`。關掉一條 ⇒ 連同底下都不畫；只關底下那條 ⇒ 上一層照畫；作品裡設為關的也可以這次打開。頁面把開關換成 `links=`（畫的那幾條全部點名）＋`hide_links=`（關掉而上一層有畫的）送出去 —— ⚠ 只送 `links=` 不夠：點名那條底下沒點名的會照自己的 enabled 被畫回來。全部在預設 ⇒ 什麼都不送；全關 ⇒ `links=none`。⛔ 開關**不寫回** links.json（長期開關用 `work sub=link enabled=0|1`）。開關的 id 帶作品 id ⇒ 換作品清單就換。**兩個文字欄有填 ⇒ 以文字欄為準**，開關這一次不生效（頁面上有寫）。GPU 預覽每幀照開關組參數；spawn 出圖（文字模式）要按渲染。
  - **Unity Hierarchy 式**（TASK-0500）：子模組的子模組（幾層都行）一樣列、一樣開關。勾 ＝ 那一條**自己的**開關（像 activeSelf）；上一層關了 ⇒ 底下每一層都不畫，但它們的勾照留、標「◌ 上層關著 ⇒ 不畫」（像 activeInHierarchy），上一層打開就照自己的勾回來。有子關聯的那一條前面有 ▶／▼ 折疊鈕（方形、跟勾選框一樣大；**預設折起來**），折起來標「底下 N 條」；另有「全部展開／全部折疊」。**折疊只管列不列，不管畫不畫**。開關與折疊都住頁面 session，⛔ 不寫回 links.json。
- **預覽 preset**（TASK-0571）：作品存幾組「這次只看／藏哪些」，觀測時用名字叫出來 —— 看藏在船殼裡面、已完工的部分。存在作品的 `view_presets.json`（入版控，⛔ 不是 voxel、不進 events）。
  ```bash
  senate cmd sculpture --arg op=work --arg sub=preset --arg work=ship --arg persona=<P> --arg name=藏船殼 --arg hide_links=船殼 --arg note=看甲板
  senate cmd sculpture --arg op=work --arg sub=presets --arg work=ship            # 列出；sub=unpreset name= 拿掉
  senate cmd sculpture --arg op=view --arg work=ship --arg preset=藏船殼 --arg out=<png>
  ```
  - 收的參數跟 CLI 同名、存原字串：links、hide_links、region、exclude_color、models、hide_models、ropes、hide_ropes、rope_groups、hide_rope_groups、sails、hide_sails、note。改一組：有給的蓋過去（給空字串 ＝ 清掉那一格），沒給的不動。links／hide_links 的路徑存的時候就驗；其他的照觀測時的規則驗。只有作者能改。
  - `preset=` 只給觀測類 op；`all`（＝ 不給）正常顯示，是保留字。繩、帆、模型的鍵只有 view／export 吃，slice／section／stats 略過（回報印略過哪幾個）。名字打錯 ⇒ exit 2、列出有哪幾組；preset 跟手打的同一個參數同時給 ⇒ exit 2（⛔ 不默默挑一個）。
  - 觀測頁：工具列「作品」下拉旁邊的「預覽 preset」下拉，預設 all；選一組 ⇒ Links／HideLinks 文字欄與關聯／繩／帆／模型開關這一次不生效（頁面上有寫），換作品回到 all。頁面與 CLI 用同一支展開（`SCP_SculptViewPresets.Expand`）⇒ 送出去的參數一樣。
- ⭐ 慣例（甲板這種切出來的區域）：子作品尺寸 ＝ 父作品、`at=0,0,0`、`turn=0` ⇒ 子作品直接用父作品的座標雕，剖面校準也能共用。
- **meta**（TASK-0517）：關聯可以帶一包給下游認部件的資訊，寫法與規則同外部模型（§9.4.9）；只改 meta 不用 overwrite，overwrite 搬位置 meta 照留。用到 meta 的 links.json 寫 **schema 3**。

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

帆與索具不用 voxel 雕（Tim）⇒ 作品可以記**固定點**（名字 → 格座標）與**繩**（兩端、顏色、粗細、鬆緊）；兩份都是作品資料（`anchors.json`／`ropes.json`，入版控，⛔ 不進 events），**一格 voxel 都不動、不收費**。固定點不限作者（TASK-0646：不影響外觀，給遊戲認關鍵點）；繩改的只有作者。觀測時才算形狀、才畫。

```bash
# 固定點：點取那一格的格心（x+0.5, y+0.5, z+0.5）
senate cmd sculpture --arg op=work --arg sub=anchor --arg work=main-mast --arg persona=<P> --arg name=masthead --arg at=4,4,180
senate cmd sculpture --arg op=work --arg sub=anchors --arg work=ship                 # 本作品的＋關聯樹上每一層的（路徑指名、換到本作品座標）
# 繩：端點 ＝ 固定點名｜關聯/…/固定點名（相對於繩所在的作品）｜x,y,z（整數 ＝ 格心，可帶小數）
senate cmd sculpture --arg op=work --arg sub=rope --arg work=ship --arg persona=<P> --arg name=main-stay --arg from=bow-channel --arg to=主桅/masthead --arg slack=1.02 --arg color=#5a4632 --arg thickness=0.6
senate cmd sculpture --arg op=work --arg sub=rope --arg work=ship --arg persona=<P> --arg name=main-stay --arg slack=1.1        # 改鬆緊／顏色／粗細／開關不用 overwrite
senate cmd sculpture --arg op=work --arg sub=ropes --arg work=ship                   # 每條的長度、最大下垂、狀態（讀數）
senate cmd sculpture --arg op=view --arg work=ship --arg hide_ropes=main-stay --arg out=<png>   # 這次拿掉一條；ropes=0 ＝ 這次一條都不畫
# 子繩：端點寫 rope:[關聯/…/]繩名/比例（0..1，沿母繩的長度比）⇒ 掛在母繩上、跟著它動、兩端拉直
senate cmd sculpture --arg op=work --arg sub=rope --arg work=ship --arg persona=<P> --arg name=ratline-01 --arg from=rope:shroud-1/0.2 --arg to=rope:shroud-4/0.2 --arg thickness=0.3
# 分組：一條一條歸組，或 regex 一次歸一批；一組（連同子組）平常開關
senate cmd sculpture --arg op=work --arg sub=rope --arg work=ship --arg persona=<P> --arg name=main-stay --arg group=standing/stays
senate cmd sculpture --arg op=work --arg sub=ropegroup --arg work=ship --arg persona=<P> --arg name=standing/ratlines --arg assign="ratline-"
senate cmd sculpture --arg op=work --arg sub=ropegroup --arg work=ship --arg persona=<P> --arg name=standing/ratlines --arg enabled=0
senate cmd sculpture --arg op=view --arg work=ship --arg hide_rope_groups=standing/ratlines --arg out=<png>   # 這次拿掉一組；rope_groups= ＝ 這次只畫這幾組
```

- **固定點** `sub=anchor name= at=x,y,z [note=]`：要在作品尺寸裡；同名搬位置要 `overwrite=1`（綁在上面的繩會跟著動，⛔ 不默默搬）。`sub=unanchor name=` 拿掉。名字的規矩同關聯名（不含 `: , | / \`、不是保留字）。
- **繩** `sub=rope name= from= to= [slack=] [color=] [thickness=] [enabled=] [group=] [note=]`：
  - `slack` ＝ 繩長 ÷ 兩端直線距離（1 ＝ 拉直、1.02 微垂、1.2 明顯下垂；上限 10）。`color` ＝ 調色盤 index 1..255 或 `#RRGGBB`（存小寫）；`thickness` ＝ **直徑**（格，可帶小數，預設 0.5）。
  - 同名已在：改 slack／color／thickness／enabled／group／note 直接改；**換兩端**要 `overwrite=1`。
  - 寫之前先解一次：端點解不到（固定點或關聯路徑打錯）⇒ exit 2、**未寫入**（⛔ 打錯字不會變成一條壞繩）。`sub=unrope name=` 拿掉；上面還掛著子繩 ⇒ exit 2、列出子繩（全庫掃）、未寫入。
- **形狀**：只受重力（−z）⇒ 懸鏈線解析解（同輸入同形狀，決定性）。拉直 ⇒ 直線；兩端在同一條鉛直線上 ⇒ 畫直線並標明（鬆度沒有往旁邊垂的方向）。⚠ **沒有碰撞**：繩會穿過格子（垂得太多就穿進船身）——鬆度自己控制，看 `rope_max_sag`。風與其他力等帆那張單（TASK-0503）。
- **讀數**：`sub=rope` 寫完印 `rope_length`／`rope_chord`／`rope_max_sag`；`sub=ropes` 與 view／export 的回報每條一行：狀態、兩端（名字＋座標）、長、直線距離、slack、最大下垂（離兩端連線往下最多幾格）、色、粗。
- **跨作品**：端點寫關聯路徑（`主桅/masthead`、`甲板2/船長室/hook`），座標照路徑上每一層關聯的 `mirror`→`turn`→`at` 換到組裝件 —— 跟 voxel 同一個公式（格心換過去正好是那一格換過去的格心）。⇒ 搬關聯（`at`）、轉向、鏡像（右舷那根桅的固定點翻到另一舷）繩都跟著走。被關聯進來的作品**自己的繩**也照那條關聯換座標、一起畫（繩路徑 ＝ `關聯/…/繩名`）。
- **跟著不畫**：綁在某一層上的繩，那一層這次沒畫（`links=none`、`hide_links`、那條關聯 `enabled=0`）⇒ 繩 `hidden`；繩自己 `enabled=0` ⇒ `disabled`；`ropes=0`／`hide_ropes=繩路徑,…` ⇒ 這次不畫（打錯路徑 ⇒ exit 2）。
- **壞掉的綁定**（固定點刪了／改名、關聯路徑不在了、固定點檔壞了）⇒ `broken`：⛔ 不靜默消失 —— 觀測照樣出圖，但回傳行大聲列 🔴 與原因（`ropes_broken`）；`sub=unanchor` 當下就全庫掃一次，印出哪幾條繩（含上層作品用關聯路徑綁的）現在壞了（`ropes_now_broken`）。把固定點用原名加回去就接上。
- **畫**：view 與觀測頁的 GPU 預覽把繩畫成管子，跟 voxel 同一個網格（吃同一組燈、會投影子、也接影子）；繩不是 voxel ⇒ `visible_voxels` 不變。回傳值 `ropes_drawn`／`ropes_hidden`／`ropes_disabled`／`ropes_broken`。`slice`／`section`／`stats` 量的是格子，不畫繩。
- **觀測頁**：選了有繩的作品，「繩索開關」：總開關「畫繩索」＋**一組一個勾**（照組樹排，取消勾 ⇒ `hide_rope_groups`，連同子組）；組裡的繩收在折疊裡、一條一個勾（`hide_ropes`），沒分組的收在「（未分組）」；完全沒分組的作品照舊一條一個勾。只影響這次觀測、⛔ 不寫回 ropes.json；壞掉的列 🔴＋原因；作品裡設為關（自己或組）的只列不勾。
- **匯出**：`export format=obj` 把繩接在 voxel 面後面輸出成管狀網格（每條一個 `o rope_n`＋材質 `rope_n`，mtl 有 Kd；有法線、繞序跟法線同向）⇒ 匯進 Unity 看得到。`format=list|vox` 只有格子 ⇒ 繩不輸出、`ropes_exported=0`。
- **帆**：端點寫帆角 `sail:[關聯/…/]帆名/角` ⇒ 繩綁在帆的角上（TASK-0503，見 §9.4.7）；其他 `種類:` 一律 exit 2（不猜）。固定點與繩的格式就是帆共用的那一份。
- **子繩**（TASK-0511）：端點寫 `rope:[關聯/…/]繩名/比例`（相對於繩所在的作品；比例 0..1 ＝ 沿母繩**實際的曲線**量的長度比，0 ＝ 母繩的 from、1 ＝ to）⇒ 那一端掛在母繩上。
  - **不算物理**：兩端之間拉直（`slack` 只能是 1）；另一端可以是固定點、座標、模型上的點或另一條繩，⛔ 不能是帆角。子繩上可以再掛子繩。
  - **跟著母繩**：母繩換端點、改 slack、跟著關聯搬、被帆拉動（觀測頁持續模擬時每幀跟）⇒ 子繩跟著走，不用重下。母繩這次沒畫（`hide_ropes`、`hide_rope_groups`、`enabled=0`、組關著、所在關聯沒組進來）⇒ 子繩跟著不畫；母繩只是沒被 `rope_groups` 點到 ⇒ 子繩照自己的組判（點名只畫 ratlines 就看得到 ratlines）。
  - **擋**：沒有這條母繩、比例超出 0..1、繞成環（印出環）⇒ 寫入 exit 2、未寫入；手改檔弄出來的環 ⇒ 觀測時環上每條都 `broken`。
  - **畫**：3 邊的細管（一段 6 個三角形；一般細繩 16 個）—— view、觀測頁、`export obj` 同一支。回傳值多 `ropes_child`。
- **分組**（TASK-0512）：繩的 `group` ＝ 組路徑（`/` 分層，例 `standing/shrouds`；`group=none` 不分組）。`sub=ropegroup name=<組> [assign=<繩名 regex>] [enabled=0|1] [note=]`：`assign` 把本作品名字符合的繩歸進這組（一條都沒符合 ⇒ exit 2）；`enabled=0` ⇒ 這組連同子組**平常**不畫（state `disabled`，原因寫哪一組）；組裡沒有繩 ⇒ exit 2。
  - **全路徑** ＝ 繩所在那一層的關聯路徑＋`/`＋組路徑（組裝件自己的就是組路徑；桅上的 `stays` 在組裝件看是 `主桅/stays`）。觀測 `rope_groups=全路徑,…`（這次只畫這幾組）／`hide_rope_groups=全路徑,…`（這次拿掉），都連同子組；打錯 ⇒ exit 2。`sub=ropes` 多一段「繩的組」：每組幾條（含子組）、各狀態幾條、平常開關。
  - 用到組或子繩的 `ropes.json` 寫 `schema 2` ⇒ 沒更新的 senate.exe 讀到會**大聲讀不了**（⛔ 不會默默丟掉組再寫回去）；都沒用的照舊 `schema 1`。
- **橫繩組**（TASK-0678）：ratlines 那種「兩條繩之間一道一道的橫繩」不用一條一條下子繩 —— 只記兩條母繩＋間距，**觀測時**才照母繩現在的形狀展開。
  ```bash
  senate cmd sculpture --arg op=work --arg sub=ratlines --arg work=ship --arg persona=<P> --arg name=fore-lower-P --arg rope_a=fore-shroud-P1 --arg rope_b=fore-shroud-P8 --arg step=4 --arg start=7 --arg stop=16 --arg min_span=5 --arg thickness=0.3 --arg color=#2b2620 --arg group=ratlines/lower
  senate cmd sculpture --arg op=work --arg sub=ratlines --arg work=ship --arg persona=<P> --arg name=fore-lower-P --arg climb=1 --arg foot=567,116,105 --arg head=582,132,264   # 繩梯
  senate cmd sculpture --arg op=work --arg sub=unratlines --arg work=ship --arg persona=<P> --arg name=fore-lower-P
  ```
  - **展開**：z 從「兩條母繩較高的那個下端＋`start`」到「較低的那個上端−`stop`」，每 `step` 格一道（z0＋k·step，等高 ⇒ 水平）；每道就是一條子繩 `rope:<rope_a>/比例 → rope:<rope_b>/比例`（比例照母繩**實際的曲線**算：垂下來的繩取靠下端那一次穿過），名字 `組名.01`、`.02`…（由下往上）、組／色／粗照這一組。一道兩端距離短於 `min_span` ⇒ 停（母繩往上收攏）；上限 400 道。
  - **跟著母繩**：母繩換端點、改 slack ⇒ 下一次觀測就重排（⛔ 不用重下）；展開的橫繩跟手寫的子繩同一支解、同一支畫 ⇒ view、觀測頁、`export obj`／`vxk`、`hide_ropes`（路徑 `組名.NN`）、`rope_groups` 都照舊。母繩上有橫繩 ⇒ `unrope` 擋（同子繩）。
  - **只看兩條母繩**：中間的其他 shroud 不算 ⇒ 起點是**這兩條**較高的下端。（憲法號：舊的 `ratlines.py` 拿整排最高的 deadeye，換成橫繩組後每道低 0.00–0.02 格。）
  - **繩梯**：`climb=1` 要給 `foot`／`head`（固定點｜關聯/…/固定點｜x,y,z｜model:；整數 ＝ 格心）—— 兩個**站得住**的格（腳下實心、上面 7 格空，同遊戲的 `standZ`）；寫入前解一次，解不到 ⇒ exit 2。展開的每道帶 meta：`kind=ratline`、`ratlines=<組路徑>`、`rung`、`rungs`，繩梯再加 `climb=1`、`foot`／`head`（組裝件座標 `x,y,z`，同繩的 pts）。`export vxk include=rig,meta` 照常帶出 ⇒ 遊戲認成一座梯子（foot → 最低一道 → 最高一道 → head）。
  - **擋**（exit 2、零寫入）：母繩不存在、a＝b、step 不在 0.5..1000、start／stop／min_span < 0、climb 沒給落腳點、落腳點是 `rope:`／`sail:`、名字跟繩撞（`組名` 或 `組名.…`）、換母繩沒 `overwrite=1`、一道都展不出來。觀測時母繩或落腳點壞了 ⇒ 整組一筆 broken（路徑 ＝ 組名，原因寫哪一條）。
  - `sub=ropes` 多一段「橫繩組」：每組幾道、z 範圍、最短／最長一道、狀態、🪜 繩梯的兩個落腳點。有橫繩組的 `ropes.json` 寫 `schema 4`（沒更新的 senate.exe 讀到會大聲讀不了）；拿掉最後一組 ⇒ 檔逐位元組回原樣。
  - **不限作者**（Tim 2026-10-17「遊戲meta相關可以調整」）：橫繩組（間距、繩梯 climb／foot／head）是給遊戲的配置 ⇒ `sub=ratlines`／`unratlines` 誰都能改（要給 persona）；不是作者改的記進那件作品的 `meta_log.jsonl`（part `ratlines`、只記有給的參數、rule 寫出處）。一條一條的繩（`sub=rope`／`unrope`／`ropegroup`）照舊只有作者。

#### 繩端綁定檢查 `work sub=ropecheck`（TASK-0716）

給遊戲用：遊戲照繩的 `ends`（`model:`／`rope:`／`sail:`／固定點）綁；寫成座標 `x,y,z` 的那一端只能照位置猜 —— 猜錯的時候砲被打掉、錨拋下去，旁邊的支索會跟著斷。這支把每條繩的兩端分類、猜得到的照報表改，給人核對。

```bash
senate cmd sculpture --arg op=work --arg sub=ropecheck --arg work=<組裝件> --arg persona=<你>             # 只讀：報表 letters/<你>/cmd/ropecheck_<作品>.md（或 out=<絕對路徑>）
senate cmd sculpture --arg op=work --arg sub=ropecheck --arg work=<組裝件> --arg persona=<你> --arg fix=1  # 照報表裡的 bind／snap 改寫繩端
```

| 分類 | 什麼時候 | fix=1 |
|---|---|---|
| ok | `model:`／`rope:`／`sail:`／固定點（作品寫明了）；座標端離實心格 ≤ 15 cm | 不動 |
| onrope | 座標端就在另一條繩上（≤ 3 格，跟遊戲找母繩同一個門檻）—— 遊戲照母繩綁 | 不動（寫成 `rope:` 會變成拉直的子繩、外貌會變） |
| bind | 座標端在某個模型的網格表面上（≤ 5 cm），而且沒有第二個一樣近 | 改寫成 `model:<模型>/<模型座標>`（反算回去跟原本同一點 ⇒ **端點一點都不動**） |
| snap | 不貼格、不在模型上，離實心格 ≤ 30 cm | 挪到那一格表面上最近的一點（最多 30 cm） |
| odd | 索具（meta `kind=shroud／stay／backstay／ratline／sheet／tack／steering`）的端點在砲、火藥桶、燈、槳、錨、小艇上 | 不動（多半是擺錯，自己看） |
| manual | 離實心格與模型都太遠 | 不動（自己決定綁哪裡） |
| broken | 繩本身綁定壞了 | 不動 |

- 報表：分類的端數、每一個不是 ok 的端點一行（繩、哪一端、現在寫什麼、最近的實心格與模型各多遠、改成什麼、為什麼）；照座標近看核對。
- **不限作者**（Tim 2026-10-18「這有關遊戲 不用作者本人修改 維持外貌接近即可」）：`fix=1` 誰都能跑，每改一端在那件作品的 `meta_log.jsonl` 記一筆（op `ropecheck`、before／after、為什麼）。只改繩的 from／to，格子、模型、帆一格都不動。
- 改的時候那一端已經不是分析時的寫法（別人剛改過）⇒ 那一端跳過、照實列出。
- 讀數（2026-10-18，Blockside 17 艘）：只有炎羽號 2 端 snap（兩條 stay 的尾端離實心格 20 cm）；其他不是 ok 的都是 onrope（側支索接三眼滑車那類）。

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

- **帆** `sub=sail name= corners=角0|角1|角2[|角3] [corner_names=名|名|…] [bind=邊,…] [free=角,…] [color=] [texture=<png>|none] [res=] [fullness=] [enabled=] [note=]`：
  - 角 3 個 ＝ 三角帆（jib、staysail）、4 個 ＝ 四邊形（方帆、梯形的縱帆 spanker）；**依序繞一圈**，`|` 分隔。
  - `bind=k`：**邊 k ＝ 角 k → 角 k+1** 整條釘在兩角之間的線段上（方帆上緣沿 yard、縱帆沿 gaff／boom）—— 那條邊上的每一個布點都釘住，不是只釘兩個角。
  - 角預設**釘住**；`free=` 的角（名字或序號）跟著布動。綁在 free 角上的繩會拉住它（見下）；沒綁繩的 free 角就隨風亂飛。
  - `color` ＝ 調色盤 index 1..255 或 `#RRGGBB`（存小寫；預設 `#f2ead8` 帆布色）。`res` ＝ 布料每邊幾格（0 ＝ 照大小自動，6..24）。`fullness` ＝ 布比角圍出來的框多幾成（預設 1.06；1 ＝ 繃平、鼓不起來；上限 1.5）。
  - **貼圖**（TASK-0530；例：拿帆當旗，圖用像素畫）`texture=<png 路徑>`：PNG 每邊 ≤ 128 像素，**複製進作品**的 `textures/<帆名>.png`（帆名不是英數＿- ⇒ `textures/sail_<8 碼>.png`），`sails.json` 記相對路徑；`texture=none` 拿掉（沒有別的帆用那張圖 ⇒ 圖檔刪掉，`sub=unsail` 同）。
    - 對應：圖的**左上角 ＝ 角 0**、欄 → 角 0→角 1、列 → 角 0→角 3（三角帆：角 0→角 2，像素中心落在斜邊外的不畫）；alpha < 128 的像素不畫（燕尾旗）；背面是鏡像。
    - 圖跟著布鼓、像素保持銳利（每個像素一塊平色，不內插）。貼圖不影響模擬：形狀跟沒貼圖時逐位元相同。
    - 有任何一面帆用貼圖 ⇒ `sails.json` schema 2（舊版 exe 讀到大聲擋）；都沒有 ⇒ schema 1、檔裡不出現 `texture`。壞圖、太大、不是 PNG ⇒ exit 2、**未寫入**。
  - 同名已在：改 free／bind／color／texture／res／fullness／enabled／note／corner_names 直接改；**換角的位置**要 `overwrite=1`。寫之前先解一次：角解不到 ⇒ exit 2、**未寫入**。`sub=unsail name=` 拿掉（綁它角的繩之後會是 broken，當下全庫掃一次印出來）。
- **模擬**：PBD 布料（距離約束：相鄰／對角／隔一格）＋**重力**＋**風**（只算布面法線方向的風壓，含固定的陣風起伏）；固定 dt 1/60 秒、每步固定迭代次數、單執行緒依序走 ⇒ **同樣的輸入＋同樣的秒數 ⇒ 逐位元同樣的形狀**。不用算到靜止（Tim）：
  - CLI `view`／`export`：取「模擬 `sail_seconds` 秒後」那一刻（不給 ⇒ 作品預設，預設 3 秒；上限 60）。
  - 觀測頁：「持續模擬」開著 ⇒ 帆會動；**每 0.1 秒更新一次、一次最多約 8 ms**（Tim「不用頻繁模擬」）—— 帆多、算不完就慢動作播放，⛔ 不改 dt、不拖垮畫面。關掉 ⇒ 停在 `sail_seconds` 那一刻（跟 CLI 同一張）。
  - 單位：重力 9.81 m/s²、風 `wind=x,y,z`（**m/s，世界座標**；例 `0,-8,0` ＝ 往 −y 吹的 8 m/s 風），照**組裝件**的 `meters_per_voxel` 換成格 ⇒ 比例對了，鼓的程度才對（憲法號一格 0.1 m）。預設風存在組裝件的 `sails.json`（`sub=wind`），`view`／`export`／`sub=sails` 的 `wind=`／`sail_seconds=` 一次性蓋過。
  - ⚠ **沒有碰撞**：帆會穿過格子與繩（同繩）—— 綁點與 fullness 自己控制，看讀數。
- **讀數**（`sub=sails` 與 view／export 的回報，每面帆一行）：狀態、角（名字＋座標、哪幾個 free）、釘住的邊、解析度與布點數、**最鼓**（離綁點平面最遠那一點幾格，帶正負號：正 ＝ 往「角依序繞一圈的右手法線」那側）、**平均位移**（全部布點離設計位置，世界座標 —— 沒風 ⇒ 幾乎只往 −z）、牽繩條數、貼圖（尺寸、幾色、畫幾個像素）；環境一行：風、一格幾公尺、模擬了幾秒幾步、平均每步幾毫秒。回傳值 `sails_drawn／hidden／disabled／broken`、`sail_seconds`、`sail_steps`、`sail_step_ms`、`sail_max_billow`。
  - 貼圖讀不了（檔不見、壞掉）⇒ 照 `color` 畫、那一行印 ⚠ 與原因、回傳 `sail_texture_broken`（⛔ 不靜默）。
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
- **匯出**：`export format=obj` 把帆（模擬到 `sail_seconds` 那一刻）接在繩後面輸出成雙面三角網格（每份網格一個 `o sail_n`＋材質 `sail_n`，mtl 有 Kd；有法線、繞序跟法線同向；帶貼圖的帆每色一份），綁帆角的繩也一起 ⇒ 匯進 Unity 看得到。`sails_exported` ＝ 幾面帆、`sail_meshes_exported` ＝ 幾份網格。`format=list|vox` 只有格子 ⇒ `sails_exported=0`。
  - `format=vxk include=rig`：帆**一面一份**（遊戲拿它跑布料物理）；帶貼圖的多一個 `texture`：`file`、`w`／`h`、`rgba`（RGBA8 列優先、第 0 列是圖的最上面、base64）、`alpha_min`、`uv`（跟 `verts` 一一對應、每頂點 2 個；u ＝ 角 0→角 1、v ＝ 角 0→角 3／三角帆角 0→角 2）。沒貼圖的帆輸出不變。
- **讀數**（淨室 SculptureSailsCleanRoom，Release）：24 面帆、每面 25×25 布點，模擬 3 秒（180 步）約 1.2 秒、每步約 6.8 ms。
- 收帆（furled，捲在 yard 上）、縮帆（reef）不做。

### 9.4.8 外部模型 —— 匯入 obj、或拿另一件作品當模型（TASK-0507）

作品可以擺**三角網格模型**：一個 obj 檔，或另一件雕刻作品（**不匯入 voxel**，而是匯出成 obj 快取在本地再畫）。存在 `models.json`（作品資料，入版控，⛔ 不進 events），**一格 voxel 都不動、不收費**，不限作者（TASK-0646；不是作者改的記進 `meta_log.jsonl`）。模型可以任意角度旋轉、縮放 —— voxel 關聯只能轉 90°、不能縮。

```bash
# obj 檔：複製進作品（models/<名字>/），之後作品自己帶著它
senate cmd sculpture --arg op=work --arg sub=model --arg work=ship --arg persona=<P> --arg name=lantern --arg file=D:/tmp/lantern.obj --arg axes=yup --arg at=512,160,140 --arg scale=10
# 作品當模型：比例照兩件作品的一格幾公尺自動換算（0.02 m 的零件放進 0.1 m 的船 ⇒ scale 0.2）
senate cmd sculpture --arg op=work --arg sub=model --arg work=ship --arg persona=<P> --arg name=wheel --arg source_work=ship-wheel --arg pivot=40,6,40 --arg at=180,160,131.5 --arg rotate=0,0,90
senate cmd sculpture --arg op=work --arg sub=model --arg work=ship --arg persona=<P> --arg name=wheel --arg rotate=0,0,95   # 只改變換：不用 overwrite
senate cmd sculpture --arg op=work --arg sub=models --arg work=ship                                                         # 每個模型的讀數
senate cmd sculpture --arg op=work --arg sub=components [--arg kind=gun]                                                      # 全部作品：哪些作品被當模型用、誰在用（找現有元件）
# 繩綁在模型上：model:[關聯/…/]模型名/點（點 ＝ 模型自己的座標，或來源作品的固定點名）
senate cmd sculpture --arg op=work --arg sub=rope --arg work=ship --arg persona=<P> --arg name=tiller-rope --arg from=model:wheel/drum_p --arg to=tiller-block
senate cmd sculpture --arg op=view --arg work=ship --arg hide_models=wheel --arg out=<png>   # 這次拿掉；models=0 ＝ 一個都不畫
```

- **`sub=model name= (file=<obj> [axes=] | source_work=<作品>) [at=] [pivot=] [rotate=] [scale=] [mirror=] [color=] [enabled=] [note=]`**：
  - **座標三層**：① 檔案座標（obj 自己的 x,y,z）→ ② 模型座標（z 朝上）→ ③ 作品座標。`axes` 管 ①→②：`yup`（預設；Blender／Maya 匯出的 obj）＝ (x, −z, y)、`zup` ＝ 原樣、`sculpt` ＝ 本引擎 `export` 出來的 obj（(x, z, y)，export 的反函數）。②→③：**p ＝ at ＋ R·(scale ⊙ mirror(p − pivot))**，`rotate=rx,ry,rz`（度）先繞 X、再 Y、再 Z（都繞作品的軸）；`scale` 一個數或三個數（> 0）；`mirror=x|y|z`（組合）在模型座標裡繞 pivot 翻面。at／pivot 可帶小數。
  - **file 來源**：obj 與它 `mtllib` 引用的 mtl 一起複製進 `models/<名字>/`（⛔ 不指向作品外的檔）；記下位元組數與 SHA256。換檔 ＝ 同名＋`file=`＋`overwrite=1`。
  - **work 來源**：那件作品（含它的關聯、繩、帆、模型）匯出成 obj，快取在 `<雕刻根>/model_cache/<作品>/<鍵>.obj`（⛔ 不入版控）；鍵 ＝ 那一整串作品的事件水位與資料檔雜湊 ⇒ 來源一改，下一次觀測**自動重出**（回報 `cache=produced|reused`）。模型座標 ＝ 那件作品的格座標。`scale` 不給 ⇒ 來源的一格幾公尺 ÷ 本作品的（寫進 models.json）。成環（A 用 B、B 又用 A；含經過關聯與隔層）⇒ exit 2 —— `sub=model` 與 `sub=link` 寫入時都看關聯＋模型兩種邊，零寫入並印出那條環。
  - 同名已在：改 at／pivot／rotate／scale／mirror／color／enabled／note 直接改；**換來源**要 `overwrite=1`。寫之前先讀一次 obj（work 來源先產生快取）：讀不了、沒有面、索引越界 ⇒ exit 2、**一個檔都沒寫**。`sub=unmodel name=` 拿掉（file 來源連同 `models/<名字>/`；綁在它上面的繩當下全庫掃一次，`ropes_now_broken`）。
  - 只讀幾何與顏色：`v`／`vn`／`f`（多邊形扇形切成三角形；索引可負）／`usemtl`／`mtllib` 的 `Kd`。沒有 `vn` ⇒ 面法線（平面著色）；缺 mtl 或材質 ⇒ 預設灰並列警告；`color=` ⇒ 整個模型一個色。貼圖、曲面、線段不讀。上限 256 MB、800 萬個三角形。
- **繩與帆角綁模型**：端點寫 `model:[關聯/…/]模型名/點`（相對於繩所在的作品，同固定點路徑）。點 ＝ `x,y,z`：file 來源是**檔案座標**（照 axes 換）、work 來源是那件作品的格座標（整數 ⇒ 格心）；work 來源也可以寫那件作品的**固定點名**。模型搬、轉、縮、跟著關聯走 ⇒ 端點跟著動。帆的角同一種寫法（`corners="model:mast/12,0,40|…"`）。
- **跟著關聯走**：被關聯進來的作品自己的模型照那條關聯的 mirror→turn→at 換座標（模型路徑 ＝ `關聯/…/模型名`）。所在那一層這次沒畫 ⇒ `hidden`（綁在上面的繩跟著 hidden）；自己 `enabled=0` ⇒ `disabled`；`models=0`／`hide_models=路徑,…` ⇒ 這次**不畫**（綁在上面的繩照畫；打錯路徑 ⇒ exit 2）。檔不見、來源作品不在、成環、快取產生失敗 ⇒ `broken`：⛔ 不靜默消失，觀測照樣出圖、回傳行大聲列 🔴。
- **讀數**（`sub=models` 與 view／export 每個模型一行）：狀態、來源（檔名＋SHA 前 8 碼／作品＋快取鍵與 produced|reused）、變換、三角形數、在組裝件裡的外框；回傳值 `models_drawn／hidden／disabled／broken`。
- **找現有元件**（`sub=components [kind=]`，Tim 2026-10-14；不用 `work=`）：掃全部作品的 models.json，把 work 來源的模型照來源作品彙整 —— 每件一行：ID、名字、作者、一格幾公尺、meta（`kind` 與子種類，例 `gun:24l`）、引用幾次與哪幾件作品、用過的 scale；被最多件作品用的在前。`kind=gun` 只列那一類，`kind=gun:24l` 只列那一種。唯讀（不產生快取、不拿鎖）；讀不了的作品列在最後（⛔ 不靜默略過）；obj 檔來源不列（別的作品引用不到）。造船的用法見 `Sculpture_Shipbuilding.md` §2.1。
- **畫**：view 與觀測頁的 GPU 預覽另一趟畫（靜態 ⇒ 內容沒變不重傳；同一組燈、會投影子也接影子）；不是 voxel ⇒ `visible_voxels` 不變；slice／section／stats 量的是格子，不畫模型。觀測頁「模型」區：總開關＋一個一個勾（只影響這次觀測，⛔ 不寫回 models.json）。
- **匯出**：`export format=obj` 把模型接在帆後面（每個模型 `o model_n`、一色一個材質 `model_n_k`）；list／vox 只有格子 ⇒ `models_exported=0`。只有繩／帆／模型、沒有 voxel 的作品也匯得出 obj。
- **meta**：`sub=model name= meta=<JSON 物件>`（只改 meta 不用 overwrite）—— 見下一節。

### 9.4.9 部件的 meta 與部件清單 —— 給遊戲認「這是什麼、關鍵點在哪」（TASK-0517）

外部模型與關聯可以帶一包 **meta**：平的 `{鍵: 字串或數字}`，給下游（遊戲、VoxelKit）認部件用（例：砲 `kind=gun`、`gun=24l`；彈藥庫 `kind=magazine`、`powder_lb=9000`）。引擎自己不讀它的意思 —— 不影響組裝、觀測、繩、帆。關鍵點不另外存：用來源作品的**固定點**（砲的 `muzzle`／`breech` ⇒ 砲軸；彈藥庫的 `blast`）。

```bash
senate cmd sculpture --arg op=work --arg sub=model --arg work=deck --arg persona=<P> --arg name=砲-S01 --arg 'meta={"kind":"gun","gun":"24l","hp":100}'
senate cmd sculpture --arg op=work --arg sub=model --arg work=deck --arg persona=<P> --arg name=砲-S01 --arg 'meta={"hp":null}'   # 合併：null ⇒ 刪那個鍵
senate cmd sculpture --arg op=work --arg sub=link  --arg work=ship --arg persona=<P> --arg name=彈藥庫-後 --arg 'meta={"kind":"magazine","powder_lb":9000}'
senate cmd sculpture --arg op=work --arg sub=model --arg work=deck --arg persona=<P> --arg name=砲-S01 --arg meta=none          # 全清
senate cmd sculpture --arg op=export --arg work=ship --arg format=parts --arg out=<絕對路徑.json>                            # 部件清單
```

- **meta=**：一個 JSON 物件，**合併**進現有的（給的鍵覆寫；值 `null` ⇒ 刪那個鍵）；`none` ⇒ 全清。值只收**字串或有限的數字**（⛔ 巢狀、陣列、布林、null 都擋）；鍵 ≤ 64 字、不含空白與 `: , | = "`；至多 64 個鍵。壞了 ⇒ exit 2、**未寫入**。改位置、換來源、改開關都不動 meta。
- **存法**：models.json／links.json 每一筆的 `meta` 物件（數字就是 JSON 數字，⛔ 不轉字串；依鍵排序）。⚠ **用到 meta 的檔升 schema**（models 2、links 3）：沒更新的 senate.exe 讀到會大聲讀不了 —— ⛔ 而不是默默丟掉、下一次寫回去清光。全部清掉 ⇒ 降回原本的 schema，檔逐位元組回到加之前。手改成「schema 舊、卻有 meta」⇒ 讀不了。
- **讀數**：`sub=models`、`sub=links`（與 view／export 的模型行）每一筆後面印 `meta k=v, …`。
- **部件清單**（`export format=parts out=<.json>`）：不讀 voxel、不改任何作品檔（模型外框要網格 ⇒ work 來源缺快取會現產生）。JSON：
  - `format: "sculpt-parts"`、`version: 1`、`work`、`size`、`meters_per_voxel`、`points`（本作品自己的固定點）；
  - `links[]`：整棵關聯樹（含巢狀、停用的）每條一筆 —— `path`、`work`、`turn`、`mirror`、`enabled`、`drawn`（這次的 links=／hide_links= 有沒有畫到）、`box`（那件作品的框換到組裝件 `[x0,y0,z0,x1,y1,z1]`）、`meta`、`points`；
  - `models[]`：每個外部模型（含關聯裡的）—— `path`、`frame`、`name`、`source`、`work`／`file`、`state`、`box`（有讀到網格才有）、`meta`、`points`（work 來源才有）。
  - **座標**：組裝件的連續座標（格 k 佔 [k, k+1]，固定點取格心 k＋0.5），跟繩的端點同一個公式（關聯：鏡像 → 轉向 → at，逐層；模型：它的 3×4 矩陣）。⇒ 砲軸 ＝ `muzzle − breech`，⛔ 下游不用再從外框猜朝向。
  - 某個部件的固定點檔讀不了 ⇒ 那一筆 `points` 空、`points_error` 寫原因、回傳值 `parts_points_errors`（⛔ 不靜默給空的）。
- **VoxelKit**：`voxel_export.py --meta`（配 `--parts`、`--rig --models`）把 meta／points 接進 `.vxk` header（形狀：平的 `meta`＋`points: {名字: [x,y,z]}`，ame 2026-10-12 酒館 seq 24096 議定）；不帶 `--meta` ⇒ header 跟以前逐欄一樣。
- 驗收：淨室 `SculptureMetaCleanRoom`（固定點＝手算、加了再清 ⇒ 檔逐位元組回原樣、壞 meta 零寫入、手改 schema 讀不了）。

#### 帆與繩的 meta、`work sub=meta`（TASK-0539）

帆與繩也可以帶 meta（同一套規則：平的、字串或數字、合併、null 刪鍵、none 全清）—— 給遊戲認「這是哪一種帆、是不是旗、這條繩屬於哪面帆」；鍵的定義在 `Sculpture_Shipbuilding.md` §9.1。

```bash
senate cmd sculpture --arg op=work --arg sub=meta --arg work=<作品> --arg persona=<P> --arg part=sail --arg name=fore-topsail --arg 'meta={"kind":"sail","mast":"fore","sail":"topsail"}'
senate cmd sculpture --arg op=work --arg sub=meta --arg work=<作品> --arg persona=<P> --arg file=<絕對路徑.tsv>      # 批次：每行 part<TAB>名<TAB>meta-JSON
senate cmd sculpture --arg op=work --arg sub=meta --arg work=<作品> [--arg part=rope]                                  # 讀數：列出 meta、報沒標的幾筆
```

- **不限作者**（Tim 2026-10-13 拍板）：meta 是給下游的標籤 ⇒ `sub=meta` 誰都能改（帆、繩、模型、關聯四種）。2026-10-16 Tim 把規則放寬成「**不影響模型外觀的修改 ok**」（TASK-0646）⇒ 外部模型、關聯、固定點的增刪改也不限作者、拆件也是（§9.4.12）；帆、繩照舊只有作者能改（`sub=sail／rope`）。
  每一筆變更記在作品資料夾的 **`meta_log.jsonl`**（一行一筆：`at`、`persona`、`owner`、`part`、`name`、`before`、`after`；只往後加）⇒ 查得到誰改的。同樣的值再下一次 ⇒ 0 筆、不記。
- **批次**（`file=`）：`#` 開頭、空行略過；同一筆寫好幾行 ⇒ 依序疊上去；先全部驗過，**一筆不合（名字找不到、壞 meta、欄數不對）⇒ 整份不寫**（exit 2、零寫入）。上限 20,000 行。
- 只認**本作品自己的**部件：關聯進來的作品裡的帆、繩要到那件作品下改（`work=<那件>`）。
- **存法**：sails.json／ropes.json 每一筆的 `meta`；用到 meta 的檔升 **schema 3**（帆：含貼圖的 2；繩：含組與子繩的 2）—— 舊版讀到會大聲讀不了。清光 ⇒ schema 回原本、逐位元組回原樣。
  作者用 `sub=sail／rope` 改屬性、換端點，meta 照留（⛔ 以前「新建物件再抄欄位」的寫法會默默清掉）。
- **讀數**：`sub=sails`、`sub=ropes`、view／export 的帆與繩那一行後面印 `meta k=v, …`（沒 meta 的行一個字都不差）。
- **匯出 .vxk**：`include=meta` ⇒ 帆與繩**有 meta 才**多一個 `meta`（⇒ 沒標的作品跟以前逐欄相同）；`include=ends`（配 rig）⇒ 每條繩多 `frame`（繩所在那一層的關聯路徑，組裝件自己 ＝ ""）與 `ends`（兩端原樣的參照，相對於 frame：`固定點`、`關聯/…/固定點`、`model:…`、`sail:帆/角`、`rope:…`、`x,y,z`）。
  ⚠ `voxel_export.py`（對拍用的舊生產端）不帶帆與繩的 meta、ends ⇒ 這兩段跟 senate 那支比會不同。
- 驗收：淨室 `SculptureRigMetaCleanRoom`（非作者寫得進去並記 log、批次合併、一筆不合整份不寫、作者改屬性不清 meta、非作者改形狀照擋、vxk 帶與不帶、清掉逐位元組回原樣、手改 schema 1 帶 meta 讀不了）。

### 9.4.10 `.vxk` 封裝 —— HTML 遊戲／影片吃的那一份由 senate 直接產（TASK-0518）

VoxelKit（ArtGallery/VoxelKit，瀏覽器端 `voxelkit.js`）吃的 `.vxk` 以前由 `voxel_export.py` 產（叫 senate 匯 list＋obj 再自己封）；現在 senate 直接產，格式一個位元都不改（規格在 `ArtGallery/VoxelKit/README.md`）。

```bash
senate cmd sculpture --arg op=export --arg format=vxk --arg work=<作品> --arg out=<絕對路徑.vxk> [--arg include=rig,models,parts,meta] [--arg links=… --arg hide_links=…]
```

- **include**（同 Python 的旗標）：`rig`（繩中線、帆三角網格）、`models`（配 rig：模型網格，同網格只存一份）、`parts`（關聯外框）、`meta`（關聯與模型的 meta＋固定點，§9.4.9）。不帶 ⇒ 只有格子（header 不多任何一欄）。打錯、`models` 沒配 `rig`、給 `region`／`exclude_color`（`.vxk` 是整件作品）⇒ exit 2、不寫檔。
- **跟 Python 那支同一份東西**：繩中線 ＝ 管子（同 obj 的 `Tube`）每一圈的平均、帆 ＝ 雙面網格（同 obj 的 `TwoSided`）、模型網格照 obj「同色一組」的三角形順序；頂點先照 obj 的 4 位小數取一次、再照 numpy 的半數取偶取到 0.01 ⇒ 憲法號實測**每一個數都跟 Python 那份相等**（繩中線 22,596 個、帆頂點 50,274 個、模型網格頂點 357,252 個、外框與固定點，最大差 0）。`meters_per_voxel` 讀書卡的原始欄位（沒記 ⇒ 0.1，同 Python；⛔ 不是 1/32）。
- ⚠ **刻意不同的一格**：繩／帆／模型也照 `links=`／`hide_links=` 的選擇（Python 那支只有格子帶 links，obj 那一趟沒帶）。不帶 links 時兩邊一樣。
- **寫檔前回讀對拍**：封裝解回來跟這次的格子逐格逐色比、checksum 也比，對不上 ⇒ 不寫（同 Python）。回傳值 `path`、`sha256`、`voxels`、`vxk_ropes／vxk_sails／vxk_models／vxk_meshes／vxk_parts`、`vxk_with_meta／vxk_points`。
- **對拍工具**：`python -I ArtGallery/VoxelKit/vxk_compare.py <a.vxk> <b.vxk> [--tamper]` —— 解開之後比（格子逐格逐色、header 逐欄；⛔ 不比壓縮後的位元組，deflate 實作不同就不同；不比 exported_at）；容差只給浮點那幾格（中線與頂點 0.01、顏色 0.0001），模型網格比內容與去重分組。`--tamper` ＝ 反向對照（改一格再比，一定要報不同）。
- 驗收：淨室 `SculptureVxkCleanRoom`（格子＝list 匯出、checksum 另一種寫法手算、區塊數與字典序、raw 版面、header 欄位順序、模型去重、關聯外框＝手算、壞參數零寫入、改一格就判不同）；憲法號整份：C# 與 Python 兩份 vxk_compare 相同、voxelkit.js 的 `verify`（ame 遊戲載入時那一支）逐格 checksum 通過、改一格的那份判失敗。

### 9.4.11 `.glb` —— 子模型各自成物件、同件共用 mesh（TASK-0510）

Tim 2026-10-12：「匯出時子模型（關聯、外部模型）各自是獨立的物件、不合併進主模型；多個同樣的子模型共用一份 mesh」；格式 2026-10-16 定 glTF 2.0 二進位（obj 沒有「同一份 mesh 套不同變換」的寫法）。Blender 內建匯入；Unity 用 glTFast（或 UnityGLTF）。

```bash
senate cmd sculpture --arg op=export --arg format=glb --arg work=<作品> [--arg out=<路徑.glb>] [--arg links=… --arg hide_links=…|self] [--arg models=0|1 --arg hide_models=…]
```

- **場景 ＝ 觀測頁 GPU 的分開畫**（§5.4，`view parts=1`）：主作品只帶自己的格；每個關聯節點、每個外部模型一個 node，指向它來源的 mesh＋自己的矩陣（關聯：鏡像→轉→平移，巢狀逐層合成；模型：at／pivot／rotate／scale／mirror）。同一件作品同一個版本（＋同一張有效替換色表，§9.4.4）、同一個 obj＋軸向＋改色 ⇒ **同一個 mesh index**。node 的 `extras.sculpt` 記 `kind`（own／link／model／rope／sail）、`source`、`path`。
- 這次出哪幾個照 `links=`／`hide_links=`（`self` ＝ 不出主作品）／`models=`／`hide_models=`（跟頁面同一支判準）；點名不存在的路徑 ⇒ exit 2、不寫檔。繩、帆照 view 的算法（模擬到 `sail_seconds`），各自一個 node（世界座標）。
- **座標**：最上面一個根 node ＝ 雕刻 (x, y, z) → glTF (x, z, y) × 一格幾公尺（`meters_per_voxel`，沒記 ⇒ 1/32）⇒ y-up、公尺。這一換是鏡像（行列式 < 0），glTF 自己照行列式翻繞序；網格照「世界裡從外面看逆時針」寫。
- **材質**：voxel 一色一個 `c<index>`（同 obj 的 mtl 名）；外部模型用頂點色 `COLOR_0`；顏色換成線性值（glTF 規定）。
- ⚠ 跟 obj／vxk（合併版）刻意不同：子模型畫**自己的全部格子**，貼合處不互相剔除、重疊處兩份都在；封閉空腔的面不省略（分開的來源各自判不出整艘船的空腔）。不吃 `region`／`exclude_color`。
- 讀數：回傳 `path`、`sha256`、`glb_nodes／glb_meshes／glb_instances／glb_shared_meshes／glb_triangles／glb_ropes／glb_sails`；回報列出被兩個以上 node 共用的 mesh。同輸入同 bytes。
- 實測（2026-10-16，拿破崙號組裝件）：node 776、mesh 454（共用 10 份）、子模型實例 373、三角形 534,752、32 MB、約 23 秒；獨立的 Python 讀檔（照規格解 accessor、索引不越界）全過，28 條關聯的 mesh 外框套矩陣後全部落在 `format=parts` 的框裡。上砲甲板用 three.js 的 GLTFLoader 讀：node 33、mesh 3（砲 30 門共用一份），74 × 4 × 18.3 m、畫得出來。
- 驗收：淨室 `SculptGlbCleanRoom`（voxel 面經 node 矩陣換回逐格＝合併版 list，含鏡像、轉向、巢狀；模型外框＝obj；共用次數；根矩陣；藏一條少一個 node、`models=0`、`hide_links=self`、打錯路徑、`region`；重匯逐位元組）。反向對照：故意把矩陣寫成沒轉置 ⇒ 缺 5、多 5、模型外框對不上。

### 9.4.12 拆件 —— 不改外觀，把一部分格子拆成子作品（TASK-0646）

規則由 Tim 定（2026-10-16）：「不影響模型外觀的修改 ok」「可以在不調整模型外觀下 修改（meta 或拆件符合遊戲規格）」⇒ **不限作者**。
起因：遊戲只轉外部模型 —— 雙舵輪的支柱跟輪雕在同一件、舵在船殼格子裡 ⇒ 要拆出來才轉得對（TASK-0640）。

```bash
senate cmd sculpture --arg op=work --arg sub=split --arg persona=<我> --arg work=<A> --arg into=<新子作品> --arg colors=136,104 --arg twin="{name}座" [--arg twin_meta='{"kind":"wheel_stand"}'] [--arg voxels=<清單檔>] [--arg title=…] [--arg confirm=1]
```

- **選格**：A 自己的格（不含關聯），照 `colors`（色號）或 `voxels`（清單檔 `x,y,z[,c]`；每一格都要在 A 裡、給了色就要對）。選到全部格、一格都沒選到 ⇒ 擋。一次最多 1,000,000 格。
- **新子作品**：作者照 A 的作者（⛔ 不改歸屬）、`parent_work` 照 A 的（A 沒有就是 A）、標 `sub`、尺寸與 `meters_per_voxel` 照 A；格子搬過去、座標不變。A、B 各落一刀 `workedit`（action `split`）。
- **雙生引用**：全庫掃，每個關聯到 A 的關聯、每個拿 A 當外部模型的模型，都在同一件作品補一個指向 B 的雙生 —— 名字照 `twin`（`{name}` ＝ 原名；沒有 `{name}` ⇒ 接在原名後面），擺放（at／turn／mirror／pivot／rotate／scale／axes／color／enabled／recolor）照抄，關聯插在原本的**下一個**（「先到的格贏」的順序不變）；meta ＝ `twin_meta`（⛔ 不抄原本的：原本的 kind 在講 A，例如舵輪的 spin 不能讓支柱也轉）。
- **外觀不變由引擎證明**：寫檔之前在記憶體裡組 A（含它底下的關聯）拆前、拆後＋B，逐格逐色比；A 的格原本蓋住它自己關聯的格（拆掉會露出來）、或任何一格對不上 ⇒ 擋下、一個檔都沒寫。回傳 `split_voxels`、`split_keep`、`split_composed`、`split_mismatch`、`split_twin_links`、`split_twin_models`。
- **預覽**：不給 `confirm=1` ⇒ 驗完、列出要搬幾格與每個雙生，一個檔都不寫。
- **留痕**：A、B 的事件最上層帶 `source{kind:split, from, to, by}`；A 與每件補了雙生的作品，`meta_log.jsonl` 各記一筆。
- ⚠ **undo**：A 那一刀單獨 undo，格子會在 A、B 各一份（外觀變了）⇒ 要復原請兩件都 undo、並拿掉雙生引用。
- 遊戲那側：雙生關聯是新的部件 ⇒ 要 reimport；雙生模型只換 meta 與固定點的話 metasync 帶不進來（新模型）⇒ 也要 reimport。
- 驗收：淨室 `SculptSplitCleanRoom`（不是作者改模型、固定點、關聯並留痕；不是作者落格子擋下；拆件預覽零寫入、雙生插對位置、組起來逐格相同；露出關聯格、全選、沒選到都擋 —— 拿掉擋關那一行，這一格會紅）。

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
