import {
  AlertCircle,
  Archive,
  ArrowRight,
  BarChart3,
  Bot,
  Check,
  CheckCircle2,
  ChevronDown,
  ChevronLeft,
  ChevronRight,
  CircleDollarSign,
  FileQuestion,
  FileSearch,
  FileSpreadsheet,
  Files,
  GripVertical,
  Lock,
  Rows3,
  MessageSquareText,
  Moon,
  PanelLeftClose,
  Plus,
  RotateCcw,
  Search,
  Send,
  Sparkles,
  Sun,
  SlidersHorizontal,
  Upload,
  X,
  XOctagon,
} from "lucide-react"
import {
  type CSSProperties,
  type DragEvent,
  type KeyboardEvent,
  type MouseEvent as ReactMouseEvent,
  type ReactNode,
  useCallback,
  useEffect,
  useRef,
  useState,
} from "react"

type Status = "Verified" | "Needs review" | "Failed rule" | "Learned" | "Kept, not processed" | "Unsupported" | "Active" | "Shadow" | "Retired"
type Screen = "upload" | "document" | "review" | "analytics" | "chat" | "costs" | "formats"

const statusIcons: Record<Status, typeof CheckCircle2> = {
  Verified: CheckCircle2,
  "Needs review": AlertCircle,
  "Failed rule": XOctagon,
  Learned: Sparkles,
  "Kept, not processed": Archive,
  Unsupported: FileQuestion,
  Active: CheckCircle2,
  Shadow: Sparkles,
  Retired: Archive,
}

function Button({
  children,
  variant = "secondary",
  className = "",
  ...props
}: React.ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: "primary" | "secondary" | "ghost"
}) {
  return (
    <button className={`button button--${variant} ${className}`} {...props}>
      {children}
    </button>
  )
}

function StatusBadge({ status }: { status: Status }) {
  const Icon = statusIcons[status]
  return (
    <span
      className={`status-badge status-badge--${status.toLowerCase().replaceAll(" ", "-").replace(",", "")}`}
    >
      <Icon aria-hidden="true" size={14} strokeWidth={2} />
      {status}
    </span>
  )
}

function RoutingBadge({
  children,
  review = false,
}: {
  children: ReactNode
  review?: boolean
}) {
  return (
    <span className={`routing-badge${review ? " routing-badge--review" : ""}`}>
      {review ? (
        <AlertCircle size={14} aria-hidden="true" />
      ) : (
        <Sparkles size={14} aria-hidden="true" />
      )}
      {children}
    </span>
  )
}

function CostLine({ children }: { children: ReactNode }) {
  return (
    <span className="cost-line">
      <CircleDollarSign size={14} aria-hidden="true" />
      {children}
    </span>
  )
}

function ConfidenceMeter({ value }: { value: number }) {
  return (
    <span
      className="confidence"
      role="meter"
      aria-label={`Confidence ${value.toFixed(2)}`}
      aria-valuemin={0}
      aria-valuemax={1}
      aria-valuenow={value}
    >
      <span className="confidence__track" aria-hidden="true">
        <span
          className="confidence__fill"
          style={{ "--confidence": `${value * 100}%` } as CSSProperties}
        />
      </span>
      <span>{value.toFixed(2)}</span>
    </span>
  )
}

function usePrefersReducedMotion() {
  const [reduced, setReduced] = useState(false)
  useEffect(() => {
    const preference = window.matchMedia("(prefers-reduced-motion: reduce)")
    const update = () => setReduced(preference.matches)
    update()
    preference.addEventListener("change", update)
    return () => preference.removeEventListener("change", update)
  }, [])
  return reduced
}

function AnimatedNumber({
  value,
  format,
}: {
  value: number
  format: (value: number) => string
}) {
  const reducedMotion = usePrefersReducedMotion()
  const [display, setDisplay] = useState(value)
  const previous = useRef(value)
  useEffect(() => {
    const from = previous.current
    previous.current = value
    if (reducedMotion) {
      setDisplay(value)
      return
    }
    const start = performance.now()
    let frame = 0
    const tick = (now: number) => {
      const progress = Math.min(1, (now - start) / 300)
      setDisplay(from + (value - from) * progress)
      if (progress < 1) frame = window.requestAnimationFrame(tick)
    }
    frame = window.requestAnimationFrame(tick)
    return () => window.cancelAnimationFrame(frame)
  }, [value, reducedMotion])
  return <>{format(display)}</>
}

const navItems = [
  ["Upload", Upload, "upload"],
  ["Documents", Files, "document"],
  ["Review queue", AlertCircle, "review"],
  ["Analytics", BarChart3, "analytics"],
  ["Chat", MessageSquareText, "chat"],
  ["Costs", CircleDollarSign, "costs"],
  ["Formats", FileSearch, "formats"],
] as const

function AppFrame({
  children,
  screen,
  onScreen,
}: {
  children: ReactNode
  screen: Screen
  onScreen: (screen: Screen) => void
}) {
  const [dark, setDark] = useState(false)
  return (
    <div className="app" data-theme={dark ? "dark" : "light"}>
      <a className="skip-link" href="#main-content">
        Skip to content
      </a>
      <aside className="nav-rail" aria-label="Primary navigation">
        <div
          className="brand-mark"
          aria-label="State Fund Loss Run Intelligence"
        >
          SF
        </div>
        <nav>
          {navItems.map(([label, Icon, target]) => {
            const active = screen === target
            return (
              <button
                key={label}
                className={`nav-item${active ? " nav-item--active" : ""}`}
                aria-current={active ? "page" : undefined}
                onClick={() => onScreen(target)}
              >
                <Icon size={18} aria-hidden="true" />
                <span>{label}</span>
              </button>
            )
          })}
        </nav>
      </aside>
      <header className="topbar">
        <button className="quote-selector">
          <span>Q-2026-018342 · Acme Fabrication Inc</span>
          <ChevronDown size={14} aria-hidden="true" />
        </button>
        <span className="poc-chip">POC · synthetic data</span>
        <CostLine>
          {screen === "document"
            ? "$0.030 · 6 pages · 0 model calls"
            : screen === "chat"
              ? "$0.0012 · 1 model call"
              : screen === "costs"
                ? "$42.18 · POC to date"
                : "$0.066 · 12 pages · 1 model call"}
        </CostLine>
        <div className="topbar__spacer" />
        <Button
          variant="ghost"
          className="icon-button"
          onClick={() => setDark(!dark)}
          aria-label={`Switch to ${dark ? "light" : "dark"} theme`}
        >
          {dark ? <Sun size={17} /> : <Moon size={17} />}
        </Button>
        <span className="user-name">M. Okafor · Underwriter</span>
      </header>
      <main className="main" id="main-content" tabIndex={-1}>
        {children}
      </main>
    </div>
  )
}

const manifest = [
  {
    name: "amtrust-loss-run-2026.pdf",
    pages: "pages 1–6",
    type: "Loss run",
    status: "Verified" as Status,
    badge: "Format memory hit · AmTrust v2 · 0 model calls",
    claims: "47 claims",
    cost: "$0.030",
  },
  {
    name: "pie-loss-run-2024.pdf",
    pages: "pages 1–3",
    type: "Loss run",
    status: "Needs review" as Status,
    reason: "Period 2023 subtotal does not match extracted rows (R-10)",
    badge: "Needs review · Pie v1 · 0 model calls",
    claims: "12 claims",
    cost: "$0.015",
  },
  {
    name: "prior-carrier-2023.xlsx",
    pages: "",
    type: "Loss run",
    status: "Learned" as Status,
    badge:
      "Format learned · Fictional Casualty v1 · 1 model call · confirming (1 of 3)",
    claims: "19 claims",
    cost: "$0.021",
  },
  {
    name: "payroll-report-2025.pdf",
    pages: "pages 1–2",
    type: "Payroll report",
    status: "Kept, not processed" as Status,
    reason: "Available to chat",
  },
  {
    name: "broker-cover-letter.pdf",
    pages: "page 1",
    type: "Letter",
    status: "Kept, not processed" as Status,
  },
  {
    name: "old-scan.tif",
    pages: "",
    type: "Unreadable image",
    status: "Unsupported" as Status,
    reason: "Image could not be read (0 words recognized)",
  },
]

const processingStories = [
  [
    ["Classified · loss run", "default", 260],
    ["Layout · 6 pages", "default", 320],
    ["Format match: checking 14 known formats…", "default", 600],
    ["Format memory hit · AmTrust v2 · 0 model calls", "verified", 150],
    ["Extracted · 47 claims", "default", 260],
    ["Validated · 26 rules, 2 need attention", "review", 260],
    ["Ready", "verified", 180],
  ],
  [
    ["Classified · loss run", "default", 240],
    ["Layout · 3 pages", "default", 280],
    ["Format match: checking 14 known formats…", "default", 600],
    ["Format memory hit · Pie v1 · 0 model calls", "verified", 150],
    ["Extracted · 12 claims", "default", 240],
    ["Validated · R-10 failed", "fail", 260],
    ["Queued automatically — hard rule", "fail", 180],
  ],
  [
    ["Classified · loss run", "default", 240],
    ["Layout · spreadsheet", "default", 260],
    ["Format match: no match among 14 formats", "review", 500],
    ["Learning format… 1 model call", "learned", 1200],
    ["Verified against printed totals ✓", "verified", 260],
    [
      "Format learned · Fictional Casualty v1 · 1 model call · confirming (1 of 3)",
      "learned",
      150,
    ],
  ],
  [
    ["Classified · payroll report", "default", 280],
    ["Kept, not processed", "default", 180],
  ],
  [
    ["Classified · letter", "default", 260],
    ["Kept, not processed", "default", 180],
  ],
  [["Could not be read", "fail", 300]],
] as const

type DemoState = {
  running: boolean
  fileIndex: number
  step: number
  finished: number
  announcement: string
}

function ProcessingStepper({
  fileIndex,
  step,
}: {
  fileIndex: number
  step: number
}) {
  const story = processingStories[fileIndex]
  return (
    <div
      className="story-stepper"
      aria-label={`Processing ${manifest[fileIndex].name}`}
    >
      {story.slice(0, step + 1).map(([message, tone], index) => (
        <div
          className={`story-step story-step--${tone}${
            index === step ? " story-step--current" : ""
          }`}
          key={message}
        >
          <span className="story-step__dot">
            {index < step ? <Check size={11} /> : <span />}
          </span>
          <span>{message}</span>
        </div>
      ))}
    </div>
  )
}

function ManifestList({ demo }: { demo: DemoState }) {
  const [expanded, setExpanded] = useState<number | null>(0)
  const visibleCount = demo.running ? demo.fileIndex + 1 : manifest.length
  return (
    <div className="manifest" aria-label="Package manifest">
      <span className="sr-only" aria-live="polite" aria-atomic="true">
        {demo.announcement}
      </span>
      <div className="manifest__header">
        <span>Package manifest</span>
        <span>6 files · 12 pages</span>
      </div>
      {manifest.slice(0, visibleCount).map((file, index) => {
        const canExpand = file.type === "Loss run"
        const isCurrent = demo.running && demo.fileIndex === index
        const finished = !demo.running || index < demo.finished
        const isExpanded = isCurrent || expanded === index
        const currentStep = isCurrent
          ? demo.step
          : processingStories[index].length - 1
        const showBadge =
          finished ||
          (isCurrent &&
            ((index < 2 && currentStep >= 3) ||
              (index === 2 && currentStep >= 5)))
        const rowStatus =
          index === 1 && !finished && currentStep < 5
            ? "Verified" as Status
            : file.status
        return (
          <div
            className={`manifest__item${
              isCurrent ? " manifest__item--processing" : ""
            }`}
            key={file.name}
          >
            <button
              className="manifest__row"
              onClick={() =>
                canExpand && setExpanded(isExpanded ? null : index)
              }
              aria-expanded={canExpand ? isExpanded : undefined}
            >
              <span className="manifest__expand">
                {canExpand ? (
                  isExpanded ? (
                    <ChevronDown size={16} />
                  ) : (
                    <ChevronRight size={16} />
                  )
                ) : (
                  <span />
                )}
              </span>
              <span className="manifest__identity">
                <strong>{file.name}</strong>
                <span>
                  {[file.pages, file.type].filter(Boolean).join(" · ")}
                </span>
                {file.reason && (
                  <span
                    className={
                      file.status === "Needs review" ||
                      file.status === "Unsupported"
                        ? "text-review"
                        : ""
                    }
                  >
                    {file.reason}
                  </span>
                )}
              </span>
              <span className="manifest__status">
                <StatusBadge status={rowStatus} />
              </span>
              <span className="manifest__routing">
                {file.badge && showBadge && (
                  <RoutingBadge review={file.status === "Needs review"}>
                    {file.badge}
                  </RoutingBadge>
                )}
              </span>
              <span className="manifest__claims">{file.claims ?? "—"}</span>
              <span className="manifest__cost">{file.cost ?? "—"}</span>
            </button>
            {isExpanded && (
              <ProcessingStepper fileIndex={index} step={currentStep} />
            )}
          </div>
        )
      })}
    </div>
  )
}

