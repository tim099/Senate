---
title: 造船：砲 —— 一門一件子作品、擺上船退到零碰撞、固定點、砲具與砲索、缺的砲種做成通用部件
description: 舷側砲：座標慣例、外部模型、退到零碰撞、追擊砲照角度搜、後座、砲身與砲座拆兩件、上下轉、固定點、砲具與砲索；缺的砲種或裝備怎麼做成通用部件、怎麼加進遊戲砲種表
last_updated: 2026-10-26（從 Sculpture_Shipbuilding 拆出來，內文照搬、§ 編號照舊；Tim 2026-10-26「把造船流程的一些細節拆成獨立文件 按步驟建造到那一步才去讀取對應細節」）
target_audience: [AI_Agent]
related:
  - Ship_Steps.md | Ship_Steps | 造船步驟表：每一步讀哪幾份
  - Sculpture_Shipbuilding.md | Sculpture_Shipbuilding | 造船入口與舊 § 編號對照
  - Ship_Setup.md | Ship_Setup | 開工前 —— 座標與比例、作品怎麼拆、用已有的元件、建造順序
  - Ship_Turrets.md | Ship_Turrets | 迴旋砲、砲塔、裝甲子模組、砲塔層、揚弾機
  - Ship_Masts.md | Ship_Masts | 桅與帆桁
  - Ship_Rigging.md | Ship_Rigging | 繩、帆、旗與它們的 meta
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

# ⛵ 造船：砲 —— 一門一件子作品、擺上船退到零碰撞、固定點、砲具與砲索、缺的砲種做成通用部件

> **什麼時候讀**：步驟 9（砲）。步驟表在 `Ship_Steps`（`senate cmd doc --arg op=show --arg name=Ship_Steps`）。
> 這份是從 `Sculpture_Shipbuilding` 拆出來的（2026-10-26）：**§ 編號照舊**（別處寫「造船規格 §9.3」照樣對得到這裡的 §9.3）；文中提到別的 § ⇒ 查 `Sculpture_Shipbuilding` 的「§ 編號對照」看住哪一份。
> 參數看 `senate cmd help sculpture`，⛔ 本檔不抄參數表。

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
- **後座**（模型 meta `recoil`；Tim 2026-10-20「是否可以讓沒有炮門的炮 也有後座力呢」「要注意排除弩炮 投石機等(要根據meta判斷)」）：遊戲照砲軸往船裡退、裝填快好再推出去（Blockside `62_ports.js` recoilDist；只是畫面，跟砲門蓋板同一個畫質項）。
  退幾格照這個順序：模型 meta `recoil=<格>`（`0` ＝ 不演）⇒ 固定彈種（meta `ammo`，沒寫照 `weapon`／`gun` 對到的砲種表）是弩箭、火箭、石彈、希臘火 ⇒ 不演（沒有火藥）⇒ 有蓋板 ⇒ 它的 `house` ⇒ 砲管長（`muzzle` → `breech`）× 0.3、至少 4 格。
  ⇒ 一般的砲不用寫；**機械武器照 §2.2 標 `ammo`／`weapon` 就自動不演**；只有預設不對的那一門才寫 `recoil`（例：砲退下去會撞到後面的東西 ⇒ 寫小一點）。匯入檢查「後座」那一行列出每艘幾門、退幾格、哪幾門不演。
- 迴旋砲的砲門（§4.5，`ports` 好幾個角度）：遊戲換砲門時砲會轉過**中間的角度** ⇒ 兩個砲門中間的舷牆柱要拿掉，連成一個口子（蘇士奎哈納號 `design/draft_pivot_sweep.py`：相鄰兩個角度之間每 2.5° 照刻砲門同一條規則再刻，刻完每個角度量零碰撞；Tim 2026-10-20「中間的竿子在砲旋轉時會穿透 應該可以移除」）。

