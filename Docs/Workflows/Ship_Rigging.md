---
title: 造船：繩、帆、旗與它們的 meta
description: 靜索、ratlines（橫繩組、繩梯 climb）、動索、帆（方帆、縱帆、三角帆）、預設風、帆角綁帆桁模型；帆、繩、旗給遊戲的 meta
last_updated: 2026-10-26（從 Sculpture_Shipbuilding 拆出來，內文照搬、§ 編號照舊；Tim 2026-10-26「把造船流程的一些細節拆成獨立文件 按步驟建造到那一步才去讀取對應細節」；同日補 §9.1「帆裝三段各張哪幾面」—— 蓬特號的帆打不到）
target_audience: [AI_Agent]
related:
  - Ship_Steps.md | Ship_Steps | 造船步驟表：每一步讀哪幾份
  - Sculpture_Shipbuilding.md | Sculpture_Shipbuilding | 造船入口與舊 § 編號對照
  - Ship_Setup.md | Ship_Setup | 開工前 —— 座標與比例、作品怎麼拆、用已有的元件、建造順序
  - Ship_Guns.md | Ship_Guns | 砲 —— 一門一件子作品、擺上船退到零碰撞、固定點、砲具與砲索、缺的砲種做成通用部件
  - Ship_Turrets.md | Ship_Turrets | 迴旋砲、砲塔、裝甲子模組、砲塔層、揚弾機
  - Ship_Masts.md | Ship_Masts | 桅與帆桁
  - Ship_Magazine.md | Ship_Magazine | 彈藥庫與火藥桶
  - Ship_Render.md | Ship_Render | 模型在遊戲裡的低面數畫法（render=lathe／pole／shell／wheel）
  - Ship_Decks.md | Ship_Decks | 甲板、梯子、空艙房（給遊戲的 meta）
  - Ship_Materials.md | Ship_Materials | 材質 —— 鐵甲、銅皮、混合材質、平滑與反光、窗的玻璃
  - Ship_Steam.md | Ship_Steam | 蒸汽動力 —— 主機、鍋爐、煙囪、軸、螺旋槳、蒸汽管
  - Ship_Fittings.md | Ship_Fittings | 門與艙蓋、裝飾、撞角、廚房、被打掉怎麼演
  - Ship_Helm.md | Ship_Helm | 舵與舵輪（照轉向轉）、操舵鏈
  - Ship_Oars.md | Ship_Oars | 槳（槳帆船）
  - Ship_Portlids.md | Ship_Portlids | 砲門蓋板
  - Ship_Game.md | Ship_Game | 給遊戲的 meta 總表、匯進 Blockside、物件 id 與掛點、交船檢查表
  - Ship_Refit.md | Ship_Refit | 改裝船（原船當一條關聯、只存差）
---

# ⛵ 造船：繩、帆、旗與它們的 meta

> **什麼時候讀**：步驟 8（索具、帆、旗）。步驟表在 `Ship_Steps`（`senate cmd doc --arg op=show --arg name=Ship_Steps`）。
> 這份是從 `Sculpture_Shipbuilding` 拆出來的（2026-10-26）：**§ 編號照舊**（別處寫「造船規格 §9.3」照樣對得到這裡的 §9.3）；文中提到別的 § ⇒ 查 `Sculpture_Shipbuilding` 的「§ 編號對照」看住哪一份。
> 參數看 `senate cmd help sculpture`，⛔ 本檔不抄參數表。

## 6. 繩

| 類 | 色 | 粗 | slack | 備註 |
|---|---|---|---|---|
| 靜索（shrouds、backstays、stays）| #2b2620（上過焦油）| 0.4–1.5 | shrouds／backstays 1.0；stays 1.0006–1.0008 | ⚠ 長而陡的繩一點點鬆就垂很多：backstays 1.0004 垂 19–37 格 ⇒ 1.0 |
| ratlines | #2b2620 | 0.3／0.25 | **橫繩組**（`sub=ratlines`，見下）| 每 4 格一道（`step=4`）；從上 deadeye 頂往上 7 格起（lower `start=7 stop=16`、topmast `start=4 stop=10`）；短於 5 格不畫（`min_span=5`） |
| 動索（sheets、tacks）| #a08458 | 0.35–0.5 | course sheet 0.85、tack 0.8 | — |
| 砲索 | #b59a6a | 0.6／0.35 | 1.12／1.03／1.05 | §4.4 |
| 扶手繩（梯子、entry port）| #cdb88e | 0.35 | 1.02／拉直 | — |

