// Staff alarm on the kiosk speaker (Web Audio – no sound files).
// Edge has to be started with --autoplay-policy=no-user-gesture-required (deploy\kiosk.cmd) so the alarm can sound
// before anyone touched the screen; otherwise it starts with the first touch.

// tones: [frequency Hz, start s, duration s]; the pattern repeats every `period` seconds
const PATTERNS = {
    // help request – a calm "ding-dong"
    help: { period: 2.5, tones: [[880, 0, 0.35], [660, 0.4, 0.5]] },
    // device / connection fault – urgent triple beep
    fault: { period: 1.5, tones: [[1000, 0, 0.12], [1000, 0.2, 0.12], [1000, 0.4, 0.12]] }
};

let ctx = null;
let timer = null;
let current = null;

function context() {
    if (!ctx) {
        ctx = new AudioContext();
        // autoplay blocked – resume with the first touch
        document.addEventListener("pointerdown", () => ctx.resume(), { capture: true });
    }
    if (ctx.state === "suspended") ctx.resume();
    return ctx;
}

function play(pattern, volume) {
    const ac = context();
    const t0 = ac.currentTime + 0.05;
    for (const [freq, start, duration] of pattern.tones) {
        const osc = ac.createOscillator();
        const gain = ac.createGain();
        osc.type = "square";
        osc.frequency.value = freq;
        // short attack/release – no clicks
        gain.gain.setValueAtTime(0, t0 + start);
        gain.gain.linearRampToValueAtTime(volume, t0 + start + 0.01);
        gain.gain.setValueAtTime(volume, t0 + start + duration - 0.02);
        gain.gain.linearRampToValueAtTime(0, t0 + start + duration);
        osc.connect(gain).connect(ac.destination);
        osc.start(t0 + start);
        osc.stop(t0 + start + duration + 0.01);
    }
}

/** Starts (or switches) the alarm; volume 0–100. */
export function start(name, volume) {
    const pattern = PATTERNS[name] ?? PATTERNS.fault;
    const key = `${name}:${volume}`;
    if (current === key) return;
    stop();
    if (volume <= 0) return;
    current = key;
    // square wave is loud – 100 % maps to 0.3 gain
    const gain = Math.min(volume, 100) / 100 * 0.3;
    play(pattern, gain);
    timer = setInterval(() => play(pattern, gain), pattern.period * 1000);
}

export function stop() {
    if (timer) clearInterval(timer);
    timer = null;
    current = null;
}