#### 4.2.1 砲身與砲座拆兩件（模型 meta `kind=gunmount`；TASK-0836）
Tim 2026-10-23：「把一些炮跟炮身拆開(在雕刻中維持原外貌) 讓炮的後座力只讓炮身移動」「炮座應該是固定在船上的」「這個屬於遊戲相關調整 確保雕刻外觀接近即可」「整門會一起飛 ok的」。

- **什麼時候要拆**：後座（§4.2）退的是**整個砲模型**。砲架本來就跟著砲退的（舷側砲的四輪砲架、carronade 的滑座）不用拆；**座固定在船上、只有砲身在搖架裡退的**（速射砲的中心樞軸座、立柱座、砲塔裡的液壓滑座：松島號那一代以後）才拆 —— 不拆的話整座砲連座一起往後滑。
- **怎麼拆**：砲的那件子作品（例 `-gun-armstrong120`）照色號用 `work sub=split`（Sculpture.md §9.4.12：不限作者、外觀由引擎逐格證明不變）把座拆成 `-mount` 子作品：
  ```bash
  senate cmd sculpture --arg op=work --arg sub=split --arg persona=<我> --arg work=<砲> --arg into=<砲>-mount --arg colors=<座的色號> --arg twin="{name}座" --arg twin_meta='{"kind":"gunmount"}' [--arg confirm=1]
  ```
  每個拿這件砲當外部模型的擺放都會自動多一個雙生 `<名>座`（擺法照抄、meta ＝ `kind=gunmount`）。再用批次 meta（`work sub=meta file=`，每行 `model<TAB><名>座<TAB>{"mount_of":"gun-N","id":"gunmount-N"}`）把每個座指到**自己那門砲的 id**（§12）。
  分法：砲身（砲管、閂、耳軸、砲身上的瞄具）留在砲那件；搖架、滑座、立柱、駐退筒、砲盾、手輪拆走。外觀「接近」就好 —— 實物會跟著仰角的搖架算座也可以。
- **遊戲怎麼用**（Blockside `05_hull.js` gmtPair、`60_rbatch.js`）：座是裝飾（規格表 deco：不算重量、不擋彈、不抽亂數）、畫質「裝飾」關著也畫（always）。
  模擬看到的砲 ＝ 砲身＋座接成一份 ⇒ 外框、砲班站位、被打中的範圍跟拆之前一樣；打飛、斷船帶走 ⇒ 整門一起。畫面分兩件：**開火只退砲身**；座跟著砲的擺法 —— 迴旋砲、砲塔（§4.5、§4.8）轉的時候跟著轉，**固定式的砲（沒標 `mount`、不在砲塔上）座就固定在船上**。
  別艘船換裝（§12.4）拿這門砲當零件 ⇒ 拿到的是砲身＋座一份（那艘照舊整門一起退）。
- 匯入檢查「砲身與砲座」那一行：座幾件、配到幾門砲；`mount_of` 配不到 ⇒ ✗、座不畫。
- 實例（松島號，2026-10-23）：Canet 32 cm（座 ＝ 液壓搖架、斜滑座、駐退筒、裝填台階，色 73）、Armstrong 12 cm（中心樞軸座、駐退筒、砲盾、手輪，色 73／146／180）、哈乞開斯 57／47 mm（立柱座、肩托，色 73）；25 個座。定遠號（同日）：克虜伯 30.5 cm、15 cm（滑架、側板、拉桿、駐退筒，色 73）、37 mm 轉膛砲（立柱座、叉架，色 73；松島號艦橋也引用這件 ⇒ 一次補兩艘的雙生）；16 個座。

#### 4.2.2 砲身上下轉（模型 meta `pitch`；TASK-0836）
Tim 2026-10-23：「上下轉動只要大型砲塔的炮支援就好 松島號主炮這種的」「然後轉動只是演出 不影響任何遊戲運算」。

