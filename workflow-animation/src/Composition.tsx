import React from "react";
import {
  AbsoluteFill,
  Composition,
  Easing,
  interpolate,
  Sequence,
  useCurrentFrame,
} from "remotion";

const FPS = 30;
const TOTAL_FRAMES = 1560;
const SLOT_COUNT = 16;

const colors = {
  ink: "#17302a",
  muted: "#60736d",
  line: "#c9d6d2",
  panel: "#ffffff",
  background: "#eef3f1",
  green: "#228b62",
  greenSoft: "#dcefe7",
  blue: "#287fa3",
  blueSoft: "#dcecf2",
  amber: "#d9911b",
  amberSoft: "#f8ead0",
  red: "#c94f52",
  redSoft: "#f6dfe0",
  violet: "#6d68a8",
  violetSoft: "#e9e7f4",
  dark: "#10231f",
};

const clamp = {
  extrapolateLeft: "clamp" as const,
  extrapolateRight: "clamp" as const,
};

type PartDefinition = {
  id: string;
  initialSlot: number;
  loadBeat: number;
  outcome: "OK" | "T1_NG" | "T2_NG" | "T3_NG";
};

const demoParts: PartDefinition[] = [
  {id: "P01", initialSlot: 0, loadBeat: 0, outcome: "OK"},
  {id: "P02", initialSlot: 1, loadBeat: 0, outcome: "T1_NG"},
  {id: "P03", initialSlot: 0, loadBeat: 2, outcome: "OK"},
  {id: "P04", initialSlot: 1, loadBeat: 2, outcome: "OK"},
  {id: "P05", initialSlot: 0, loadBeat: 4, outcome: "OK"},
  {id: "P06", initialSlot: 1, loadBeat: 4, outcome: "OK"},
];

const slotAngle = (slot: number) => 145 - slot * (360 / SLOT_COUNT);

const pointAt = (slot: number, radius: number) => {
  const angle = (slotAngle(slot) * Math.PI) / 180;
  return {
    x: 310 + Math.cos(angle) * radius,
    y: 310 + Math.sin(angle) * radius,
  };
};

const statusColor = (outcome: PartDefinition["outcome"], beatFloat: number, loadBeat: number) => {
  if (outcome === "T1_NG" && beatFloat >= loadBeat + 1) {
    return colors.red;
  }

  if (outcome === "T2_NG" && beatFloat >= loadBeat + 2) {
    return colors.red;
  }

  if (outcome === "T3_NG" && beatFloat >= loadBeat + 3) {
    return colors.red;
  }

  if (outcome === "OK" && beatFloat >= loadBeat + 3) {
    return colors.green;
  }

  return colors.blue;
};

const FadeIn: React.FC<React.PropsWithChildren<{delay?: number; style?: React.CSSProperties}>> = ({
  children,
  delay = 0,
  style,
}) => {
  const frame = useCurrentFrame();
  return (
    <div
      style={{
        ...style,
        opacity: interpolate(frame, [delay, delay + 18], [0, 1], {
          ...clamp,
          easing: Easing.bezier(0.16, 1, 0.3, 1),
        }),
        translate: `0 ${interpolate(frame, [delay, delay + 18], [26, 0], {
          ...clamp,
          easing: Easing.bezier(0.16, 1, 0.3, 1),
        })}px`,
      }}
    >
      {children}
    </div>
  );
};

const SceneShell: React.FC<
  React.PropsWithChildren<{eyebrow: string; title: string; note?: string}>
> = ({eyebrow, title, note, children}) => (
  <AbsoluteFill
    style={{
      background: colors.background,
      color: colors.ink,
      padding: "76px 92px 82px",
    }}
  >
    <div style={{display: "flex", alignItems: "flex-end", justifyContent: "space-between", gap: 64}}>
      <div>
        <div style={{fontSize: 28, fontWeight: 800, color: colors.blue, marginBottom: 12}}>{eyebrow}</div>
        <div style={{fontSize: 68, lineHeight: 1.08, fontWeight: 900}}>{title}</div>
      </div>
      {note ? (
        <div
          style={{
            maxWidth: 600,
            fontSize: 30,
            lineHeight: 1.45,
            color: colors.muted,
            textAlign: "right",
          }}
        >
          {note}
        </div>
      ) : null}
    </div>
    <div style={{flex: 1, minHeight: 0, display: "flex", paddingTop: 46}}>{children}</div>
  </AbsoluteFill>
);