function UploadScreen({ onContinue }: { onContinue: () => void }) {
  const [dragging, setDragging] = useState(false)
  const reducedMotion = usePrefersReducedMotion()
  const runId = useRef(0)
  const [demo, setDemo] = useState<DemoState>({
    running: false,
    fileIndex: 5,
    step: 0,
    finished: 6,
    announcement: "Package processing complete.",
  })
  const replayDemo = async () => {
    const id = ++runId.current
    if (reducedMotion) {
      setDemo({
        running: false,
        fileIndex: 5,
        step: 0,
        finished: 6,
        announcement: "All six files rendered in their final state.",
      })
      return
    }
    setDemo({
      running: true,
      fileIndex: 0,
      step: 0,
      finished: 0,
      announcement: "Processing package.",
    })
    for (let fileIndex = 0; fileIndex < processingStories.length; fileIndex++) {
      const story = processingStories[fileIndex]
      for (let step = 0; step < story.length; step++) {
        if (runId.current !== id) return
        setDemo({
          running: true,
          fileIndex,
          step,
          finished: fileIndex,
          announcement: "",
        })
        await new Promise((resolve) =>
          window.setTimeout(resolve, story[step][2]),
        )
      }
      if (runId.current !== id) return
      setDemo({
        running: fileIndex < manifest.length - 1,
        fileIndex,
        step: story.length - 1,
        finished: fileIndex + 1,
        announcement: `${manifest[fileIndex].name}: ${story[story.length - 1][0]}.`,
      })
      await new Promise((resolve) => window.setTimeout(resolve, 180))
    }
  }
  const drop = (event: DragEvent) => {
    event.preventDefault()
    setDragging(false)
    replayDemo()
  }
  const completed = demo.finished
  const accruedCost = [0.03, 0.015, 0.021, 0, 0, 0]
    .slice(0, completed)
    .reduce((sum, value) => sum + value, 0)
  return (
    <div
      className={`upload-screen${dragging ? " upload-screen--dragging" : ""}`}
      onDragEnter={(event) => {
        event.preventDefault()
        setDragging(true)
      }}
      onDragOver={(event) => event.preventDefault()}
      onDragLeave={(event) =>
        event.currentTarget === event.target && setDragging(false)
      }
      onDrop={drop}
    >
      {dragging && (
        <div className="drag-overlay">
          <Upload size={30} aria-hidden="true" />
          <strong>Drop package to begin</strong>
          <span>PDF, Excel, CSV, or ZIP packages</span>
        </div>
      )}
      <div className="page-header">
        <div>
          <h1>Upload loss runs</h1>
          <p>Build a verified claims ledger from carrier reports.</p>
        </div>
        <div className="upload-totals" aria-label="Upload totals">
          <span>
            <strong>{completed > 0 ? "1.4 s" : "—"}</strong> first claim ready
          </span>
          <span>
            <strong>{((18.2 * completed) / 6).toFixed(1)} s</strong> completed
          </span>
          <span>
            <strong>${accruedCost.toFixed(3)}</strong> total cost
          </span>
        </div>
      </div>
      <div className="upload-layout">
        <section className="upload-primary" aria-label="Upload package">
          <div className="dropzone">
            <div className="dropzone__icon">
              <Upload size={22} aria-hidden="true" />
            </div>
            <div>
              <strong>Drop a carrier package here</strong>
              <p>
                PDF, Excel, CSV, or ZIP packages. Files remain attached to this
                quote.
              </p>
            </div>
            <Button onClick={replayDemo}>Choose files</Button>
            <Button variant="ghost" onClick={replayDemo}>
              <RotateCcw size={15} /> Replay demo
            </Button>
          </div>
          <ManifestList demo={demo} />
        </section>
        <aside className="outcome-panel">
          <div className="outcome-panel__mark">
            <CheckCircle2 size={24} aria-hidden="true" />
          </div>
          <h2>Package processed</h2>
          <p>
            We found <strong>3 loss runs</strong> and{" "}
            <strong>2 other documents</strong>.{" "}
            <strong>1 file could not be read.</strong>
          </p>
          <div className="missing-period">
            <AlertCircle size={18} aria-hidden="true" />
            <p>
              <strong>Policy year 2022 is missing</strong>
              <br />
              Upload it or continue without it.
            </p>
          </div>
          <Button variant="primary" onClick={replayDemo}>
            <Plus size={16} /> Upload more
          </Button>
          <Button onClick={onContinue}>Continue without 2022</Button>
          <p className="outcome-panel__note">
            Continuing records the missing period in the audit history.
          </p>
        </aside>
      </div>
    </div>
  )
}

const claims = [
  [
    "WC-23-0417",
    "2023-09-14",
    "Open",
    "Indemnity",
    "Strain / Lifting / Lower back",
    "18,450.00",
    "22,000.00",
    "31,220.55",
    "9,000.00",
    "2,150.00",
    "82,820.55",
    "Yes",
    0.99,
  ],
  [
    "WC-23-0418",
    "2023-11-02",
    "Closed",
    "Medical only",
    "Laceration / Hand tool / Finger(s)",
    "0.00",
    "0.00",
    "1,340.00",
    "0.00",
    "0.00",
    "1,340.00",
    "No",
    1.0,
  ],
  [
    "WC-24-0421",
    "2024-02-27",
    "Reopened",
    "Indemnity",
    "Cumulative injury / Repetitive motion / Shoulder(s)",
    "9,800.00",
    "14,500.00",
    "12,765.40",
    "6,200.00",
    "3,400.00",
    "46,665.40",
    "Yes",
    0.93,
  ],
  [
    "WC-24-0425",
    "2024-06-19",
    "Closed",
    "Indemnity",
    "Contusion / Fall on same level / Knee",
    "4,120.00",
    "0.00",
    "7,980.10",
    "0.00",
    "410.00",
    "12,510.10",
    "No",
    0.98,
  ],
  [
    "WC-25-0430",
    "2025-01-08",
    "Open",
    "Indemnity",
    "Fracture / Fall from ladder / Ankle",
    "27,300.00",
    "48,000.00",
    "61,200.00",
    "35,000.00",
    "5,100.00",
    "176,600.00",
    "No",
    0.87,
  ],
] as const

const columns = [
  "Claim #",
  "DOI",
  "Status",
  "Type",
  "Nature / Cause / Body",
  "Indemnity paid",
  "Indemnity reserve",
  "Indemnity incurred",
  "Medical paid",
  "Medical reserve",
  "Medical incurred",
  "Expense paid",
  "Expense reserve",
  "Expense incurred",
  "Total paid",
  "Total reserve",
  "Total incurred",
  "Litigated",
  "Confidence",
] as const

const moneyColumns = new Set([5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16])

const parseMoney = (value: string) => Number(value.replaceAll(",", ""))
const formatMoney = (value: number) =>
  value.toLocaleString("en-US", {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  })

const claimRows = claims.map((claim, sourceRow) => {
  const indemnityPaid = parseMoney(claim[5])
  const indemnityReserve = parseMoney(claim[6])
  const medicalPaid = parseMoney(claim[7])
  const medicalReserve = parseMoney(claim[8])
  const expensePaid = parseMoney(claim[9])
  return {
    sourceRow,
    values: [
      claim[0],
      claim[1],
      claim[2],
      claim[3],
      claim[4],
      formatMoney(indemnityPaid),
      formatMoney(indemnityReserve),
      formatMoney(indemnityPaid + indemnityReserve),
      formatMoney(medicalPaid),
      formatMoney(medicalReserve),
      formatMoney(medicalPaid + medicalReserve),
      formatMoney(expensePaid),
      "0.00",
      formatMoney(expensePaid),
      formatMoney(indemnityPaid + medicalPaid + expensePaid),
      formatMoney(indemnityReserve + medicalReserve),
      claim[10],
      claim[11],
      claim[12],
    ],
  }
})

const traceColumnIndices = [0, 1, 2, 3, 5, 6, 8, 9, 11, 16, 17] as const
const traceFieldNames = [
  "Claim number",
  "Date",
  "Status",
  "Type",
  "Indemnity paid",
  "Indemnity reserve",
  "Medical paid",
  "Medical reserve",
  "Expense",
  "Total incurred",
  "Litigated",
] as const
const sourceColumnGeometry = [
  [36, 68],
  [104, 44],
  [148, 38],
  [186, 50],
  [236, 50],
  [286, 50],
  [336, 50],
  [386, 50],
  [436, 42],
  [478, 65],
  [543, 31],
] as const
const printedTraceRows = claimRows.map((claim) =>
  traceColumnIndices.map((column, index) => {
    const value = String(claim.values[column])
    if (index === 1) {
      const [year, month, day] = value.split("-")
      return `${month}/${day}/${year.slice(2)}`
    }
    if (index === 2 || index === 10) return value.toUpperCase()
    return value
  }),
)
const sourceFieldIndexForColumn = (column: number) => {
  const exact = (traceColumnIndices as readonly number[]).indexOf(column)
  if (exact >= 0) return exact
  if (column === 4) return 3
  if (column === 7) return 4
  if (column === 10) return 6
  if (column === 12 || column === 13) return 8
  return 9
}

type Selection = { row: number col: number value: string element: HTMLElement }

