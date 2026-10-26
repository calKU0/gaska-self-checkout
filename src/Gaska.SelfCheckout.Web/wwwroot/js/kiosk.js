// Barcode scanner working as a keyboard (HID keyboard wedge).
// The scanner "types" very fast and ends with Enter – that is how it is told apart from a human.
// The scanned code goes to .NET regardless of whether the search field has focus.

const MAX_INTERVAL_MS = 45;   // max. interval between scanner keystrokes
const MIN_LENGTH = 3;

let state = null;

export function init(dotnetRef) {
    dispose();
    state = {
        dotnetRef,
        buffer: "",
        last: 0,
        target: null,
        targetValue: null,
        intervals: []
    };
    document.addEventListener("keydown", onKeyDown, true);
    document.addEventListener("contextmenu", blockContext, true);
    document.body.classList.add("kiosk-mode");
}

export function dispose() {
    if (!state) return;
    document.removeEventListener("keydown", onKeyDown, true);
    document.removeEventListener("contextmenu", blockContext, true);
    state = null;
}

function blockContext(e) { e.preventDefault(); }

function onKeyDown(e) {
    if (!state || e.ctrlKey || e.altKey || e.metaKey) return;
    const now = performance.now();
    const gap = now - state.last;

    if (e.key === "Enter") {
        const code = state.buffer;
        const fast = state.intervals.length >= MIN_LENGTH - 1 &&
            state.intervals.every(i => i < MAX_INTERVAL_MS);
        if (code.length >= MIN_LENGTH && fast && gap < 150) {
            e.preventDefault();
            e.stopPropagation();
            // restore the value of the field the scanner "typed" into
            if (state.target && state.targetValue !== null) {
                state.target.value = state.targetValue;
                state.target.dispatchEvent(new Event("input", { bubbles: true }));
            }
            state.dotnetRef.invokeMethodAsync("OnScanned", code);
        }
        reset(now);
        return;
    }

    if (e.key.length !== 1) return; // special keys

    if (gap > 120) {
        // start of a new sequence
        state.buffer = "";
        state.intervals = [];
        const el = document.activeElement;
        state.target = el && (el.tagName === "INPUT" || el.tagName === "TEXTAREA") ? el : null;
        state.targetValue = state.target ? state.target.value : null;
    } else {
        state.intervals.push(gap);
    }
    state.buffer += e.key;
    state.last = now;
}

function reset(now) {
    state.buffer = "";
    state.intervals = [];
    state.target = null;
    state.targetValue = null;
    state.last = now;
}

export function focus(el) { if (el) el.focus(); }
export function scrollTop(el) { if (el) el.scrollTop = 0; }