const ProgressBar: React.FC = () => {
  const frame = useCurrentFrame();
  return (
    <div
      style={{
        position: "absolute",
        left: 0,
        bottom: 0,
        height: 10,
        width: `${interpolate(frame, [0, TOTAL_FRAMES - 1], [0, 100], clamp)}%`,
        background: colors.green,
      }}
    />
  );
};

const IntroScene: React.FC = () => {
  const frame = useCurrentFrame();
  const steps = ["双位置上料", "DD 转一格", "T1 / T2 / T3 并行", "按最终结果分 BIN"];

  return (
    <AbsoluteFill
      style={{
        background: colors.dark,
        color: "white",
        padding: "105px 110px",
        justifyContent: "center",
      }}
    >
      <div
        style={{
          position: "absolute",
          top: 0,
          right: 0,
          width: 680,
          height: 680,
          borderRadius: "0 0 0 680px",
          background: colors.green,
          opacity: 0.2,
        }}
      />
      <div style={{fontSize: 30, color: "#8bd1b3", fontWeight: 800, marginBottom: 28}}>整机自动流程设计</div>
      <div
        style={{
          fontSize: 94,
          lineHeight: 1.06,
          fontWeight: 900,
          maxWidth: 1500,
          opacity: interpolate(frame, [0, 24], [0, 1], {...clamp, easing: Easing.bezier(0.16, 1, 0.3, 1)}),
          translate: `0 ${interpolate(frame, [0, 24], [34, 0], clamp)}px`,
        }}
      >
        两次分度一组上下料
        <br />
        三个测试站流水并行
      </div>
      <div style={{display: "flex", gap: 18, marginTop: 70}}>
        {steps.map((step, index) => (
          <div
            key={step}
            style={{
              flex: 1,
              minHeight: 110,
              display: "flex",
              alignItems: "center",
              gap: 18,
              padding: "20px 24px",
              border: "2px solid rgba(255,255,255,0.16)",
              background: "rgba(255,255,255,0.06)",
              opacity: interpolate(frame, [28 + index * 10, 46 + index * 10], [0, 1], clamp),
            }}
          >
            <div
              style={{
                width: 54,
                height: 54,
                borderRadius: 27,
                background: index === 2 ? colors.amber : colors.green,
                display: "flex",
                alignItems: "center",
                justifyContent: "center",
                fontSize: 26,
                fontWeight: 900,
                flexShrink: 0,
              }}
            >
              {index + 1}
            </div>
            <div style={{fontSize: 30, lineHeight: 1.25, fontWeight: 800}}>{step}</div>
          </div>
        ))}
      </div>
    </AbsoluteFill>
  );
};

const StationLabel: React.FC<{slot: number; label: string; color: string; width?: number}> = ({
  slot,
  label,
  color,
  width = 126,
}) => {
  const point = pointAt(slot, 330);
  return (
    <div
      style={{
        position: "absolute",
        left: point.x - width / 2,
        top: point.y - 25,
        width,
        height: 50,
        borderRadius: 6,
        background: color,
        color: "white",
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        fontSize: 25,
        fontWeight: 900,
        boxShadow: "0 8px 20px rgba(16,35,31,0.15)",
      }}
    >
      {label}
    </div>
  );
};

