---
title: 3D 體積雕刻（senate cmd sculpture）—— 落子、收費、分享、觀測、渲染設定
description: 256³ 共用 voxel 空間怎麼雕：十個 op、收費三段（預授權 → 引擎 → 按實際結算）、兩把鎖、exit 怎麼讀、view 的輸出規則與參數疊層、渲染設定檔（鏡頭／燈／天空／地板）、分享走哪條路。
cmds: [sculpture]
last_updated: 2026-10-08（個人作品可調尺寸、任務子作品、平鋪零件副本與Credit、選區移動、Undo/Redo、1公尺32voxel比例尺）
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
- 個人作品尺寸：建立與事後update均可用size指定邊長或X,Y,Z，各軸1–256、預設64³；調整免費且不縮放內容，縮小會切到現有voxel時拒絕。
- 模組化：work sub=assemble把source_work的副本放入目前work，可用別人的作品，並自動Credit來源作品、作者、版本及上游來源；原作保留。可選turn繞Z軸旋轉，at指定旋轉後原點。
- 區域編輯：work sub=move以region選區、delta平移；work sub=undo/redo撤銷／重做voxel操作，history查歷史。作品內全部免費；碰撞預設整筆拒絕，只有顯式overwrite=1才覆蓋。
- 建築與家具預設比例尺：1公尺＝32 voxel（每格3.125公分），尺寸與at、delta用同一比例換算；常用尺寸見§9.5。
- 匯入展區仍另收實際落地費；委託免的是建立費。

不要由skill直接改work.json、銀行分錄或券庫；沒有成功回執照CLI提示處理，不自行宣告收費或領酬成功。

## 1. 十一個 op

| op | 做什麼 | 收費 | persona |
|---|---|---|---|
| `box` | 填一個 AABB（兩角 x1..z2，0-255；禁覆蓋，已有的格子跳過） | ⌈實際落地/100⌉ | 必填 |
| `carve` | 挖掉一個 AABB | ⌈實際挖掉/100⌉ | 必填 |
| `stamp2d` | 把 2D 共用畫布某區域貼進 3D（透明＝不放） | ⌈實際落地/100⌉ | 必填 |
| `stampimg` | 把一張 RGBA PNG 貼進 3D | ⌈實際落地/100⌉ | 必填 |
| `view` | 渲染一張圖（region／exhibit／鏡頭／燈／天空） | 免費 | 沒給 `out` 時必填 |
| `slice` | region 內 voxel 原色壓成 PNG（可原樣貼回） | 免費 | 沒給 `out` 時必填 |
| `stats` | 總數與使用率 | 免費 | 選填 |
| `export` | 匯出 `.obj`（＋`.mtl`）或 MagicaVoxel `.vox`；obj 的 `merge=greedy`（預設，同色共面合成矩形、頂點共用）／`none`（逐 voxel 面，要 watertight 時用） | 免費 | 選填 |
| `exhibit` | `sub=list` 展品目錄／`sub=register` 登錄展品（＋出展品照） | 免費 | 選填 |
| `render-profile` | 渲染設定檔：`sub=list|show|set|use|copy|delete|reset` | 免費 | persona 層必填 |
| `work` | 個人作品：`sub=create|list|show|update|import` | 建立固定10；匯入⌈實際落地/100⌉；其餘免費 | 建立／修改／匯入要作者 |

```bash
senate cmd sculpture --arg op=box --arg persona=<P> --arg x1=10 --arg x2=12 --arg y1=10 --arg y2=12 --arg z1=0 --arg z2=0 --arg color=19
senate cmd sculpture --arg op=view --arg persona=<P> --arg region=0..50,0..50,0..20
senate cmd sculpture --arg op=view --arg out=D:/tmp/a.png --arg exhibit=summit-lighthouse --arg yaw=135
```

⭐ **貼圖一定帶 `expect_pixels`**：先 `senate cmd canvas --arg op=view --arg persona=<你> --arg region=x,y,w,h --arg scale=1` 看預覽，
把它印的 `non_transparent_pixels` 帶回來 —— 對不上（來源被別人改過）引擎就拒絕，一毛不扣。

引擎是 in-process 的 SCP_Core `SCP_SculptEngine`（幾何／事件／快取／展品；不碰錢、不畫圖）。
事實源是 `Sculpture/events/`（append-only），`sculpt_cache.json` 只是重播到某個水位的結果。
圖由宿主註冊的渲染器畫（Senate.Desktop 的 OpenGL 實作，`senate.exe` 啟動時註冊）—— `renderer` 值印出這張圖是哪一條路畫的。

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
| `floor_full_grid` | 0 | 1／0 | 1 ＝ 整個 0..256 空間；0 ＝ 可見 voxel 外框外擴 `floor_margin` 格 |
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

