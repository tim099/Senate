---
title: 造船規格與做法（從憲法號整理）—— 結構、建造順序、砲、帆桁、繩、帆、彈藥庫、給遊戲的 meta
description: 風帆戰艦這一類作品專用的規格：座標與比例、作品怎麼拆（船殼／組裝件／子作品／一舷＋鏡像）、建議的建造順序、砲（一門一件子作品、外部模型、退到零碰撞、固定點、砲索）、帆桁（每根一件子作品、pivot、繞 z 轉）、繩與帆的數值、彈藥庫與火藥桶、給遊戲的 meta 與 .vxk 匯出；每條附憲法號當時的讀數與踩過的坑。
last_updated: 2026-10-14（§2.1 儘量用已有的元件、§9.4 匯進遊戲預設壓縮；ame。原稿 2026-10-12 憲法號 TASK-0478 階段一～三十三；basecamp）
target_audience: [AI_Agent]
related:
  - Sculpture_Blueprint_Build.md | Sculpture_Blueprint_Build | 照圖紙雕大型作品的通用流程（新圖紙、兩本帳、反向對照、verifier）
  - Sculpture.md | Sculpture | 雕刻指令本身（op、收費、觀測、外部模型、繩、帆、匯出）
---

# ⛵ 造船規格與做法（從憲法號整理）

> **一句話：船殼雕成格子；會重複的、會動的、遊戲要認的東西做成子作品，用關聯或外部模型引用，帶固定點與 meta。**
> 通用流程（新圖紙、兩本帳、反向對照）看 [Sculpture_Blueprint_Build](Sculpture_Blueprint_Build.md)；參數看 `senate cmd help sculpture`，⛔ 本檔不抄參數表。
> 範例作品：`Sculpture/works/basecamp-frigate-constitution-01m/`（船殼）＋ `…-assembly`（組裝件）。每一條的出處是它 `notes.md` 的那一段（下面寫「階段 N」）。

---

## 造船skill入口

造船是雕刻的一種。這一節只告訴你**該讀哪幾份文件**，全部都用 CLI 讀；操作方法以文件和 `help` 為準，⛔ skill 裡不抄。

| 順序 | 讀什麼 | CLI |
|---|---|---|
| 1 | 雕刻 skill 入口：委託、收費、子作品、續作規則 | `senate cmd skill --arg op=show --arg name=scp-sculpture` |
| 2 | 本檔：船才有的規格、建造順序、砲／帆桁／繩／帆／彈藥庫、給遊戲的 meta、換船檢查表 | `senate cmd doc --arg op=show --arg name=Sculpture_Shipbuilding` |
| 3 | 照圖紙雕大型作品的通用流程：新圖紙、遮罩、兩本帳、反向對照 | `senate cmd doc --arg op=show --arg name=Sculpture_Blueprint_Build` |
| 4 | 雕刻指令本身：op、參數、觀測、關聯、外部模型、繩、帆、匯出 | 先跑 `senate cmd help sculpture`，再跑 `senate cmd doc --arg op=show --arg name=Sculpture` |

- 續造一艘船時，先依第 1 份的續作規則，用 CLI 讀那件作品的書卡、心得和 TODO，再開工。
- 範例船：憲法號 `basecamp-frigate-constitution-01m`（從龍骨照圖紙造），餘燼號 `basecamp-ember-01m`（從憲法號魔改）。產生程式在各自作品的 `design/` 底下。
- ⭐ **儘量用已有的元件**（Tim 2026-10-14）：砲、火藥桶、救生艇、舵輪、羅經櫃、絞盤、燈、格柵這類功能元件，別的船已經做過的就直接當外部模型引用（大小不合可以縮放），⛔ 不要重做一份 —— 比例、固定點、meta 都對過了；匯進遊戲時同一份網格也只存一次（§9.4）。船首像、船尾雕飾這種船的個性裝飾不在此限。現有元件看第 2 份 §2.1。
- 給遊戲標的東西（帆、繩、旗的 meta，空艙房，甲板與梯子）在第 2 份 §9；標完照 §10 檢查表逐條勾。
- 現有的 op 不夠用時，照第 1 份最後一條，擴充引擎。

---

## 0. 適用範圍

- 風帆船這一類：有船殼、甲板、桅檣索具、砲，要給人看也要給遊戲用（ame《憲法號對轟》讀 `.vxk`）。
- 本檔記**船才有的**規格與數值；換一艘船時先讀這份，再從憲法號的 `design/` 複製產生程式改參數。
- ⚠ 這裡的數字是憲法號（0.1 m／格、1024×320×800）量到的，換比例或換船要重量，⛔ 不要直接抄。

## 1. 座標、比例、方向（先寫下來再動手）

