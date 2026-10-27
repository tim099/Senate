---
title: 造船：船頭船尾的曲線與裝飾（大帆船）
description: 西班牙大帆船（galeón）的船頭船尾怎麼從「方盒子」改成模型那樣 —— 舷頂曲線、船尾 counter 與往裡收、beakhead 艙壁＋整片平台、stern walk、head 寬帶、真的能走出去的門；每一項改哪支產生程式、連帶要重產哪些件、照什麼順序套、踩過的坑。範例：康塞普西翁號（TASK-0969 步驟 17）
last_updated: 2026-10-27（新寫；Tim 2026-10-27「是否可以嘗試優化一下船的曲線(會需要動船殼)」「特別是船尾部分」「船頭 船尾 可以參考西班牙大帆船的模型 & 圖紙」「重新繪製船頭船尾曲線&裝飾」「另外可以把修改船頭 船尾的方案記錄成文件 開task讓其他類似船參考(收穫號)」；apex-two）
target_audience: [AI_Agent]
related:
  - Ship_Steps.md | Ship_Steps | 造船步驟表：每一步讀哪幾份
  - Ship_Setup.md | Ship_Setup | 開工前 —— 座標與比例、作品怎麼拆、用已有的元件
  - Ship_Decks.md | Ship_Decks | 甲板、梯子（甲板 meta 的 floor、走路格、第一人稱）
  - Ship_Fittings.md | Ship_Fittings | 門（kind=door）、裝飾
  - Ship_Materials.md | Ship_Materials | 窗的玻璃
  - Ship_Rigging.md | Ship_Rigging | 繩、帆的固定點
  - Ship_Portlids.md | Ship_Portlids | 砲門蓋板（追擊砲那兩片要跟著船尾重擺）
  - Ship_Game.md | Ship_Game | 匯進 Blockside、物件 id、交船檢查表
---

# ⛵ 造船：船頭船尾的曲線與裝飾（大帆船）

> **什麼時候讀**：船已經造好、要把兩頭從「方盒子」改成模型、圖紙那樣（改船殼）；或新造一艘大帆船、步驟 3 畫線形時就想把兩頭做好。
> 範例：康塞普西翁號 `apex-two-galeon-01m`（Sculpture，`design/` 裡的程式與 `notes.md` 步驟 17）。收穫號 `calli-cosecha-01m`、藍鯊號 `gura-pinas-01m` 是同一套產生程式（`lines.py`、`draft_ports.py`、`draft_deco.py`…），照抄就接得上。
> 參數看 `senate cmd help sculpture`，⛔ 本檔不抄參數表。

## 1. 參考什麼、改成什麼樣子

Tim 給的參考：San Felipe 整船模型（側面、船尾四分之一）、OcCre 船頭特寫、Galeón Andalucía 複製船的船頭。改之前的毛病：舷頂是直角台階的折線、船尾是一整面照艉柱斜度的平板、船頭從水線一路圓到船首樓頂、喙是一片薄板。

| 部位 | 改成 | 改哪裡 |
|---|---|---|
| 舷頂 | 船樓前後緣的落差從直角台階改成 S 形；船尾樓往船尾翹、船首樓往船頭翹 | `lines.py` `top_curve` |
| 船尾外形 | wing transom 以上往後鼓出一片 counter；船尾樓兩舷往裡收（上窄） | `lines.py` `counter`、`stern_tumble` |
| 船尾的漆與開口 | counter 上畫扇形（深淺木色＋金線）；counter 上那排窗拿掉；走廊後面兩舷開真窗；兩層正中間各一扇門 | `draft_ports.py` `counter_fan`、`stern.side_windows`、`stern.doors` |
| 船尾裝飾 | 兩層開放的 stern walk 從船尾繞過兩角、沿兩舷往前（欄杆、金扶手、底下金托架）；船尾拱形板跟著舷頂抬高、變窄 | `draft_deco.py` `stern_deco.walks` |
| 船頭外形 | 船首樓前面切一道平的 beakhead 艙壁、往後退；底下的船頭往前突出，頂面成平台 | `lines.py` `beakhead` |
| 船頭裝飾 | 艙壁上兩扇真的門、圓窗、菱格；喙改成跟平台同高的一整片平台（側板包下來、獅子站上去）；knee 從船底陡彎上來接平台底；一條寬帶（深色底、金邊、卷草）從艙壁上角彎到船首像後面；兩條細金線（cheek） | `draft_ports.py` `beakhead`、`draft_deco.py` `espolon`、`draft_ornament.py` `head_band`、`cheeks` |