- 標在**砲**的模型 meta（或砲那件子作品的預設 meta `work sub=meta part=work`）：`pitch=1` ⇒ −5° 到砲種表的最大仰角（`elev`，沒寫 15°）；`pitch="-5,10"` ⇒ 自己給下限、上限（度）。
- 要有：固定點 `trunnion`（耳軸：繞它轉）、`breech`、`muzzle`；**砲身跟座要拆兩件**（§4.2.1）—— 不然整門連座一起轉，匯入檢查報 ⚠、不轉。
- 遊戲（`62_ports.js` gelAnim）：照瞄準點用開火同一條仰角公式算、扣掉船身沿砲軸的傾斜、每秒最多轉 8°；後座改成沿抬起來的砲管退。**只是畫面**：砲口、砲軸、開火的仰角一個都不動 ⇒ 戰局不變。
- 只給大型砲塔的砲（砲塔層 §4.8、`mount=turret`）用；舷側砲、速射砲不標。實例：松島號 32 cm Canet（`-gun-canet320` 的預設 meta `pitch=1`、`recoil=10`：預設照砲管長 × 0.3 會退 3.8 m、砲尾穿出砲塔後面）、定遠號 30.5 cm 克虜伯（`-gun-krupp305` 的預設 meta `pitch=1`；砲罩整圈罩住，退 2.3 m 照預設）。
- 拆兩件之後座不動、砲身照退 ⇒ **退的距離比較看得出來**：大口徑的砲用預設（砲管長 × 0.3）退得太遠時，在砲的子作品預設 meta 寫 `recoil=<格>`（§4.2「後座」）。

### 4.3 固定點（子作品裡登記，經關聯／模型每門都有）
| 名字 | 位置 | 用途 |
|---|---|---|
| `muzzle` | 砲口內膛那一格（中線挖空，⛔ 不是實心格）| 砲軸 ＝ muzzle − breech（遊戲）|
| `breech` | 砲尾那一格 | 同上 |
| `cascabel` | 砲尾的環 | breeching 繩 |
| `eye-aft`／`eye-fwd` | 兩片 cheek 外側的環首螺栓 | side tackle（⚠ 是船尾側／船頭側，不是左右舷 —— 第一次登記成 eye-P／eye-S，改過名）|
| `train` | 砲架尾 | train tackle |
| `pivot` | carronade 的樞軸 | — |
| `trunnion` | 耳軸的中心 | 砲身上下轉的轉軸（§4.2.2 `pitch`）|

### 4.4 砲具與砲索（階段二十一、二十二）
- 船牆的環：砲門中線往船尾／船頭 8 格、甲板面 ＋8；甲板的環：砲架尾往裡 9 格。**每個環都量**：砲尾 → 環、cheek → 環兩條線不准穿過別的實心（自己的砲架不算），另一舷鏡過去也量；不行就換 offset 9／10／11，再不行就不放、那一側的繩不掛（照實記）。
- 繩：breeching（cascabel → 兩側船牆的環，slack 1.12、粗 0.6、#b59a6a）、side tackle（eye → 同側的環，1.03、0.35）、train tackle（train → 甲板的環，1.05、0.35）。端點寫 `model:<甲板關聯>/<砲>/<固定點>`。
- 甲板環兩舷一起找（鏡像）：砲正後方那一舷是梯子的井（沒有甲板）、或繩擦過井的圍板 ⇒ 沿砲的側向挪（藍鯊號 plan `deck_ring.lat`：0、±3、±5、8、−10、10、−13、12、−16，兩舷同一個 lat；M04 挪 10 格才閃過底艙梯子的井，TASK-0967）。
- rammer／sponge 掛在砲正上方那根橫樑底下（貼著甲板板會被 3 格深的橫樑擋住、從艙裡看不到）。

### 2.2 缺的砲種或裝備：做成新的通用部件（Tim 2026-10-18）