const RotaryTable: React.FC<{
  beatFloat?: number;
  compact?: boolean;
  showParts?: boolean;
  activeSlots?: number[];
}> = ({beatFloat = 0, compact = false, showParts = true, activeSlots = []}) => {
  const size = compact ? 520 : 620;
  const scale = size / 620;
  const frame = useCurrentFrame();

  return (
    <div style={{position: "relative", width: size, height: size, flexShrink: 0}}>
      <div
        style={{
          position: "absolute",
          inset: 54 * scale,
          borderRadius: "50%",
          border: `${14 * scale}px solid #b8c9c3`,
          background: "#dfe8e5",
          boxShadow: "inset 0 0 0 2px #a9bdb6, 0 18px 46px rgba(30,55,48,0.16)",
        }}
      />
      <div
        style={{
          position: "absolute",
          left: 245 * scale,
          top: 245 * scale,
          width: 130 * scale,
          height: 130 * scale,
          borderRadius: "50%",
          background: colors.dark,
          color: "white",
          display: "flex",
          flexDirection: "column",
          alignItems: "center",
          justifyContent: "center",
          fontWeight: 900,
          rotate: `${interpolate(frame, [0, 60], [0, -22.5], {...clamp, easing: Easing.bezier(0.4, 0, 0.2, 1)})}deg`,
        }}
      >
        <div style={{fontSize: 24 * scale}}>DD</div>
        <div style={{fontSize: 42 * scale, lineHeight: 1}}>↻</div>
      </div>

      {Array.from({length: SLOT_COUNT}).map((_, slot) => {
        const point = pointAt(slot, 228);
        const active = activeSlots.includes(slot);
        const special = slot <= 7;
        return (
          <div
            key={slot}
            style={{
              position: "absolute",
              left: (point.x - 29) * scale,
              top: (point.y - 29) * scale,
              width: 58 * scale,
              height: 58 * scale,
              borderRadius: "50%",
              border: `${active ? 6 : 3}px solid ${active ? colors.amber : special ? "#78978d" : "#b8c8c3"}`,
              background: active ? colors.amberSoft : "#f8fbfa",
              color: colors.muted,
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              fontSize: 20 * scale,
              fontWeight: 800,
            }}
          >
            {slot}
          </div>
        );
      })}

      {showParts
        ? demoParts.map((part) => {
            const age = beatFloat - part.loadBeat;
            if (age < -0.02 || age > 6.25) {
              return null;
            }

            const position = part.initialSlot + Math.max(0, age);
            const point = pointAt(position, 228);
            const entering = interpolate(age, [0, 0.18], [0, 1], clamp);
            const leaving = interpolate(age, [6, 6.22], [1, 0], clamp);
            return (
              <div
                key={part.id}
                style={{
                  position: "absolute",
                  left: (point.x - 23) * scale,
                  top: (point.y - 23) * scale,
                  width: 46 * scale,
                  height: 46 * scale,
                  borderRadius: "50%",
                  background: statusColor(part.outcome, beatFloat, part.loadBeat),
                  color: "white",
                  display: "flex",
                  alignItems: "center",
                  justifyContent: "center",
                  fontSize: 17 * scale,
                  fontWeight: 900,
                  opacity: Math.min(entering, leaving),
                  boxShadow: "0 5px 12px rgba(16,35,31,0.25)",
                }}
              >
                {part.id}
              </div>
            );
          })
        : null}

      <StationLabel slot={0.5} label="XY1 上料" color={colors.blue} width={150} />
      <StationLabel slot={2} label="T1" color={colors.amber} width={94} />
      <StationLabel slot={3} label="T2" color={colors.violet} width={94} />
      <StationLabel slot={4} label="T3" color={colors.green} width={94} />
      <StationLabel slot={6.5} label="XY2 下料" color={colors.red} width={150} />
    </div>
  );
};

const LegendItem: React.FC<{color: string; title: string; detail: string}> = ({color, title, detail}) => (
  <div style={{display: "flex", gap: 20, alignItems: "flex-start"}}>
    <div style={{width: 18, height: 72, background: color, borderRadius: 4, flexShrink: 0}} />
    <div>
      <div style={{fontSize: 34, fontWeight: 900}}>{title}</div>
      <div style={{fontSize: 26, color: colors.muted, marginTop: 6, lineHeight: 1.4}}>{detail}</div>
    </div>
  </div>
);

const TopologyScene: React.FC = () => (
  <SceneShell
    eyebrow="01 设备布局"
    title="16个工位就是一条环形队列"
    note="动画按当前流程假设：上料与下料各占两个相邻位置，测试站各相隔一格。"
  >
    <div style={{display: "flex", width: "100%", alignItems: "center", justifyContent: "space-between", gap: 70}}>
      <FadeIn delay={0} style={{marginLeft: 40}}>
        <RotaryTable showParts={false} />
      </FadeIn>
      <div style={{display: "flex", flexDirection: "column", gap: 38, flex: 1, maxWidth: 680}}>
        <FadeIn delay={18}>
          <LegendItem color={colors.blue} title="位置 0 + 1：第一套 XY" detail="一次取两个料，分别放进相邻的两个工位。" />
        </FadeIn>
        <FadeIn delay={34}>
          <LegendItem color={colors.amber} title="位置 2 / 3 / 4：T1、T2、T3" detail="每一拍面对的是不同产品，三个站可同时测试。" />
        </FadeIn>
        <FadeIn delay={50}>
          <LegendItem color={colors.red} title="位置 6 + 7：第二套 XY" detail="两件产品到齐后，分别按自己的最终 BIN 下料。" />
        </FadeIn>
      </div>
    </div>
  </SceneShell>
);