function ClaimsTable({
  onSelect,
  selected,
  failedCell,
  onTraceRow,
  traceAll = false,
  onTraceAll,
  tracedRow,
  onRowHover,
  traceLayout = false,
  onCellInteraction,
  routingRow,
}: {
  onSelect: (selection: Selection) => void
  selected: Selection | null
  failedCell?: { row: number col: number }
  onTraceRow?: (row: number, element: HTMLElement) => void
  traceAll?: boolean
  onTraceAll?: () => void
  tracedRow?: number | null
  onRowHover?: (row: number | null) => void
  traceLayout?: boolean
  onCellInteraction?: (row: number, col: number) => void
  routingRow?: number | null
}) {
  const [density, setDensity] = useState<"dense" | "comfortable">("comfortable")
  const [selectedRows, setSelectedRows] = useState<Set<number>>(new Set())
  const [focusedCell, setFocusedCell] = useState({ row: 0, col: 0 })
  const [sort, setSort] = useState<{
    col: number
    direction: "ascending" | "descending"
  }>({
    col: 0,
    direction: "ascending",
  })
  const sortedRows = [...claimRows].sort((a, b) => {
    const left = a.values[sort.col]
    const right = b.values[sort.col]
    const comparison = moneyColumns.has(sort.col)
      ? parseMoney(String(left)) - parseMoney(String(right))
      : String(left).localeCompare(String(right))
    return sort.direction === "ascending" ? comparison : -comparison
  })

  useEffect(() => {
    if (!failedCell) return
    const frame = window.requestAnimationFrame(() =>
      document
        .querySelector<HTMLElement>("[data-failed-cell='true']")
        ?.scrollIntoView({ block: "nearest", inline: "center" }),
    )
    return () => window.cancelAnimationFrame(frame)
  }, [failedCell])

  const toggleSort = (col: number) =>
    setSort((current) => ({
      col,
      direction:
        current.col === col && current.direction === "ascending"
          ? "descending"
          : "ascending",
    }))

  const moveCell = (
    event: KeyboardEvent<HTMLButtonElement>,
    visualRow: number,
    col: number,
    sourceRow: number,
    value: string,
  ) => {
    if (event.key === "Enter") {
      event.preventDefault()
      onSelect({ row: sourceRow, col, value, element: event.currentTarget })
      return
    }
    const deltas: Record<string, [number, number]> = {
      ArrowUp: [-1, 0],
      ArrowDown: [1, 0],
      ArrowLeft: [0, -1],
      ArrowRight: [0, 1],
    }
    const delta = deltas[event.key]
    if (!delta) return
    event.preventDefault()
    const nextRow = Math.max(
      0,
      Math.min(sortedRows.length - 1, visualRow + delta[0]),
    )
    let nextCol = Math.max(0, Math.min(columns.length - 1, col + delta[1]))
    if (traceLayout && delta[1] !== 0) {
      const currentIndex = (traceColumnIndices as readonly number[]).indexOf(
        col,
      )
      const nextIndex = Math.max(
        0,
        Math.min(traceColumnIndices.length - 1, currentIndex + delta[1]),
      )
      nextCol = traceColumnIndices[nextIndex]
    }
    setFocusedCell({ row: nextRow, col: nextCol })
    document
      .querySelector<HTMLButtonElement>(
        `[data-ledger-cell="${nextRow}-${nextCol}"]`,
      )
      ?.focus()
  }

  return (
    <div className={`claims-table-shell claims-table-shell--${density}`}>
      <div className="table-toolbar" aria-label="Claims table controls">
        <span>Claims ledger</span>
        <span className="table-toolbar__selection">
          {selectedRows.size
            ? `${selectedRows.size} selected`
            : "No rows selected"}
        </span>
        {onTraceAll && (
          <>
            <button
              className="trace-all-toggle"
              aria-pressed={traceAll}
              onClick={onTraceAll}
            >
              <FileSearch size={14} />{" "}
              {traceAll ? "Clear all traces" : "Trace all"}
            </button>
            {traceAll && (
              <span className="trace-count">5 rows · 55 values traced</span>
            )}
          </>
        )}
        <div className="density-toggle" aria-label="Table density">
          <Rows3 size={14} aria-hidden="true" />
          <button
            aria-pressed={density === "dense"}
            onClick={() => setDensity("dense")}
          >
            Dense
          </button>
          <button
            aria-pressed={density === "comfortable"}
            onClick={() => setDensity("comfortable")}
          >
            Comfortable
          </button>
        </div>
      </div>
      <div className="claims-table-wrap">
        {traceLayout && <div className="trace-scroll-pad" aria-hidden="true" />}
        <table
          className={`claims-table${traceLayout ? " claims-table--trace" : ""}`}
        >
          {traceLayout ? (
            <thead>
              <tr>
                <th className="select-column">
                  <span className="sr-only">Select</span>
                </th>
                {traceFieldNames.map((field) => (
                  <th key={field}>{field}</th>
                ))}
              </tr>
            </thead>
          ) : (
            <thead>
              <tr className="claims-table__groups">
                <th rowSpan={2} className="select-column">
                  <span className="sr-only">Select</span>
                </th>
                {columns.slice(0, 5).map((column, index) => (
                  <th
                    key={column}
                    rowSpan={2}
                    aria-sort={sort.col === index ? sort.direction : "none"}
                  >
                    <button onClick={() => toggleSort(index)}>{column}</button>
                  </th>
                ))}
                {["Indemnity", "Medical", "Expense", "Total"].map((group) => (
                  <th
                    key={group}
                    colSpan={3}
                    scope="colgroup"
                    className="numeric"
                  >
                    {group}
                  </th>
                ))}
                {columns.slice(17).map((column, offset) => {
                  const index = offset + 17
                  return (
                    <th
                      key={column}
                      rowSpan={2}
                      aria-sort={sort.col === index ? sort.direction : "none"}
                    >
                      <button onClick={() => toggleSort(index)}>
                        {column}
                      </button>
                    </th>
                  )
                })}
              </tr>
              <tr className="claims-table__subgroups">
                {columns.slice(5, 17).map((column, offset) => {
                  const index = offset + 5
                  const label = column.split(" ")[1]
                  return (
                    <th
                      key={column}
                      className="numeric"
                      aria-sort={sort.col === index ? sort.direction : "none"}
                    >
                      <button onClick={() => toggleSort(index)}>{label}</button>
                    </th>
                  )
                })}
              </tr>
            </thead>
          )}
          <tbody>
            {sortedRows.map((claim, visualRow) => (
              <tr
                key={String(claim.values[0])}
                className={`${
                  selectedRows.has(claim.sourceRow) ? "row--selected" : ""
                }${tracedRow === claim.sourceRow ? " row--traced" : ""}${
                  routingRow === claim.sourceRow || traceAll
                    ? " row--routing"
                    : ""
                }`}
                onMouseEnter={() => onRowHover?.(claim.sourceRow)}
                onMouseLeave={() => onRowHover?.(null)}
              >
                <td className="select-column">
                  <div className="row-controls">
                    <input
                      type="checkbox"
                      checked={selectedRows.has(claim.sourceRow)}
                      aria-label={`Select claim ${String(claim.values[0])}`}
                      onChange={(event) =>
                        setSelectedRows((current) => {
                          const next = new Set(current)
                          if (event.target.checked) next.add(claim.sourceRow)
                          else next.delete(claim.sourceRow)
                          return next
                        })
                      }
                    />
                    {onTraceRow && (
                      <button
                        className="row-trace-button"
                        aria-label={`${
                          tracedRow === claim.sourceRow
                            ? "Clear trace for"
                            : "Trace row"
                        } claim ${String(claim.values[0])}`}
                        title="Trace row"
                        onClick={(event) =>
                          onTraceRow(claim.sourceRow, event.currentTarget)
                        }
                      >
                        <FileSearch size={13} />
                      </button>
                    )}
                  </div>
                </td>
                {claim.values.map((value, col) =>
                  traceLayout &&
                  !(traceColumnIndices as readonly number[]).includes(
                    col,
                  ) ? null : (
                    <td
                      key={col}
                      data-failed-cell={
                        failedCell?.row === claim.sourceRow &&
                        failedCell.col === col
                          ? "true"
                          : undefined
                      }
                      className={`${moneyColumns.has(col) ? "numeric " : ""}${
                        selected?.row === claim.sourceRow &&
                        selected.col === col
                          ? "cell--selected"
                          : ""
                      }${
                        failedCell?.row === claim.sourceRow &&
                        failedCell.col === col
                          ? " cell--failed"
                          : ""
                      }`}
                    >
                      <button
                        className="cell-button"
                        data-ledger-cell={`${visualRow}-${col}`}
                        data-ledger-source-row={claim.sourceRow}
                        data-ledger-col={col}
                        tabIndex={
                          focusedCell.row === visualRow &&
                          focusedCell.col === col
                            ? 0
                            : -1
                        }
                        onFocus={() => {
                          setFocusedCell({ row: visualRow, col })
                          onCellInteraction?.(claim.sourceRow, col)
                        }}
                        onMouseEnter={() =>
                          onCellInteraction?.(claim.sourceRow, col)
                        }
                        onKeyDown={(event) =>
                          moveCell(
                            event,
                            visualRow,
                            col,
                            claim.sourceRow,
                            String(value),
                          )
                        }
                        onClick={(event) =>
                          onSelect({
                            row: claim.sourceRow,
                            col,
                            value: String(value),
                            element: event.currentTarget,
                          })
                        }
                        aria-label={`${columns[col]}, ${String(value)}, show source`}
                      >
                        {col === 18 ? (
                          <ConfidenceMeter value={Number(value)} />
                        ) : (
                          String(value)
                        )}
                      </button>
                    </td>
                  ),
                )}
              </tr>
            ))}
          </tbody>
        </table>
        {traceLayout && <div className="trace-scroll-pad" aria-hidden="true" />}
      </div>
    </div>
  )
}

