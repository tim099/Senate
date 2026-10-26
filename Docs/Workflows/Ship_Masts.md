---
title: 造船：桅與帆桁
description: 桅一根一件子作品、帆桁一根一件子作品＋外部模型（pivot、繞 z 轉、角度）、三角帆桁 rig=lateen
last_updated: 2026-10-26（從 Sculpture_Shipbuilding 拆出來，內文照搬、§ 編號照舊；Tim 2026-10-26「把造船流程的一些細節拆成獨立文件 按步驟建造到那一步才去讀取對應細節」；同日補 `yard` 值遊戲怎麼用、上下兩根帆桁夾一面方帆 —— 蓬特號收帆只被拉直）
target_audience: [AI_Agent]
related:
  - Ship_Steps.md | Ship_Steps | 造船步驟表：每一步讀哪幾份
  - Sculpture_Shipbuilding.md | Sculpture_Shipbuilding | 造船入口與舊 § 編號對照
  - Ship_Setup.md | Ship_Setup | 開工前 —— 座標與比例、作品怎麼拆、用已有的元件、建造順序
  - Ship_Guns.md | Ship_Guns | 砲 —— 一門一件子作品、擺上船退到零碰撞、固定點、砲具與砲索、缺的砲種做成通用部件
  - Ship_Turrets.md | Ship_Turrets | 迴旋砲、砲塔、裝甲子模組、砲塔層、揚弾機
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

# ⛵ 造船：桅與帆桁

> **什麼時候讀**：步驟 7（桅與帆桁）。步驟表在 `Ship_Steps`（`senate cmd doc --arg op=show --arg name=Ship_Steps`）。
> 這份是從 `Sculpture_Shipbuilding` 拆出來的（2026-10-26）：**§ 編號照舊**（別處寫「造船規格 §9.3」照樣對得到這裡的 §9.3）；文中提到別的 § ⇒ 查 `Sculpture_Shipbuilding` 的「§ 編號對照」看住哪一份。
> 參數看 `senate cmd help sculpture`，⛔ 本檔不抄參數表。

## 5. 桅與帆桁

- 桅：每根一件子作品（fore／main／mizzen／bowsprit），軸照甲板已開的桅孔；top 的 trestletrees 要緊貼 masthead（`round(160 ± 4.56)` 會空一格 ⇒ 主桅 top 浮空，階段十三 v1.1）。
- **帆桁：每根一件子作品，模型放在它的桅子作品裡**（階段三十三）：
  - 子作品裡放**沒轉**的帆桁（沿 y），不扣跟桅重疊的格（獨立零件要完整）。
  - 固定點：`arm-P／S`（yardarm 兩端）、`earing-P／S`（帆上緣兩角）、`sling`、`jackstay-*`（每 8 格）；gaff `top*`；boom `jaws`／`end`；外加 **`pivot`**。
  - 模型：scale 1 明寫、`rotate=0,0,θ`（繞世界 z）、pivot ＝ `pivot` 那一格的格心 —— 方帆桁＝帆桁中心那一格；gaff／boom ＝ 桅在 jaws 高度的軸心（繞桅擺，pivot 不在桿上 ⇒ 子作品框要包到它）。
  - 角度：右舷那一頭往前（＋x）＝ 負角（憲法號 course −20、topsail −22、topgallant −24、royal −26；預設風 4.5,−1.5,0 正對是 18.4°）。真船受 lee shrouds 限制約 30–35°（plan.json brace 的出處欄）。
  - meta：`kind=yard, mast, yard, length_ft, diameter_in, pivot_anchor=pivot, turn_axis=z, rotate_z_deg`＋`sail`。
  - **三角帆桁（lateen；槳帆船）**：`yard=gaff`（遊戲照 gaff 往下風擺）＋ **`rig=lateen`**＋固定點 **`fore`**（帆桁前端 car，tack 綁在這裡往下拉到船頭）。遊戲照 `rig=lateen` 限制擺角：前端只能在船頭左右移 2.5 m（tack 換邊的範圍）⇒ 最多擺 asin(2.5 m ÷ pivot 到 `fore` 的水平距離)（皇家號主帆桁 10.7°、前帆桁 11.9°）。
    🩸 只標 `yard=gaff`（皇家號 2026-10-18 第一版）⇒ 照 gaff 擺到 80°，主帆桁前端甩出舷外 13 m、tack 從那裡斜拉到中央走道前端 ⇒ 看起來是一條浮在海上的纜（Tim：「發現浮空纜線」）。
  - **`yard` 的值遊戲怎麼用**（Blockside `10_ship.js`、`30_rig_physics.js`；值寫錯不會報錯，只會在海上看到怪樣子 ⇒ 照這張挑）：

    | `yard` | 遊戲怎麼轉（追風） | 另外拿來做什麼 |
    |---|---|---|
    | `course`／`crossjack` | 方帆桁：帆面落在相對風與船頭的角平分線上，最多轉離正橫 40° | **這根桅在哪、多高**照它的 `pivot`（沒有 ⇒ 照最低一根不是 `boom` 的帆桁）⇒ 每根有方帆的桅最低那根標這兩個之一 |
    | `topsail`／`topgallant`／`royal`… 其他沒列在這裡的值 | 同方帆桁 | —— |
    | `gaff`／`boom` | 縱帆：繞桅往下風擺 8°–80°（`rig=lateen` 再限擺角，見上） | `boom` 上的帆綁點收帆時**跟著往上收**（不是收攏點） |
    | `signal` | 不轉、不掛帆 | 只給遊戲認桅（蒸汽船的戰鬥桅） |

    ⛔ 不要為了「收帆時往上收」把方帆的下帆桁標 `boom`：它就會照縱帆往下風擺，跟上面那根方帆桁轉的角度不一樣，帆面被扭開。
  - **上下兩根帆桁夾一面方帆**（古埃及、蓬特號這類：帆頂綁上帆桁、帆腳綁下帆桁，沒有 sheet 綁甲板）：
    - 兩根都標 `sail=<帆名>`、同一個 `mast`；上面那根 `yard=course`（認桅照它），下面那根標方帆桁的值（蓬特號寫 `lower`，⛔ 不要 `boom`、`gaff`）。遊戲給每根方帆桁同一個角度 ⇒ 兩根的 `pivot` 都要在帆桁中心、同一條鉛直線上，轉起來布才不會被扭。
    - 遊戲怎麼認：一面帆掛了兩根以上帆桁 ⇒ 最低那根當帆腳跟著轉的帆桁（`footYard`）、帆頂那根排第一（打中帆照它轉回作品角度）；收帆時，綁點平均高度落在帆的**下半**的那幾根帆桁（gaff 除外）跟 `boom` 一樣往上收，布收成一捲掛在上帆桁下面（Tim 2026-10-26「收帆後 帆布只會被拉直」，Blockside e62dd3d）。
    - 🩸 2026-10-26 以前遊戲只認 `yard=boom` 往上收 ⇒ 蓬特號收帆時布被上下兩根一起拉住，畫面上整面照樣撐開（看起來像張著，其實已經收了）。造船這側不用為它改 meta；但**驗的時候要看「全收」那一刻的畫面**：布應該在上帆桁下面一捲，不是整面還在。
- ⛔ 不要把帆桁貼進桅、也不要用格子轉角度：一格粗的截面轉了會變成只靠斜角相接的階梯（6 連通斷掉，階段二十六得把最細截面 0.5 → 0.75），拆出來要刻掉、重綁。
- ⚠ 網格轉 20° 後中段靠後那一小段會穿進桅（格子版是「不放」那 6 格）。
