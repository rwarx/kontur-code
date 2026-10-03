import { useEffect, useState } from "react";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "./globals.css";
import { KonturApp } from "./components/kontur/KonturApp";
import { useKontur } from "./lib/kontur/store";
import { hydrateFromBackend } from "./lib/kontur/sync";

function Boot() {
  const [backendSettled, setBackendSettled] = useState(false);
  const hydrated = useKontur((s) => s.hydrated);

  useEffect(() => {
    let cancelled = false;
    const timeout = setTimeout(() => {
      if (!cancelled) setBackendSettled(true);
    }, 4000);
    hydrateFromBackend()
      .catch(() => undefined)
      .finally(() => {
        if (!cancelled) {
          clearTimeout(timeout);
          setBackendSettled(true);
        }
      });
    return () => {
      cancelled = true;
      clearTimeout(timeout);
    };
  }, []);

  if (!hydrated || !backendSettled) {
    return (
      <div className="flex h-screen w-screen items-center justify-center bg-app">
        <span className="h-6 w-6 animate-spin-slow rounded-full border-2 border-line border-t-accent" />
      </div>
    );
  }
  return <KonturApp />;
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Boot />
  </StrictMode>,
);