| 項目 | 憲法號的約定 | 坑 |
|---|---|---|
| 軸 | X 船長、船頭朝 +X；Y 船寬、中線 y=160；Z 高度，z=0 ＝ 護龍骨底面 | — |
| 舷 | P（左舷）＝ y < 160、S（右舷）＝ y > 160 | 從船尾看，引擎視圖觀者的左手邊是 −y（階段十四船名第一次落反）|
| 鏡像 | 格 y ↔ 320 − y（160 那一格是自己）；一舷子作品的另一舷關聯 mirror=y、**at.y ＝ 321 − at.y − 高** | 關聯翻面在自己的框裡翻；外部模型繞 pivot 翻 ⇒ 兩者的 at 換算不一樣（階段三十一：關聯換模型，P 舷 at.y ＋ 32）|
| 比例 | `meters_per_voxel` 0.1 —— **組裝件和每件子作品都要記** | 組裝件沒記 ⇒ 預設 1 m ＝ 32 格，帆的重力與風大了 3.2 倍（掛帆試試）；沒記的作品當模型來源時 scale 自動換算會錯 ⇒ **模型一律明寫 scale** |
| 固定點 | 只收整數格（點取格心）、而且要在作品框裡 | 柱頂、繩端要用「那一格」，框不夠就放大框（階段三十）|
| 繩端點 | 整數 ＝ 格心；帶小數照原值 | 要精準就一律寫小數（階段二十二先拿一條量過：100 → 100.5、110.25 → 110.25）|

## 2. 作品怎麼拆

- **名稱**（Tim 2026-10-14）：組裝件 ＝ 成品，名稱直接用船名（例「憲法號」），不帶 `sub`（主作品，作品下拉列的是它）；船殼名稱「<船名> 船殼」、帶 `tags=sub`。其他部件「<船名> <部件>」。ID：船殼 `<作者>-<船>-01m`、組裝件 `<船殼 id>-assembly`。
- **船殼**＝任務作品（委託、薪水掛在它；組裝件與子作品的 `parent_work` 都指它）：龍骨、肋骨、外板、內板、砲門、舷牆、船尾窗。
- **組裝件**：一開始就建（船殼開工就建、關聯船殼 at 0,0,0）。一格都不雕，只有關聯（船殼、各層甲板、桅、船頭像、channels…）；整件的部件 at 0,0,0，一舷的部件照自己的固定框 at（channels 150,161,70、它的鏡像 150,80,70）；之後加的外部模型、繩、帆、風都掛在組裝件（階段「組裝件與子作品」）。
- **預覽 preset**：組裝件存幾組（`work sub=preset`，見 `Sculpture.md` §9.4.4）—— 藏船殼看裡面、剖半、只看某一層甲板…；Tim 在觀測頁「預覽 preset」下拉切換檢查完工的部分。加了新部件就補一組。
- **子作品**：`parent_work` 掛在船殼或所在的甲板底下（免費、不另領薪），建立時帶 `tags=sub`；房間掛在各自的甲板下 ⇒ 藏甲板會一起藏。
- **左右對稱的東西只做一舷**（固定框，例如 `-head-side` at 630,161,70），另一舷用 mirror=y 關聯；鏡像不要套在整層甲板上，只用在上面的物件（Tim 2026-10-11）。
- **重複的東西做一次**：一門砲、一隻錨、一個火藥桶、一個舵輪各一件子作品，引用很多次。**跨船也一樣**：別的船做過的元件直接引用（§2.1）。
- **會動的、遊戲要個別處理的** ⇒ 外部模型（不是關聯）：砲、帆桁、舵輪、火藥桶。理由：模型可以任意角度轉（關聯只有 90° 的 turn）、可以帶 meta、遊戲拿得到每一個的路徑與固定點。

### 2.1 儘量用已有的元件（Tim 2026-10-14）

開新船先查現有元件，有的就引用（`work sub=model` 的來源指那件作品），⛔ 不要另做一份長得一樣的。清單用指令查（⛔ 不寫死在這裡 —— 元件一直在加）：

```bash
senate cmd sculpture --arg op=work --arg sub=components                 # 全部：被當模型用的作品，引用最多的在前
senate cmd sculpture --arg op=work --arg sub=components --arg kind=gun  # 只看砲（boat、wheel、binnacle、capstan、light、powder…）；kind=gun:24l 只看那一種
```

每一行：來源作品 ID、名字、作者、一格幾公尺、meta（`kind` 與子種類，例 `gun:24l`、`boat:cutter`）、引用幾次與哪幾件作品（看得出哪幾艘船在用）、用過的 scale。
用法：挑一件 ⇒ `work sub=model` 的來源指它、meta 照那一行（同一種東西 meta 一樣，遊戲才認得）；它的固定點看來源作品（`work sub=anchors`）。