## 2. 線形（`lines.py`）—— 船身中段一格都不能動

- **舷頂 `top_curve`**：`knots` 照舊的高度（船樓、船腰）列節點，**節點之間照 smoothstep 走**（平的那幾段不變、落差變 S 形）；`rise` 再加兩頭往上翹（`((x − from) ÷ (to − from))^pow × dz`）。落差一律放在**矮的那一邊**（toldilla 前緣落在 alcázar 那段、alcázar 前緣落在船腰那段）⇒ 高的那層甲板邊上的舷牆不會變矮。
  - ⚠ 支索板照 `top_z − below_rail` 放 ⇒ 翹的範圍要讓開前桅支索板那一段（康塞普西翁號船首樓從 x 590 才翹）。
- **counter**：`x_aft(z) = x_post(z) − c·smoothstep((z − z0) ÷ (z1 − z0))`（z0 ＝ wing transom、z1 ＝ 下層 stern walk 的地板高）。**長度比例 `s` 照艉柱線量**（不是照 counter 的最後面）⇒ 船身其他地方的寬一格不變；艉柱線後面那一段照 s ＝ 0 的寬往後伸、俯視兩角收 `round`（圓的船尾）。舵沿艉柱、在 wing transom 底下 ⇒ 不受影響。
- **船尾往裡收 `stern_tumble`**：船尾那一段（s < s_aft）z0 以上每格再收 k × ua²（ua ＝ 1 − s ÷ s_aft）⇒ 艉柱線那裡全收、往船中漸漸沒有。⚠ 這一項會動到船尾那幾門砲、後桅與 bonaventure 的支索板、兩舷側廊的位置（都要重產，見 §5）。
- **beakhead `{x, z}`**：z 以上、x 以前切掉（格心判斷）。x 要讓開前桅（桅軸在船首樓甲板那一層的 x ＋ 半徑，再加艙壁 4 格的牆）；z 要在艏斜桅從船頭穿出去的那一段**上面**（艏斜桅頂 ＋ 平台的牆 4 格 ⇒ 不撞）。
- 讀數：`draft_lines.py` 1 元件、空腔 0、鏡像差 0；新舊船殼清單**照 x 分段數差**（多／少／換色）⇒ 只有兩頭與三個船樓前後緣有差（康塞普西翁號：船尾 x < 350、船樓前後緣、船首樓 x ≥ 540）。⛔ 改程式之前先拿舊的 plan 重跑一次 ⇒ 輸出要逐位元組相同（控制組），不然分不出差是程式還是參數造成的。

## 3. 船殼的漆與開口（`draft_ports.py`）