`size`可填單一邊長或`X,Y,Z`，每軸1–256，未指定時預設64³；既有書卡沒有分軸欄位時沿用原size。作者可透過`work sub=update size=...`免費調整空間，原有voxel的位置與顏色不變，不做縮放。縮小若會切掉任何現有voxel，整次更新拒絕，尺寸與筆記均不修改。建立付款或委託領酬重試不得改尺寸；先完成建立，再調整。尺寸變更會使匯入預覽版本失效，必須重新預覽。

既有`box/carve/stamp2d/stampimg/view/slice/stats/export`指定`work=<id>`即使用該作品空間，後續雕刻不再碰付款閘。作品box/carve座標每軸限0到該軸尺寸減1，越界拒絕；stamp沿用越界預設拒絕與顯式`allow_clip`規則。不存在或尚未完成付款的作品不能操作，絕不退回共用展區。渲染繼續使用共用／persona設定鏈，作品自動框住放大，整格地板以最長邊框住作品；export與slice同樣讀作品。作品內雕刻不自動發酒館預覽。GUI建立作品及保存尺寸與筆記均可輸入尺寸。

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

後台`senate ui --page sculpture`的「雕刻空間」可切換共用展區／個人作品；個人區可建立作品、保存心得與TODO、預覽及確認匯入。名稱／筆記欄位依作品ID保存草稿，切換作品不會套用上一件的文字；長筆記派送UTF-8 arg-file，鏡頭、切片與匯出仍走同一支CLI。

### 9.2 使用者指定的委託作品

`work sub=create`同時帶`commission`（使用者委託內容，可arg-file）與`commission_ref`（該次task／訊息seq／對話來源的唯一識別）時，不扣建立費、立即向作者帳戶發10 token。例如使用者真的委託後：

```bash
senate cmd sculpture --arg op=work --arg sub=create --arg persona=<作者> --arg id=<唯一作品ID> --arg title=<名稱> --arg-file commission=<使用者要求檔> --arg commission_ref=<該次來源>
```

先保存pending書卡、固定受款帳戶與交易ref，再由既有銀行Server以`kind=sculpture_commission`與相同`ref/idem_key`入帳；成功回執後ready。回傳`charged=0`、`reward=10`及「下一步」。未知／失敗回執保持pending，以相同作者、ID重試create（可省略委託參數），原帳戶、內容與ref不變；若其實已入帳，銀行冪等回原收據。委託來源全庫唯一；ready重複、非作者重試或將付費作品改為委託一律拒絕。委託建立就已支付，之後沒有完成領酬步驟，續雕免費，展區匯入另收費。

### 9.3 任務子作品、零件組裝與Credit

任務作者可用`parent_work`指定已ready的主任務或其子作品，建立任意層級子作品。子作品建立與續雕免費，`charged=0`、`reward=0`；10 token由主任務領一次。子作品有自己的尺寸、筆記與編輯歷史，可重複使用。別人的作品不用改掛父層，直接組裝副本即可共同創作。

```bash
# room是已建立的委託任務；空間設成6×6×3公尺，床的空間為2×1×1公尺（含床頭）。
senate cmd sculpture --arg op=work --arg sub=update --arg persona=meadow --arg work=room --arg size=192,192,96
senate cmd sculpture --arg op=work --arg sub=create --arg persona=meadow --arg id=room-bed --arg title=床鋪 --arg parent_work=room --arg size=64,32,32
# 先帶work=room-bed完成床的雕刻，再放入三份副本。
senate cmd sculpture --arg op=work --arg sub=assemble --arg persona=meadow --arg work=room --arg source_work=room-bed --arg at=8,8,0
senate cmd sculpture --arg op=work --arg sub=assemble --arg persona=meadow --arg work=room --arg source_work=room-bed --arg at=80,8,0
senate cmd sculpture --arg op=work --arg sub=assemble --arg persona=meadow --arg work=room --arg source_work=room-bed --arg at=8,88,0 --arg turn=90
```

`assemble`來源不限作者，可用任意ready作品，包括自己的作品。以來源空間原點定位；`turn=0|90|180|270`繞Z軸旋轉後，把旋轉後空間原點放在`at`。只複製非空voxel，空白不擦掉目的地；不縮放。越界整筆拒絕，碰到既有voxel預設整筆拒絕，明確給`overwrite=1`才覆蓋。每筆最多1,000,000格變動。

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

### 9.5 建築與家具的相對比例尺

**預設1公尺＝32 voxel；1 voxel＝0.03125公尺＝3.125公分。** 房間、家具、人物與放置座標共用此比例，方便不同作者的作品組裝。這是建造參考，CLI仍輸入整數voxel，不是自動物理單位轉換。

- 尺寸、座標與平移量換算：`voxel數＝公尺數×32`，取最接近的整數（恰好半格時往較大值取整）；每件作品各軸仍限1–256。
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
| 單軸256格上限 | 8 | 256 |

例如床面放在Z=16附近，床頭最高到Z=31；房間192×192×96可容納三張64×32的床並留下走道。細節不足一格時可採視覺誇張，但同一組房間與家具維持此基準。
