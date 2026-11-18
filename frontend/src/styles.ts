/** The guests tab's styles, on the shell's design tokens, each with a fallback (docs/architecture.md §7.14). */
export const virtStyles = `
.nk-virt { display: flex; flex-direction: column; height: 100%; min-height: 0; background: var(--color-bg, #0b1220);
  color: var(--color-text, #e5ebf5); font-family: var(--font-sans, system-ui, sans-serif); font-size: 0.875rem; }
.nk-virt[hidden] { display: none; }
.nk-virt-status { display: flex; align-items: center; gap: 0.5rem; min-height: 2.25rem; padding: 0.25rem 1rem;
  background: var(--color-surface, #111a2e); border-bottom: 1px solid var(--color-border, #233150); font-size: 0.8125rem; }
.nk-virt-dot { width: 0.5rem; height: 0.5rem; border-radius: 999px; background: var(--color-text-subtle, #7d8ba6); }
.nk-virt-dot[data-state='connecting'] { background: var(--color-warning, #f5b84b); }
.nk-virt-dot[data-state='open'] { background: #4ade80; }
.nk-virt-dot[data-state='ended'] { background: #f87171; }
.nk-virt-actions { display: flex; align-items: center; gap: 0.5rem; margin-left: auto; }
.nk-virt-body { display: flex; flex-direction: column; flex: 1; min-height: 0; overflow: auto; padding: 0.75rem 1rem; gap: 0.5rem; }
.nk-virt-muted { color: var(--color-text-muted, #9aa8c1); }
.nk-virt-error { margin: 0; color: var(--color-danger-text, #fca5a5); }
.nk-virt-notice { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; margin: 0; padding: 0.5rem 0.75rem;
  border: 1px solid var(--color-border-strong, #33456b); border-radius: var(--radius-md, 8px); background: var(--color-surface, #111a2e); }
.nk-virt-notice > span { flex: 1; }
.nk-virt-toolbar { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; }
.nk-virt-button { display: inline-flex; align-items: center; min-height: 1.75rem; padding: 0 0.625rem; border: 1px solid var(--color-border-strong, #33456b);
  border-radius: var(--radius-md, 8px); background: transparent; color: var(--color-text, #e5ebf5); font: inherit; font-size: 0.8125rem;
  font-weight: 600; cursor: pointer; white-space: nowrap; }
.nk-virt-button:hover:not(:disabled) { background: var(--color-surface-raised, #16213a); }
.nk-virt-button:disabled { opacity: 0.5; cursor: default; }
.nk-virt-button[data-danger] { border-color: var(--color-danger-border, #b91c1c); color: var(--color-danger-text, #fca5a5); }
.nk-virt-table { width: 100%; border-collapse: collapse; }
.nk-virt-table th, .nk-virt-table td { padding: 0.375rem 0.5rem; border-bottom: 1px solid var(--color-border, #233150); text-align: left; vertical-align: middle; }
.nk-virt-table th { font-size: 0.75rem; font-weight: 600; color: var(--color-text-muted, #9aa8c1); }
.nk-virt-row-actions { display: flex; flex-wrap: wrap; gap: 0.25rem; }
.nk-virt-state { display: inline-block; padding: 0 0.5rem; border-radius: 999px; background: var(--color-surface-raised, #16213a); font-size: 0.75rem; }
.nk-virt-state[data-running] { background: #14532d; color: #bbf7d0; }
.nk-virt-console { display: flex; flex-direction: column; flex: 1; min-height: 0; gap: 0.5rem; }
.nk-virt-screen { flex: 1; min-height: 0; background: #000; overflow: hidden; }
`