function SourceViewer({
  active,
  onRegionFocus,
  initialPage = 2,
  variant = "amtrust",
  pulse = false,
  activeRegionIndex = 0,
  traceRowIndex = null,
  onRegionsReady,
  onRegionInteraction,
}: {
  active: boolean
  onRegionFocus: (element: HTMLElement, index: number) => void
  initialPage?: number
  variant?: "amtrust" | "pie"
  pulse?: boolean
  activeRegionIndex?: number
  traceRowIndex?: number | null
  onRegionsReady?: (elements: HTMLElement[]) => void
  onRegionInteraction?: (index: number) => void
}) {
  const regions = useRef<Array<HTMLButtonElement | null>>([])
  const rowBands = useRef<Array<HTMLSpanElement | null>>([])
  const canvasRef = useRef<HTMLDivElement>(null)
  const [page, setPage] = useState(initialPage)
  const [zoomMode, setZoomMode] = useState<"fit" | "100" | "125">("fit")
  const [fitScale, setFitScale] = useState(0.78)
  const reducedMotion = usePrefersReducedMotion()
  const pageCount = variant === "pie" ? 3 : 6
  useEffect(() => {
    const target =
      traceRowIndex !== null
        ? rowBands.current[traceRowIndex]
        : regions.current[activeRegionIndex]
    if (!active || !target) return
    const frame = window.requestAnimationFrame(() => {
      target.scrollIntoView({
        behavior: reducedMotion ? "auto" : "smooth",
        block: "center",
        inline: "center",
      })
      const activeRegion = regions.current[activeRegionIndex]
      if (activeRegion) onRegionFocus(activeRegion, activeRegionIndex)
    })
    return () => window.cancelAnimationFrame(frame)
  }, [active, activeRegionIndex, onRegionFocus, reducedMotion, traceRowIndex])
  useEffect(() => {
    const canvas = canvasRef.current
    if (!canvas) return
    const update = () =>
      setFitScale(Math.min(1, Math.max(0.3, (canvas.clientWidth - 48) / 610)))
    update()
    const observer = new ResizeObserver(update)
    observer.observe(canvas)
    return () => observer.disconnect()
  }, [])
  useEffect(() => {
    onRegionsReady?.(
      regions.current.filter((region): region is HTMLButtonElement =>
        Boolean(region),
      ),
    )
  }, [onRegionsReady, variant])
  const handleKey = (
    event: KeyboardEvent<HTMLButtonElement>,
    index: number,
  ) => {
    if (
      event.key !== "ArrowDown" &&
      event.key !== "ArrowRight" &&
      event.key !== "ArrowUp" &&
      event.key !== "ArrowLeft"
    )
      return
    event.preventDefault()
    const next =
      event.key === "ArrowDown" || event.key === "ArrowRight"
        ? (index + 1) % regions.current.length
        : (index + regions.current.length - 1) % regions.current.length
    regions.current[next]?.focus()
  }
  return (
    <div className="source-viewer">
      <div className="thumbnail-strip" aria-label="Document pages">
        {Array.from({ length: pageCount }, (_, index) => index + 1).map(
          (pageNumber) => (
            <button
              className={`thumbnail${
                page === pageNumber ? " thumbnail--active" : ""
              }`}
              key={pageNumber}
              aria-label={`Page ${pageNumber}`}
              onClick={() => setPage(pageNumber)}
            >
              <span className="thumbnail__paper">
                <span />
                <span />
                <span />
              </span>
              <span>{pageNumber}</span>
            </button>
          ),
        )}
      </div>
      <div className="document-canvas" ref={canvasRef}>
        <div className="document-toolbar">
          <div className="page-controls">
            <button
              aria-label="Previous page"
              disabled={page === 1}
              onClick={() => setPage((current) => Math.max(1, current - 1))}
            >
              <ChevronLeft size={14} aria-hidden="true" />
            </button>
            <span>
              Page {page} of {pageCount}
            </span>
            <button
              aria-label="Next page"
              disabled={page === pageCount}
              onClick={() =>
                setPage((current) => Math.min(pageCount, current + 1))
              }
            >
              <ChevronRight size={14} aria-hidden="true" />
            </button>
          </div>
          <div className="zoom-control" aria-label="Page zoom">
            <button
              aria-pressed={zoomMode === "fit"}
              onClick={() => setZoomMode("fit")}
            >
              Fit width
            </button>
            <button
              aria-pressed={zoomMode === "100"}
              onClick={() => setZoomMode("100")}
            >
              100%
            </button>
            <button
              aria-pressed={zoomMode === "125"}
              onClick={() => setZoomMode("125")}
            >
              125%
            </button>
          </div>
        </div>
        <article
          className={`paper${zoomMode === "fit" ? " paper--fit" : ""}${
            zoomMode === "125" ? " paper--125" : ""
          }${variant === "pie" ? " paper--pie" : ""}`}
          style={
            {
              "--fit-scale": fitScale,
            } as CSSProperties
          }
          aria-label={`Synthetic ${
            variant === "pie" ? "Pie Insurance" : "AmTrust"
          } loss run page ${page}`}
        >
          {variant === "pie" ? (
            <>
              <header className="paper__header paper__header--pie">
                <div className="paper__logo paper__logo--pie">PIE</div>
                <div>
                  <strong>Pie Insurance</strong>
                  <span>Loss Run</span>
                </div>
                <div className="paper__meta">
                  Report date
                  <br />
                  <strong>12/31/2024</strong>
                </div>
              </header>
              <section className="paper__insured">
                <div>
                  <span>Insured</span>
                  <strong>Acme Fabrication Inc</strong>
                </div>
                <div>
                  <span>Policy</span>
                  <strong>PIE-WC-22804</strong>
                </div>
                <div>
                  <span>Valuation</span>
                  <strong>12/31/2024</strong>
                </div>
              </section>
              <table className="paper-table paper-table--pie">
                <thead>
                  <tr>
                    <th>Claim No</th>
                    <th>Date of Loss</th>
                    <th>Claimant</th>
                    <th>Status</th>
                    <th>Med Paid</th>
                    <th>Ind Paid</th>
                    <th>Reserves</th>
                    <th>Total</th>
                  </tr>
                </thead>
                <tbody>
                  <tr>
                    <td>WC-23-0088</td>
                    <td>08/14/23</td>
                    <td>Worker 0088</td>
                    <td>OPEN</td>
                    <td>9,300.00</td>
                    <td>12,500.00</td>
                    <td>8,900.00</td>
                    <td>30,700.00</td>
                  </tr>
                  <tr>
                    <td>WC-23-0091</td>
                    <td>10/02/23</td>
                    <td>Worker 0091</td>
                    <td>CLOSED</td>
                    <td>4,500.00</td>
                    <td>2,000.00</td>
                    <td>4,000.00</td>
                    <td>10,500.00</td>
                  </tr>
                  <tr>
                    <td colSpan={4}>2023 PERIOD SUBTOTAL</td>
                    <td>13,800.00</td>
                    <td>14,500.00</td>
                    <td>12,900.00</td>
                    <td>41,200.00</td>
                  </tr>
                </tbody>
              </table>
              <p className="paper__footer">
                Pie Insurance · Loss activity reported through the valuation
                date.
              </p>
            </>
          ) : (
            <>
              <header className="paper__header">
                <div className="paper__logo">AMTRUST</div>
                <div>
                  <strong>AmTrust North America</strong>
                  <span>Loss Run Report</span>
                </div>
                <div className="paper__meta">
                  Valuation date
                  <br />
                  <strong>08/31/2026</strong>
                </div>
              </header>
              <section className="paper__insured">
                <div>
                  <span>Insured</span>
                  <strong>Acme Fabrication Inc</strong>
                </div>
                <div>
                  <span>Policy</span>
                  <strong>WC-55-11923</strong>
                </div>
                <div>
                  <span>Policy period</span>
                  <strong>07/01/2023 – 07/01/2024</strong>
                </div>
              </section>
              <table className="paper-table paper-table--lineage-map">
                <colgroup>
                  {sourceColumnGeometry.map(([_, width], index) => (
                    <col key={index} style={{ width }} />
                  ))}
                </colgroup>
                <thead>
                  <tr>
                    <th>Claim number</th>
                    <th>Date</th>
                    <th>Status</th>
                    <th>Type</th>
                    <th>Ind paid</th>
                    <th>Ind reserve</th>
                    <th>Med paid</th>
                    <th>Med reserve</th>
                    <th>Exp paid</th>
                    <th>Total incurred</th>
                    <th>Lit.</th>
                  </tr>
                </thead>
                <tbody>
                  {printedTraceRows.map((row, rowIndex) => (
                    <tr key={rowIndex}>
                      {row.map((value, index) => (
                        <td key={index}>{value}</td>
                      ))}
                    </tr>
                  ))}
                  <tr>
                    <td colSpan={8}>POLICY PERIOD SUBTOTAL</td>
                    <td>11,060.00</td>
                    <td>83,830.55</td>
                    <td>—</td>
                  </tr>
                </tbody>
              </table>
              <p className="paper__footer">
                This report contains claim information as reported through the
                valuation date shown above.
              </p>
            </>
          )}
          {variant === "amtrust" &&
            printedTraceRows.map((_, rowIndex) => (
              <span
                key={`band-${rowIndex}`}
                ref={(element) => {
                  rowBands.current[rowIndex] = element
                }}
                className={`source-row-band${
                  traceRowIndex === rowIndex ? " source-row-band--active" : ""
                }`}
                style={
                  {
                    "--source-row-top": `${171 + rowIndex * 21}px`,
                  } as CSSProperties
                }
              />
            ))}
          {(variant === "pie"
            ? [
                {
                  label: "Printed subtotal 41,200.00",
                  className: "source-region--pie-subtotal",
                },
                {
                  label: "Medical paid 9,300.00",
                  className: "source-region--pie-medical",
                },
                {
                  label: "Claim WC-23-0088",
                  className: "source-region--pie-claim",
                },
              ]
            : printedTraceRows.flatMap((row, rowIndex) =>
                row.map((raw, fieldIndex) => ({
                  label: `${traceFieldNames[fieldIndex]} ${raw}`,
                  className: "source-region--cell",
                  raw,
                  style: {
                    "--region-left": `${sourceColumnGeometry[fieldIndex][0]}px`,
                    "--region-width": `${sourceColumnGeometry[fieldIndex][1]}px`,
                    "--region-top": `${171 + rowIndex * 21}px`,
                  } as CSSProperties,
                })),
              )
          ).map((region, index) => (
            <button
              key={`${region.label}-${index}`}
              ref={(element) => {
                regions.current[index] = element
              }}
              style={"style" in region ? region.style : undefined}
              className={`source-region ${region.className}${
                active && index === activeRegionIndex
                  ? " source-region--active"
                  : ""
              }${
                variant === "amtrust" &&
                traceRowIndex !== null &&
                Math.floor(index / traceColumnIndices.length) === traceRowIndex
                  ? " source-region--row-active"
                  : ""
              }${
                pulse && index === activeRegionIndex
                  ? " source-region--pulse"
                  : ""
              }`}
              aria-label={region.label}
              onFocus={(event) => {
                onRegionFocus(event.currentTarget, index)
                onRegionInteraction?.(index)
              }}
              onMouseEnter={() => onRegionInteraction?.(index)}
              onMouseLeave={() => onRegionInteraction?.(-1)}
              onKeyDown={(event) => handleKey(event, index)}
            />
          ))}
        </article>
      </div>
    </div>
  )
}

function LineagePanel({
  selection,
  onClose,
  traceRow,
  onTraceRow,
  collapsed = false,
  onExpand,
}: {
  selection: Selection
  onClose: () => void
  traceRow: boolean
  onTraceRow: () => void
  collapsed?: boolean
  onExpand?: () => void
}) {
  const isDefault = selection.row === 0 && selection.col === 8
  const confidence = Number(claims[selection.row][12])
  const displayValue =
    selection.col === 18 ? confidence.toFixed(2) : selection.value
  if (collapsed) {
    return (
      <aside
        className="lineage-panel lineage-panel--collapsed"
        aria-label="11 of 11 traced, lowest confidence 0.93"
      >
        <button
          className="collapsed-lineage__expand"
          onClick={onExpand}
          aria-label="Expand lineage panel"
        >
          <ChevronLeft size={16} />
          <span>11 of 11 traced · lowest 0.93</span>
        </button>
        <button className="collapsed-lineage__clear" onClick={onTraceRow}>
          Clear
        </button>
      </aside>
    )
  }
  return (
    <aside
      className="lineage-panel"
      aria-label="Value lineage"
      aria-live="polite"
    >
      <div className="lineage-panel__header">
        <div>
          <span className="panel-kicker">Value lineage</span>
          <h2>{columns[selection.col]}</h2>
        </div>
        <Button
          variant="ghost"
          className="icon-button"
          onClick={onClose}
          aria-label="Close lineage panel"
        >
          <X size={18} />
        </Button>
      </div>
      <div className="lineage-value">
        <span>Verified value</span>
        <strong>{displayValue}</strong>
      </div>
      <Button
        className="trace-row-panel-button"
        onClick={onTraceRow}
        aria-pressed={traceRow}
      >
        <FileSearch size={15} />
        {traceRow ? "Clear row trace" : "Trace row"}
      </Button>
      {traceRow && (
        <div className="trace-summary">
          <CheckCircle2 size={16} />
          <p>
            <strong>11 of 11 values traced to page 2</strong>
            <span>Lowest confidence 0.93 (Medical reserve)</span>
          </p>
        </div>
      )}
      <dl className="lineage-details">
        <div>
          <dt>Raw text</dt>
          <dd>{displayValue}</dd>
        </div>
        <div>
          <dt>Confidence</dt>
          <dd>
            <ConfidenceMeter value={confidence} />
          </dd>
        </div>
        <div>
          <dt>Source</dt>
          <dd>Page 2 · region 6.10,3.41 → 6.90,3.58</dd>
        </div>
        <div>
          <dt>Format</dt>
          <dd>amtrust-pdf@2</dd>
        </div>
        <div>
          <dt>Extractor</dt>
          <dd>replay (deterministic, no model)</dd>
        </div>
      </dl>
      <section className="lineage-rules">
        <h3>Rules that touched this value</h3>
        <div className="lineage-rule">
          <CheckCircle2 size={16} />
          <p>
            <strong>R-02</strong> Incurred = paid + reserve <span>Passed</span>
          </p>
        </div>
        <div className="lineage-rule lineage-rule--info">
          <FileSearch size={16} />
          <p>
            <strong>R-20</strong> Total = components{" "}
            <span>
              {isDefault
                ? "Derived, not printed"
                : "Checked against printed source"}
            </span>
          </p>
        </div>
      </section>
      <div className="audit-note">
        <Archive size={16} aria-hidden="true" />
        <p>
          Opening this value is recorded locally. Any change will create a new
          revision; the source record is never overwritten.
        </p>
      </div>
    </aside>
  )
}

