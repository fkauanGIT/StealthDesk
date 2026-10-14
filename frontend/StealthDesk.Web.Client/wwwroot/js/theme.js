// The page's theme: light or dark on <html>, from the user's choice. "auto" follows the device, also when it
// changes while the page is open. The choice is remembered in this browser so the next visit starts with it; the
// script in index.html reads it back before anything is drawn.

const key = 'stealthdesk:theme';
const system = window.matchMedia('(prefers-color-scheme: dark)');
let mode = 'auto';

function paint() {
  const dark = mode === 'dark' || (mode === 'auto' && system.matches);
  document.documentElement.dataset.theme = dark ? 'dark' : 'light';
}

system.addEventListener('change', () => {
  if (mode === 'auto') {
    paint();
  }
});

// The choice this browser remembers, or null when it was never made here.
export function stored() {
  try {
    return localStorage.getItem(key);
  } catch {
    return null;
  }
}

export function apply(chosen) {
  mode = chosen;
  try {
    localStorage.setItem(key, chosen);
  } catch {
    // Storage can be off; the theme still applies to this page.
  }

  paint();
}