- 為什麼（除了省工）：遊戲成品裡**同一份網格只存一次**（§9.4）。2026-10-14 量：六艘的模型網格 161 種，跨船共用的 53 種 —— 24 磅長砲四艘共用、carronade 與火藥桶三艘、舵輪／羅經櫃／絞盤三艘、6 磅砲兩艘；每多一種新網格，成品就多一份（一門砲的網格原始約 20–30 KB；一整艘船自己的網格壓縮後可到 1 MB）。
- **大小不合就縮放**：外部模型的 `scale` 收一個數或 `x,y,z`（`work sub=model`，見 `senate cmd help sculpture`）—— 舵、小艇、絞盤這類照船的大小縮一縮就能用；縮放繞 `pivot`，固定點跟著縮。⚠ 縮過的在遊戲裡是另一份網格（省的是造的工夫，不是成品大小）；遊戲照 meta 認的長度（例 `boat` 的 `length_m`）縮了要跟著改。
- ⚠ 只差一點也是新的一份：改過幾格、換了 scale、換了來源版本 ⇒ 遊戲裡是另一份網格。同一個元件的不同擺法（P 舷鏡像、rotate 180）本來就各一份（憲法號的 24 磅砲兩舷各一份、carronade 四份）。
- **不用複用的**：船首像、船尾雕飾、船名板、旗這種船的個性裝飾 —— 每艘船自己做（Tim 2026-10-14）。
- 真的要不一樣（例如三叉戟號的三管砲、風暴角號的追擊砲）才做新的；做成外部模型來源、帶 meta，下一艘船查得到。

## 3. 建議的建造順序（憲法號實際順序，加上事後才知道該早做的）

1. 龍骨 → 艏柱／艉柱 → apron、內艉柱、deadwood → 線形圖 → 底肋＋keelson → 肋骨 → 船頭船尾骨架（每階段一張新圖紙）
2. 外板與塗裝（dubbing、wales、銅／黑／白帶）→ 開砲門 → 船殼內部（內板、clamps、斜肋）
3. 甲板（每層一件子作品；橫樑照「每個砲門底下一根、兩個砲門之間一根」）→ 房間 → 舷牆與舷弧起翹
4. 桅（每根一件子作品）→ 船尾窗與裝飾 → 船頭（knee、billethead、rails、cathead）
5. 舵 → channels → 靜索 → 甲板繫繩點（fife rails、pin rails）→ 帆
6. 砲 → 砲具與砲索 → ratlines → 梯子 → 錨 → deadeyes／lanyards → 舵輪 → 上甲板砲門與 carronade → entry port
7. 彈藥庫與火藥桶 → 給遊戲的 meta

⭐ **事後才知道該一開始就這樣做的**（新船直接照做）：
- **砲一開始就做成外部模型**（階段二十的砲是關聯，階段三十一才換成模型、140 條砲索重綁）。
- **帆桁一開始就是一根一件子作品＋模型**（階段十六把帆桁貼進桅、階段二十六用格子轉角度、階段三十三才拆成模型、刻掉 18,638 格）。
- **組裝件一建立就記 meters_per_voxel**。
- **上甲板砲門跟 channels 一起規劃**：階段二十九開砲門之後，channels 的 deadeye 與 shroud 擋在砲門前面，要整排重擠（channels v2）。
- **甲板高度先照砲門定**：砲門下緣離甲板 6–9 格、clamp 在砲門上緣之上（階段九）。

## 4. 砲

### 4.1 一門砲一件子作品
- 座標：**x 沿船長（砲軸在 x 中線）、y ＝ 砲口方向、z 0 ＝ 甲板面那一層**（階段二十 -gun-24：15×32×16、軸在 x 7）。
- 比例：0.1 m 的砲身看不出台階；第二批改 **0.05 m**（-gun-long18、-gun-carronade32），模型 scale 0.5 明寫（階段二十八）。火藥桶 0.02 m、scale 0.2。
- 形狀要選「在格子上會換形狀」的半徑（base ring、reinforce、chase、muzzle swell 才看得出台階）。
- 🩸 草稿期間改過的（每一次都是渲出來才看到）：base ring r 2.9 ⇒ 5×5 方塊、從後面看像黑磚 ⇒ 2.6；輪子兩格厚像黑球 ⇒ 一格厚＋輪軸頭；砲架 164 渲出來偏亮橘紅 ⇒ 132。
- 色：砲管黑 36、砲架 132、quoin 136（階段二十）；carronade、long18 同一套。

### 4.2 擺上船（外部模型）
- 模型掛在**所在甲板的子作品**上（`甲板-gun/砲-S01`），藏甲板就一起藏。
- S 舷照原樣；P 舷：carronade 用 rotate 180（pivot 在 skid 前緣）；從關聯換過來的長砲用 mirror=y、at.y ＋ 32（§1）。
- **退到零碰撞**：每個砲門從舷牆外面往裡退，直到「模型每一格的格心＋八個角都不在組裝件的實心格裡」，再要求砲口在船殼外（階段二十八／二十九；對「草稿套上去」的組裝件量）。
- 船頭追擊砲：兩舷的長砲在船頭會在中線撞（半寬約 30 格、砲連砲架 32 格）⇒ 不能正橫放；**照角度搜**（憲法號：往船頭偏 5–20° 推得出去、25° 砲口出不了砲門、30° 以上塞不進去 ⇒ 取 −20°）。另一舷用 mirror=y 對 y 321 鏡（`chase18-P`）。
- 砲口伸出：carronade 推到前端時，砲口要超出 skid 前端到夠伸出牆（牆 0.45 m 厚，砲口只超出 0.35 m ⇒ 縮在牆裡；改 0.55 m）。

