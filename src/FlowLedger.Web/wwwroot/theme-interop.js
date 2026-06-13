// theme-interop.js
// Tiny localStorage helpers for theme persistence.
// All functions are exposed on window.FlowLedgerTheme so they can be called
// via IJSRuntime without a module import (works on both SSR + interactive).

window.FlowLedgerTheme = {
    /**
     * Returns the stored preference string ("dark" | "light") or null when no
     * explicit choice has been saved.  Never throws — returns null on any error.
     */
    getPreference: function () {
        try {
            return localStorage.getItem('flowledger-theme');
        } catch (e) {
            console.warn('[FlowLedger] Could not read theme preference from localStorage:', e);
            return null;
        }
    },

    /**
     * Saves an explicit preference ("dark" | "light").
     * Logs a warning and returns false on failure; never throws.
     */
    setPreference: function (value) {
        try {
            localStorage.setItem('flowledger-theme', value);
            return true;
        } catch (e) {
            console.warn('[FlowLedger] Could not save theme preference to localStorage:', e);
            return false;
        }
    },

    /**
     * Removes the explicit preference so the system preference takes over.
     * Never throws.
     */
    clearPreference: function () {
        try {
            localStorage.removeItem('flowledger-theme');
        } catch (e) {
            console.warn('[FlowLedger] Could not clear theme preference from localStorage:', e);
        }
    },

    /**
     * Returns true when the OS/browser prefers dark mode.
     * Falls back to false when the media-query API is unavailable.
     */
    getSystemPrefersDark: function () {
        try {
            return window.matchMedia &&
                   window.matchMedia('(prefers-color-scheme: dark)').matches;
        } catch (e) {
            console.warn('[FlowLedger] Could not read system color-scheme preference:', e);
            return false;
        }
    }
};
