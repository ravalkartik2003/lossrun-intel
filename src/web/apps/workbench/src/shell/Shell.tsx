import { useEffect, type ReactNode } from "react";
import { Link, Outlet, useRouterState } from "@tanstack/react-router";
import { ChartColumn, Coins, Library, ListChecks, LogOut, MessageSquare, SearchCheck, Upload } from "lucide-react";
import { CostLine, DensityToggle, SkipLink, ThemeToggle } from "@lossrun/ui";
import { AUTH_MODE, ENVIRONMENT_LABEL, MOCK } from "../env.ts";
import { useAuth } from "../auth/auth.ts";
import { useMe, useQuotes } from "../api/queries.ts";
import { useUi } from "../state/ui.ts";
import { LoginScreen } from "./Login.tsx";
import { useWidth } from "./hooks.ts";

interface NavItem {
  to: "/upload" | "/reviews" | "/analytics" | "/chat" | "/costs" | "/formats" | "/escalations";
  label: string;
  icon: typeof Upload;
  reviewerOnly: boolean;
}

const NAV: NavItem[] = [
  { to: "/upload", label: "Upload", icon: Upload, reviewerOnly: false },
  { to: "/reviews", label: "Review", icon: ListChecks, reviewerOnly: true },
  { to: "/analytics", label: "Analytics", icon: ChartColumn, reviewerOnly: true },
  { to: "/chat", label: "Chat", icon: MessageSquare, reviewerOnly: false },
  { to: "/costs", label: "Costs", icon: Coins, reviewerOnly: true },
  { to: "/formats", label: "Formats", icon: Library, reviewerOnly: true },
  { to: "/escalations", label: "Investigate", icon: SearchCheck, reviewerOnly: true },
];

/**
 * Global frame (design-brief §5): 56px nav rail (icons + labels; icons only below 1280px), 48px top bar with the
 * quote selector, the environment chip, the contextual cost line, user + role. Applicants see Upload and Chat only.
 */
export function Shell() {
  const basic = useAuth((s) => s.basic);
  const theme = useUi((s) => s.theme);
  const density = useUi((s) => s.density);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    document.documentElement.dataset.density = density;
  }, [theme, density]);

  if (AUTH_MODE === "basic" && !basic) return <LoginScreen />;
  return <SignedIn />;
}

function SignedIn() {
  const me = useMe();
  const width = useWidth();
  const path = useRouterState({ select: (s) => s.location.pathname });
  const canRead = me.data?.canRead ?? me.data?.canReview ?? false;
  const items = NAV.filter((n) => canRead || !n.reviewerOnly);

  return (
    <div className={`app app--${width}`}>
      <SkipLink />
      <nav className="rail" aria-label="Main">
        <span className="rail__brand" aria-hidden="true">LR</span>
        <ul>
          {items.map((n) => {
            const Icon = n.icon;
            const current = path === n.to || path.startsWith(n.to + "/") || (n.to === "/reviews" && path.startsWith("/documents/"));
            return (
              <li key={n.to}>
                <Link to={n.to} className="rail__link" aria-current={current ? "page" : undefined} title={width === "wide" ? undefined : n.label}>
                  <Icon size={20} strokeWidth={1.75} aria-hidden="true" />
                  <span className={width === "wide" ? "rail__label" : "lr-sr-only"}>{n.label}</span>
                </Link>
              </li>
            );
          })}
        </ul>
      </nav>
      <TopBar canReview={canRead} role={me.data?.role} userId={me.data?.userId} />
      <main id="main" className="main" tabIndex={-1}>
        {me.isError ? <p className="lr-error" role="alert">Could not reach the API: {(me.error as Error).message}</p> : null}
        {me.data ? <Outlet /> : null}
      </main>
    </div>
  );
}

function TopBar({ canReview, role, userId }: { canReview: boolean; role?: string; userId?: string }) {
  const { theme, setTheme, density, setDensity, contextCost } = useUi();
  const signOut = useAuth((s) => s.signOut);
  const width = useWidth();
  return (
    <header className="topbar">
      {canReview ? <QuoteSelector /> : <span className="topbar__title">Loss Run Intelligence</span>}
      <span className="chip" title="Every document here is synthetic">{ENVIRONMENT_LABEL}{MOCK ? " · offline mock" : ""}</span>
      <span className="topbar__spacer" />
      {contextCost && width !== "small" ? <CostLine usd={contextCost.usd} pages={contextCost.pages} modelCalls={contextCost.modelCalls} label={contextCost.label} /> : null}
      {width === "wide" ? <DensityToggle density={density} onChange={setDensity} /> : null}
      <ThemeToggle theme={theme} onChange={setTheme} />
      {MOCK ? <MockRolePicker /> : null}
      <span className="topbar__user">
        <span>{userId ?? "…"}</span>
        <span className="lr-meta">{role ?? ""}</span>
      </span>
      {AUTH_MODE === "basic" || AUTH_MODE === "entra" ? (
        <button type="button" className="lr-btn lr-btn--sm lr-btn--quiet lr-topbar__icon" aria-label="Sign out" title="Sign out"
          onClick={() => (AUTH_MODE === "entra" ? void import("../auth/msal.ts").then((m) => m.signOutEntra()) : signOut())}>
          <LogOut size={16} aria-hidden="true" />
        </button>
      ) : null}
    </header>
  );
}

function QuoteSelector() {
  const quotes = useQuotes();
  const { quoteId, setQuote } = useUi();
  const list = quotes.data ?? [];
  useEffect(() => {
    if (list.length > 0 && (!quoteId || !list.some((q) => q.quoteId === quoteId))) setQuote(list[0].quoteId);
  }, [list.length, quoteId]);
  return (
    <label className="quote-select">
      <span className="lr-meta">Quote</span>
      <select className="lr-select" value={quoteId ?? ""} onChange={(e) => setQuote(e.target.value)} disabled={list.length === 0}>
        {list.length === 0 ? <option value="">{quotes.isLoading ? "Loading…" : "No quotes"}</option> : null}
        {list.map((q) => (
          <option key={q.quoteId} value={q.quoteId}>
            {q.displayName ?? q.quoteId} — {q.insuredName ?? "unnamed"} ({q.claims} claims)
          </option>
        ))}
      </select>
    </label>
  );
}

function MockRolePicker() {
  const { mockRole, setMockRole } = useAuth();
  return (
    <label className="quote-select">
      <span className="lr-meta">Role</span>
      <select
        className="lr-select"
        value={mockRole}
        onChange={(e) => {
          setMockRole(e.target.value);
          window.location.assign("/");
        }}
      >
        {["Applicant", "Underwriter", "UWManager", "Admin", "Engineer"].map((r) => <option key={r}>{r}</option>)}
      </select>
    </label>
  );
}

/** A screen's frame: title row (h1 + actions) and content. */
export function Page({ title, actions, children, wide }: { title: ReactNode; actions?: ReactNode; children: ReactNode; wide?: boolean }) {
  return (
    <div className={`page${wide ? " page--wide" : ""}`}>
      <div className="page__head">
        <h1>{title}</h1>
        {actions ? <div className="page__actions">{actions}</div> : null}
      </div>
      {children}
    </div>
  );
}