Tim 2026-10-18：「有缺的炮種或裝備 就加成新的通用部件吧」。`sub=components` 查不到那一種（kind 或子種類），或現有的跟史料差太多（固定砲架 vs 叉架上的迴旋砲、有橫桿的錨 vs 四爪錨）⇒ **做一件新的通用部件**，⛔ 不要只為這艘船雕進船殼或甲板的格子裡 —— 雕進格子的下一艘船拿不到、遊戲也認不得。
只差大小、口徑差一點的（36 磅 vs 32 磅、8 磅 vs 6 磅）照 §2.1 縮放現有的，⛔ 不另做。

| 項目 | 規定 | 為什麼 |
|---|---|---|
| 作品 | 一件子作品、`tags=sub`、`parent_work` 掛在現在這艘船的船殼（免費、不另領薪）；ID `<船殼 id>-<kind>-<名>`（例 `apex-one-reale-01m-gun-pierrier`） | 一件就是一份網格（§9.4）；跟船的格子分開 ⇒ 誰都能當模型來源 |
| 名稱 | 寫成通用的：「<東西>（<子種類>，通用部件，<一格幾公尺>）」，⛔ 不寫「<船名> 的…」 | `sub=components` 列的是名字；下一個造船的人照名字挑 |
| 比例 | 小件 0.05 m（模型 scale 0.5 明寫）、更小的 0.02 m（scale 0.2）；書卡記 `meters_per_voxel` | §1：沒記的作品當模型來源時 scale 自動換算會錯 |
| 座標 | 照同一種東西的慣例：砲照 §4.1（x 中線、砲口朝 +y、z 0 ＝ 站的那一面）；錨照憲法號的錨（錨幹沿 +z、ring 在上）；門照 §9.7 | ⛔ 自己發明擺法 ⇒ 每個引用的人都要重算 rotate |
| 固定點 | 照 §9 kind 表那一列登記（砲 `muzzle`（內膛空格）／`breech`／`cascabel`，會轉的加 `pivot`；錨 `ring`／`crown`／`fluke-a／b`；門 `hinge-a／b`…） | 遊戲與繩照固定點找；缺 ⇒ 匯入檢查報 |
| meta | 照 §9 kind 表；同一種東西 meta 一樣（`kind=gun, gun=<代碼>`、`kind=anchor, anchor=…`） | 遊戲照 meta 認，不看名字 |
| 產生程式 | 放在做它的那艘船的 `design/`，檔頭寫「通用部件」；格數、框、固定點寫進那艘船的 notes | 要改的人找得到來源 |
| 量 | 零件 1 元件、空腔 0；在自己的船上照 §4.2 退到零碰撞（格心＋八角）、砲口射線不碰到自己的船 | 下一艘船引用時只剩擺法要量 |

