// The terminal's input handles some keys itself, so the browser's own action for them is held back: Enter runs what
// is typed instead of adding a line, Shift+Enter adds one only in multiline mode, Up and Down walk the history
// instead of moving the caret unless the input has several lines, Tab completes instead of leaving the input, and
// Ctrl+Space and Ctrl+. list the completions. The page reads the keys as usual.
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
    } else if (e.key === 'Tab' || (e.ctrlKey && (e.key === ' ' || e.key === '.'))) {
      e.preventDefault();
    }
  });
}

// Where the caret is: completions are for what is typed up to it.
export function caret(input) {
  return input ? input.selectionStart ?? input.value.length : 0;
}

// Puts the caret after a completion, once the input shows it.
export function placeCaret(input, position) {
  if (input) {
    input.focus();
    input.setSelectionRange(position, position);
  }
}

// Enter in the completions' filter picks a match and sends the focus back to the input; held back, it doesn't then
// add a line there.
export function holdEnter(element) {
  if (element && !element.dataset.holdEnter) {
    element.dataset.holdEnter = 'on';
    element.addEventListener('keydown', e => {
      if (e.key === 'Enter') {
        e.preventDefault();
      }
    });
  }
}
