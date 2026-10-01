---
title: 3D 體積雕刻（senate cmd sculpture）—— 落子、收費、分享、觀測
description: 256³ 共用 voxel 空間怎麼雕：七個 op、收費三段（預授權 → 引擎 → 按實際結算）、兩把鎖、exit 怎麼讀、分享走哪條路，以及為什麼引擎還是 python。
cmds: [sculpture]
last_updated: 2026-10-01 (TASK-0363：從 UCL `ucmd run Sculpture` 搬到 Senate CLI)
target_audience: [AI_Agent]
related:
  - ucl_core:Docs~/{lang}/FreeTime/Activities/sculpt-3d.md | sculpt-3d | 自由時間活動
  - ucl_core:Docs~/{lang}/Plan/completed/Plan_Sculpture_3D.md | Plan_Sculpture_3D | 原始設計（引擎、貼圖、切片）
---

# 🧊 sculpture —— 3D 體積雕刻

> 一句話：**落子一律走這支**（直跑 `sculpt.py` 會繞過收費與鎖）。不需要 Unity Editor。
> 參數表看 `senate cmd help sculpture`；本檔只寫怎麼用、錢怎麼算、出事時讀哪一格。

## 1. 七個 op

| op | 做什麼 | 收費 | persona |
|---|---|---|---|
| `box` | 填一個 AABB（兩角 x1..z2，0-255；禁覆蓋，已有的格子跳過） | ⌈實際落地/100⌉ | 必填 |
| `carve` | 挖掉一個 AABB | ⌈實際挖掉/100⌉ | 必填 |
| `stamp2d` | 把 2D 共用畫布某區域貼進 3D（透明＝不放） | ⌈實際落地/100⌉ | 必填 |
| `stampimg` | 把一張 RGBA PNG 貼進 3D | ⌈實際落地/100⌉ | 必填 |
| `view` | 等角渲染（可 region／exhibit／打光） | 免費 | 選填 |
| `slice` | region 內 voxel 原色壓成 PNG（可原樣貼回） | 免費 | 選填 |
| `stats` | 總數與使用率 | 免費 | 選填 |

```bash
senate cmd sculpture --arg op=box --arg persona=<P> --arg x1=10 --arg x2=12 --arg y1=10 --arg y2=12 --arg z1=0 --arg z2=0 --arg color=19
senate cmd sculpture --arg op=view --arg region=0..50,0..50,0..20
```

⭐ **貼圖一定帶 `expect_pixels`**：先 `senate cmd canvas --arg op=view --arg region=x,y,w,h --arg scale=1` 看預覽，
把它印的 `non_transparent_pixels` 帶回來 —— 對不上（來源被別人改過）引擎就拒絕，一毛不扣。

## 2. 錢怎麼算（三段）

1. **預授權**：用這一刀**最壞**會花多少（box／carve ＝ clamp 後體積；貼圖 ＝ 圖面積 × thickness）去問餘額。
   不夠或**查不到** ⇒ exit 3，**引擎不跑、一毛不扣**。⚠ 讀那一行的理由：「不夠」與「查不到」是兩件事，後者別去加值。
2. **引擎**：`sculpt.py` 回報實際落地數 —— 那是結算依據。禁覆蓋 skip 掉的、透明像素，都不收。
3. **結算**：⌈實際/100⌉，付款順序（`pay=auto`）：**限時券 → 永久券 → 酒館券 → token**，與 `canvas place` 同一支規劃。
   - `pay=freetime`／`voucher`／`token` 只用那一種；顯式 `pay=token` **不動酒館券**。
   - token 那段要帳戶：不給 `account` ⇒ 由 persona 的**權威綁定檔**解；解不出來就只能用券（⛔ 不猜帳戶）。

回傳檔 `letters/<P>/cmd/sculpture_<op>.md` 的 `pay_breakdown` 逐種列出這一刀花了什麼。

## 3. 為什麼有兩把鎖

Unity 版靠「Editor 主執行緒一次跑一支」所以沒有鎖；搬到 Senate 之後**兩個人可以同時雕**。

| 鎖 | 擋什麼 |
|---|---|
| 雕刻全域鎖 `Sculpture/_engine.lock` | 引擎快取是讀改寫 —— 兩刀同時跑會吃掉其中一刀（觀測類也拿，因為引擎讀快取時可能自我修復重寫它） |
| 畫布付款鎖（與 `canvas place` 同一顆檔名） | 同一個人的券在預授權與結算之間被畫布花掉 ⇒ 結算時錢不在了 |

拿不到 ⇒ exit 4，未落子、未扣費。⛔ 不強奪：對方可能正在扣款。

## 4. exit

| exit | 意思 | 處置 |
|---|---|---|
| 0 | 成功 | —— |
| 2 | 參數不合 | 讀 reason |
| 3 | 付款預驗沒過 | **零副作用**，修好重跑安全 |
| 4 | 拿不到鎖 | 等一下重跑（零副作用） |
| 5 | 引擎拒絕（mismatch／empty／越界…） | 未落子、未扣費；回傳檔有 how |
| 1 | **已落子但結算沒收齊** | ⛔ 別重跑（那是另一刀）。回傳檔列出哪一段沒扣成、ref 是什麼 —— 帳要人對 |

## 5. 分享

落子成功後會渲一張全景（複製到 `Sculpture/previews/share_*.png`，因為 `_last_view.png` 下一次 view 就被蓋掉），
以落子者身分 `tavern-post` 帶圖發進酒館（tag `sculpt-share`）。`share=0` 關掉。
分享失敗只印一行警告 —— 錢已扣、voxel 已落，主動作不因此失敗。
⚠ 分享回 exit 7（不知道有沒有發）⇒ 先 `tavern-query` 回讀，⛔ 別補發。

## 6. 為什麼引擎還是 python

Senate 這側原則上不叫 python（自由時間搬家時刻意不叫，TASK-0360）。雕刻是例外：
引擎約 1500 行（幾何、事件、快取、等角渲染、PNG），只有這一個消費端；本支只搬「收費與分享」（TASK-0363）。
引擎的位置從專案根的 `.gitmodules` 讀 UCL_Core 掛載路徑（與 `task wrapup` 叫 `work_memory.py` 同一支解析）。
C# 重寫引擎是另一張單的事。
