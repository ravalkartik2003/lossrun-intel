import { createRootRoute, createRoute, createRouter, lazyRouteComponent, Navigate } from "@tanstack/react-router";
import { Shell } from "./shell/Shell.tsx";
import { UploadScreen } from "./screens/Upload.tsx";
import { DocumentScreen } from "./screens/Document.tsx";
import { ReviewQueueScreen } from "./screens/ReviewQueue.tsx";
import { ReviewDetailScreen } from "./screens/ReviewDetail.tsx";
import { useMe } from "./api/queries.ts";

function Home() {
  const me = useMe();
  if (!me.data) return null;
  return <Navigate to={(me.data.canRead ?? me.data.canReview) ? "/analytics" : "/upload"} replace />;
}

const root = createRootRoute({ component: Shell });

const routes = [
  createRoute({ getParentRoute: () => root, path: "/", component: Home }),
  createRoute({ getParentRoute: () => root, path: "/upload", component: UploadScreen }),
  createRoute({ getParentRoute: () => root, path: "/documents/$documentId", component: DocumentScreen }),
  createRoute({
    getParentRoute: () => root,
    path: "/reviews",
    component: ReviewQueueScreen,
    validateSearch: (s: Record<string, unknown>): { tab?: "documents" | "formats" | "decided" } =>
      s.tab === "formats" || s.tab === "decided" ? { tab: s.tab } : {},
  }),
  createRoute({ getParentRoute: () => root, path: "/reviews/$documentId", component: ReviewDetailScreen }),
  // Chart-heavy and secondary screens load on demand (ECharts and Recharts stay out of the Document screen's first load).
  createRoute({ getParentRoute: () => root, path: "/analytics", component: lazyRouteComponent(() => import("./screens/Analytics.tsx"), "AnalyticsScreen") }),
  createRoute({ getParentRoute: () => root, path: "/chat", component: lazyRouteComponent(() => import("./screens/Chat.tsx"), "ChatScreen") }),
  createRoute({ getParentRoute: () => root, path: "/costs", component: lazyRouteComponent(() => import("./screens/Costs.tsx"), "CostsScreen") }),
  createRoute({ getParentRoute: () => root, path: "/formats", component: lazyRouteComponent(() => import("./screens/Formats.tsx"), "FormatsScreen") }),
  createRoute({ getParentRoute: () => root, path: "/escalations", component: lazyRouteComponent(() => import("./screens/Escalations.tsx"), "EscalationsScreen") }),
];

export const router = createRouter({ routeTree: root.addChildren(routes), defaultPreload: "intent" });

declare module "@tanstack/react-router" {
  interface Register {
    router: typeof router;
  }
}
