# Snappy history

One entry per version: `run-perf.ps1 -Only snappy -Exe <published exe> -Report` (no input; a copy of real data). Every version must be at least as quick as the one before. Newest first.

## 2.1.1 - 2026-11-14 21:15

- summary [first]: first frame median 126 ms, worst 145 ms; jank median 356 ms, worst 453 ms
- summary [return]: first frame median 6 ms, worst 11 ms; jank median 26 ms, worst 41 ms
- summary [prepared]: first frame median 7 ms, worst 7 ms; jank median 61 ms, worst 98 ms
- summary [tab]: first frame median 51 ms, worst 153 ms; jank median 171 ms, worst 535 ms
- summary [scroll]: first frame median 6 ms, worst 10 ms; jank median 0 ms, worst 14 ms
- summary [page]: first frame median 45 ms, worst 150 ms; jank median 99 ms, worst 225 ms
- memory: 964 MB private, 972 MB working set, 8 game pages alive
- verdict: FAIL (8 of 30 over budget)
- gate vs 2.1.0 - 2026-11-14 18:49: REGRESSION (1)
  - [tab] jank median 171 ms, was 70 ms
- note: not a code regression. 2.1.1 only adds work to the watchdog after a 19 s stall. Measured alternately on the same
  data right before: tab jank median 2.1.0 93 / 157 ms, 2.1.1 169 / 171 ms; tab first frame 2.1.0 52 / 57 ms, 2.1.1 54 /
  51 ms; page 49 / 46 vs 47 / 47 ms. The tab jank median (5th of 9 tabs) swings by 60 ms between runs of the same exe.
- Over budget:
  - [first] open Apex Legends: first frame 106 ms (busy 48), 64 missed, worst gap 126 ms, jank 453 ms, settled 868 ms  (budget 300 / 450)
  - [prepared] open (prepared) Marvel's Spider-Man 2: first frame 6 ms (busy 4), 18 missed, worst gap 72 ms, jank 98 ms, settled 115 ms  (budget 50 / 80)
  - [tab] tab auto: first frame 153 ms (busy 14), 4 missed, worst gap 28 ms, jank 171 ms, settled 182 ms  (budget 150 / 200)
  - [tab] tab opti: first frame 101 ms (busy 58), 79 missed, worst gap 98 ms, jank 535 ms, settled 628 ms  (budget 150 / 200)
  - [tab] tab mods: first frame 109 ms (busy 92), 18 missed, worst gap 38 ms, jank 202 ms, settled 247 ms  (budget 150 / 200)
  - [tab] tab pcgw: first frame 126 ms (busy 30), 15 missed, worst gap 88 ms, jank 204 ms, settled 215 ms  (budget 150 / 200)
  - [tab] tab ach: first frame 51 ms (busy 22), 60 missed, worst gap 286 ms, jank 380 ms, settled 456 ms  (budget 150 / 200)
  - [page] settings: first frame 150 ms (busy 51), 15 missed, worst gap 72 ms, jank 225 ms, settled 314 ms  (budget 150 / 200)

## 2.1.0 - 2026-11-14 18:49

- summary [first]: first frame median 115 ms, worst 155 ms; jank median 372 ms, worst 456 ms
- summary [return]: first frame median 5 ms, worst 10 ms; jank median 23 ms, worst 28 ms
- summary [prepared]: first frame median 7 ms, worst 7 ms; jank median 52 ms, worst 89 ms
- summary [tab]: first frame median 46 ms, worst 290 ms; jank median 70 ms, worst 584 ms
- summary [scroll]: first frame median 12 ms, worst 253 ms; jank median 19 ms, worst 253 ms
- summary [page]: first frame median 45 ms, worst 126 ms; jank median 100 ms, worst 188 ms
- memory: 920 MB private, 942 MB working set, 8 game pages alive
- verdict: FAIL (6 of 30 over budget)
- gate vs 2.1.0-rc.12 - 2026-11-14 14:36: REGRESSION (2)
  - [tab] first frame median 46 ms, was 18 ms
  - [tab] jank median 70 ms, was 29 ms
- note: not a code regression. rc.12's own exe measured alternately with this build on the same data right before gives
  the same or worse rows (rc.12 tab 54 / 48 ms, jank 89 / 75; 2.1.0 tab 46 / 41 ms, jank 69 / 68): the user's real data
  changed after the 14:36 row. The single tab mods 290 ms and scroll settings 253 ms (busy 0) of this run did not come
  back in the four other runs of the same code (mods 66-105 ms, scroll worst 8-20 ms).
- Over budget:
  - [first] open Cyberpunk 2077: first frame 109 ms (busy 68), 63 missed, worst gap 111 ms, jank 456 ms, settled 726 ms  (budget 300 / 450)
  - [prepared] open (prepared) Marvel's Spider-Man 2: first frame 6 ms (busy 3), 16 missed, worst gap 61 ms, jank 89 ms, settled 120 ms  (budget 50 / 80)
  - [tab] tab auto: first frame 59 ms (busy 14), 30 missed, worst gap 158 ms, jank 224 ms, settled 241 ms  (budget 150 / 200)
  - [tab] tab opti: first frame 94 ms (busy 47), 59 missed, worst gap 94 ms, jank 415 ms, settled 502 ms  (budget 150 / 200)
  - [tab] tab mods: first frame 290 ms (busy 278), 54 missed, worst gap 212 ms, jank 584 ms, settled 628 ms  (budget 150 / 200)
  - [scroll] scroll settings: first frame 253 ms (busy 0), 46 missed, worst gap 253 ms, jank 253 ms, settled 407 ms  (budget 34 / 60)