const BeatScene: React.FC = () => {
  const frame = useCurrentFrame();
  const phases = [
    {name: "安全确认", detail: "Z轴在上位 / XY退出", color: colors.blue},
    {name: "DD 转一格", detail: "轴0到位后锁定", color: colors.green},
    {name: "三轴同下", detail: "轴13 / 14 / 15", color: colors.amber},
    {name: "仪表 START", detail: "T1 / T2 / T3 并发", color: colors.violet},
    {name: "写入结果", detail: "PASS / NG / 跳过", color: colors.red},
    {name: "三轴同上", detail: "完成本拍测试", color: colors.green},
  ];
  const phase = Math.min(phases.length - 1, Math.floor(frame / 42));

  return (
    <SceneShell eyebrow="02 单拍顺序" title="每转一格，只执行一个固定的 RunOneBeat" note="同一拍内三个测试站并行；转盘动作与下压动作必须串行互锁。">
      <div style={{width: "100%", display: "flex", flexDirection: "column", justifyContent: "center", gap: 42}}>
        <div style={{display: "grid", gridTemplateColumns: "repeat(6, 1fr)", gap: 16}}>
          {phases.map((item, index) => {
            const active = index === phase;
            const done = index < phase;
            return (
              <div
                key={item.name}
                style={{
                  height: 174,
                  borderRadius: 6,
                  padding: "24px 20px",
                  border: `3px solid ${active ? item.color : done ? colors.green : colors.line}`,
                  background: active ? `${item.color}18` : colors.panel,
                  scale: active ? 1.04 : 1,
                  boxShadow: active ? "0 18px 38px rgba(29,53,46,0.14)" : "none",
                }}
              >
                <div style={{fontSize: 25, fontWeight: 800, color: done ? colors.green : active ? item.color : colors.muted}}>
                  {done ? "完成" : `0${index + 1}`}
                </div>
                <div style={{fontSize: 33, fontWeight: 900, marginTop: 18}}>{item.name}</div>
                <div style={{fontSize: 23, color: colors.muted, marginTop: 10, lineHeight: 1.35}}>{item.detail}</div>
              </div>
            );
          })}
        </div>

        <div style={{display: "grid", gridTemplateColumns: "1fr 1fr 1fr", gap: 24}}>
          {[
            {station: "T1", part: "P04", result: phase >= 4 ? "PASS" : phase >= 3 ? "TESTING" : "WAIT", color: colors.amber},
            {station: "T2", part: "P01", result: phase >= 4 ? "PASS" : phase >= 3 ? "TESTING" : "WAIT", color: colors.violet},
            {station: "T3", part: "P02", result: phase >= 3 ? "跳过：T1 NG" : "WAIT", color: colors.green},
          ].map((station) => (
            <div
              key={station.station}
              style={{
                minHeight: 190,
                background: colors.panel,
                borderLeft: `12px solid ${station.color}`,
                padding: "28px 34px",
                display: "flex",
                alignItems: "center",
                justifyContent: "space-between",
                boxShadow: "0 12px 28px rgba(31,58,50,0.08)",
              }}
            >
              <div>
                <div style={{fontSize: 45, fontWeight: 900}}>{station.station}</div>
                <div style={{fontSize: 28, color: colors.muted, marginTop: 10}}>当前产品 {station.part}</div>
              </div>
              <div
                style={{
                  fontSize: 28,
                  fontWeight: 900,
                  color: station.result.includes("NG") ? colors.red : station.result === "PASS" ? colors.green : colors.muted,
                  textAlign: "right",
                }}
              >
                {station.result}
              </div>
            </div>
          ))}
        </div>
      </div>
    </SceneShell>
  );
};

type FlowCellProps = {
  label: string;
  state: "pass" | "fail" | "skip" | "wait" | "bin";
  revealAt: number;
};