- **counter 扇形**：艉柱線往後外伸的那一片外板（x ＜ x_post ＋ dx、z0–z1），以（中線, zc）為心每 deg 度一條金線、線之間兩色輪流；頂上一條金邊。扇形蓋過 wale ⇒ 讀數「帶子裡還是黑的」要扣掉扇形（也扣掉 beakhead 艙壁那一面）。
- **追擊砲門**：counter 是斜面 —— 砲門底下那段牆往前、頂上往後 ⇒ 砲口伸出去時砲管頂擦到砲門頂（康塞普西翁號 18 磅兩門都退到 37.5 格、砲口在船殼裡 26 格）⇒ 砲門整個往上挪 1 格（sill 6 ⇒ 7，大小不變 ⇒ 蓋板照樣蓋得住）。改之前先拿 `draft_guns.py` 的 Grid／Gun 沿砲軸一格一格探，看撞到的是哪幾格。
- **窗**：落在 counter 斜面上的那一排拿掉（`stern.rows` 刪那一列）。stern walk 後面兩舷的窗跟船尾窗同一套：從舷外往裡那一段牆，最外一格刻開、第二格玻璃（併進 -stern-glass）、後面刻穿到艙裡；外一圈金框。
  - ⚠ **「第二格」照窗洞裡 6 連通的距離算，不要一柱一柱算**：船尾兩個轉角的牆是斜的，一柱一柱放「從外往裡第二格」⇒ 相鄰兩柱的玻璃只在斜角相碰、轉角的船尾窗跟舷窗之間還空一格 ⇒ 空氣（水）鑽得過去（康塞普西翁號，Tim 在遊戲裡看到「玻璃好像有縫」）。窗洞的格照離船外的 6 連通距離，距離 1 那一層整層是玻璃（距離每一步只變 1 ⇒ 那一層一定封得住）；被玻璃夾住、哪邊都不通的空格也填成玻璃（不然組裝件多封閉空腔）。
  - 驗：每一片玻璃各自從船外灌、看灌不灌得到艙裡（康塞普西翁號 `design/tools/glass_seal.py`；反向對照拿掉一格玻璃 ⇒ 要報漏）；遊戲那邊觀察玩法的「💧 漏水」／匯入檢查的「漏水」一行（Blockside `js/75_leakcheck.js`）量整艘船。
- **beakhead 艙壁那一面**（朝 +x、x ≥ 牆的位置、z ≥ 切口）：先整片回到底色，再畫金邊框、甲板高度的金線、船首樓舷牆那一段的菱格（**x 換成 y 排**，不然整面是一條一條橫紋）；平台頂面甲板色（喙接上來之後外緣不描金）。
- **門洞**（§6）也在這裡刻，`verify_ports.json` 的 `doors` 給門板擺法。

## 4. 裝飾

### 4.1 stern walk（`draft_deco.py` `stern_deco.walks`）

每一層：`floor_z`（地板）、`dep`（伸出幾格）、`rail_h`、`x_end`（沿兩舷到哪裡）、托架 `bracket_every`／`bracket_len`／`bracket_off`。
- 地板那一層的**船殼外形照線形算**（`L.hb`）⇒ 2D 距離場 ⇒ 外形外 dep 格以內、x ≤ x_end 的格 ＝ 走廊。⛔ 照格子算外形：窗、門刻出來的凹口會被當成外面 ⇒ 托架落進窗洞裡浮空（量過：4 小塊）。
- 外緣取 `dist > dep − 1.5`（斜的地方兩格相接）⛔ `dep − 1` 只有斜角相接 ⇒ 扶手斷成單格。
- **地板 2 格厚**（下面那一層也填，外緣金）⛔ 只在外緣放一條金邊 ⇒ 船殼往外鼓（tumblehome）那一段被地板、金邊、船殼圍成封閉空腔（量過 548 格）。
- 欄杆：底欄、欄杆柱、金扶手；走廊前端封一片欄杆。**欄杆柱只放外緣最外那一層、每 3 格一根**（船尾面照 |dy|、兩舷與前端照 x）：外緣常是 2 層（外緣取 dep − 1.5），兩層各「隔一格一根」（`(x + y) % 2`）會前後錯開 ⇒ 從窗裡、從船外看過去一整片，兩舷是斜的、柱子沿台階連成牆（康塞普西翁號 v1，Tim：「一整片會遮住窗戶視野」）。柱子之間 2 格空、扶手底下 5 格 ⇒ 第一人稱（頭上要 7 格）鑽不過去。柱子遇到別的裝飾（鏡板的壁柱）就讓開，不然 `link_overlap`。**`rail_h` 至少 7**：第一人稱一步跨得上 6 格 ⇒ 4 格的扶手頂踩得上去、走出門就翻出走廊（康塞普西翁號 v1，遊戲裡走過才發現；喙平台邊的欄杆同一條）。
- 托架：沿走廊的位置（船尾面 ≈ |dy|、兩舷 ≈ x）每 every 一組、從地板底下往下收成 0，掛在 2 格厚的地板上。`bracket_off` 讓開門口（上層正中間那組會撞到下層門頂）。
- 舊的船尾走廊（balcony）、兩舷側廊、側廊第二層與托架全部拿掉 —— stern walk 取代；側廊的窗改成船殼上的窗（§3）。