### 4.3 固定點（子作品裡登記，經關聯／模型每門都有）
| 名字 | 位置 | 用途 |
|---|---|---|
| `muzzle` | 砲口內膛那一格（中線挖空，⛔ 不是實心格）| 砲軸 ＝ muzzle − breech（遊戲）|
| `breech` | 砲尾那一格 | 同上 |
| `cascabel` | 砲尾的環 | breeching 繩 |
| `eye-aft`／`eye-fwd` | 兩片 cheek 外側的環首螺栓 | side tackle（⚠ 是船尾側／船頭側，不是左右舷 —— 第一次登記成 eye-P／eye-S，改過名）|
| `train` | 砲架尾 | train tackle |
| `pivot` | carronade 的樞軸 | — |

### 4.4 砲具與砲索（階段二十一、二十二）
- 船牆的環：砲門中線往船尾／船頭 8 格、甲板面 ＋8；甲板的環：砲架尾往裡 9 格。**每個環都量**：砲尾 → 環、cheek → 環兩條線不准穿過別的實心（自己的砲架不算），另一舷鏡過去也量；不行就換 offset 9／10／11，再不行就不放、那一側的繩不掛（照實記）。
- 繩：breeching（cascabel → 兩側船牆的環，slack 1.12、粗 0.6、#b59a6a）、side tackle（eye → 同側的環，1.03、0.35）、train tackle（train → 甲板的環，1.05、0.35）。端點寫 `model:<甲板關聯>/<砲>/<固定點>`。
- rammer／sponge 掛在砲正上方那根橫樑底下（貼著甲板板會被 3 格深的橫樑擋住、從艙裡看不到）。

## 5. 桅與帆桁

- 桅：每根一件子作品（fore／main／mizzen／bowsprit），軸照甲板已開的桅孔；top 的 trestletrees 要緊貼 masthead（`round(160 ± 4.56)` 會空一格 ⇒ 主桅 top 浮空，階段十三 v1.1）。
- **帆桁：每根一件子作品，模型放在它的桅子作品裡**（階段三十三）：
  - 子作品裡放**沒轉**的帆桁（沿 y），不扣跟桅重疊的格（獨立零件要完整）。
  - 固定點：`arm-P／S`（yardarm 兩端）、`earing-P／S`（帆上緣兩角）、`sling`、`jackstay-*`（每 8 格）；gaff `top*`；boom `jaws`／`end`；外加 **`pivot`**。
  - 模型：scale 1 明寫、`rotate=0,0,θ`（繞世界 z）、pivot ＝ `pivot` 那一格的格心 —— 方帆桁＝帆桁中心那一格；gaff／boom ＝ 桅在 jaws 高度的軸心（繞桅擺，pivot 不在桿上 ⇒ 子作品框要包到它）。
  - 角度：右舷那一頭往前（＋x）＝ 負角（憲法號 course −20、topsail −22、topgallant −24、royal −26；預設風 4.5,−1.5,0 正對是 18.4°）。真船受 lee shrouds 限制約 30–35°（plan.json brace 的出處欄）。
  - meta：`kind=yard, mast, yard, length_ft, diameter_in, pivot_anchor=pivot, turn_axis=z, rotate_z_deg`＋`sail`。
- ⛔ 不要把帆桁貼進桅、也不要用格子轉角度：一格粗的截面轉了會變成只靠斜角相接的階梯（6 連通斷掉，階段二十六得把最細截面 0.5 → 0.75），拆出來要刻掉、重綁。
- ⚠ 網格轉 20° 後中段靠後那一小段會穿進桅（格子版是「不放」那 6 格）。

## 6. 繩

| 類 | 色 | 粗 | slack | 備註 |
|---|---|---|---|---|
| 靜索（shrouds、backstays、stays）| #2b2620（上過焦油）| 0.4–1.5 | shrouds／backstays 1.0；stays 1.0006–1.0008 | ⚠ 長而陡的繩一點點鬆就垂很多：backstays 1.0004 垂 19–37 格 ⇒ 1.0 |
| ratlines | #2b2620 | 0.3／0.25 | 子繩（`rope:<shroud>/比例`）| 每 4 格一道；從上 deadeye 頂往上 7 格起；短於 5 格不畫 |
| 動索（sheets、tacks）| #a08458 | 0.35–0.5 | course sheet 0.85、tack 0.8 | — |
| 砲索 | #b59a6a | 0.6／0.35 | 1.12／1.03／1.05 | §4.4 |
| 扶手繩（梯子、entry port）| #cdb88e | 0.35 | 1.02／拉直 | — |