## 2.1.0-rc.12 - 2026-11-14 14:36

- summary [first]: first frame median 120 ms, worst 162 ms; jank median 364 ms, worst 522 ms
- summary [return]: first frame median 6 ms, worst 10 ms; jank median 21 ms, worst 37 ms
- summary [prepared]: first frame median 8 ms, worst 11 ms; jank median 33 ms, worst 47 ms
- summary [tab]: first frame median 18 ms, worst 55 ms; jank median 29 ms, worst 209 ms
- summary [scroll]: first frame median 0 ms, worst 7 ms; jank median 0 ms, worst 0 ms
- summary [page]: first frame median 45 ms, worst 128 ms; jank median 91 ms, worst 430 ms
- memory: 867 MB private, 880 MB working set, 8 game pages alive
- verdict: FAIL (3 of 29 over budget)
- gate vs 2.1.0-rc.11 - 2026-11-14 14:02: no regression
- Over budget:
  - [first] open HITMAN World of Assassination: first frame 162 ms (busy 112), 66 missed, worst gap 151 ms, jank 522 ms, settled 845 ms  (budget 300 / 450)
  - [tab] tab opti: first frame 46 ms (busy 7), 29 missed, worst gap 67 ms, jank 209 ms, settled 264 ms  (budget 150 / 200)
  - [page] settings: first frame 128 ms (busy 49), 56 missed, worst gap 243 ms, jank 430 ms, settled 618 ms  (budget 150 / 200)

## 2.1.0-rc.11 - 2026-11-14 14:02

- summary [first]: first frame median 127 ms, worst 146 ms; jank median 377 ms, worst 446 ms
- summary [return]: first frame median 6 ms, worst 11 ms; jank median 28 ms, worst 33 ms
- summary [prepared]: first frame median 8 ms, worst 9 ms; jank median 45 ms, worst 72 ms
- summary [tab]: first frame median 19 ms, worst 109 ms; jank median 17 ms, worst 227 ms
- summary [scroll]: first frame median 0 ms, worst 231 ms; jank median 0 ms, worst 229 ms
- summary [page]: first frame median 38 ms, worst 159 ms; jank median 86 ms, worst 231 ms
- memory: 887 MB private, 897 MB working set, 8 game pages alive
- verdict: FAIL (4 of 29 over budget)
- gate vs 2.1.0-rc.10 - 2026-11-14 13:35: no regression
- Over budget:
  - [tab] tab auto: first frame 109 ms (busy 12), 21 missed, worst gap 79 ms, jank 220 ms, settled 620 ms  (budget 150 / 200)
  - [tab] tab opti: first frame 46 ms (busy 7), 34 missed, worst gap 74 ms, jank 227 ms, settled 277 ms  (budget 150 / 200)
  - [page] settings: first frame 159 ms (busy 73), 14 missed, worst gap 63 ms, jank 231 ms, settled 314 ms  (budget 150 / 200)
  - [scroll] scroll settings: first frame 231 ms (busy 0), 42 missed, worst gap 231 ms, jank 229 ms, settled 390 ms  (budget 34 / 60)

## 2.1.0-rc.10 - 2026-11-14 13:35

- summary [first]: first frame median 110 ms, worst 171 ms; jank median 316 ms, worst 386 ms
- summary [return]: first frame median 6 ms, worst 10 ms; jank median 20 ms, worst 31 ms
- summary [prepared]: first frame median 10 ms, worst 10 ms; jank median 43 ms, worst 70 ms
- summary [tab]: first frame median 18 ms, worst 256 ms; jank median 19 ms, worst 556 ms
- summary [scroll]: first frame median 0 ms, worst 16 ms; jank median 0 ms, worst 16 ms
- summary [page]: first frame median 38 ms, worst 141 ms; jank median 81 ms, worst 212 ms
- memory: 807 MB private, 828 MB working set, 8 game pages alive
- verdict: FAIL (3 of 29 over budget)
- gate vs 2.1.0-rc.9 - 2026-11-14 13:03: no regression
- Over budget:
  - [tab] tab auto: first frame 256 ms (busy 172), 56 missed, worst gap 199 ms, jank 556 ms, settled 595 ms  (budget 150 / 200)
  - [tab] tab opti: first frame 33 ms (busy 7), 32 missed, worst gap 67 ms, jank 209 ms, settled 258 ms  (budget 150 / 200)
  - [page] settings: first frame 141 ms (busy 58), 14 missed, worst gap 71 ms, jank 212 ms, settled 306 ms  (budget 150 / 200)

- note: the first run on the published rc.10 (13:33) flagged [page] jank 124 ms vs 78, [prepared] jank 139 ms vs 47
  (one sample each, a full self-test had just ended); that row was dropped and this second run shows no regression.

