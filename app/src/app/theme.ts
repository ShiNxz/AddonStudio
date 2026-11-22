// Applies the theme setting (dark, light, system) to <html>, the native title bar, and remembers it for the next start.
import { useEffect } from "react";
import type { Theme } from "../rpc/types";
import { detectMica, setNativeTheme } from "./platform";

/** index.html reads this before the first paint so the window never flashes the wrong theme. */
export const THEME_STORAGE_KEY = "addonstudio.theme";

function systemPrefersDark(): boolean {
  return typeof window.matchMedia !== "function" || window.matchMedia("(prefers-color-scheme: dark)").matches;
}

export function resolveTheme(theme: Theme): "dark" | "light" {
  if (theme === "system") return systemPrefersDark() ? "dark" : "light";
  return theme;
}

export function applyTheme(theme: Theme): void {
  const resolved = resolveTheme(theme);
  const root = document.documentElement;
  root.classList.toggle("dark", resolved === "dark");
  root.classList.toggle("light", resolved === "light");
  root.dataset.theme = resolved;
  root.style.colorScheme = resolved;
  try {
    localStorage.setItem(THEME_STORAGE_KEY, theme);
  } catch {
    // storage can be blocked; the theme still applies for this session
  }
}

/** Keeps <html> and the title bar in sync with the setting, following the OS while it is "system". */
export function useApplyTheme(theme: Theme | undefined): void {
  useEffect(() => {
    if (!theme) return;
    applyTheme(theme);
    void setNativeTheme(theme === "system" ? null : theme);
    if (theme !== "system" || typeof window.matchMedia !== "function") return;
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => applyTheme("system");
    media.addEventListener("change", onChange);
    return () => media.removeEventListener("change", onChange);
  }, [theme]);
}

/** Turns on the transparent root on Windows 11 so Mica shows through (solid fallback everywhere else). */
export function useMica(): void {
  useEffect(() => {
    let active = true;
    void detectMica().then((mica) => {
      if (active && mica) document.documentElement.classList.add("mica");
    });
    return () => {
      active = false;
    };
  }, []);
}
