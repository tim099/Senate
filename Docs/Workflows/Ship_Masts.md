---
title: 造船：桅與帆桁
description: 桅一根一件子作品、帆桁一根一件子作品＋外部模型（pivot、繞 z 轉、角度）、三角帆桁 rig=lateen
last_updated: 2026-10-26（從 Sculpture_Shipbuilding 拆出來，內文照搬、§ 編號照舊；Tim 2026-10-26「把造船流程的一些細節拆成獨立文件 按步驟建造到那一步才去讀取對應細節」）
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
- ⛔ 不要把帆桁貼進桅、也不要用格子轉角度：一格粗的截面轉了會變成只靠斜角相接的階梯（6 連通斷掉，階段二十六得把最細截面 0.5 → 0.75），拆出來要刻掉、重綁。
- ⚠ 網格轉 20° 後中段靠後那一小段會穿進桅（格子版是「不放」那 6 格）。