- 固定點登記在**實際的格子上量**（hounds ＝ trestletrees 下一格的桅側…）；channels 的 deadeye 只登記一舷，另一舷用 `channels-鏡像/…`（兩舷每條長度相同）。
- 上 deadeye 用一段短而粗的繩當圓盤（軸朝舷外、直徑 3、長 2），lanyards 3 條排開 ±0.9（階段二十五）。
- 全部分組（`rope_groups.tsv`）：standing/…、ratlines/…、running/…、guns/…、ladders/…、steering —— 觀測時可以一組一組開關。
- 順序坑：會改同一批繩的工具（rig_standing → rig_deadeyes → ratlines）要照順序重跑，前一支會把後一支改過的端點改回去。

## 7. 帆

- 方帆：上緣（邊 0）整條釘在兩個 `earing` 之間、下兩角 free；topsail 以上的 sheet 綁到下一根帆桁同舷的 earing；course 的 sheet 往後、tack 往前綁到舷牆的 belaying pin。
- spanker：gaff、boom、桅三條邊釘住，只有 leech 自由。三角帆：tack 在 stay 下端、head 是 stay 上 70–80% 那一格、luff 沿 stay（那條 stay 改 slack 1 拉直）。
- 數值：預設風 **4.5,−1.5,0**（4.7 m/s）、fullness 1.05（7.6 m/s ＋ 1.08 ⇒ 像降落傘）；帆布 #f2ead8。鼓度主要的旋鈕是**風**，fullness 只差幾格（kotoko 兩點的試驗表）。
- 帆角綁帆桁模型（`model:<桅關聯>/yard-<帆桁>/earing-P`）⇒ 帆桁一轉，帆跟著走；⚠ course 下角綁在甲板 pin 上、不跟帆桁轉，轉很大時會被拉歪。

## 8. 彈藥庫與火藥桶（階段三十二）

- 位置照圖（憲法號 1849 圖：後 x 200–271、前 x 521–574），在水線下 —— ⚠ ame 量到對戰裡砲彈碰海面就停，打不到（本來就擺在打不到的地方）。
- 子作品（銅皮 168、松木地板 177、橡木壓條 136、門洞 5 格寬）；固定點 `blast`（房間空氣重心最近的一格）、`door`（門洞地板中線）。
- 火藥桶一件子作品（0.02 m）當模型放進去；上層木架的立柱要放在架板靠走道的兩角（桶是模型、不是格子 ⇒ 架板以格子來看會浮空，遊戲的連通判斷會讓它掉下去）。
- 封閉空腔：產生程式最後局部掃一次，≤ 50 格的封閉空腔填實、新格每一塊都要連到原本的實心。

## 9. 給遊戲的 meta 與匯出（TASK-0517／0518）

| kind | 掛在 | 其他鍵 | 固定點 |
|---|---|---|---|
| `gun` | 砲模型 | `gun=24l｜32c｜18chase` | muzzle、breech… |
| `magazine` | 彈藥庫關聯 | `powder_lb`（＝ 桶數 × 100，⚠ 不是史料）| blast、door |
| `powder` | 火藥桶模型 | `powder_lb=100` | — |
| `yard` | 帆桁模型 | 見 §5 | pivot、earing… |
| `ladders` | 梯子所在的關聯（一個關聯可放好幾座） | — | 每座 4 個：`<梯名>.foot`／`.lo`／`.hi`／`.head` |
| `wheel` | 舵輪模型 | — | 來源作品有的照給（憲法號 -wheel：`hub-aft`、`hub-fore`、`drum-rope-a／b`）|
| `binnacle` | 羅經櫃模型 | — | 沒有（用模型外框）|
| `capstan` | 絞盤模型 | — | 沒有（用模型外框）|
| `boat` | 小艇模型 | `boat=cutter`、`length_m`（⚠ 照一般比例，未查證）| 沒有（用模型外框）|
| `grating` | 艙口格柵模型（掛在所在甲板的子作品；0.05 m 子作品、scale 0.5） | — | 沒有（用模型外框；格柵頂 ＝ 外框頂）|
| `light` | 會發光的燈模型（夜燈號船首像手上的提燈 `-lantern`，0.01 m） | — | `wick`（火焰中心：遊戲在這裡放點光源）、`bail`（提環頂：手握的地方）、`base` |

- `wheel`／`binnacle`／`capstan`／`boat`／`grating` 只說「這是什麼」：站位、血量、能不能放下水、踩不踩得過去這些由遊戲決定，⛔ 不寫進 meta（餘燼號階段十七～二十、憲法號階段四十，2026-10-13 補）。

- meta 是平的 `{鍵: 字串或數字}`，`work sub=model｜link meta=` 合併進去；有 meta 的檔 schema 會升（models 2、links 3），舊 exe 讀不了會大聲擋。
- 梯子的四個點都取「腳踩的那一格」＝ 地板或踏板上面第一格空氣（讀的人取格心）：foot ＝ 下層甲板、最低一階往下梯方向站著等的那一格；lo／hi ＝ 最低／最高那一階；head ＝ 上層甲板、跨過艙口圍欄站穩的那一格（往外第一段連續 3 欄一樣高的平地）。哪兩層甲板由 foot／head 的高度判斷，不另外標。
  算點：餘燼號 `design/tools/ladder_points.py <組裝件> <甲板子作品>:<關聯名>:<梯子子作品>…`（梯名與方向從扶手繩固定點 `manrope-<梯名>-<P|S>-<top|bot>` 讀；不帶 `--apply` 只算）。