function RuleOutcomeList() {
  const [open, setOpen] = useState(true)
  const [queued, setQueued] = useState(false)
  const [showToast, setShowToast] = useState(false)
  const queueForReview = () => {
    setQueued(true)
    setShowToast(true)
    window.setTimeout(() => setShowToast(false), 3200)
  }
  const groups = [
    {
      heading: "Hard",
      outcomes: [
        {
          id: "R-02",
          severity: "Passed",
          message: "Incurred arithmetic",
          claim: "All claims",
        },
        {
          id: "R-10",
          severity: "Passed",
          message: "Printed subtotal matches extracted rows",
          claim: "2023–24",
        },
      ],
    },
    {
      heading: "Soft",
      outcomes: [
        {
          id: "R-04",
          severity: "Needs review",
          message: "Closed claim carries a reserve",
          claim: "WC-24-0425",
          actions: true,
        },
      ],
    },
    {
      heading: "Flags",
      outcomes: [
        {
          id: "R-24",
          severity: "Flag",
          message: "Large loss: total incurred ≥ $100,000",
          claim: "WC-25-0430",
        },
      ],
    },
    {
      heading: "Informational",
      outcomes: [
        {
          id: "R-20",
          severity: "Information",
          message: "Total derived, not printed",
          claim: "All claims",
        },
      ],
    },
  ]
  return (
    <section className="rules-panel">
      {showToast && (
        <div className="action-toast" role="status">
          <CheckCircle2 size={16} /> Queued for review · nothing is blocked
        </div>
      )}
      <button
        className="rules-panel__toggle"
        onClick={() => setOpen(!open)}
        aria-expanded={open}
      >
        <span>
          {open ? <ChevronDown size={16} /> : <ChevronRight size={16} />}{" "}
          Validation outcomes
        </span>
        <span>
          <CheckCircle2 size={14} /> 2 passed <AlertCircle size={14} /> 2 need
          attention
        </span>
      </button>
      {open && (
        <div className="rule-groups">
          {groups.map((group) => (
            <section className="rule-group" key={group.heading}>
              <h3>{group.heading}</h3>
              {group.heading === "Soft" && (
                <p className="rule-group__note">
                  Soft findings don't block the document; queue one if you want
                  a second look.
                </p>
              )}
              <div className="rule-list" role="list">
                {group.outcomes.map((outcome) => (
                  <div className="rule-row" role="listitem" key={outcome.id}>
                    <strong className="rule-row__id">{outcome.id}</strong>
                    <span
                      className={`severity-badge severity-badge--${outcome.severity.toLowerCase().replace(" ", "-")}`}
                    >
                      {outcome.severity === "Passed" ? (
                        <CheckCircle2 size={13} />
                      ) : outcome.severity === "Information" ? (
                        <FileSearch size={13} />
                      ) : (
                        <AlertCircle size={13} />
                      )}
                      {outcome.severity}
                    </span>
                    <span className="rule-row__message">{outcome.message}</span>
                    <span className="rule-row__claim">{outcome.claim}</span>
                    <span className="rule-row__actions">
                      {outcome.actions && (
                        <>
                          <Button variant="ghost">Open in source</Button>
                          <Button disabled={queued} onClick={queueForReview}>
                            {queued ? <Check size={14} /> : <Send size={14} />}
                            {queued ? "Queued" : "Queue for review"}
                          </Button>
                        </>
                      )}
                    </span>
                  </div>
                ))}
              </div>
            </section>
          ))}
        </div>
      )}
    </section>
  )
}

function DocumentScreen() {
  const [selection, setSelection] = useState<Selection | null>(null)
  const [leftWidth, setLeftWidth] = useState(45)
  const [regionElement, setRegionElement] = useState<HTMLElement | null>(null)
  const [regionElements, setRegionElements] = useState<HTMLElement[]>([])
  const reducedMotion = usePrefersReducedMotion()
  const [lineagePhase, setLineagePhase] =
    useState<"idle" | "focus" | "pulse" | "thread" | "panel">("idle")
  const [traceMode, setTraceMode] = useState<"row" | "all" | null>(null)
  const [hoveredRow, setHoveredRow] = useState<number | null>(null)
  const [activeThread, setActiveThread] = useState<string | null>(null)
  const [rowPanelExpanded, setRowPanelExpanded] = useState(false)
  const workspaceRef = useRef<HTMLDivElement>(null)
  const [lines, setLines] = useState<Array<{
    x1: number
    y1: number
    x2: number
    y2: number
    row: number
    fieldIndex: number
    field: string
    raw: string
  }>>([])
  const setActiveRegion = useCallback((element: HTMLElement) => {
    setRegionElement((current) => (current === element ? current : element))
  }, [])
  const selectedFieldIndex = selection
    ? sourceFieldIndexForColumn(selection.col)
    : 0
  const activeSourceIndex = selection
    ? selection.row * traceColumnIndices.length + selectedFieldIndex
    : 0

  const closeLineage = useCallback(() => {
    if (!selection) return
    const element = selection.element
    setSelection(null)
    setTraceMode(null)
    setLineagePhase("idle")
    setLines([])
    setRowPanelExpanded(false)
    setTimeout(() => element.focus(), 0)
  }, [selection])

  const selectCell = useCallback((next: Selection) => {
    setTraceMode(null)
    setRowPanelExpanded(false)
    setSelection(next)
  }, [])

  useEffect(() => {
    if (!selection) return
    if (reducedMotion) {
      setLineagePhase("panel")
      return
    }
    setLineagePhase("focus")
    const pulseTimer = window.setTimeout(() => setLineagePhase("pulse"), 250)
    const threadTimer = window.setTimeout(() => setLineagePhase("thread"), 650)
    const panelTimer = window.setTimeout(() => setLineagePhase("panel"), 900)
    return () => {
      window.clearTimeout(pulseTimer)
      window.clearTimeout(threadTimer)
      window.clearTimeout(panelTimer)
    }
  }, [selection, reducedMotion])

  useEffect(() => {
    if (!selection) return
    const frame = window.requestAnimationFrame(() =>
      selection.element.scrollIntoView({ block: "nearest", inline: "nearest" }),
    )
    return () => window.cancelAnimationFrame(frame)
  }, [selection])

  useEffect(() => {
    const update = () => {
      if (!selection || !regionElements.length || !workspaceRef.current)
        return setLines([])
      const base = workspaceRef.current.getBoundingClientRect()
      let pairs: Array<{
        element: HTMLElement
        region: HTMLElement
        row: number
        fieldIndex: number
      }> = []
      if (traceMode === "row") {
        pairs = traceColumnIndices.flatMap((column, fieldIndex) => {
          const element = document.querySelector<HTMLElement>(
            `[data-ledger-source-row="${selection.row}"][data-ledger-col="${column}"]`,
          )
          const region =
            regionElements[
              selection.row * traceColumnIndices.length + fieldIndex
            ]
          return element && region
            ? [{ element, region, row: selection.row, fieldIndex }]
            : []
        })
      } else if (traceMode === "all") {
        pairs = claimRows
          .flatMap((claim) =>
            traceColumnIndices.flatMap((column, fieldIndex) => {
              const element = document.querySelector<HTMLElement>(
                `[data-ledger-source-row="${claim.sourceRow}"][data-ledger-col="${column}"]`,
              )
              const region =
                regionElements[
                  claim.sourceRow * traceColumnIndices.length + fieldIndex
                ]
              return element && region
                ? [
                    {
                      element,
                      region,
                      row: claim.sourceRow,
                      fieldIndex,
                    },
                  ]
                : []
            }),
          )
          .slice(0, 60)
      } else {
        const region =
          regionElements[
            selection.row * traceColumnIndices.length + selectedFieldIndex
          ] ?? regionElement
        if (region)
          pairs = [
            {
              element: selection.element,
              region,
              row: selection.row,
              fieldIndex: selectedFieldIndex,
            },
          ]
      }
      setLines(
        pairs
          .map(({ element, region, row, fieldIndex }) => {
            const table =
              element.closest("table")?.getBoundingClientRect() ??
              element.getBoundingClientRect()
            const ledgerRow =
              element.closest("tr")?.getBoundingClientRect() ??
              element.getBoundingClientRect()
            const target = region.getBoundingClientRect()
            return {
              x1: table.left - base.left,
              y1: ledgerRow.top - base.top + ledgerRow.height / 2,
              x2: target.right - base.left,
              y2: target.top - base.top + target.height / 2,
              row,
              fieldIndex,
              field: traceFieldNames[fieldIndex],
              raw: printedTraceRows[row][fieldIndex],
            }
          })
          .sort(
            (a, b) =>
              Math.abs(a.x1 - a.x2) +
              Math.abs(a.y1 - a.y2) -
              (Math.abs(b.x1 - b.x2) + Math.abs(b.y1 - b.y2)),
          ),
      )
    }
    update()
    window.addEventListener("resize", update)
    return () => window.removeEventListener("resize", update)
  }, [
    selection,
    selectedFieldIndex,
    regionElement,
    regionElements,
    leftWidth,
    lineagePhase,
    traceMode,
  ])

  useEffect(() => {
    if (traceMode !== "row" || !selection) return
    let alignmentTimer = 0
    let correctionFrame = 0
    const frame = window.requestAnimationFrame(() => {
      const ledgerRow = document
        .querySelector<HTMLElement>(
          `[data-ledger-source-row="${selection.row}"]`,
        )
        ?.closest("tr")
      const sourceBand = document.querySelector<HTMLElement>(
        ".source-row-band--active",
      )
      const sourceCanvas =
        workspaceRef.current?.querySelector<HTMLElement>(".document-canvas")
      const ledgerScroll =
        workspaceRef.current?.querySelector<HTMLElement>(".claims-table-wrap")
      sourceBand?.scrollIntoView({
        behavior: reducedMotion ? "auto" : "smooth",
        block: "center",
        inline: "nearest",
      })
      alignmentTimer = window.setTimeout(
        () => {
          if (!ledgerRow || !sourceBand || !sourceCanvas || !ledgerScroll)
            return
          const ledgerRect = ledgerRow.getBoundingClientRect()
          const sourceRect = sourceBand.getBoundingClientRect()
          const delta =
            ledgerRect.top +
            ledgerRect.height / 2 -
            (sourceRect.top + sourceRect.height / 2)
          ledgerScroll.scrollBy({
            top: delta,
            behavior: reducedMotion ? "auto" : "smooth",
          })
          correctionFrame = window.requestAnimationFrame(() => {
            const correctedLedger = ledgerRow.getBoundingClientRect()
            const correctedSource = sourceBand.getBoundingClientRect()
            ledgerScroll.scrollTop +=
              correctedLedger.top +
              correctedLedger.height / 2 -
              (correctedSource.top + correctedSource.height / 2)
          })
        },
        reducedMotion ? 0 : 360,
      )
    })
    return () => {
      window.cancelAnimationFrame(frame)
      window.cancelAnimationFrame(correctionFrame)
      window.clearTimeout(alignmentTimer)
    }
  }, [traceMode, selection, reducedMotion, lineagePhase])

  useEffect(() => {
    if (traceMode !== "row" || !selection) return
    let timer = 0
    const realign = () => {
      window.clearTimeout(timer)
      timer = window.setTimeout(() => {
        const ledgerRow = document
          .querySelector<HTMLElement>(
            `[data-ledger-source-row="${selection.row}"]`,
          )
          ?.closest("tr")
        const sourceBand = document.querySelector<HTMLElement>(
          ".source-row-band--active",
        )
        const ledgerScroll =
          workspaceRef.current?.querySelector<HTMLElement>(".claims-table-wrap")
        if (!ledgerRow || !sourceBand || !ledgerScroll) return
        const ledgerRect = ledgerRow.getBoundingClientRect()
        const sourceRect = sourceBand.getBoundingClientRect()
        ledgerScroll.scrollTop +=
          ledgerRect.top +
          ledgerRect.height / 2 -
          (sourceRect.top + sourceRect.height / 2)
      }, 450)
    }
    window.addEventListener("resize", realign)
    return () => {
      window.removeEventListener("resize", realign)
      window.clearTimeout(timer)
    }
  }, [traceMode, selection])

  useEffect(() => {
    const close = (event: globalThis.KeyboardEvent) => {
      if (event.key === "Escape" && selection) {
        closeLineage()
      }
    }
    document.addEventListener("keydown", close)
    return () => document.removeEventListener("keydown", close)
  }, [selection, closeLineage])

  const toggleRowTrace = (row = selection?.row, element?: HTMLElement) => {
    if (row === undefined) return
    if (traceMode === "row" && selection?.row === row) {
      setTraceMode(null)
      setRowPanelExpanded(false)
      return
    }
    if (!selection || selection.row !== row) {
      const firstCell = document.querySelector<HTMLElement>(
        `[data-ledger-source-row="${row}"][data-ledger-col="0"]`,
      )
      setSelection({
        row,
        col: 0,
        value: String(claimRows[row].values[0]),
        element: firstCell ?? element ?? document.body,
      })
    }
    setTraceMode("row")
    setRowPanelExpanded(false)
  }

  const toggleAllTraces = () => {
    if (traceMode === "all") {
      setTraceMode(null)
      return
    }
    if (!selection) {
      const firstCell = document.querySelector<HTMLElement>(
        '[data-ledger-source-row="0"][data-ledger-col="0"]',
      )
      if (firstCell)
        setSelection({
          row: 0,
          col: 0,
          value: String(claimRows[0].values[0]),
          element: firstCell,
        })
    }
    setTraceMode("all")
  }

  const beginResize = (event: ReactMouseEvent) => {
    event.preventDefault()
    const move = (moveEvent: globalThis.MouseEvent) => {
      if (!workspaceRef.current) return
      const bounds = workspaceRef.current.getBoundingClientRect()
      setLeftWidth(
        Math.min(
          58,
          Math.max(
            32,
            ((moveEvent.clientX - bounds.left) / bounds.width) * 100,
          ),
        ),
      )
    }
    const up = () => {
      document.removeEventListener("mousemove", move)
      document.removeEventListener("mouseup", up)
    }
    document.addEventListener("mousemove", move)
    document.addEventListener("mouseup", up)
  }

  return (
    <div className="document-screen">
      <header className="document-header">
        <div className="document-title-row">
          <Button
            variant="ghost"
            className="compact-icon"
            aria-label="Collapse file list"
          >
            <PanelLeftClose size={17} />
          </Button>
          <div>
            <h1>amtrust-loss-run-2026.pdf</h1>
            <p className="document-trust-line">
              47 of 47 values traced to the page · 26 rules run · 2 need
              attention · 0 model calls
            </p>
            <div className="document-subline">
              <span>AmTrust North America</span>
              <span>Acme Fabrication Inc</span>
              <span>Valued 2026-08-31</span>
              <span>Revision 1</span>
            </div>
          </div>
          <RoutingBadge>
            Format memory hit · AmTrust v2 · 0 model calls
          </RoutingBadge>
          <div className="document-header__spacer" />
          <CostLine>$0.030 · 6 pages · 0 model calls</CostLine>
          <div className="reporting-toggle" aria-label="Reporting view">
            <button aria-pressed="true">As reported</button>
            <button disabled title="No overrides">
              As adjusted
            </button>
            <span>No overrides</span>
          </div>
        </div>
      </header>
      <div
        className={`workspace${
          lineagePhase === "panel" ? " workspace--lineage" : ""
        }${traceMode === "row" ? " workspace--row-trace" : ""}`}
        ref={workspaceRef}
      >
        <section
          className="source-pane"
          style={{ "--left-width": `${leftWidth}%` } as CSSProperties}
        >
          <div className="pane-title">
            <span>Source document</span>
            <button>
              <Search size={15} /> Find
            </button>
          </div>
          <SourceViewer
            active={Boolean(selection)}
            onRegionFocus={setActiveRegion}
            pulse={lineagePhase === "pulse"}
            activeRegionIndex={activeSourceIndex}
            traceRowIndex={traceMode === "row" ? selection?.row : null}
            onRegionsReady={setRegionElements}
            onRegionInteraction={(index) => {
              if (index < 0) return
              setActiveThread(
                `${Math.floor(index / traceColumnIndices.length)}-${index % traceColumnIndices.length}`,
              )
            }}
          />
        </section>
        <button
          className="resizer"
          onMouseDown={beginResize}
          aria-label="Resize source and ledger panes"
        >
          <GripVertical size={16} />
        </button>
        <section className="ledger-pane">
          <div
            className="period-tabs"
            role="tablist"
            aria-label="Policy periods"
          >
            {["2023–24", "2024–25", "2025–26"].map((period, index) => (
              <button role="tab" aria-selected={index === 0} key={period}>
                {period}
              </button>
            ))}
            <span className="period-tabs__spacer" />
            <StatusBadge status="Verified" />
            <span className="claim-count">5 claims</span>
          </div>
          <div className="ledger-content">
            <ClaimsTable
              selected={selection}
              onSelect={selectCell}
              onTraceRow={(row, element) => toggleRowTrace(row, element)}
              tracedRow={traceMode === "row" ? selection?.row : null}
              traceAll={traceMode === "all"}
              onTraceAll={toggleAllTraces}
              onRowHover={setHoveredRow}
              traceLayout={traceMode === "row"}
              onCellInteraction={(row, col) => {
                const fieldIndex =
                  (traceColumnIndices as readonly number[]).indexOf(col)
                if (fieldIndex >= 0) setActiveThread(`${row}-${fieldIndex}`)
              }}
              routingRow={selection?.row}
            />
            <div className="totals-strip">
              <span>Period reconciliation</span>
              <span>
                Printed subtotal <strong>83,830.55</strong>
              </span>
              <span>
                Extracted <strong>83,830.55</strong>{" "}
                <CheckCircle2 size={15} aria-label="Matched" />
              </span>
            </div>
            <RuleOutcomeList />
          </div>
        </section>
        {lines.length > 0 &&
          (lineagePhase === "thread" || lineagePhase === "panel") && (
            <svg className="lineage-thread" aria-hidden="true">
              {lines.map((line, index) => (
                <g
                  key={`${line.row}-${line.fieldIndex}`}
                  className={`${
                    traceMode === "all" ? "thread-group--all" : ""
                  }${hoveredRow === line.row ? " thread-group--hovered" : ""}${
                    activeThread === `${line.row}-${line.fieldIndex}`
                      ? " thread-group--active"
                      : ""
                  }`}
                  style={
                    {
                      "--thread-delay": `${index * (traceMode === "row" ? 40 : 12)}ms`,
                    } as CSSProperties
                  }
                >
                  <path
                    d={`M ${line.x1} ${line.y1} C ${line.x1 - 44} ${line.y1}, ${line.x2 + 44} ${line.y2}, ${line.x2} ${line.y2}`}
                    onMouseEnter={() =>
                      setActiveThread(`${line.row}-${line.fieldIndex}`)
                    }
                    onMouseLeave={() => setActiveThread(null)}
                  />
                  <circle cx={line.x2} cy={line.y2} r="2.5" />
                  {activeThread === `${line.row}-${line.fieldIndex}` && (
                    <>
                      <text
                        className="thread-end-label"
                        x={line.x1 + 4}
                        y={line.y1 - 4}
                      >
                        {line.field}
                      </text>
                      <text
                        className="thread-end-label"
                        textAnchor="end"
                        x={line.x2 - 4}
                        y={line.y2 - 4}
                      >
                        {line.raw}
                      </text>
                    </>
                  )}
                </g>
              ))}
              {lines
                .filter(
                  (line, index, all) =>
                    all.findIndex((candidate) => candidate.row === line.row) ===
                    index,
                )
                .map((line) => (
                  <circle
                    key={`origin-${line.row}`}
                    className="thread-origin"
                    cx={line.x1}
                    cy={line.y1}
                    r="3"
                  />
                ))}
            </svg>
          )}
        {selection && lineagePhase === "panel" && (
          <LineagePanel
            selection={selection}
            onClose={closeLineage}
            traceRow={traceMode === "row"}
            onTraceRow={() => toggleRowTrace()}
            collapsed={traceMode === "row" && !rowPanelExpanded}
            onExpand={() => setRowPanelExpanded(true)}
          />
        )}
      </div>
    </div>
  )
}