const FlowCell: React.FC<FlowCellProps> = ({label, state, revealAt}) => {
  const frame = useCurrentFrame();
  const color = state === "pass" || state === "bin" ? colors.green : state === "fail" ? colors.red : state === "skip" ? colors.muted : colors.line;
  return (
    <div
      style={{
        minWidth: 245,
        height: 122,
        borderRadius: 6,
        border: `4px solid ${color}`,
        background: state === "fail" ? colors.redSoft : state === "skip" ? "#edf1f0" : state === "pass" || state === "bin" ? colors.greenSoft : colors.panel,
        display: "flex",
        alignItems: "center",
        justifyContent: "center",
        fontSize: 34,
        fontWeight: 900,
        opacity: interpolate(frame, [revealAt, revealAt + 14], [0, 1], clamp),
        scale: interpolate(frame, [revealAt, revealAt + 14], [0.92, 1], clamp),
      }}
    >
      {label}
    </div>
  );
};

const FlowArrow: React.FC<{revealAt: number}> = ({revealAt}) => {
  const frame = useCurrentFrame();
  return (
    <div style={{fontSize: 52, color: colors.line, opacity: interpolate(frame, [revealAt, revealAt + 10], [0, 1], clamp)}}>→</div>
  );
};

const DecisionScene: React.FC = () => (
  <SceneShell eyebrow="03 短路判定" title="BIN 属于产品，不属于整组载具" note="同一批的两件产品必须分别保存状态，不能因为其中一件 NG 就跳过另一件。">
    <div style={{width: "100%", display: "flex", flexDirection: "column", justifyContent: "center", gap: 64}}>
      <FadeIn delay={0}>
        <div style={{display: "flex", alignItems: "center", gap: 24}}>
          <div style={{width: 155, fontSize: 40, fontWeight: 900, color: colors.blue}}>P01</div>
          <FlowCell label="T1 PASS" state="pass" revealAt={18} />
          <FlowArrow revealAt={28} />
          <FlowCell label="T2 PASS" state="pass" revealAt={40} />
          <FlowArrow revealAt={50} />
          <FlowCell label="T3 PASS" state="pass" revealAt={62} />
          <FlowArrow revealAt={72} />
          <FlowCell label="BIN_OK" state="bin" revealAt={84} />
        </div>
      </FadeIn>

      <FadeIn delay={8}>
        <div style={{display: "flex", alignItems: "center", gap: 24}}>
          <div style={{width: 155, fontSize: 40, fontWeight: 900, color: colors.blue}}>P02</div>
          <FlowCell label="T1 NG" state="fail" revealAt={104} />
          <FlowArrow revealAt={114} />
          <FlowCell label="T2 跳过" state="skip" revealAt={126} />
          <FlowArrow revealAt={136} />
          <FlowCell label="T3 跳过" state="skip" revealAt={148} />
          <FlowArrow revealAt={158} />
          <FlowCell label="BIN_T1_NG" state="fail" revealAt={170} />
        </div>
      </FadeIn>

      <div
        style={{
          marginLeft: 178,
          background: colors.amberSoft,
          borderLeft: `10px solid ${colors.amber}`,
          padding: "24px 34px",
          fontSize: 31,
          lineHeight: 1.45,
          color: colors.ink,
        }}
      >
        仪表返回 NG 是正常分 BIN；通信超时、轴报警、急停属于设备故障，必须停机保留现场，不能当作产品 NG。
      </div>
    </div>
  </SceneShell>
);

const stationForBeat: Record<number, {t1: string; t2: string; t3: string; transfer?: string}> = {
  0: {t1: "空", t2: "空", t3: "空", transfer: "XY1 放入 P01 / P02"},
  1: {t1: "P02 NG", t2: "空", t3: "空"},
  2: {t1: "P01 PASS", t2: "P02 跳过", t3: "空", transfer: "XY1 放入 P03 / P04"},
  3: {t1: "P04 PASS", t2: "P01 PASS", t3: "P02 跳过"},
  4: {t1: "P03 PASS", t2: "P04 PASS", t3: "P01 PASS", transfer: "XY1 放入 P05 / P06"},
  5: {t1: "P06 PASS", t2: "P03 PASS", t3: "P04 PASS"},
  6: {t1: "P05 PASS", t2: "P06 PASS", t3: "P03 PASS", transfer: "XY2 下料 P01 / P02"},
};

