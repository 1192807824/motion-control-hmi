# Motion Control Single-Column Layout QA

## Evidence

- Source visual truth: `C:\Users\Lenovo\Desktop\摆盘机\tmp\design-audit-compact\10-after-jog-final.png`, `13-after-fixed-absolute-final.png`, `12-after-home-final.png`
- Final implementation: `C:\Users\Lenovo\Desktop\摆盘机\tmp\design-audit-single-column\04-after-jog-final.png`, `06-after-fixed-final.png`, `05-after-home-final.png`
- Viewport: 1680 × 950
- States: JOG, fixed/absolute positioning, homing
- Full-view comparisons: `comparison-jog-final.png`, `comparison-fixed-final.png`, `comparison-home-final.png`
- Focused axis comparison: `comparison-axis-focused-final.png`

## Comparison history

1. First single-column pass (`01-after-jog.png`, `02-after-fixed.png`, `03-after-home.png`): P1 — axis 16 was below the fixed list viewport. The list item margin and height were reduced and the header was reorganized.
2. Final pass (`04-after-jog-final.png`, `06-after-fixed-final.png`, `05-after-home-final.png`): all 16 axes are visible in numeric order with no scrollbar, clipping, or overlap. No remaining P0/P1/P2 issue.

## Required fidelity surfaces

- Fonts and typography: existing Microsoft YaHei UI hierarchy, weights, sizes, and numeric emphasis are preserved. Compact axis rows remain readable.
- Spacing and layout rhythm: the 220 px single-column axis rail frees space for the primary motion workbench. Bottom I/O is fixed at 520 px and alarms receive the remaining width. Gaps and panel radii remain consistent.
- Colors and visual tokens: dark HMI palette, selected blue, healthy green, deceleration gray, immediate-stop orange, and emergency red are unchanged.
- Image and icon quality: existing project motion icons remain at their native treatment; no placeholder or substitute asset was introduced.
- Copy and content: all inputs and safety actions remain. “顺序号” is renamed “回零次序 (0=不参与)” and the multi-axis action is renamed “按次序回原点”.

## Findings

- P0: none
- P1: none
- P2: none

## Verification

- Release x86 build: 0 warnings, 0 errors.
- Behavior harness: passed, including persistence, homing sequence, stop hierarchy, axis switching, invalid-input blocking, and native safety flows.
- All 16 axes are visible and selectable in one vertical list.
- JOG, fixed positioning, homing, motion parameters, real-time status, I/O, and alarm records remain visible at 1680 × 950.

final result: passed