### 4.2 船尾拱形板、燈

拱形板坐在船殼頂上（`z0 ＝ 船尾最高的舷頂 ＋ 0`），船尾往裡收之後頂上變窄 ⇒ `half` 收到船尾頂的半寬（康塞普西翁號 22 ⇒ 20；名帶的字 3×5、10 個字要 39 格寬 ⇒ half ≥ 20）。燈的位置（`lanterns.dy`）跟著收、模型 `overwrite=1` 搬上去。

### 4.3 船頭：整片平台、獅子、knee（`draft_deco.py` `espolon`）

- 喙的甲板頂 ＝ beakhead 平台頂（`deck_top_z`）、從艙壁前面 `deck_from_x` 起鋪（半寬從艙壁的寬收到喙根）⇒ 船殼的平台跟喙連成一整片（船殼佔的格照樣跳過）。
- 側板：甲板邊往下包到 `skirt_from_z`、上下兩條金線、最前面那一欄整片封、**底下不封**（封了 ⇒ 封閉空腔）。側板貼著船殼的那幾格會圍出 1 格的縫 ⇒ 喙的範圍外擴 3 格的框裡，船＋所有部件填滿內部、多出來的空格補進喙（`espolon_cavity_filled`）。
- knee：`cutwater_to_z`（平台底）、`cutwater_tip_x`（前緣在哪裡接上平台底）、`cutwater_pow`（前緣多凹）；從艏柱填到前緣（`cutwater_band` 夠大）；水線以下跟船底同色；兩側沿前緣往裡兩條金線（trailboard）。
- 船首像跟著 `deck_top_z` 抬高 ⇒ **先量艏斜桅的路**（仰角 30° ⇒ x 每往前 10 格高 5.8 格）：康塞普西翁號獅子在 x 650 正好擋在艏斜桅上（擋掉 216 格）⇒ 挪到喙尖 x 676。
- 艏斜桅從平台中間穿出去：喙的格碰到艏斜桅照樣跳過（自然留出孔）。

### 4.4 head 寬帶、cheek（`draft_ornament.py`）

- `head_band`：上緣兩段拋物線 ——（艙壁上角 x_h, z_h）往前往下到最低點（x_l, z_l，斜率 0）再到船首像後面（x_e, z_e）；帶高 h、2 格厚（外面那一層深色底＋卷草：一條波浪＋波峰上、波谷下各一個金點；裡面那一層素的）；dy 從船殼外一格照 t^p 收到 dy_e（＜ 喙在 x_e 的半寬）。最低點的帶底要在平台欄杆頂上面。
- v1 是三條細的 U 形欄杆＋直立格柵（San Felipe 整船照）；OcCre 特寫是一條寬帶 ⇒ `rails` 留空。⛔ 兩段拋物線之前用「兩頭連線往下凹 sag·sin」⇒ 終點只比最低點高 2 格、看起來是斜直線。
- 寬帶的起點與 cheek 貼的是**船殼**的最外面（只拿船殼算）⛔ 拿組裝件算 ⇒ 錨架在那個高度伸出去 ⇒ cheek 貼到錨架外面、離船殼 50 格浮空（8 小塊）。
- `cheeks`：船頭兩側貼著船殼往前往下彎進艏柱的金線；下面那條過了艏柱沿 knee 兩側再往前 `ext` 格（⚠ 不要超過 knee 在那個高度的前緣 ⇒ 浮空）。
- 重產時扣掉「自己現在的格」⇒ 自己的格如果也屬於別的件（之前重疊了）**不要扣** —— 不然一扣就把重疊的格當成空的、永遠重疊下去（`link_overlap` 卡在 4 的原因）。