- 匯出：`op=export format=parts`（外框＋meta＋固定點的 JSON）、`format=vxk include=rig,models,parts,meta`（給遊戲；out 要絕對路徑）。改完任何部件都重匯一次，**格數與 checksum 會變**，要跟遊戲那側說。

### 9.1 帆、繩、旗的 meta（遊戲照 meta 判斷；TASK-0539）

Tim 2026-10-13：「之後依 meta 判斷吧（然後需要定義好 meta）」。帆與繩也能帶 meta（`work sub=meta`，見 `Sculpture.md` §9.4.9「帆與繩的 meta」）——
**meta 不限作者**（給遊戲的標籤；記在作品的 `meta_log.jsonl`），所以遊戲那側（ame）可以替已經在遊戲裡的船標；新船由造船的人照這張表標。
值一律小寫英文；帆名、繩名照舊自己取（遊戲不再靠名字認，但 `sail=` 要一字不差等於帆名）。

**帆**（`part=sail`）

| 鍵 | 值 | 遊戲拿來做什麼 |
|---|---|---|
| `kind` | `sail`／`flag` | 旗：不收、不算推力 |
| `mast` | `fore`／`main`／`mizzen`／`bowsprit` | 掛在哪根桅（跟帆桁 meta 的 `mast` 同一套；三角帆掛艏斜桅 ⇒ `bowsprit`） |
| `sail`（帆） | `course`／`topsail`／`topgallant`／`royal`／`gaff`（縱主帆：spanker、夜隼號的 mainsail）／`staysail`／`jib` | 帆的種類 ⇒ 帆裝（W／S）的預設：收帆 ＝ topsail、jib；戰鬥帆再加 topgallant、gaff；滿帆 ＝ 全部 |
| `set`（帆，可選） | `1`／`2`／`3` | 例外：最少在哪一段帆裝打開（1 收帆、2 戰鬥帆、3 滿帆）—— 跟種類的預設不同才寫（憲法號 fore-topmast-staysail `2`、flying-jib `3`） |
| `flag`（旗） | `ensign`（船尾旗）／`pennant`（長旗）／`jack` | 降旗時降 `ensign` |

**繩**（`part=rope`）—— 必標的只有「帆自己的繩」；其他繩可以不標

| 鍵 | 值 | 遊戲拿來做什麼 |
|---|---|---|
| `kind` | `sheet`／`tack`（導索、延長段同一類）；其他繩若要標：`shroud`、`backstay`、`stay`、`ratline`、`brace`、`lift`、`halyard`、`footrope`、`steering`、`gun`、`manrope` | 分類 |
| `sail` | 帆名 | 這條繩屬於哪面帆 ⇒ topsail 以上的 sheet 綁在哪根帆桁 ＝ 帆腳跟那根帆桁轉；帆掉了這幾條一起斷 |

**關聯**：彈藥庫 `kind=magazine` 加 `pos=fore／mid／aft`（廣播「前／中／後彈藥庫」）。
**桅**：帆桁 meta 的 `mast`（§5）—— 有幾根桅、各在哪、多高都從帆桁推（最低那根橫帆桁 course／crossjack 的 `pivot`）。
**砲、舵輪、舵、甲板上的東西**：一律模型 meta（上面的 kind 表）。

- 例（夜隼號）：`sail	mainsail	{"kind":"sail","mast":"main","sail":"gaff"}`、`rope	fore-topsail-sheet-S	{"kind":"sheet","sail":"fore-topsail"}`、`link	彈藥庫	{"pos":"aft"}`。
- 遊戲裡的三艘（憲法號、餘燼號、夜隼號）已由 ame 標好（2026-10-13，Sculpture e80831c）：帆 17／17／10、帆自己的繩 32／32／22、彈藥庫 2／1／1。
- ⚠ **沒有 meta 的舊匯出**，遊戲照舊照名字認（退路，⛔ 新船不要靠它）：帆自己的繩 `<帆名>.xxx` 或 `<帆名>-sheet…`／`-tack…`；旗 `ensign`／`pennant`／`flag` 開頭；
  帆裝照名字結尾（topsail、jib ⇒ 收帆；topgallant、spanker、fore-topmast-staysail ⇒ 戰鬥帆）；桅照帆名 `<fore｜main｜mizzen>-<course｜topsail｜…>`；彈藥庫照關聯名裡的「前／中／後」。
  🩸 這套名字規則分岔過一次（basecamp `fore-topsail.sheet-S`、summit `fore-topsail-sheet-S`）：遊戲只認點號那段期間，夜隼號上桅帆以上的帆腳不跟著轉、帆掉了 sheet 也不斷，**讀數上看不出來** —— 這是改用 meta 的原因。