const PipelineScene: React.FC = () => {
  const frame = useCurrentFrame();
  const beatFloat = interpolate(frame, [20, 250], [0, 6.18], {
    ...clamp,
    easing: Easing.bezier(0.45, 0, 0.25, 1),
  });
  const beat = Math.min(6, Math.floor(beatFloat + 0.12));
  const current = stationForBeat[beat];
  const transferBeat = beat % 2 === 0;

  return (
    <SceneShell eyebrow="04 六拍展开" title="两次分度补一对料，测试流水不断" note="P02 在 T1 失败后继续随转盘移动，但到 T2、T3 时不再发送 START。">
      <div style={{display: "flex", width: "100%", alignItems: "center", gap: 74}}>
        <div style={{width: 760, display: "flex", justifyContent: "center"}}>
          <RotaryTable beatFloat={beatFloat} activeSlots={transferBeat ? [0, 1, 6, 7] : [2, 3, 4]} />
        </div>
        <div style={{flex: 1, display: "flex", flexDirection: "column", gap: 22}}>
          <div style={{display: "flex", alignItems: "baseline", gap: 22}}>
            <div style={{fontSize: 84, fontWeight: 900, color: colors.green}}>第 {beat} 拍</div>
            <div style={{fontSize: 30, color: colors.muted}}>{transferBeat ? "偶数拍：允许双 XY" : "奇数拍：只做分度与测试"}</div>
          </div>

          {[
            {label: "T1", value: current.t1, color: colors.amber},
            {label: "T2", value: current.t2, color: colors.violet},
            {label: "T3", value: current.t3, color: colors.green},
          ].map((row) => (
            <div
              key={row.label}
              style={{
                height: 102,
                background: colors.panel,
                borderLeft: `11px solid ${row.color}`,
                display: "flex",
                alignItems: "center",
                padding: "0 32px",
                gap: 34,
                boxShadow: "0 10px 24px rgba(28,54,46,0.08)",
              }}
            >
              <div style={{fontSize: 38, fontWeight: 900, width: 70}}>{row.label}</div>
              <div style={{fontSize: 31, fontWeight: 800, color: row.value.includes("NG") || row.value.includes("跳过") ? colors.red : colors.ink}}>
                {row.value}
              </div>
            </div>
          ))}

          <div
            style={{
              minHeight: 112,
              background: transferBeat ? colors.blueSoft : "#e6ecea",
              border: `3px solid ${transferBeat ? colors.blue : colors.line}`,
              padding: "24px 30px",
              fontSize: 29,
              fontWeight: 900,
              color: transferBeat ? colors.blue : colors.muted,
            }}
          >
            {current.transfer ?? "本拍不上料、不下料"}
          </div>
        </div>
      </div>
    </SceneShell>
  );
};

const CodeScene: React.FC = () => {
  const frame = useCurrentFrame();
  const lines = [
    "await WaitIndexSafeAsync();",
    "await Axis0.IndexOnePitchAsync();",
    "State.AdvanceOneSlot();",
    "await Axes13To15.DownTogetherAsync();",
    "await Task.WhenAll(T1(), T2(), T3());",
    "await Axes13To15.UpTogetherAsync();",
    "if (State.Beat % 2 == 0)",
    "    await RunLoadThenUnloadAsync();",
  ];
  const activeLine = Math.min(lines.length - 1, Math.floor(frame / 27));

  return (
    <SceneShell eyebrow="05 代码骨架" title="一个节拍函数，管理全部设备动作" note="运动、测试、状态、BIN 分开封装；主页只负责启动、停止和显示。">
      <div style={{display: "grid", gridTemplateColumns: "1.18fr 0.82fr", width: "100%", gap: 44, alignItems: "stretch"}}>
        <div style={{background: colors.dark, padding: "34px 38px", boxShadow: "0 18px 44px rgba(16,35,31,0.18)"}}>
          <div style={{fontSize: 25, color: "#8bd1b3", fontWeight: 800, marginBottom: 22}}>RunOneBeatAsync</div>
          {lines.map((line, index) => (
            <div
              key={line}
              style={{
                minHeight: 67,
                display: "flex",
                alignItems: "center",
                padding: "0 18px",
                fontFamily: "Consolas, monospace",
                fontSize: 30,
                color: index === activeLine ? "#ffffff" : "#adc0ba",
                background: index === activeLine ? "rgba(34,139,98,0.34)" : "transparent",
                borderLeft: `7px solid ${index === activeLine ? colors.green : "transparent"}`,
              }}
            >
              {line}
            </div>
          ))}
        </div>
        <div style={{display: "flex", flexDirection: "column", gap: 20}}>
          {[
            {title: "RotaryFlowState", detail: "16 个位置、拍号、每件产品的 T1/T2/T3 与 BIN", color: colors.blue},
            {title: "TestStationController", detail: "只负责 START、等待完成、解析 PASS/NG", color: colors.violet},
            {title: "XY Loader / Unloader", detail: "每 2 拍处理两个相邻位置", color: colors.red},
            {title: "ProductionOrchestrator", detail: "互锁、动作顺序、异常停机与恢复入口", color: colors.green},
          ].map((item, index) => (
            <div
              key={item.title}
              style={{
                flex: 1,
                background: colors.panel,
                borderLeft: `11px solid ${item.color}`,
                padding: "24px 30px",
                opacity: interpolate(frame, [18 + index * 18, 34 + index * 18], [0, 1], clamp),
              }}
            >
              <div style={{fontSize: 34, fontWeight: 900}}>{item.title}</div>
              <div style={{fontSize: 26, color: colors.muted, lineHeight: 1.42, marginTop: 8}}>{item.detail}</div>
            </div>
          ))}
        </div>
      </div>
    </SceneShell>
  );
};