## 5. 真的能走出去的門

Tim：「開真的可以打開的門(第一人稱模式可以走出去) 船尾有陽台的地方也可以開門出去」。

- 門不擋人（`Ship_Fittings` §9.7）⇒ 走不走得過去看**門洞有沒有刻穿、兩邊站不站得住**：第一人稱（Blockside `81_fps.js`）頭上 7 格空、一步上 6 格、下 8 格；船員走路格頭上 12 格、一步 3 格（`Ship_Decks`）。
- 門洞（`draft_ports.py`）：門洞那幾欄整段牆刻穿；底邊 ＝ 外面那一層站的那一格（stern walk 地板上一格、beakhead 平台上一格）、艙裡的甲板面差 1–2 格 ⇒ 一階。高度照艙裡到上一層橫樑底的空間（康塞普西翁號：第三層 11、alcázar 13、beakhead 15）。
- **船尾的牆往後倒** ⇒ 門板直立、擺在門洞最底下那一格牆的外面一格；門板那一面前面的牆也刻掉 ⇒ 門嵌在凹進去的門廊裡（⛔ 擺在最上面那一格外面 ⇒ 門板底下離牆 3 格）。
- **船頭 beakhead 的門**：門洞底邊要高過艙裡的甲板面、艙壁的牆留一段當門檻 ⛔ 從平台面刻起 ⇒ 門口正下方是底下船頭的艙，腳下一路空到下一層甲板，走不出去（康塞普西翁號 v1）。門板從平台面立起、比門洞高（連門檻一起蓋）。
- 讀數：`tools/walk_doors.py`（照 81_fps.js 的走法建有向圖、從艙裡走到門外再走回來；從門外走得到的格裡站在扶手頂上的要 0）；**反向對照** `--nocarve`（門洞當實心）⇒ 每扇都要走不出去；`--lowrail=3`（欄杆最上面 3 層拿掉）⇒ 扶手頂要站得上（康塞普西翁號 234 格）。
- 遊戲裡：`__game.fp(true, [x, y, z], yaw)` 進第一人稱（座標 ＝ 雕刻座標；⚠ 這支傳送照船員的頭上 14 格找站點 ⇒ 走廊上被船尾的牆壓住的地方傳不過去，要從艙裡用 W 鍵走出去：`KeyboardEvent('keydown', {code: 'KeyW'})`＋`__game.fpRun(秒)`）。
- 門板沿用藍鯊號 `gura-pinas-01m-door`（16 格高）、**只縮放高度**（`scale=0.5,0.5,<門板格數 ÷ 32>`）；`draft_doors.py` 照 `verify_ports.json` 的 `doors` 擺、查撞實心與腳下；掛在門洞那一層甲板的子作品（船尾朝 −x ⇒ rotate 180、beakhead 朝 +x ⇒ 0）。
- 遊戲：新的門 ⇒ 匯入後 `admin.py ids` 發物件 id；直立的門匯入檢查每扇報 ⚠「門被打掉也不會從這裡進水」（門洞在水線上很高，照實留著）。

## 6. 連帶要重產的件、套用順序

改了線形之後，**每一件照它自己的產生程式重產、`tools/delta_apply.py` 讓子作品對齊清單**，順序：

