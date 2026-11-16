export async function copyPrompt(id) {
    const field = document.getElementById(id);
    if (!field || !field.value || !navigator.clipboard) return false;
    try {
        await navigator.clipboard.writeText(field.value);
        return true;
    } catch {
        field.focus();
        field.select();
        return false;
    }
}
