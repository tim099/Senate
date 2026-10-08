---
title: 可繪製球面（senate cmd globe）—— prototype
description: 等角立方體球（6 面 × 2048²）的資料格式、經緯度畫點／線／多邊形／油漆桶、Undo、預覽渲染與後台「球面繪製」頁。
cmds: [globe]
last_updated: 2026-10-08 (TASK-0467：prototype)
target_audience: [AI_Agent, Tools_Maintainer]
---

# 🌍 globe —— 可繪製球面（prototype）

> 一句話：**用經緯度在一顆球上畫圖**，資料是純格子、沒有 mesh；海水是底色，不是塗出來的。
> 參數表看 `senate cmd help globe`；本檔只寫資料長什麼樣、畫錯怎麼退、哪裡會咬人。

## 1. 資料

| 東西 | 位置 | 角色 |
|---|---|---|
| `meta.json` | `<資料根>/Globe/` | N、底色、**每一面的基底向量**（Normal／U／V）、格子編碼 `rgb24` |
| `events/NNNNNN.json` | 同上 | **正本**。一筆一檔、只追加；繪製事件逐格記 `[index, 新值, 舊值]` |
| `_cache/` | 同上 | 重播捷徑（256² 分塊，只存畫過的分塊）。刪掉會從頭重播，⛔ 不是正本 |

- **格子**：等角立方體球，6 面 × N×N（N=2048 ⇒ 2516 萬格、約 4.9 km／格），每格面積最大／最小 ≈ 1.40，極區不變形。
  `index = face·N² + j·N + i`；面序 `+X +Y +Z −X −Y −Z`。世界座標 +Z＝北極、經度 0 在 +X、東經 90 在 +Y。
- **顏色**：每格 24-bit RGB（全彩）。`0`＝沒畫過＝顯示 `meta.BaseColor`；畫純黑會存成 `#000001`（肉眼無差），好跟「沒畫過」分開。
- **基底只住 meta**：哪一面朝哪、i/j 往哪增加，程式一律從 meta 讀。手改壞了（不是右手系）建格時就會喊。

## 2. 畫

```bash
senate cmd globe --arg op=point   --arg persona=<P> --arg lat=23.47 --arg lon=120.96 --arg radius=2 --arg color=#FFFFFF
senate cmd globe --arg op=line    --arg persona=<P> --arg color=#F2E6C9 --arg width=1 --arg-file points=<點列檔>
senate cmd globe --arg op=polygon --arg persona=<P> --arg color=#3A9D5D --arg-file points=<點列檔>
senate cmd globe --arg op=fill    --arg persona=<P> --arg lat=23.7 --arg lon=121 --arg color=#2E8B57
senate cmd globe --arg op=undo    --arg persona=<P>
```

- 點列：`lat,lon;lat,lon;…`（或一行一點）。線沿**大圓**連，不自動封口；多邊形會自動封口並連輪廓一起塗。
- `color=empty` ＝ 擦回底色。改底色走 `op=base`，**一格都不動**。
- 油漆桶：從那一格開始、四鄰同色的連通區。超過 `max_cells`（預設 20 萬）**整筆拒絕**——通常是輪廓沒封口、漏進海裡了。
- 塗的格子全部已經是那個顏色 ⇒ 不寫事件（`changed=0`）。

## 3. 畫錯了：Undo

- `op=undo` 一次退**最後一筆仍有效的繪製**，連按就照順序往回退；退的方式是追加一筆 undo 事件，⛔ 不刪事件檔。
- 只能從最後一筆往回退（堆疊）：退中間那筆的話，它的舊值會蓋掉後面那筆畫的格子。
- 沒得退 ⇒ exit 1、零寫入。

## 4. 看

- `op=render --arg center=lat,lon --arg zoom=12`：CPU 正交投影輸出 PNG（最近鄰取色，決定性）。`graticule` 疊經緯線，`seams=1` 疊面接縫（除錯用）。
- 後台頁「球面繪製」（`senate ui --page globe`）：視角、台灣特寫、畫筆（點／線／多邊形／油漆桶）、Undo、底色；寫入走同一支 `cmd globe`。

## 5. prototype 的限制

- 多邊形在經緯度平面判內外：⛔ 不能含極點、經度跨度要 < 180°（大區域拆成幾塊）。
- 不收費、沒有區域宣稱；`persona` 只記進事件。
- 還沒有表面高度、沒有 mesh。