- **不是大砲的武器做成「換皮大砲」**（Tim 2026-10-18：「弩炮目前我想直接做成換皮大炮(只是彈藥不同) 一樣走裝彈 發射流程」「弩炮可以給一定的轉動空間(參考維吉尼亞號的前後炮)」）：
  照砲的規格做元件（§4.1 座標、`muzzle`／`breech`／`pivot`），模型 meta `kind=gun, gun=<砲種表的代碼>, weapon=<武器種類>, ammo=<彈種>`；遊戲砲種表要有那一種（缺的照上一條自己加）。
  走同一套裝填、送藥、發射；彈種照砲種表的 `ammo`（固定彈種 ⇒ 換彈鍵換不到它；只有主武器的彈種能換）。要轉 ⇒ `mount=pivot, ports=0,±N`（§4.5，**每個停靠角度都量零碰撞**）。
  持火號：弩砲 `gun=ballista, weapon=toxoballistra, ammo=firebolt`（彈種火箭：照弩箭飛、畫成一支帶火的箭、沒有砲煙，碰到船那一點著火，見下面「彈種」）、船頭 `ports=0,35,-35`（±40 擦到艏樓欄杆 9 格）、木塔 `ports=0,45,-45`；
  siphon `gun=siphon, ammo=greekfire`；投石機 `gun=mangonel, weapon=manganikon, ammo=stone, mount=turret, arc=-180,180`（整圈轉：船頭對敵的船只給兩舷 `ports=0,180` ⇒ 120 秒只對得到 6.5 秒、一發都沒開；每 30° 量零碰撞）。
  - **砲種表的通用鍵**（Blockside `src/config/game.json` 的 `guns`；Tim 2026-10-18）—— 不寫 ⇒ 跟大砲一樣，別的船戰局一個值都不變：
    | 鍵 | 意思 | 例 |
    |---|---|---|
    | `ammo` | 固定彈種：`greekfire`、`bolt`（弩箭）、`firebolt`（火箭）、`stone`（石彈）；沒寫 ⇒ 實心／爆裂／鏈彈輪流 | 弩砲 `firebolt`、投石機 `stone` |
    | `box` | 彈藥量倍率（Tim：「跟共用的是乘數關係(弩炮設定三倍彈藥的話 就是裝填一次可以發射六發 但不是連射)」）：砲旁藥箱裝 `lim.box`（遊戲設定，預設 2）× `box` 發；送藥手一趟補一份 ＝ `box` 發。每發照樣走裝填時間 ⇒ 不是連射（連射是 `burst`） | 弩砲 `3` ⇒ 一箱 6 發 |
    | `elev` | 最大仰角（度）；沒寫 ＝ 15°（大砲的砲門）。射程照 min(elev, 45°) 算 | 投石機 `75` |
    | `lob` | `true` ⇒ 曲射：兩條拋物線取高的那一條（從上面砸到甲板）；高的那條超過 `elev`（目標太近）⇒ 退回低的；前置量照真的飛行時間算。臼砲照用 | 投石機 `true` |
  - **彈種**：火箭 `firebolt` 照實心彈咬進去，第一次碰到船那一點著火 —— 半徑 `fire.arrow`（6 格）的人燒傷、帆扣血、細繩燒斷；木頭點火半徑 `fire.arrowIgn`（3 格，希臘火 10）、旁邊 `fire.arrowSail`（4 格）內的帆也著，之後照著火那一套延燒、燒穿、滅火（TASK-0717）；飛過帆 ⇒ 打個小洞、帆從那一點燒起來。
    石彈 `stone`：照實心彈算、沒有砲煙。⚠ 投石機的數字（V 30、8 kg、每發傷害 `dmgmg` 60）是估的，不是史料。
- **武器演出**（Tim 2026-10-18：「弩炮的弦是否可以改用繩索 並在開火時有動態」「可以讓武器系統支援演出」；Blockside 26_gunanim.js）：會動的部分拆出來、用 meta 宣告，遊戲照「開火與裝填的進度」動（開火那一刻放出去、彈過頭，裝填時回到作品的樣子）：
  - **弦、拉繩**：畫成繩（不是格子），兩端寫明 `model:<模型>/<固定點>`；繩 meta `anim=gun, rest=<拉滿時那一端的固定點>, fire=<放出去之後的固定點>`；固定點登記在武器元件裡。綁在迴旋砲上的繩會跟著砲轉。
    持火號的弩弦：每座兩條 `string-a → nock`、`nock → string-b`，`kind=bowstring anim=gun rest=nock fire=nock-fire`（`nock-fire` 在兩弓臂尖的連線上）；產生程式 `design/apply_bowstrings.py`。
  - **綁在會轉的零件上的繩**（投石機的拉繩、投袋繩；繩 meta `kind=gunrope`）：一端（或兩端）綁在會轉的零件的固定點上 ⇒ 那一端跟著零件轉；另一端綁在武器上、照 `rest`／`fire` 走（沒寫 `rest`／`fire` ⇒ 只跟著零件轉）。
    持火號的投石機（`design/apply_mangonel_ropes.py`，建在投石機所在的 assembly）：拉繩 ×3 `mangonel-arm/pull-N → mangonel/grip-N`、`kind=gunrope anim=gun rest=grip-N fire=grip-N-fire`（上端跟著投臂甩、下端從拉好拉到底）；
    投袋繩 ×2 `mangonel-arm/sling → mangonel-arm/pouch-a｜b`、`kind=gunrope anim=gun`（兩端都在投臂上）。
  - **會轉的零件**（投石機的投臂、弩砲的弓臂）：拆成自己的子模型（整船的框或照武器的座標都行），模型 meta `spin=gun, spin_k=<放出去轉幾度>, spin_gun=<武器模型的路徑或 id>, spin_lag=0`、固定點 `spin-a → spin-b`（轉軸，§9.8）。
    武器是迴旋砲（`mount=pivot｜turret`）⇒ 零件先繞自己的軸轉、再跟著砲迴旋（Blockside 26_spin.js）⇒ 零件跟武器**同框、同 pivot、同 at** 放，才對得齊。
    持火號：投石機拆成架子 `erina-dromon-01m-mangonel`（固定點 `grip-1..3`、`grip-N-fire`）＋投臂 `-mangonel-arm`（`spin-a`／`spin-b` 鐵軸、`pull-1..3`、`sling`、`pouch-a／b`），投臂 `spin=gun spin_gun=mangonel spin_k=-135`（長臂甩到正上方）；產生程式 `design/draft_weapons.py`。

