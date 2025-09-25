export function getHardwareConcurrency() {
    // Some environments may not expose it; default to 1
    return (typeof navigator !== "undefined" && navigator.hardwareConcurrency) ? navigator.hardwareConcurrency : 1;
}