### 9.2 空艙房（遊戲配置功能艙；TASK-0568）

遊戲（《憲法號對轟》）把空艙房當容器：玩家在船隻配置頁選房間類型（醫療艙…），在 0.5 m 一格的俯視格子上擺物品（病床、手術台、藥箱），或一鍵自動擺滿。**空艙房 ＝ 一個長方體**，標在艙房那件子作品裡：

- 固定點 `<房名>.min`、`<房名>.max`：兩個角的那一格（含兩端）。**只框空氣**；地板 ＝ 甲板面上第一層空氣（跟梯子 foot 同一個量法），頂 ＝ 上一層甲板底下那一格。房名照舊自己取（例 `1`、`2`…）。
- 可選 `<房名>.door`：門口站人的那一格（沒標 ⇒ 遊戲自己從四面牆找通出去的格）。
- 那條關聯 meta `kind=rooms`（meta 誰都能標）。**鏡像的關聯固定點會自己翻過去** ⇒ 一舷標一次、另一舷的那條關聯也標 `kind=rooms` 就好。
- 遊戲裡的名字 ＝ 關聯路徑／房名（例 `甲板-berth/艙房/3`、`甲板-berth/艙房-鏡像/3`）。艙房照關聯路徑屬於那一層甲板（§9.3）。

遊戲怎麼用這個框（標之前先想好）：

- 框裡切成 0.5 m（5 格）一格，零頭平分在兩邊。一格要整格腳下實心、頭上 1.4 m 內都是空氣才能擺 ⇒ 框貼著牆、地板要平。
- 門 ＝ 四面牆上往外連續 0.8 m 站得住的那幾格。門口那一格（加上門的兩旁各一格）留給走道、不能擺；每件物品都要有一邊從門口走得到。
- 病床 1.9 × 0.8 m 佔 4 × 2 格 ⇒ 要擺得下一張床，房間最少約 2.0 × 1.5 m（門開在短邊 ⇒ 長邊要 2.5 m）。更小的照樣標，配置頁會寫「放不下」。
- 那一層甲板沒有梯子通到 ⇒ 配置頁標「走不到」（傷員送不過去）。

候選與檢查（遊戲那側的工具，讀 `VoxelKit/_cache` 的 .vxk）：

```bash
python -I ArtGallery/HtmlGames/_build/ame_constitution_broadside/rooms.py scan  --id <船的代號>   # 候選：每個「艙房」部件裡被牆隔開的每一間 ⇒ 最大的全空氣長方體；印組裝件座標與子作品自己的座標（sub=anchor 直接用）
python -I ArtGallery/HtmlGames/_build/ame_constitution_broadside/rooms.py check --id <船的代號>   # 標好的：尺寸、裡面幾格不是空氣、跟別間重疊幾格、門；不合格的打 ✗
```

- ⚠ 候選照「關聯路徑結尾是 艙房／艙房-鏡像」找；艙房叫別的名字 ⇒ 自己量。scan 的座標是匯出當下的作品，改過船要重匯再掃。
- 憲法號、餘燼號（2026-10-14 量）：每舷 orlop 7、berth 8、砲甲板 2（餘燼號 orlop 多 1）；夜隼號沒有隔出來的艙房。拿候選試排醫療艙：憲法號 34 間有 20 間放得下病床、餘燼號 36 間有 22 間。
- 標完到遊戲裡看：`admin.py metasync --id <船>`（或重新匯入）、重建 ⇒ 船隻配置頁「甲板與艙房」一間一列；`?rooms=1` 畫出每一間的外框與名字。

### 9.3 甲板與通道（遊戲照 meta 判定甲板；TASK-0572）

遊戲照甲板分層（砲甲板、預備水手走得到的層、停格拿掉甲板、艙房屬於哪一層），⛔ 不自己量層高。結構：甲板 → 房間 → 功能物品（床、砲）；甲板本身也是一個大房間。

**甲板**：組裝件裡那條甲板關聯（在 0,0,0）的 meta（meta 誰都能標）

| 鍵 | 值 | 用途 |
|---|---|---|
| `kind` | `deck` | — |
| `deck` | 代號（`orlop`、`berth`、`gun`、`lower`、`upper`、`spar`、`poop`、`main`、`quarter`…） | — |
| `level` | 由下往上，0 起 | 順序 |
| `name` | 顯示名（底艙甲板、住艙甲板、砲甲板…） | 停格、廣播、配置頁 |
| `floor` | 舷弧 `"x:z,x:z,…"`：z ＝ 地板上第一層空氣；點之間線性內插，範圍外 ＝ 這一層在那裡沒有甲板 | 走路格、切面 |
| `thick` | 地板＋底下的樑幾格 | 停格拿掉甲板切多深 |