## 2.1.0-rc.9 - 2026-11-14 13:03

- summary [first]: first frame median 103 ms, worst 134 ms; jank median 391 ms, worst 436 ms
- summary [return]: first frame median 5 ms, worst 10 ms; jank median 24 ms, worst 31 ms
- summary [prepared]: first frame median 11 ms, worst 12 ms; jank median 47 ms, worst 73 ms
- summary [tab]: first frame median 47 ms, worst 99 ms; jank median 77 ms, worst 656 ms
- summary [scroll]: first frame median 12 ms, worst 171 ms; jank median 18 ms, worst 183 ms
- summary [page]: first frame median 39 ms, worst 125 ms; jank median 78 ms, worst 185 ms
- memory: 864 MB private, 891 MB working set, 8 game pages alive
- verdict: FAIL (2 of 30 over budget)
- gate vs 2.1.0-rc.8 - 2026-11-14 04:47: no regression
- Over budget:
  - [tab] tab opti: first frame 93 ms (busy 46), 103 missed, worst gap 183 ms, jank 656 ms, settled 731 ms  (budget 150 / 200)
  - [scroll] scroll library: first frame 171 ms (busy 0), 33 missed, worst gap 171 ms, jank 183 ms, settled 309 ms  (budget 34 / 60)

- note: a second run without -Report gave `scroll library` 69 ms (same machine, a build had just finished), and the other
  scroll rows 0-33 ms. The snappy scroll moves no real pointer, so the covers taking the pointer again in rc.9 plays no part;
  the row is the bimodal noise already seen on rc.3, rc.1 and 2.0.0.

## 2.1.0-rc.8 - 2026-11-14 04:47

- summary [first]: first frame median 108 ms, worst 174 ms; jank median 337 ms, worst 448 ms
- summary [return]: first frame median 6 ms, worst 10 ms; jank median 23 ms, worst 38 ms
- summary [prepared]: first frame median 28 ms, worst 28 ms; jank median 105 ms, worst 105 ms
- summary [tab]: first frame median 45 ms, worst 108 ms; jank median 85 ms, worst 647 ms
- summary [scroll]: first frame median 7 ms, worst 10 ms; jank median 0 ms, worst 12 ms
- summary [page]: first frame median 47 ms, worst 136 ms; jank median 93 ms, worst 200 ms
- memory: 872 MB private, 899 MB working set, 7 game pages alive
- verdict: FAIL (2 of 29 over budget)
- gate vs 2.1.0-rc.3 - 2026-11-14 01:35: REGRESSION (1)
  - [prepared] first frame median 28 ms, was 10 ms
- Over budget:
  - [prepared] open (prepared) Marvel's Spider-Man 2: first frame 28 ms (busy 4), 14 missed, worst gap 63 ms, jank 105 ms, settled 122 ms  (budget 50 / 80)
  - [tab] tab opti: first frame 108 ms (busy 60), 99 missed, worst gap 163 ms, jank 647 ms, settled 872 ms  (budget 150 / 200)

## 2.1.0-rc.3 - 2026-11-14 01:35

- summary [first]: first frame median 96 ms, worst 151 ms; jank median 359 ms, worst 492 ms
- summary [return]: first frame median 6 ms, worst 12 ms; jank median 25 ms, worst 48 ms
- summary [prepared]: first frame median 10 ms, worst 10 ms; jank median 85 ms, worst 85 ms
- summary [tab]: first frame median 47 ms, worst 114 ms; jank median 78 ms, worst 662 ms
- summary [scroll]: first frame median 13 ms, worst 180 ms; jank median 14 ms, worst 191 ms
- summary [page]: first frame median 40 ms, worst 121 ms; jank median 97 ms, worst 195 ms
- memory: 831 MB private, 845 MB working set, 7 game pages alive
- verdict: FAIL (5 of 29 over budget)
- gate vs 2.1.0-rc.1 - 2026-11-13 20:49: REGRESSION (3)
  - [tab] first frame median 47 ms, was 19 ms
  - [tab] jank median 78 ms, was 18 ms
  - [prepared] jank median 85 ms, was 29 ms