function ScreenHeader({
  title,
  description,
  actions,
}: {
  title: string
  description: string
  actions?: ReactNode
}) {
  return (
    <header className="screen-header">
      <div>
        <h1>{title}</h1>
        <p>{description}</p>
      </div>
      {actions && <div className="screen-header__actions">{actions}</div>}
    </header>
  )
}

function ReviewQueueScreen() {
  const [tab, setTab] = useState<"documents" | "formats">("documents")
  const [detail, setDetail] = useState(false)
  const [detailTab, setDetailTab] = useState<"review" | "audit">("review")
  const [newValue, setNewValue] = useState("9,200.00")
  const [rationale, setRationale] = useState("")
  const [saved, setSaved] = useState(false)
  const [errors, setErrors] = useState<{ value?: string rationale?: string }>(
    {},
  )

  const save = () => {
    const nextErrors = {
      value: newValue.trim()
        ? undefined
        : "Enter the corrected medical paid value.",
      rationale: rationale.trim()
        ? undefined
        : "Explain why this override is required.",
    }
    setErrors(nextErrors)
    if (nextErrors.value || nextErrors.rationale) return
    setSaved(true)
    setDetailTab("audit")
    window.setTimeout(() => setSaved(false), 3200)
  }

  if (detail) {
    return (
      <div className="review-detail">
        {saved && (
          <div className="action-toast" role="status">
            <CheckCircle2 size={16} /> Override saved
          </div>
        )}
        <div className="review-detail__header">
          <Button variant="ghost" onClick={() => setDetail(false)}>
            <ChevronLeft size={16} /> Review queue
          </Button>
          <div>
            <h1>pie-loss-run-2024.pdf</h1>
            <p>2 issues · Pie Insurance · opened 14:03</p>
          </div>
          <RoutingBadge review>
            Needs review · Pie v1 · 0 model calls
          </RoutingBadge>
          <div className="review-detail__tabs" role="tablist">
            <button
              role="tab"
              aria-selected={detailTab === "review"}
              onClick={() => setDetailTab("review")}
            >
              Review
            </button>
            <button
              role="tab"
              aria-selected={detailTab === "audit"}
              onClick={() => setDetailTab("audit")}
            >
              Audit trail
            </button>
          </div>
        </div>
        {detailTab === "audit" ? (
          <div className="audit-screen">
            <div className="audit-entry">
              <Lock size={16} aria-hidden="true" />
              <div>
                <strong>Medical paid override · WC-23-0088</strong>
                <p>
                  m.okafor · Underwriter ·
                  policyPeriods[0].claims[3].medical.paid · 9,300.00 → 9,200.00
                  · {rationale || "Carrier statement correction"} · revision 2
                </p>
                <span>Ledger verified 2 min ago</span>
              </div>
            </div>
          </div>
        ) : (
          <div className="review-workspace">
            <section className="review-source">
              <div className="pane-title">
                <span>Source document</span>
                <span>Page 2 of 3</span>
              </div>
              <SourceViewer
                variant="pie"
                active
                onRegionFocus={() => undefined}
              />
            </section>
            <section className="review-ledger">
              <ClaimsTable
                selected={null}
                onSelect={() => undefined}
                failedCell={{ row: 0, col: 8 }}
              />
              <div className="totals-strip totals-strip--failed">
                <span>Period reconciliation failed</span>
                <span>
                  Printed <strong>41,200.00</strong>
                </span>
                <span>
                  Extracted <strong>41,100.00</strong>
                </span>
                <span>
                  Delta <strong>−100.00</strong>
                </span>
              </div>
            </section>
            <aside className="override-panel">
              <span className="panel-kicker">Resolve extracted value</span>
              <h2>Medical paid · WC-23-0088</h2>
              <div className="current-value">
                <span>Current value</span>
                <strong>9,300.00</strong>
                <ConfidenceMeter value={0.71} />
              </div>
              <form
                onSubmit={(event) => {
                  event.preventDefault()
                  save()
                }}
                noValidate
              >
                <label htmlFor="override-value">New value</label>
                <input
                  id="override-value"
                  value={newValue}
                  onChange={(event) => setNewValue(event.target.value)}
                  aria-invalid={Boolean(errors.value)}
                  aria-describedby={
                    errors.value ? "override-value-error" : undefined
                  }
                />
                {errors.value && (
                  <p className="field-error" id="override-value-error">
                    <AlertCircle size={14} /> {errors.value}
                  </p>
                )}
                <label htmlFor="override-rationale">Required rationale</label>
                <textarea
                  id="override-rationale"
                  value={rationale}
                  onChange={(event) => setRationale(event.target.value)}
                  aria-invalid={Boolean(errors.rationale)}
                  aria-describedby={
                    errors.rationale ? "override-rationale-error" : undefined
                  }
                />
                {errors.rationale && (
                  <p className="field-error" id="override-rationale-error">
                    <AlertCircle size={14} /> {errors.rationale}
                  </p>
                )}
                <label className="check-label">
                  <input type="checkbox" /> Apply this mapping to future Pie
                  Insurance documents
                </label>
                <div className="audit-preview">
                  <Lock size={14} />
                  <p>
                    <strong>Audit-entry preview</strong>
                    <br />
                    m.okafor · Underwriter ·
                    policyPeriods[0].claims[3].medical.paid · 9,300.00 →{" "}
                    {newValue || "—"} · {rationale || "rationale"} · revision 2
                  </p>
                </div>
                <Button variant="primary" type="submit">
                  Save override
                </Button>
                <Button type="button">Accept extracted value</Button>
                <Button type="button" className="danger-button">
                  Reject document
                </Button>
              </form>
            </aside>
          </div>
        )}
      </div>
    )
  }

  const rows =
    tab === "documents"
      ? [
          {
            file: "pie-loss-run-2024.pdf",
            reasons: [
              "Period 2023 subtotal does not match extracted rows (R-10): printed 41,200.00, extracted 41,100.00",
              "Claim WC-23-0088 medical paid read at low confidence (0.71)",
            ],
            badge: "Needs review · Pie v1 · 0 model calls",
            time: "14:03",
            claims: "12 claims",
          },
          {
            file: "broker-package-2025.pdf",
            reasons: [
              "Policy year label is present but the effective date needs confirmation",
            ],
            badge: "Needs review · Unknown PDF · 1 model call",
            time: "13:41",
            claims: "8 claims",
          },
        ]
      : [
          {
            file: "prior-carrier-2023.xlsx",
            reasons: [
              "New carrier format learned from this document; confirm the column mapping",
            ],
            badge:
              "Format learned · Fictional Casualty v1 · confirming (1 of 3)",
            time: "13:56",
            claims: "19 claims",
          },
        ]
  return (
    <div className="standard-screen">
      <ScreenHeader
        title="Review queue"
        description="Resolve uncertainty without losing the carrier’s original record."
      />
      <div className="screen-tabs" role="tablist">
        <button
          role="tab"
          aria-selected={tab === "documents"}
          onClick={() => setTab("documents")}
        >
          Documents needing review (2)
        </button>
        <button
          role="tab"
          aria-selected={tab === "formats"}
          onClick={() => setTab("formats")}
        >
          Confirm new formats (1)
        </button>
      </div>
      <div className="queue-list">
        <div className="queue-list__head">
          <span>Document and reason</span>
          <span>Routing</span>
          <span>Opened</span>
          <span>Claims</span>
          <span />
        </div>
        {rows.map((row) => (
          <div className="queue-row" key={row.file}>
            <div>
              <strong>{row.file}</strong>
              {row.reasons.map((reason) => (
                <p key={reason}>
                  <AlertCircle size={14} /> {reason}
                </p>
              ))}
            </div>
            <RoutingBadge review>{row.badge}</RoutingBadge>
            <span>{row.time}</span>
            <span>{row.claims}</span>
            <Button onClick={() => setDetail(true)}>
              Open <ArrowRight size={14} />
            </Button>
          </div>
        ))}
      </div>
    </div>
  )
}

