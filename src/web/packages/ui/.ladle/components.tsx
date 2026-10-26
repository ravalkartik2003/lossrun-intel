import { useEffect } from "react";
import type { GlobalProvider } from "@ladle/react";
import "@fontsource/public-sans/400.css";
import "@fontsource/public-sans/500.css";
import "@fontsource/public-sans/600.css";
import "../src/tokens.css";
import "../src/base.css";
import "../src/components.css";

/** Maps Ladle's theme switch onto the tokens' [data-theme] and adds a density switch via the story's args. */
export const Provider: GlobalProvider = ({ children, globalState }) => {
  useEffect(() => {
    document.documentElement.dataset.theme = globalState.theme === "dark" ? "dark" : "light";
  }, [globalState.theme]);
  return <div style={{ padding: 24, background: "var(--surface)", minHeight: "100vh" }}>{children}</div>;
};
