// The terminal's input handles some keys itself, so the browser's own action for them is held back: Enter runs what
// is typed instead of adding a line, Shift+Enter adds one only in multiline mode, and Up and Down walk the history
// instead of moving the caret, unless the input has several lines. The page reads the keys as usual.
export function attach(input) {
  if (!input || input.dataset.terminalKeys) {
    return;
  }

  input.dataset.terminalKeys = 'on';
  input.addEventListener('keydown', e => {
    const multiline = input.dataset.multiline === 'true';
    if (e.key === 'Enter' && (!e.shiftKey || !multiline)) {
      e.preventDefault();
    } else if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && !multiline) {
      e.preventDefault();
    }
  });
}