- 固定點登記在**實際的格子上量**（hounds ＝ trestletrees 下一格的桅側…）；channels 的 deadeye 只登記一舷，另一舷用 `channels-鏡像/…`（兩舷每條長度相同）。
- 上 deadeye 用一段短而粗的繩當圓盤（軸朝舷外、直徑 3、長 2），lanyards 3 條排開 ±0.9（階段二十五）。
- 全部分組（`rope_groups.tsv`）：standing/…、ratlines/…、running/…、guns/…、ladders/…、steering —— 觀測時可以一組一組開關。
- 順序坑：會改同一批繩的工具（rig_standing → rig_deadeyes → ratlines）要照順序重跑，前一支會把後一支改過的端點改回去。
- **ratlines 用橫繩組，⛔ 不再一條一條下子繩**（TASK-0678，Tim 2026-10-17）：每根桅、每一層 shrouds、每一舷一組，只標**最前、最後那兩根 shroud**＋間距 ⇒ 觀測時引擎照它們現在的形狀等高展開（`組名.01…`，由下往上）。shroud 的下端換了（rig_deadeyes）也不用重跑 ratlines（上一條的順序坑對 ratlines 不再成立）。寫法與擋法見 `Sculpture.md` §9.4.6「橫繩組」。**不限作者**（Tim 2026-10-17「遊戲meta相關可以調整」）：遊戲那側可以直接替別人的船改橫繩組、標繩梯（記在 `meta_log.jsonl`）。
  ```bash
  senate cmd sculpture --arg op=work --arg sub=ratlines --arg work=<組裝件> --arg persona=<P> --arg name=fore-lower-P --arg rope_a=fore-shroud-P1 --arg rope_b=fore-shroud-P8 --arg step=4 --arg start=7 --arg stop=16 --arg min_span=5 --arg thickness=0.3 --arg color=#2b2620 --arg group=ratlines/lower
  ```
  - 組名照 `<桅>-<lower|topmast>-<P|S>`；`rope_a` 用最前那根（x 最大）、`rope_b` 最後那根 ⇒ 每道從前往後拉（跟舊的子繩同方向）。
  - 起點只看這兩根的下端（中間那幾根不算）：憲法號換成橫繩組後，每道比舊的 `ratlines.py`（拿整排最高的 deadeye）低 0.00–0.02 格；道數 12 組全部相同（328 道）。
- **繩梯**（`climb=1 foot= head=`）：標了的那一組，遊戲（第一人稱）照 foot → 最低一道 → 最高一道 → head 爬（§9 `ladders` 那一列、§9.3 通道）。lower ratlines：foot ＝ 舷牆**內側**的甲板（channel 在船殼外，⛔ 不要放在 channel 上）、head ＝ 桅頂平台靠這一舷的邊（＝ topmast ratlines 的起點那一格）；topmast ratlines 上面的 crosstrees 沒有站得住的格 ⇒ 先不標 climb。落腳點要**站得住**：腳下實心、上面 7 格空（同遊戲的 `standZ`）——量法：沿這一組最低一道的中點往船內走，過了舷牆之後第一格甲板再往內 2 格。

## 7. 帆