const analyticsKpis = [
  {
    label: "Claims",
    value: 78,
    format: (value: number) => Math.round(value).toString(),
    unit: "",
  },
  { label: "Total incurred", value: 1284330, format: formatMoney, unit: "" },
  {
    label: "Frequency",
    value: 2.41,
    format: (value: number) => value.toFixed(2),
    unit: "per $100K payroll",
  },
  {
    label: "Severity",
    value: 16465.77,
    format: formatMoney,
    unit: "per claim",
  },
  {
    label: "Paid-to-incurred",
    value: 61.3,
    format: (value: number) => `${value.toFixed(1)}%`,
    unit: "",
  },
  {
    label: "Litigation rate",
    value: 14.1,
    format: (value: number) => `${value.toFixed(1)}%`,
    unit: "",
  },
  {
    label: "Cumulative trauma share",
    value: 23.1,
    format: (value: number) => `${value.toFixed(1)}%`,
    unit: "",
  },
  {
    label: "Open claims",
    value: 19,
    format: (value: number) => Math.round(value).toString(),
    unit: "",
  },
  {
    label: "Median reporting lag",
    value: 6,
    format: (value: number) => Math.round(value).toString(),
    unit: "days",
  },
  {
    label: "Closure velocity",
    value: 212,
    format: (value: number) => Math.round(value).toString(),
    unit: "median days to close",
  },
]