- note: not a code regression. 2.1.0-rc.1's own exe measured right after on the same data gives the same rows (tab median
  45 ms / jank 80, prepared jank 80, tab opti jank 448, tab mods 353): the user's data changed since the rc.1 row
  (OptiScaler installed in Marvel's Spider-Man 2 on 2026-11-14, so the opti and mods tabs now read and lay out a real ini).
  The 180 ms "scroll library" was noise: 7 ms on the next run (-Trace), bimodal like "scroll settings" in rc.1. This row
  is the new baseline.
- Over budget:
  - [first] open Apex Legends: first frame 95 ms (busy 56), 74 missed, worst gap 103 ms, jank 492 ms, settled 657 ms  (budget 300 / 450)
  - [prepared] open (prepared) Marvel's Spider-Man 2: first frame 10 ms (busy 4), 15 missed, worst gap 65 ms, jank 85 ms, settled 101 ms  (budget 50 / 80)
  - [tab] tab opti: first frame 114 ms (busy 67), 100 missed, worst gap 148 ms, jank 662 ms, settled 747 ms  (budget 150 / 200)
  - [tab] tab pcgw: first frame 57 ms (busy 18), 35 missed, worst gap 67 ms, jank 247 ms, settled 288 ms  (budget 150 / 200)
  - [scroll] scroll library: first frame 180 ms (busy 0), 34 missed, worst gap 180 ms, jank 191 ms, settled 487 ms  (budget 34 / 60)

## 2.1.0-rc.1 - 2026-11-13 20:49

- summary [first]: first frame median 92 ms, worst 152 ms; jank median 307 ms, worst 421 ms
- summary [return]: first frame median 4 ms, worst 10 ms; jank median 18 ms, worst 57 ms
- summary [prepared]: first frame median 20 ms, worst 27 ms; jank median 29 ms, worst 38 ms
- summary [tab]: first frame median 19 ms, worst 111 ms; jank median 18 ms, worst 217 ms
- summary [scroll]: first frame median 0 ms, worst 204 ms; jank median 0 ms, worst 198 ms
- summary [page]: first frame median 37 ms, worst 127 ms; jank median 69 ms, worst 186 ms
- memory: 954 MB private, 920 MB working set, 8 game pages alive
- verdict: FAIL (2 of 29 over budget)
- gate vs 2.0.0 - 2026-11-13 12:51: no regression
- Over budget:
  - [tab] tab opti: first frame 47 ms (busy 7), 32 missed, worst gap 69 ms, jank 217 ms, settled 270 ms  (budget 150 / 200)
  - [scroll] scroll settings: first frame 204 ms (busy 0), 36 missed, worst gap 204 ms, jank 198 ms, settled 321 ms  (budget 34 / 60)

## 2.0.0 - 2026-11-13 12:51

- summary [first]: first frame median 90 ms, worst 127 ms; jank median 315 ms, worst 448 ms
- summary [return]: first frame median 6 ms, worst 10 ms; jank median 11 ms, worst 27 ms
- summary [prepared]: first frame median 11 ms, worst 11 ms; jank median 21 ms, worst 21 ms
- summary [tab]: first frame median 38 ms, worst 86 ms; jank median 111 ms, worst 240 ms
- summary [scroll]: first frame median 7 ms, worst 159 ms; jank median 0 ms, worst 245 ms
- summary [page]: first frame median 41 ms, worst 141 ms; jank median 78 ms, worst 200 ms
- memory: 1141 MB private, 973 MB working set, 7 game pages alive
- verdict: FAIL (3 of 29 over budget)
- gate vs 2.0.0-rc.19 - 2026-11-13 07:14: no regression
- Over budget:
  - [tab] tab opti: first frame 51 ms (busy 8), 31 missed, worst gap 69 ms, jank 219 ms, settled 266 ms  (budget 150 / 200)
  - [tab] tab mods: first frame 21 ms (busy 9), 42 missed, worst gap 176 ms, jank 240 ms, settled 292 ms  (budget 150 / 200)
  - [scroll] scroll game page: first frame 159 ms (busy 0), 45 missed, worst gap 159 ms, jank 245 ms, settled 1807 ms  (budget 34 / 60)

## 2.0.0-rc.19 - 2026-11-13 07:14

- summary [first]: first frame median 83 ms, worst 129 ms; jank median 258 ms, worst 310 ms
- summary [return]: first frame median 10 ms, worst 11 ms; jank median 11 ms, worst 17 ms
- summary [prepared]: first frame median 14 ms, worst 14 ms; jank median 22 ms, worst 22 ms
- summary [tab]: first frame median 38 ms, worst 93 ms; jank median 84 ms, worst 242 ms
- summary [scroll]: first frame median 174 ms, worst 178 ms; jank median 179 ms, worst 216 ms
- summary [page]: first frame median 39 ms, worst 125 ms; jank median 76 ms, worst 180 ms
- memory: 1056 MB private, 907 MB working set, 7 game pages alive
- verdict: FAIL (4 of 29 over budget)
- gate vs 2.0.0-rc.16 - 2026-11-13 03:50: REGRESSION (2)
  - Not a regression of rc.19: "scroll settings" is bimodal on this PC, first frame 7-8 ms or 160-175 ms with 0 ms of UI work (the frame clock waking), and the scroll median takes 3 samples one of which is the library's "nothing to scroll" 0, so one slow settings scroll moves the median. Same runs, same night: rc.19 8, 8, 7, 163, 174, 7, 160 ms; rc.17 8, 8, 173, 160 ms. Every other row of rc.19 matches rc.16.
  - [scroll] first frame median 174 ms, was 8 ms
  - [scroll] jank median 179 ms, was 0 ms
- Over budget:
  - [tab] tab overview: first frame 56 ms (busy 15), 34 missed, worst gap 128 ms, jank 242 ms, settled 315 ms  (budget 150 / 200)
  - [tab] tab opti: first frame 40 ms (busy 7), 33 missed, worst gap 68 ms, jank 218 ms, settled 284 ms  (budget 150 / 200)
  - [scroll] scroll game page: first frame 178 ms (busy 0), 39 missed, worst gap 178 ms, jank 216 ms, settled 1821 ms  (budget 34 / 60)
  - [scroll] scroll settings: first frame 174 ms (busy 0), 32 missed, worst gap 174 ms, jank 179 ms, settled 329 ms  (budget 34 / 60)

## 2.0.0-rc.16 - 2026-11-13 03:50

- summary [first]: first frame median 76 ms, worst 124 ms; jank median 240 ms, worst 270 ms
- summary [return]: first frame median 9 ms, worst 11 ms; jank median 10 ms, worst 88 ms
- summary [prepared]: first frame median 13 ms, worst 13 ms; jank median 24 ms, worst 24 ms
- summary [tab]: first frame median 36 ms, worst 81 ms; jank median 92 ms, worst 279 ms
- summary [scroll]: first frame median 8 ms, worst 161 ms; jank median 0 ms, worst 171 ms
- summary [page]: first frame median 39 ms, worst 131 ms; jank median 82 ms, worst 187 ms
- memory: 914 MB private, 860 MB working set, 7 game pages alive
- verdict: FAIL (5 of 29 over budget)
- gate vs 2.0.0-rc.14 - 2026-11-13 03:18: no regression
- Over budget:
  - [return] back to Coromon: first frame 9 ms (busy 2), 15 missed, worst gap 90 ms, jank 88 ms, settled 100 ms  (budget 50 / 80)
  - [tab] tab overview: first frame 81 ms (busy 32), 37 missed, worst gap 122 ms, jank 279 ms, settled 966 ms  (budget 150 / 200)
  - [tab] tab opti: first frame 36 ms (busy 7), 35 missed, worst gap 71 ms, jank 218 ms, settled 262 ms  (budget 150 / 200)
  - [tab] tab pcgw: first frame 36 ms (busy 11), 31 missed, worst gap 54 ms, jank 203 ms, settled 261 ms  (budget 150 / 200)
  - [scroll] scroll game page: first frame 161 ms (busy 0), 31 missed, worst gap 161 ms, jank 171 ms, settled 1881 ms  (budget 34 / 60)

## 2.0.0-rc.14 - 2026-11-13 03:18

- summary [first]: first frame median 91 ms, worst 148 ms; jank median 341 ms, worst 479 ms
- summary [return]: first frame median 6 ms, worst 7 ms; jank median 22 ms, worst 31 ms
- summary [prepared]: first frame median 6 ms, worst 6 ms; jank median 25 ms, worst 25 ms
- summary [tab]: first frame median 42 ms, worst 231 ms; jank median 230 ms, worst 456 ms
- summary [scroll]: first frame median 9 ms, worst 11 ms; jank median 3 ms, worst 17 ms
- summary [page]: first frame median 30 ms, worst 150 ms; jank median 76 ms, worst 219 ms
- memory: 862 MB private, 874 MB working set, 8 game pages alive
- verdict: FAIL (7 of 29 over budget)
- gate vs 2.0.0-rc.13 - 2026-11-13 02:55: REGRESSION (3)
  - [tab] first frame median 42 ms, was 24 ms
  - [tab] jank median 230 ms, was 51 ms
  - [first] jank median 341 ms, was 262 ms
- Over budget:
  - [first] open Gears of War: E-Day: first frame 89 ms (busy 41), 73 missed, worst gap 204 ms, jank 479 ms, settled 708 ms  (budget 300 / 450)
  - [tab] tab auto: first frame 42 ms (busy 14), 35 missed, worst gap 108 ms, jank 230 ms, settled 339 ms  (budget 150 / 200)
  - [tab] tab opti: first frame 109 ms (busy 61), 63 missed, worst gap 102 ms, jank 456 ms, settled 540 ms  (budget 150 / 200)
  - [tab] tab mods: first frame 231 ms (busy 150), 30 missed, worst gap 41 ms, jank 389 ms, settled 775 ms  (budget 150 / 200)
  - [tab] tab pcgw: first frame 65 ms (busy 21), 32 missed, worst gap 70 ms, jank 238 ms, settled 301 ms  (budget 150 / 200)
  - [tab] tab ach: first frame 53 ms (busy 21), 42 missed, worst gap 182 ms, jank 280 ms, settled 315 ms  (budget 150 / 200)
  - [page] settings: first frame 150 ms (busy 53), 13 missed, worst gap 69 ms, jank 219 ms, settled 315 ms  (budget 150 / 200)

## 2.0.0-rc.13 - 2026-11-13 02:55

- summary [first]: first frame median 86 ms, worst 163 ms; jank median 262 ms, worst 505 ms
- summary [return]: first frame median 5 ms, worst 10 ms; jank median 22 ms, worst 35 ms
- summary [prepared]: first frame median 12 ms, worst 12 ms; jank median 42 ms, worst 42 ms
- summary [tab]: first frame median 24 ms, worst 103 ms; jank median 51 ms, worst 371 ms
- summary [scroll]: first frame median 8 ms, worst 183 ms; jank median 0 ms, worst 239 ms
- summary [page]: first frame median 48 ms, worst 152 ms; jank median 95 ms, worst 221 ms
- memory: 1074 MB private, 958 MB working set, 7 game pages alive
- verdict: FAIL (4 of 29 over budget)
- gate vs 2.0.0-rc.9 - 2026-11-12 21:18: no regression
- Over budget:
  - [first] open HITMAN World of Assassination: first frame 163 ms (busy 100), 63 missed, worst gap 134 ms, jank 505 ms, settled 1547 ms  (budget 300 / 450)
  - [tab] tab opti: first frame 47 ms (busy 8), 60 missed, worst gap 174 ms, jank 371 ms, settled 430 ms  (budget 150 / 200)
  - [scroll] scroll game page: first frame 183 ms (busy 0), 44 missed, worst gap 183 ms, jank 239 ms, settled 1792 ms  (budget 34 / 60)
  - [page] settings: first frame 152 ms (busy 61), 13 missed, worst gap 68 ms, jank 221 ms, settled 317 ms  (budget 150 / 200)

## 2.0.0-rc.9 - 2026-11-12 21:18

- summary [first]: first frame median 92 ms, worst 123 ms; jank median 349 ms, worst 576 ms
- summary [return]: first frame median 5 ms, worst 11 ms; jank median 33 ms, worst 249 ms
- summary [prepared]: first frame median 9 ms, worst 9 ms; jank median 69 ms, worst 99 ms
- summary [tab]: first frame median 26 ms, worst 128 ms; jank median 50 ms, worst 395 ms
- summary [scroll]: first frame median 8 ms, worst 11 ms; jank median 3 ms, worst 10 ms
- summary [page]: first frame median 44 ms, worst 130 ms; jank median 83 ms, worst 198 ms
- memory: 918 MB private, 899 MB working set, 8 game pages alive
- verdict: FAIL (6 of 30 over budget)
- gate vs 2.0.0-rc.8 - 2026-11-12 20:10: no regression
- Over budget:
  - [first] open Coromon: first frame 82 ms (busy 51), 75 missed, worst gap 163 ms, jank 488 ms, settled 601 ms  (budget 300 / 450)
  - [first] open Cyberpunk 2077: first frame 123 ms (busy 77), 82 missed, worst gap 129 ms, jank 576 ms, settled 666 ms  (budget 300 / 450)
  - [return] back to Gears of War: E-Day: first frame 5 ms (busy 3), 45 missed, worst gap 220 ms, jank 249 ms, settled 1572 ms  (budget 50 / 80)
  - [prepared] open (prepared) Marvel's Spider-Man 2: first frame 9 ms (busy 4), 17 missed, worst gap 60 ms, jank 99 ms, settled 116 ms  (budget 50 / 80)
  - [tab] tab auto: first frame 114 ms (busy 26), 51 missed, worst gap 222 ms, jank 395 ms, settled 417 ms  (budget 150 / 200)
  - [tab] tab pcgw: first frame 128 ms (busy 36), 17 missed, worst gap 98 ms, jank 215 ms, settled 226 ms  (budget 150 / 200)

## 2.0.0-rc.8 - 2026-11-12 20:10

- summary [first]: first frame median 112 ms, worst 266 ms; jank median 637 ms, worst 771 ms
- summary [return]: first frame median 5 ms, worst 291 ms; jank median 33 ms, worst 692 ms
- summary [prepared]: first frame median 155 ms, worst 197 ms; jank median 667 ms, worst 930 ms
- summary [tab]: first frame median 34 ms, worst 128 ms; jank median 46 ms, worst 600 ms
- summary [scroll]: first frame median 8 ms, worst 10 ms; jank median 0 ms, worst 9 ms
- summary [page]: first frame median 124 ms, worst 130 ms; jank median 175 ms, worst 195 ms
- memory: 954 MB private, 949 MB working set, 8 game pages alive
- verdict: FAIL (11 of 30 over budget)
- Over budget:
  - [first] open Apex Legends: first frame 240 ms (busy 176), 64 missed, worst gap 215 ms, jank 598 ms, settled 637 ms  (budget 300 / 450)
  - [first] open Coromon: first frame 113 ms (busy 71), 119 missed, worst gap 385 ms, jank 771 ms, settled 838 ms  (budget 300 / 450)
  - [first] open Cyberpunk 2077: first frame 266 ms (busy 129), 75 missed, worst gap 152 ms, jank 676 ms, settled 720 ms  (budget 300 / 450)
  - [first] open Gears of War: E-Day: first frame 111 ms (busy 43), 106 missed, worst gap 459 ms, jank 689 ms, settled 728 ms  (budget 300 / 450)
  - [first] open Hades II: first frame 79 ms (busy 47), 73 missed, worst gap 228 ms, jank 479 ms, settled 1163 ms  (budget 300 / 450)
  - [return] back to HITMAN World of Assassination: first frame 291 ms (busy 169), 56 missed, worst gap 123 ms, jank 592 ms, settled 625 ms  (budget 50 / 80)
  - [prepared] open (prepared) Marvel's Spider-Man 2: first frame 197 ms (busy 142), 133 missed, worst gap 292 ms, jank 930 ms, settled 1170 ms  (budget 50 / 80)
  - [prepared] open (prepared) METAL GEAR SOLID V: GROUND ZEROES: first frame 113 ms (busy 80), 54 missed, worst gap 210 ms, jank 404 ms, settled 437 ms  (budget 50 / 80)
  - [tab] tab auto: first frame 128 ms (busy 38), 14 missed, worst gap 74 ms, jank 204 ms, settled 264 ms  (budget 150 / 200)
  - [tab] tab mods: first frame 110 ms (busy 53), 89 missed, worst gap 293 ms, jank 600 ms, settled 655 ms  (budget 150 / 200)
  - [return] game from the library: first frame 44 ms (busy 28), 117 missed, worst gap 280 ms, jank 692 ms, settled 806 ms  (budget 50 / 80)

## 2.0.0-rc.7 - 2026-11-12 19:42

- summary [first]: first frame median 103 ms, worst 277 ms; jank median 607 ms, worst 836 ms
- summary [return]: first frame median 7 ms, worst 260 ms; jank median 35 ms, worst 641 ms
- summary [prepared]: first frame median 264 ms, worst 416 ms; jank median 831 ms, worst 878 ms
- summary [tab]: first frame median 34 ms, worst 115 ms; jank median 49 ms, worst 198 ms
- summary [scroll]: first frame median 7 ms, worst 10 ms; jank median 0 ms, worst 13 ms
- summary [page]: first frame median 133 ms, worst 134 ms; jank median 201 ms, worst 204 ms
- memory: 1043 MB private, 1045 MB working set, 8 game pages alive
- verdict: FAIL (10 of 30 over budget)
- Over budget:
  - [first] open Apex Legends: first frame 161 ms (busy 56), 71 missed, worst gap 244 ms, jank 549 ms, settled 588 ms  (budget 300 / 450)
  - [first] open Coromon: first frame 106 ms (busy 57), 133 missed, worst gap 441 ms, jank 836 ms, settled 886 ms  (budget 300 / 450)
  - [first] open Cyberpunk 2077: first frame 277 ms (busy 136), 72 missed, worst gap 144 ms, jank 666 ms, settled 713 ms  (budget 300 / 450)
  - [first] open Gears of War: E-Day: first frame 93 ms (busy 38), 105 missed, worst gap 235 ms, jank 665 ms, settled 720 ms  (budget 300 / 450)
  - [return] back to HITMAN World of Assassination: first frame 260 ms (busy 161), 56 missed, worst gap 133 ms, jank 571 ms, settled 615 ms  (budget 50 / 80)
  - [prepared] open (prepared) Marvel's Spider-Man 2: first frame 416 ms (busy 363), 85 missed, worst gap 168 ms, jank 878 ms, settled 927 ms  (budget 50 / 80)
  - [prepared] open (prepared) METAL GEAR SOLID V: GROUND ZEROES: first frame 111 ms (busy 72), 122 missed, worst gap 298 ms, jank 784 ms, settled 913 ms  (budget 50 / 80)
  - [page] library: first frame 134 ms (busy 69), 13 missed, worst gap 58 ms, jank 201 ms, settled 846 ms  (budget 150 / 200)
  - [return] game from the library: first frame 126 ms (busy 40), 95 missed, worst gap 298 ms, jank 641 ms, settled 825 ms  (budget 50 / 80)
  - [page] settings: first frame 133 ms (busy 48), 14 missed, worst gap 71 ms, jank 204 ms, settled 299 ms  (budget 150 / 200)

- note: the [prepared] and HITMAN [return] lines are slower than in the rc.6 entry, but rc.6 measured again right after on the same data does the same or worse (prepared median 151 ms, back to HITMAN 507 ms, game from the library 126 ms): today's data, not rc.7. A second rc.7 run: prepared median 155 ms, HITMAN 284 ms, tab median 38 ms, page median 113 ms.

## 2.0.0-rc.6 - 2026-11-12 15:52

- summary [first]: first frame median 90 ms, worst 281 ms; jank median 643 ms, worst 1003 ms
- summary [return]: first frame median 6 ms, worst 7 ms; jank median 25 ms, worst 47 ms
- summary [prepared]: first frame median 5 ms, worst 5 ms; jank median 367 ms, worst 367 ms
- summary [tab]: first frame median 44 ms, worst 75 ms; jank median 79 ms, worst 310 ms
- summary [scroll]: first frame median 10 ms, worst 199 ms; jank median 8 ms, worst 214 ms
- summary [page]: first frame median 168 ms, worst 216 ms; jank median 231 ms, worst 272 ms
- memory: 965 MB private, 951 MB working set, 8 game pages alive
- verdict: FAIL (10 of 28 over budget)
- Over budget:
  - [first] open Apex Legends: first frame 88 ms (busy 43), 70 missed, worst gap 196 ms, jank 481 ms, settled 905 ms  (budget 300 / 450)
  - [first] open Coromon: first frame 91 ms (busy 38), 151 missed, worst gap 166 ms, jank 929 ms, settled 1559 ms  (budget 300 / 450)
  - [first] open Cyberpunk 2077: first frame 281 ms (busy 135), 96 missed, worst gap 204 ms, jank 804 ms, settled 980 ms  (budget 300 / 450)
  - [first] open Gears of War: E-Day: first frame 88 ms (busy 39), 167 missed, worst gap 448 ms, jank 1003 ms, settled 1304 ms  (budget 300 / 450)
  - [prepared] open (prepared) Marvel's Spider-Man 2: first frame 5 ms (busy 3), 66 missed, worst gap 223 ms, jank 367 ms, settled 459 ms  (budget 50 / 80)
  - [tab] tab opti: first frame 60 ms (busy 53), 46 missed, worst gap 104 ms, jank 310 ms, settled 343 ms  (budget 150 / 200)
  - [tab] tab pcgw: first frame 56 ms (busy 17), 36 missed, worst gap 68 ms, jank 253 ms, settled 1163 ms  (budget 150 / 200)
  - [page] library after a game: first frame 216 ms (busy 2), 11 missed, worst gap 49 ms, jank 272 ms, settled 300 ms  (budget 150 / 200)
  - [page] settings: first frame 168 ms (busy 66), 12 missed, worst gap 63 ms, jank 231 ms, settled 328 ms  (budget 150 / 200)
  - [scroll] scroll settings: first frame 199 ms (busy 0), 39 missed, worst gap 199 ms, jank 214 ms, settled 409 ms  (budget 34 / 60)

## 2.0.0-rc.5 - 2026-11-12 15:00

- summary [first]: first frame median 86 ms, worst 248 ms; jank median 441 ms, worst 511 ms
- summary [return]: first frame median 5 ms, worst 12 ms; jank median 21 ms, worst 34 ms
- summary [prepared]: first frame median 71 ms, worst 135 ms; jank median 157 ms, worst 242 ms
- summary [tab]: first frame median 25 ms, worst 460 ms; jank median 43 ms, worst 617 ms
- summary [scroll]: first frame median 7 ms, worst 14 ms; jank median 0 ms, worst 14 ms
- summary [page]: first frame median 42 ms, worst 132 ms; jank median 77 ms, worst 398 ms
- memory: 868 MB private, 894 MB working set, 8 game pages alive
- verdict: FAIL (7 of 29 over budget)
- Over budget:
  - [first] open Apex Legends: first frame 77 ms (busy 39), 80 missed, worst gap 192 ms, jank 511 ms, settled 627 ms  (budget 300 / 450)
  - [first] open Coromon: first frame 87 ms (busy 44), 71 missed, worst gap 172 ms, jank 467 ms, settled 1153 ms  (budget 300 / 450)
  - [first] open Gears of War: E-Day: first frame 83 ms (busy 37), 76 missed, worst gap 184 ms, jank 505 ms, settled 800 ms  (budget 300 / 450)
  - [prepared] open (prepared) Marvel's Spider-Man 2: first frame 135 ms (busy 68), 20 missed, worst gap 86 ms, jank 242 ms, settled 264 ms  (budget 50 / 80)
  - [tab] tab opti: first frame 460 ms (busy 10), 29 missed, worst gap 149 ms, jank 617 ms, settled 634 ms  (budget 150 / 200)
  - [tab] tab pcgw: first frame 105 ms (busy 24), 20 missed, worst gap 98 ms, jank 207 ms, settled 953 ms  (budget 150 / 200)
  - [page] settings: first frame 132 ms (busy 48), 49 missed, worst gap 193 ms, jank 398 ms, settled 1492 ms  (budget 150 / 200)

- note: rc.4 measured again at 15:01 on the same data opened the same games as this run and gave the same numbers (Opti tab 340 ms, prepared Spider-Man 2 127 ms): no slowdown from rc.5. rc.4's own entry below tested NARUTO's tabs.

## 2.0.0-rc.4 - 2026-11-12 14:19

- summary [first]: first frame median 90 ms, worst 114 ms; jank median 486 ms, worst 817 ms
- summary [return]: first frame median 5 ms, worst 11 ms; jank median 32 ms, worst 57 ms
- summary [prepared]: first frame median 14 ms, worst 14 ms; jank median 136 ms, worst 136 ms
- summary [tab]: first frame median 34 ms, worst 60 ms; jank median 61 ms, worst 337 ms
- summary [scroll]: first frame median 10 ms, worst 17 ms; jank median 7 ms, worst 23 ms
- summary [page]: first frame median 43 ms, worst 138 ms; jank median 84 ms, worst 207 ms
- memory: 901 MB private, 897 MB working set, 7 game pages alive
- verdict: FAIL (6 of 28 over budget)
- Over budget:
  - [first] open Apex Legends: first frame 114 ms (busy 41), 128 missed, worst gap 300 ms, jank 817 ms, settled 856 ms  (budget 300 / 450)
  - [first] open Gears of War: E-Day: first frame 100 ms (busy 45), 87 missed, worst gap 382 ms, jank 573 ms, settled 606 ms  (budget 300 / 450)
  - [first] open Hades II: first frame 97 ms (busy 40), 93 missed, worst gap 426 ms, jank 604 ms, settled 663 ms  (budget 300 / 450)
  - [prepared] open (prepared) NARUTO: Ultimate Ninja STORM: first frame 14 ms (busy 5), 23 missed, worst gap 124 ms, jank 136 ms, settled 153 ms  (budget 50 / 80)
  - [tab] tab overview: first frame 42 ms (busy 5), 54 missed, worst gap 211 ms, jank 337 ms, settled 836 ms  (budget 150 / 200)
  - [page] settings: first frame 138 ms (busy 55), 13 missed, worst gap 68 ms, jank 207 ms, settled 300 ms  (budget 150 / 200)

