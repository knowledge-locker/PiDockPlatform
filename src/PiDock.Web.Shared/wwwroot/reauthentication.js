let trigger;
const origins = new WeakMap();
// Capture the initiating button before the server disables it while awaiting confirmation.
document.addEventListener('click', event => {
    const button = event.target.closest?.('button');
    if (button && !button.closest('dialog')) trigger = button;
}, true);

export function show(dialog) {
    origins.set(dialog, trigger);
    if (!dialog.open) dialog.showModal();
    dialog.querySelector('input[type="password"]')?.focus();
}

export function close(dialog) {
    dialog.close();
    const origin = origins.get(dialog);
    origins.delete(dialog);
    if (!origin?.isConnected) return;
    const restore = () => {
        if (origin.disabled) return false;
        if (document.activeElement === document.body) origin.focus();
        return true;
    };
    if (restore()) return;
    // The caller re-enables its button after the confirmation task completes.
    const observer = new MutationObserver(() => { if (!origin.isConnected || restore()) observer.disconnect(); });
    observer.observe(origin, { attributes: true, attributeFilter: ['disabled'] });
    setTimeout(() => observer.disconnect(), 2000);
}