- 方帆：上緣（邊 0）整條釘在兩個 `earing` 之間、下兩角 free；topsail 以上的 sheet 綁到下一根帆桁同舷的 earing；course 的 sheet 往後、tack 往前綁到舷牆的 belaying pin。
- spanker：gaff、boom、桅三條邊釘住，只有 leech 自由。三角帆：tack 在 stay 下端、head 是 stay 上 70–80% 那一格、luff 沿 stay（那條 stay 改 slack 1 拉直）。
- 數值：預設風 **4.5,−1.5,0**（4.7 m/s）、fullness 1.05（7.6 m/s ＋ 1.08 ⇒ 像降落傘）；帆布 #f2ead8。鼓度主要的旋鈕是**風**，fullness 只差幾格（kotoko 兩點的試驗表）。
- 帆角綁帆桁模型（`model:<桅關聯>/yard-<帆桁>/earing-P`）⇒ 帆桁一轉，帆跟著走。下角綁在甲板上的（course、三角帆的 sheet）：遊戲把 sheet 長度照「帆腳跟著這面帆的帆桁轉過之後」到甲板那一端的距離放長（Blockside 30_rig_physics.js tethYard，2026-10-18）⇒ 帆桁轉很大（拋錨、大轉彎）帆腳不會被拉回原處、整片拖到甲板（持火號前帆以前偏 7.3 m，修完 1.7 m）。
  ⇒ sheet 一定要標 `kind=sheet, sail=<帆名>`（§9.1）；帆要綁在帆桁模型上（綁最多點的那根算這面帆的帆桁）。

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
| `set`（帆，可選） | `1`／`2`／`3` | 例外：最少在哪一段帆裝打開（1 收帆、2 戰鬥帆、3 滿帆）—— 跟種類的預設不同才寫（憲法號 fore-topmast-staysail `2`、flying-jib `3`；只有兩面帆的船要分得出段：持火號前帆 `3`、中帆 `1` ⇒ 收帆、戰鬥帆只張中帆，Tim 2026-10-18「沒有單帆狀態」） |
| `flag`（旗；`kind=flag` 才寫） | `ensign`（國旗：船尾旗桿或後桅斜桁頂）／`pennant`（長旗：桅頂）／`jack`（艏旗：艏斜桅旗桿）／`signal`（信號旗） | 降旗時降 `ensign`；遊戲的陣營旗要換掉作品的哪一面，照這個種類找（下面「旗」） |

**帆裝三段各張哪幾面 —— 標完帆的 meta 一定要排一次**（Tim 2026-10-26「蓬特號的帆好像打不到」）：
- 每面帆（旗不算）算出它的段數：有 `set` ⇒ 照 `set`；沒有 ⇒ 照 `sail` 的預設（`topsail`、`jib` 1；`topgallant`、`gaff` 2；`course`、`royal`、`staysail` 和其他 3）。帆裝第 k 段張「段數 ≤ k」的那幾面（0 全收一面都不張）。
- 排成一張表寫進 notes（收帆／戰鬥帆／滿帆各張哪幾面），看三件事：
  1. **收帆、戰鬥帆都至少張一面**（除非這艘船本來就該收光）。電腦開戰常用戰鬥帆 ⇒ 戰鬥帆一面都不張的船，一開戰帆就全收了。
  2. 三段要分得出來（持火號兩面帆：前帆 `3`、中帆 `1`）。
  3. 少帆的船多半要寫 `set`：**只有一面帆 ⇒ `set 1`**（德羅蒙號、fusta、蓬特號）；預設是照三桅全裝帆船排的，course 要到滿帆才張。
- 收起來的帆：不算推力、⛔ **砲彈與鏈彈打不到**（Blockside `21_gunnery.js` cutRig 跳過收著的帆）、索具斷了也不掉 —— 規則就是這樣，不是 bug。
  🩸 蓬特號（2026-10-26）：唯一一面帆是 `course`、沒寫 `set` ⇒ 戰鬥帆收著 ⇒ 打不到；偏偏同時有「上下兩根帆桁收帆被拉直」的 bug（`Ship_Masts` §5），畫面上看起來還張著，所以只看到「打不到」。補 `set 1`（Sculpture 87cf9e96）之後戰鬥帆張著，牛津號 285 m 開 11 發，帆血 150 → 124。
- 遊戲裡驗（`?ship=<船>` 開一局，W／S 換帆裝；或主控台）：`__game.ships()[0].sails.filter(s => !s.flag).map(s => s.name + (s.furled ? ' 收' : ' 張'))` —— 每一段都看一次，跟 notes 那張表一樣才算過。⚠ 電腦會自己換帆裝：要看自己的船，先按一次 W／S（玩家動過帆，自動駕駛就不替他操帆）。
- 已經在遊戲裡的船改了帆的 meta ⇒ 遊戲那側要重匯（`admin.py reimport --id <船>`）：印出來的差別要看得到這一行 meta（`vxk_compare` 有比帆的 meta）。印「內容相同 ⇒ 不換」卻明明改了 ⇒ `_cache` 已經是新的那份（之前跑到一半被擋下的那次就先換了）⇒ `admin.py relock --id <船>`＋`ropebind --id <船>`（會重建），⛔ 只 relock 再 build 會少了繩端綁定（ship 檔小了一截：蓬特號 38444 → 38168 bytes，跑完 ropebind 才是 38448）。