function ChartPanel({
  title,
  type = "line",
}: {
  title: string
  type?: "line" | "bar" | "scatter" | "stack"
}) {
  const [table, setTable] = useState(false)
  const bars = [42, 68, 54, 82, 61, 74]
  return (
    <section className="chart-panel">
      <div className="chart-panel__head">
        <h2>{title}</h2>
        <button onClick={() => setTable(!table)}>
          {table ? "View as chart" : "View as table"}
        </button>
      </div>
      {table ? (
        <table className="chart-table">
          <thead>
            <tr>
              <th>Period</th>
              <th>As reported</th>
              <th>Comparison</th>
            </tr>
          </thead>
          <tbody>
            {["2023–24", "2024–25", "2025–26"].map((year, index) => (
              <tr key={year}>
                <td>{year}</td>
                <td>{(index + 2) * 14}.2</td>
                <td>{(index + 2) * 12}.8</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : (
        <svg
          className="mini-chart"
          viewBox="0 0 420 150"
          role="img"
          aria-label={`${title} chart`}
        >
          <line x1="34" y1="125" x2="404" y2="125" className="chart-axis" />
          {type === "line" && (
            <>
              <path
                d="M40 106 L120 82 L210 92 L300 48 L396 62"
                className="chart-main-line"
              />
              <path
                d="M40 115 L120 98 L210 76 L300 83 L396 70"
                className="chart-compare-line"
              />
            </>
          )}
          {type === "bar" &&
            bars.map((height, index) => (
              <rect
                key={index}
                x={42 + index * 58}
                y={125 - height}
                width="28"
                height={height}
                className="chart-bar"
              />
            ))}
          {type === "stack" &&
            [0, 1, 2].map((index) => (
              <g key={index}>
                <rect
                  x={70 + index * 112}
                  y={48}
                  width="52"
                  height="77"
                  className="chart-bar"
                />
                <rect
                  x={70 + index * 112}
                  y={28}
                  width="52"
                  height="20"
                  className="chart-compare-bar"
                />
              </g>
            ))}
          {type === "scatter" && (
            <>
              <line
                x1="34"
                y1="58"
                x2="404"
                y2="58"
                className="chart-threshold"
              />
              {[
                [52, 104],
                [98, 91],
                [146, 66],
                [224, 37],
                [282, 84],
                [350, 51],
              ].map(([x, y], index) => (
                <circle
                  key={index}
                  cx={x}
                  cy={y}
                  r="5"
                  className={
                    y < 58 ? "chart-point chart-point--review" : "chart-point"
                  }
                />
              ))}
            </>
          )}
        </svg>
      )}
    </section>
  )
}

function AnalyticsScreen() {
  const [reserve, setReserve] = useState(0)
  const [thresholdEnabled, setThresholdEnabled] = useState(false)
  const [threshold, setThreshold] = useState(250000)
  const [trend, setTrend] = useState(1)
  const [nonCa, setNonCa] = useState(false)
  const [ct, setCt] = useState(true)
  const active =
    reserve !== 0 || thresholdEnabled || trend !== 1 || nonCa || !ct
  const reset = () => {
    setReserve(0)
    setThresholdEnabled(false)
    setThreshold(250000)
    setTrend(1)
    setNonCa(false)
    setCt(true)
  }
  return (
    <div className="analytics-screen">
      <div className="analytics-main">
        <ScreenHeader
          title="Quote analytics"
          description="As-reported loss experience for Acme Fabrication Inc."
        />
        <div className="filter-bar">
          <label>
            Policy years
            <select defaultValue="all">
              <option value="all">All three years</option>
              <option>2023–24</option>
              <option>2024–25</option>
              <option>2025–26</option>
            </select>
          </label>
          <label>
            Injury type
            <select>
              <option>All types</option>
              <option>Indemnity</option>
              <option>Medical only</option>
            </select>
          </label>
          <label>
            Body part
            <select>
              <option>All body parts</option>
              <option>Back</option>
              <option>Shoulder</option>
            </select>
          </label>
          <label>
            Class code
            <select>
              <option>All classes</option>
              <option>3632</option>
              <option>8810</option>
            </select>
          </label>
          <label>
            Jurisdiction
            <select>
              <option>CA</option>
              <option>Other</option>
            </select>
          </label>
          <label className="check-label">
            <input type="checkbox" /> Cumulative trauma only
          </label>
        </div>
        <div className="kpi-strip">
          {analyticsKpis.map((kpi, index) => {
            const factor =
              index === 0 || index === 7 ? 1 : (1 + reserve / 100) * trend
            const scenarioValue = kpi.value * factor
            const delta = ((scenarioValue / kpi.value - 1) * 100).toFixed(1)
            return (
              <div className="kpi-cell" key={kpi.label}>
                <span>{kpi.label}</span>
                <strong>
                  <AnimatedNumber
                    value={active ? scenarioValue : kpi.value}
                    format={kpi.format}
                  />
                </strong>
                {kpi.unit && <span className="kpi-cell__unit">{kpi.unit}</span>}
                {active && (
                  <small>
                    <span>{kpi.format(kpi.value)}</span> →{" "}
                    <b>{kpi.format(scenarioValue)}</b>{" "}
                    <em
                      className={reserve <= 0 ? "delta-good" : "delta-review"}
                    >
                      {Number(delta) > 0 ? "+" : ""}
                      {delta}%
                    </em>
                  </small>
                )}
              </div>
            )
          })}
        </div>
        <div className="charts-grid">
          <ChartPanel title="Frequency by policy year" />
          <ChartPanel title="Severity by policy year" />
          <ChartPanel title="Medical-only vs lost-time" type="stack" />
          <ChartPanel title="Loss drivers by cause" type="bar" />
          <ChartPanel title="Loss drivers by body part" type="bar" />
          <ChartPanel
            title="Large-loss scatter · $100,000 threshold"
            type="scatter"
          />
          <ChartPanel title="Paid vs incurred development" />
        </div>
        <footer className="analytics-footer">
          <span>
            From 78 claims in 3 documents · valued 2026-08-31 · 2 overrides
            applied · catalog v1.2
          </span>
          <div className="as-toggle">
            <button aria-pressed={!active}>As reported</button>
            <button aria-pressed={active}>As adjusted</button>
          </div>
          <Button>Export pricing memo</Button>
          <Button>Push flags to quote</Button>
          <Button>Assign follow-up</Button>
        </footer>
      </div>
      <aside className="scenario-panel">
        <div className="scenario-panel__head">
          <SlidersHorizontal size={18} />
          <div>
            <h2>Scenario</h2>
            <p>Changes stay local until exported.</p>
          </div>
        </div>
        <label>
          Reserve adjustment ±%
          <div className="range-input">
            <input
              type="range"
              min="-25"
              max="25"
              value={reserve}
              onChange={(event) => setReserve(Number(event.target.value))}
            />
            <input
              type="number"
              min="-25"
              max="25"
              value={reserve}
              onChange={(event) => setReserve(Number(event.target.value))}
            />
          </div>
        </label>
        <label className="check-label">
          <input
            type="checkbox"
            checked={thresholdEnabled}
            onChange={(event) => setThresholdEnabled(event.target.checked)}
          />{" "}
          Exclude catastrophic claims above
        </label>
        <label htmlFor="cat-threshold" className="sr-only">
          Catastrophic claim threshold
        </label>
        <input
          id="cat-threshold"
          type="number"
          value={threshold}
          disabled={!thresholdEnabled}
          onChange={(event) => setThreshold(Number(event.target.value))}
        />
        <label>
          Trend factor
          <input
            type="number"
            step=".01"
            value={trend}
            onChange={(event) => setTrend(Number(event.target.value))}
          />
        </label>
        <label className="check-label">
          <input
            type="checkbox"
            checked={nonCa}
            onChange={(event) => setNonCa(event.target.checked)}
          />{" "}
          Include non-CA losses
        </label>
        <label className="check-label">
          <input
            type="checkbox"
            checked={ct}
            onChange={(event) => setCt(event.target.checked)}
          />{" "}
          Include cumulative trauma
        </label>
        <Button onClick={reset}>
          <RotateCcw size={15} /> Reset to as-reported
        </Button>
      </aside>
    </div>
  )
}

function CitationChip({
  children,
  onClick,
}: {
  children: ReactNode
  onClick?: () => void
}) {
  return (
    <button className="citation-chip" onClick={onClick}>
      <FileSearch size={13} /> {children}
    </button>
  )
}

function ChatScreen() {
  const [scope, setScope] = useState("quote")
  const [sourcePage, setSourcePage] = useState<number | null>(null)
  return (
    <div className="chat-screen">
      <div className="chat-main">
        <ScreenHeader
          title="Ask the loss runs"
          description="Answers are grounded in the extracted ledger and its source pages."
        />
        <div className="scope-control" aria-label="Chat scope">
          <button
            aria-pressed={scope === "all"}
            onClick={() => setScope("all")}
          >
            All documents
          </button>
          <button
            aria-pressed={scope === "quote"}
            onClick={() => setScope("quote")}
          >
            This quote
          </button>
          <button
            aria-pressed={scope === "workspace"}
            onClick={() => setScope("workspace")}
          >
            My workspace (3 files)
          </button>
        </div>
        <p className="scope-sentence">
          Answers use the 3 documents on quote Q-2026-018342.
        </p>
        <div className="chat-thread">
          <div className="chat-turn chat-turn--user">
            <strong>You</strong>
            <p>Which claims are still open with reserves over $50,000?</p>
          </div>
          <div className="chat-turn chat-turn--assistant">
            <Bot size={18} />
            <div>
              <strong>Loss Run Intelligence</strong>
              <p>
                One open claim exceeds $50,000 in reserves. The second open
                claim is shown for comparison.
              </p>
              <table>
                <thead>
                  <tr>
                    <th>Claim</th>
                    <th>Reserve</th>
                    <th>Status</th>
                    <th>Result</th>
                  </tr>
                </thead>
                <tbody>
                  <tr>
                    <td>WC-25-0430</td>
                    <td>83,000.00</td>
                    <td>Open</td>
                    <td>
                      <StatusBadge status="Verified" />
                    </td>
                  </tr>
                  <tr>
                    <td>WC-23-0417</td>
                    <td>31,000.00</td>
                    <td>Open</td>
                    <td>Below threshold</td>
                  </tr>
                </tbody>
              </table>
              <div className="citations">
                <CitationChip onClick={() => setSourcePage(2)}>
                  amtrust-loss-run-2026.pdf · p.2
                </CitationChip>
                <CitationChip onClick={() => setSourcePage(4)}>
                  amtrust-loss-run-2026.pdf · p.4
                </CitationChip>
              </div>
              <details>
                <summary>How this was answered</summary>
                <p>Tools used: search documents → query claims</p>
                <CostLine>This turn: $0.0012 · 1 model call</CostLine>
              </details>
            </div>
          </div>
          <div className="chat-turn chat-turn--user">
            <strong>You</strong>
            <p>What share of claims are cumulative trauma?</p>
          </div>
          <div className="chat-turn chat-turn--assistant">
            <Bot size={18} />
            <div>
              <strong>Loss Run Intelligence</strong>
              <p>
                <strong>23.1% (18 of 78 claims)</strong>, concentrated in
                2024–25.
              </p>
              <CitationChip>
                Analytics catalog · cumulative-trauma-share
              </CitationChip>
            </div>
          </div>
        </div>
        <div className="suggestions">
          <span>Suggested for this quote</span>
          <button>Which carriers contributed large losses?</button>
          <button>Show claims with failed rules</button>
          <button>Compare reporting lag by year</button>
        </div>
        <form
          className="chat-composer"
          onSubmit={(event) => event.preventDefault()}
        >
          <label htmlFor="chat-message">Ask a question about this quote</label>
          <div>
            <textarea
              id="chat-message"
              placeholder="Ask about claims, totals, trends, or source documents"
            />
            <Button variant="primary" type="submit">
              Send
            </Button>
          </div>
        </form>
      </div>
      {sourcePage && (
        <aside className="chat-source">
          <div className="lineage-panel__header">
            <div>
              <span className="panel-kicker">Citation source</span>
              <h2>amtrust-loss-run-2026.pdf</h2>
            </div>
            <Button
              variant="ghost"
              className="icon-button"
              onClick={() => setSourcePage(null)}
              aria-label="Close source panel"
            >
              <X size={18} />
            </Button>
          </div>
          <SourceViewer
            key={sourcePage}
            initialPage={sourcePage}
            active={false}
            onRegionFocus={() => undefined}
          />
        </aside>
      )}
    </div>
  )
}

function CostsScreen() {
  const documents = [
    [
      "amtrust-loss-run-2026.pdf",
      "Replay",
      "6",
      "1,840",
      "320",
      "0 / 0 / 0",
      "$0.030",
    ],
    [
      "pie-loss-run-2024.pdf",
      "Replay",
      "3",
      "910",
      "180",
      "0 / 0 / 0",
      "$0.015",
    ],
    [
      "prior-carrier-2023.xlsx",
      "Learn",
      "1",
      "640",
      "110",
      "2,840 / 1,120 / 380",
      "$0.021",
    ],
    [
      "broker-package-2025.pdf",
      "Review",
      "4",
      "1,220",
      "240",
      "1,980 / 0 / 260",
      "$0.067",
    ],
  ]
  return (
    <div className="standard-screen">
      <ScreenHeader
        title="Costs"
        description="Metered document-processing cost across the proof of concept."
      />
      <section className="cost-hero">
        <span>POC to date</span>
        <strong>$42.18</strong>
      </section>
      <div className="plain-figures">
        <div>
          <strong>318</strong>
          <span>documents processed</span>
        </div>
        <div>
          <strong>$0.133</strong>
          <span>average per document</span>
        </div>
        <div>
          <strong>84%</strong>
          <span>processed with zero model calls</span>
        </div>
      </div>
      <div className="cost-grid">
        <ChartPanel title="Cost per document · last 30 days" type="bar" />
        <section className="carrier-savings">
          <div className="chart-panel__head">
            <h2>Learn once vs replay savings</h2>
          </div>
          {[
            ["AmTrust", "$0.019", "$3.96", "2"],
            ["Pie", "$0.024", "$1.08", "2"],
            ["Synthetic Mutual", "Hand-written", "$1.79", "—"],
          ].map(([carrier, learn, savings, breakeven], index) => (
            <div className="carrier-saving" key={carrier}>
              <div>
                <strong>{carrier}</strong>
                <span>
                  Learn {learn} · break-even at document {breakeven}
                </span>
              </div>
              <span className="saving-bar">
                <i
                  style={{ "--bar": `${88 - index * 18}%` } as CSSProperties}
                />
              </span>
              <strong>{savings} saved</strong>
            </div>
          ))}
        </section>
      </div>
      <section className="cost-table-panel">
        <h2>Per-document meter lines</h2>
        <div className="cost-table-wrap">
          <table>
            <thead>
              <tr>
                <th>Document</th>
                <th>Path</th>
                <th>CU layout pages</th>
                <th>Classifier tokens</th>
                <th>Embeddings</th>
                <th>Model tokens in / cached / out</th>
                <th>Cost</th>
              </tr>
            </thead>
            <tbody>
              {documents.map((row) => (
                <tr key={row[0]}>
                  {row.map((cell, index) => (
                    <td key={cell}>
                      {index === 1 ? (
                        <span
                          className={`path-label path-label--${cell.toLowerCase()}`}
                        >
                          {cell}
                        </span>
                      ) : (
                        cell
                      )}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
      <div className="reconciliation-line">
        <strong>Metered $42.18 · billed $43.90 · 96% · export of Oct 23</strong>
        <span>
          The 4% gap is provider rounding and jobs billed after the latest meter
          export.
        </span>
      </div>
    </div>
  )
}

const formatRows = [
  [
    "AmTrust North America",
    "amtrust-pdf",
    "v2",
    "Active",
    "delta · 2026-10-20",
    "pdf-totals",
    "212",
    "212",
  ],
  [
    "Pie Insurance",
    "pie-pdf",
    "v1",
    "Active",
    "learned · 2026-10-19",
    "3 documents",
    "58",
    "58",
  ],
  [
    "Fictional Casualty Group",
    "fictional-casualty-spreadsheet",
    "v1",
    "Shadow",
    "learned · 2026-10-22",
    "confirming 1 of 3",
    "1",
    "0",
  ],
  [
    "Synthetic Mutual",
    "synthetic-mutual-pdf",
    "v3",
    "Active",
    "hand",
    "pdf-totals",
    "96",
    "96",
  ],
  [
    "Synthetic Mutual",
    "synthetic-mutual-pdf",
    "v2",
    "Retired",
    "hand",
    "retired",
    "44",
    "44",
  ],
] as const

function FormatsScreen() {
  const [retiring, setRetiring] = useState<string | null>(null)
  const [reason, setReason] = useState("")
  return (
    <div className="standard-screen">
      <ScreenHeader
        title="Format memory"
        description="A format is learned once. Every later document from that carrier is read deterministically, with no model call."
      />
      <div className="formats-table-wrap">
        <table className="formats-table">
          <thead>
            <tr>
              <th>Carrier</th>
              <th>Format id</th>
              <th>Version</th>
              <th>Status</th>
              <th>Created by</th>
              <th>Verification gate</th>
              <th>Documents processed</th>
              <th>Model calls saved</th>
              <th>Actions</th>
            </tr>
          </thead>
          <tbody>
            {formatRows.map((row) => (
              <tr key={`${row[1]}-${row[2]}`}>
                {row.map((cell, index) => (
                  <td key={index}>
                    {index === 3 ? (
                      <StatusBadge status={cell as Status} />
                    ) : (
                      cell
                    )}
                  </td>
                ))}
                <td>
                  {row[3] !== "Retired" && (
                    <Button
                      onClick={() =>
                        setRetiring(`${row[0]} · ${row[1]} ${row[2]}`)
                      }
                    >
                      Retire…
                    </Button>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {retiring && (
        <div className="dialog-backdrop">
          <div
            className="retire-dialog"
            role="dialog"
            aria-modal="true"
            aria-labelledby="retire-title"
          >
            <div className="lineage-panel__header">
              <div>
                <span className="panel-kicker">Format lifecycle</span>
                <h2 id="retire-title">Retire {retiring}</h2>
              </div>
              <Button
                variant="ghost"
                className="icon-button"
                onClick={() => setRetiring(null)}
                aria-label="Close retire dialog"
              >
                <X size={18} />
              </Button>
            </div>
            <p>
              Future documents will route to the next eligible format. Existing
              audit records remain unchanged.
            </p>
            <label htmlFor="retire-reason">Reason for retiring</label>
            <textarea
              id="retire-reason"
              value={reason}
              onChange={(event) => setReason(event.target.value)}
            />
            <div>
              <Button onClick={() => setRetiring(null)}>Cancel</Button>
              <Button
                variant="primary"
                disabled={!reason.trim()}
                onClick={() => setRetiring(null)}
              >
                Retire format
              </Button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

export default function App() {
  const [screen, setScreen] = useState<Screen>("upload")
  return (
    <AppFrame screen={screen} onScreen={setScreen}>
      {screen === "upload" && (
        <UploadScreen onContinue={() => setScreen("document")} />
      )}
      {screen === "document" && <DocumentScreen />}
      {screen === "review" && <ReviewQueueScreen />}
      {screen === "analytics" && <AnalyticsScreen />}
      {screen === "chat" && <ChatScreen />}
      {screen === "costs" && <CostsScreen />}
      {screen === "formats" && <FormatsScreen />}
    </AppFrame>
  )
}