const SafetyScene: React.FC = () => {
  const frame = useCurrentFrame();
  const rules = [
    {title: "转盘前", detail: "轴13/14/15 全部上位，两个 XY 全部退出危险区", color: colors.blue},
    {title: "测试后", detail: "先把每件产品结果写入状态，再允许下一次分度", color: colors.green},
    {title: "正常 NG", detail: "立即锁定最终 BIN，后续站只跳过当前产品", color: colors.red},
    {title: "设备异常", detail: "仪表超时、轴报警、急停：停止循环并保留当前工位", color: colors.amber},
  ];

  return (
    <SceneShell eyebrow="06 必要互锁" title="检测 NG 才能分 BIN，设备故障必须停机" note="这是整套流程稳定运行的边界。">
      <div style={{width: "100%", display: "grid", gridTemplateColumns: "1fr 1fr", gap: 28, alignContent: "center"}}>
        {rules.map((rule, index) => (
          <div
            key={rule.title}
            style={{
              minHeight: 230,
              background: colors.panel,
              borderTop: `12px solid ${rule.color}`,
              padding: "34px 38px",
              opacity: interpolate(frame, [index * 22, index * 22 + 18], [0, 1], clamp),
              translate: `${interpolate(frame, [index * 22, index * 22 + 18], [index % 2 === 0 ? -30 : 30, 0], clamp)}px 0`,
              boxShadow: "0 14px 32px rgba(29,56,48,0.09)",
            }}
          >
            <div style={{fontSize: 44, fontWeight: 900, color: rule.color}}>{rule.title}</div>
            <div style={{fontSize: 31, lineHeight: 1.48, marginTop: 20, color: colors.ink}}>{rule.detail}</div>
          </div>
        ))}
      </div>
      <div
        style={{
          position: "absolute",
          left: 92,
          right: 92,
          bottom: 52,
          textAlign: "center",
          fontSize: 31,
          fontWeight: 900,
          color: colors.green,
          opacity: interpolate(frame, [105, 130], [0, 1], clamp),
        }}
      >
        最终结构：主页 → ProductionOrchestrator → 运动 / 仪表 / 状态 / BIN
      </div>
    </SceneShell>
  );
};

const WorkflowVideo: React.FC = () => (
  <AbsoluteFill>
    <Sequence durationInFrames={120}>
      <IntroScene />
    </Sequence>
    <Sequence from={120} durationInFrames={210}>
      <TopologyScene />
    </Sequence>
    <Sequence from={330} durationInFrames={270}>
      <BeatScene />
    </Sequence>
    <Sequence from={600} durationInFrames={270}>
      <DecisionScene />
    </Sequence>
    <Sequence from={870} durationInFrames={270}>
      <PipelineScene />
    </Sequence>
    <Sequence from={1140} durationInFrames={240}>
      <CodeScene />
    </Sequence>
    <Sequence from={1380} durationInFrames={180}>
      <SafetyScene />
    </Sequence>
    <ProgressBar />
  </AbsoluteFill>
);

export const MyComposition = () => (
  <Composition
    id="RotaryWorkflow"
    component={WorkflowVideo}
    durationInFrames={TOTAL_FRAMES}
    fps={FPS}
    width={1920}
    height={1080}
    defaultProps={{}}
  />
);