**遊戲還不認得的**（新的砲種代碼、新的 kind）—— 一起加，⛔ 不要借一個接近的代碼湊數（借了 ⇒ 遊戲照那一種的初速、彈重、砲班算）：

- **新砲種**（Tim 2026-10-18「可以加炮種」）：自己加進 Blockside 的砲種表。改的地方（範例 TASK-0703：36l／8l／pierrier）：
  - `src/config/game.json`：`guns` 加一種（`code`、`name`、`short`、`V` 初速、`reload` 裝填倍率、`big`、`hole`、`crew` 滿編／`min` 最少、`mass` 噸、`shot` 彈重 kg；連發另加 `burst`／`burstGap`）＋ `lim` 加每發傷害 `dmg<代碼>: [下限, 上限, 預設]`；
  - `src/js/04_state.js` 的 `DMG`（註解寫數字出處）、`src/js/69_prefs.js` 與 `admin.html` 的 `DMG_Q`、`admin.html` 後台的標籤。
  - 數字：彈重照磅（livre ＝ 0.4895 kg、英磅 0.4536 kg）換 kg；砲重照史料（沒有就標二手或設計決定）；每發傷害照現有砲種的彈重內插（6 磅 40、12 磅 55、24 磅 100、32 磅長砲 130）。
  - 照 Blockside 的流程：自己的 worktree（從 `heads/dev` 開）→ `python -I admin.py build`、`srccheck` → **對拍**：沒有這種砲的船 `?demo=120&dump=1&seed=3`（＋一組 `&ship=…`）改之前／之後逐位元組相同 → 暫存匯入自己的船跑 `shipcheck`，確認認得 → 合進 dev 時才開施工場。
- **新 kind**（遊戲沒有的功能部件）：`src/config/meta_kinds.json` 加一列（`admin.py metaspec` 對得過）、本檔 §9 的 kind 表加一列。遊戲怎麼用由遊戲那側定 ⇒ 開單給遊戲那側；遊戲還沒讀它之前，部件照樣做（meta 先標好），匯入檢查會報「規格表沒有這個 kind」。

- 實例（皇家號，2026-10-18）：叉架迴旋砲 pierrier 沒有現成的 ⇒ 新部件 `apex-one-reale-01m-gun-pierrier`（0.05 m、11×38×13、662 格；`muzzle`／`breech`／`cascabel`／`pivot`；meta `gun=pierrier, mount=turret`）；36 磅、8 磅照現有長砲縮放（scale 0.52／0.55）；砲種表加 `36l`／`8l`／`pierrier`（TASK-0703）。產生程式 `apex-one-reale-01m/design/draft_guns.py`。