**旗**：作品裡每一面旗都標 `kind=flag` ＋ `flag=<種類>`（Tim 2026-10-15：作品原本的旗標上 meta、遊戲照 meta 認是不是旗、是哪一種）。
- 遊戲（Blockside `77_colours.js`）另外加**陣營旗**（藍方藍旗、紅方紅旗，仿英國艦隊旗）：每根桅頂往上加一根旗桿掛在上面 ⇒ ⛔ 不碰作品的旗、造船的人不用留位置。
- 要把作品的某一面換成陣營旗（例：憲法號的國旗）⇒ 遊戲設定 `ships[].flags` 寫 `{"kind": "ensign", "replace": "ensign"}`：照 meta `flag` 的種類找那一面（沒有 meta 才照名字），布料照原本那面、只換貼圖 ⇒ **作品的旗要有貼圖（uv）** 才換得了。
- 旗是布（跟帆同一套）：被打扣血、破洞、掛的地方斷了就掉；`flag=ensign` 降旗時降下來。

**繩**（`part=rope`）—— 必標的只有「帆自己的繩」；其他繩可以不標

| 鍵 | 值 | 遊戲拿來做什麼 |
|---|---|---|
| `kind` | `sheet`／`tack`（導索、延長段同一類）；其他繩若要標：`shroud`、`backstay`、`stay`、`ratline`、`brace`、`lift`、`halyard`、`footrope`、`steering`、`gun`、`manrope`；武器演出的繩 `bowstring`（弦）、`gunrope`（拉繩、投袋繩）＋ `anim=gun`（§2.2） | 分類 |
| `sail` | 帆名 | 這條繩屬於哪面帆 ⇒ topsail 以上的 sheet 綁在哪根帆桁 ＝ 帆腳跟那根帆桁轉；帆掉了這幾條一起斷 |

**關聯**：彈藥庫 `kind=magazine` 加 `pos=fore／mid／aft`（廣播「前／中／後彈藥庫」）。
**桅**：帆桁 meta 的 `mast`（§5）—— 有幾根桅、各在哪、多高都從帆桁推（最低那根橫帆桁 course／crossjack 的 `pivot`）。
**砲、舵輪、舵、甲板上的東西**：一律模型 meta（上面的 kind 表）。

- 例（夜隼號）：`sail	mainsail	{"kind":"sail","mast":"main","sail":"gaff"}`、`rope	fore-topsail-sheet-S	{"kind":"sheet","sail":"fore-topsail"}`、`link	彈藥庫	{"pos":"aft"}`。
- 遊戲裡的三艘（憲法號、餘燼號、夜隼號）已由 ame 標好（2026-10-13，Sculpture e80831c）：帆 17／17／10、帆自己的繩 32／32／22、彈藥庫 2／1／1。
- ⚠ **沒有 meta 的舊匯出**，遊戲照舊照名字認（退路，⛔ 新船不要靠它）：帆自己的繩 `<帆名>.xxx` 或 `<帆名>-sheet…`／`-tack…`；旗 `ensign`／`pennant`／`flag` 開頭；
  帆裝照名字結尾（topsail、jib ⇒ 收帆；topgallant、spanker、fore-topmast-staysail ⇒ 戰鬥帆）；桅照帆名 `<fore｜main｜mizzen>-<course｜topsail｜…>`；彈藥庫照關聯名裡的「前／中／後」。
  🩸 這套名字規則分岔過一次（basecamp `fore-topsail.sheet-S`、summit `fore-topsail-sheet-S`）：遊戲只認點號那段期間，夜隼號上桅帆以上的帆腳不跟著轉、帆掉了 sheet 也不斷，**讀數上看不出來** —— 這是改用 meta 的原因。