1. `draft_lines.py` ⇒ `draft_ports.py` ⇒ `tools/hull_expected.py`（船殼帳）⇒ 船殼打 `步驟17:begin` 標記、`delta_apply`。⚠ 舊的側廊窗洞清單要先清空（帳裡會把它當成要刻的）。
2. `draft_layout.py`、`draft_decks.py` ⇒ 每層甲板 `delta_apply`（船首樓扣掉煙囪洞）。梯子不變就不動。
3. 玻璃子作品 ＝ 船尾窗＋兩舷窗（＋側廊玻璃，清空）。
4. `draft_deco.py`（喙、船首像、stern walk、拱形板）⇒ `draft_ornament.py` ⇒ `delta_apply`；燈、門（`draft_doors.py`＋`tools/build_doors.py`）。
5. 組裝件 `stats structure=1`：1 元件（支索板浮空的話等 6.）、空腔 0、`link_overlap` 0；有重疊 ⇒ 逐件匯出兩兩取交集找是誰。
6. `draft_rigging.py`（舷頂、船殼的綁點、支索板、橫繩組的落腳點、bobstay 都會動）⇒ 支索板 `delta_apply`、`tools/rigging_apply.py anchors sails`、變了的橫繩組 `unratlines` 再下；`ropecheck`。⚠ 船頭那一欄的綁點上限（`bow_belay_max_x`）要在 beakhead 艙壁以後。
   - 砲門蓋板的工具找開口時拿砲口上下 ±14 格裡最外面那格當外表面 ⇒ counter 斜面量偏、追擊砲那兩片被跳過 ⇒ 船尾的砲門只看 ±6（`portlids.py opening(wz)`）。開著的追擊砲蓋板往上掀會插進上面那段 counter（只是畫面，照實留著）。
7. 錨（`draft_rudder_anchor.py`：量切線的 `x_try` 要在船殼還在的地方；錨架托板只留最大的那一塊連通的，斜邊只有斜角相接的小塊會浮空）、砲（`draft_guns.py`：船尾那幾門與追擊砲）、砲門蓋板（`tools/portlids.py draft／check／apply`，新的 cache 資料夾）。
8. 甲板 meta：每層 `export format=vxk links=none` ⇒ Blockside `src/decks.py` ⇒ `floor` 範圍跟著變（船尾往後、船頭收到艙壁）⇒ `work sub=meta`（名字照舊）。
9. Blockside worktree：reimport ⇒ `ids`（新的門）⇒ game.json 照 HEAD 原樣、只改真的變的那幾行 ⇒ shipcheck、ladcheck、遊戲裡看。
   - **外框變長了**（船尾的拱形板、stern walk 往後伸）⇒ 遊戲的 `hx0` 跟著動 ⇒ 陸戰隊照 `geo.marines`（從外框兩頭往裡讓幾格）整排往船尾挪、站不滿（康塞普西翁號 10／12）⇒ `marines[0]` 加上外框往後多出來的格數（35 ⇒ 56）。
   - Blocksail 的 `FLEET.hullLoa`（泊位停不停得進）＝ .vxk 外框長 × 0.1 ⇒ 跟著改（康塞普西翁號 65.1 ⇒ 67.2），泊位重對一次。
   - 船卡的水線長是量出來的：喙底下的 knee 形狀改了 ⇒ 水線那一層的前端跟著變（康塞普西翁號 50.7 ⇒ 47.5 m；只是顯示）。

- 設計還沒落格時：`draft_deco.py`／`draft_ornament.py` 帶 `--occ=<船殼帳;甲板…>` 拿本機清單當船跑；`tools/render_preview.py` 直接把清單畫成預覽圖（不用先貼進作品，正式產清單一律從組裝件讀）。
- ⛔ 套用的指令後面不要接 `| head`：管線一關，後面的那幾刀可能沒跑（讀回才會發現）。

## 7. 給收穫號（以及同一套產生程式的船）

收穫號 `calli-cosecha-01m` 的 `lines.py`／`draft_ports.py`／`draft_deco.py` 跟康塞普西翁號同源 ⇒ 把康塞普西翁號 `design/` 裡步驟 17 的改動（`lines.py` 四項、`draft_ports.py` 扇形／兩舷窗／門／beakhead 那一面、`draft_deco.py` stern walk／喙平台／填縫、`draft_ornament.py` 寬帶與 cheek、`draft_doors.py` 新的門）照抄過去，再照收穫號自己的尺寸調參數（甲板高度、桅位、艏斜桅仰角、船尾窗的排）。先跑控制組（舊 plan 逐位元組相同）再開新參數。