- 量：每一層甲板子作品單獨匯出 .vxk，`python -I ArtGallery/HtmlGames/_build/ame_constitution_broadside/decks.py --vxk 甲板-berth=<…vxk> …  --tsv <out.tsv>`，再 `work sub=meta file=<out.tsv>` 標上去；遊戲那側 `admin.py metasync --id <船>` 同步（只換 meta 與固定點，格子不動）。
- 房間、梯子、砲照**關聯路徑**屬於那一層（`甲板-berth/艙房`、`甲板-berth/梯子`）；路徑看不出來的照高度落在哪一層。

**通道**：梯子（§9 `kind=ladders`，foot／lo／hi／head）。兩頭各落在哪一層照那一欄的 `floor` 判斷（x 也看：兩層高度重疊、前後不重疊也分得開 —— 風暴角號後甲板與艏樓）。
- 要直接指定：同一條梯子關聯的 meta 加 `<梯名>.decks`，值是兩層的 `deck` 代號、`name` 或 `level`，逗號分隔（例 `{"bak-P.decks":"upper,forecastle"}`）⇒ 遊戲照它接，不看其他判定；level 低的那層接 foot。寫錯（找不到那層）⇒ 遊戲主控台警告、照 `floor` 自動判。
- 兩頭各自離那一層的走路格太遠（自動判 1 m、直接指定 2 m 內接不上）⇒ 那座梯子不用。遊戲裡 lo、hi 底下的踏板被打掉、或兩頭的甲板被打穿 ⇒ 那條通道斷（遊戲設定「尋路更新」開著時）。
- 沒有梯子通到的甲板 ⇒ 預備水手走不到（憲法號、餘燼號的 orlop 現在沒有梯子）。

### 9.4 匯進遊戲：成品預設壓縮（TASK-0583）

- **造船這側什麼都不用改**：作品與 `format=vxk` 匯出的格式不變；遊戲那側 `admin.py import／reimport` 存的 `_cache/<船>.vxk` 也是原檔（指紋防呆照舊）。
- 遊戲建成品（`admin.py build`，匯入、重新匯入、同步 meta、後台「儲存並重建」都走它）時才壓縮：模型網格收成一個池子 —— **同一份網格不管幾艘船用到只存一次**、頂點去重＋差值；寫出去之前照遊戲的讀法還原、跟原檔逐個比（對不上 ⇒ 不寫）。六艘：成品 9.09 MB → 5.36 MB。
- 量過大小的組成（壓縮後）：格子整艘約 0.1–0.2 MB，**模型網格才是大頭**（憲法號、三叉戟號各約 1 MB）⇒ 對成品大小最有效的是 §2.1「用已有的元件」，不是少雕幾格。
- 原格式 `admin.py build --raw`；`admin.py verify` 把成品跟 `_cache` 原檔比。⚠ 壓縮版的成品解不回原本的 .vxk ⇒ 遊戲那側 `_cache` 掉了要 reimport（`ArtGallery/HtmlGames/_build/ame_constitution_broadside/README.md`）。

## 10. 換一艘船時的檢查表

- [ ] 組裝件名稱 ＝ 船名（主作品）；船殼「<船名> 船殼」帶 `sub`（§2）
- [ ] 組裝件與每件子作品記了 meters_per_voxel；每個模型明寫 scale
- [ ] 開工前跑過 `work sub=components`（§2.1），能用的現有元件都直接引用；新做的元件列在 notes（為什麼不能用現有的）
- [ ] 砲、帆桁、舵輪、火藥桶是外部模型，各帶 meta 與固定點；羅經櫃、絞盤、小艇這類甲板上的東西也要帶 `kind`
- [ ] 梯子的關聯 meta `kind=ladders`，每座 foot／lo／hi／head 四個固定點
- [ ] 每一層甲板的關聯照 §9.3 標 `kind=deck`（level、name、floor、thick）；每一層都有梯子通到（要讓人走得到的）
- [ ] 空艙房照 §9.2 標（艙房子作品的固定點 `<房名>.min／.max`、關聯 meta `kind=rooms`）；`rooms.py check` 全部 ✓
- [ ] 每門砲退到零碰撞、砲口在船殼外；追擊砲照角度搜
- [ ] 繩與帆的端點綁 `model:` 路徑；`ropes_broken 0`、`sails_broken 0`、`models_broken 0`
- [ ] 每面帆、旗、帆自己的繩（sheet、tack）照 §9.1 標 meta（`work sub=meta part=… meta=` 或 `file=` 批次；`work sub=meta part=sail` 讀數的「沒有 meta」要是 0；繩只有帆自己的那幾條必標）；每根帆桁 meta 的 `sail` 等於它掛的那面帆的名字、`mast` 有寫
- [ ] 組裝件 1 元件、空腔 0、link_overlap 0；一舷＋鏡像的部件兩舷逐格對稱
- [ ] 產生程式重跑逐位元組相同（讀「藏起本階段關聯」或標記 `:begin` 的現況）
- [ ] 匯出 vxk、跟遊戲那側對 checksum
