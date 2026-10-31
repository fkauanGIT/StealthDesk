// Draws the remote screen. C# holds the relay connection and the protocol; this only turns an encoded region into
// pixels on a canvas, which is what the browser does well. One view per session id, so several could run at once.

const views = new Map();

export function init(id, canvas) {
  views.set(id, { canvas, context: canvas.getContext("2d", { alpha: false }) });
}

// Sizes the canvas to the shown display's pixels; CSS then scales it to fit the page.
export function resize(id, width, height) {
  const view = views.get(id);
  if (view) {
    view.canvas.width = width;
    view.canvas.height = height;
  }
}

export async function drawRegion(id, x, y, width, height, encoding, bytes) {
  const view = views.get(id);
  if (!view) {
    return;
  }

  const type = encoding === 1 ? "image/png" : "image/jpeg";
  try {
    const bitmap = await createImageBitmap(new Blob([bytes], { type }));
    view.context.drawImage(bitmap, x, y, width, height);
    bitmap.close();
  } catch (error) {
    console.debug("A region couldn't be drawn.", error);
  }
}

// Sends the viewer's mouse and keyboard over the canvas to C#, as fractions of the shown display (0..1) and the
// browser's own key names. Only attached when the viewer may control; view-only never calls this.
export function attachInput(id, canvas, dotNet) {
  const view = views.get(id);
  if (!view) {
    return;
  }

  canvas.setAttribute("tabindex", "0");

  const fraction = (event) => {
    const rect = canvas.getBoundingClientRect();
    const x = rect.width ? (event.clientX - rect.left) / rect.width : 0;
    const y = rect.height ? (event.clientY - rect.top) / rect.height : 0;
    return [Math.min(1, Math.max(0, x)), Math.min(1, Math.max(0, y))];
  };

  // One notch is 120; the device wants up and right positive, the opposite of the browser's vertical delta.
  const notch = (delta) => (delta > 0 ? -120 : delta < 0 ? 120 : 0);

  const handlers = {
    mousemove: (e) => { const [x, y] = fraction(e); dotNet.invokeMethodAsync("OnMove", x, y); },
    mousedown: (e) => { canvas.focus(); const [x, y] = fraction(e); dotNet.invokeMethodAsync("OnButton", e.button, true, x, y); e.preventDefault(); },
    mouseup: (e) => { const [x, y] = fraction(e); dotNet.invokeMethodAsync("OnButton", e.button, false, x, y); e.preventDefault(); },
    wheel: (e) => { const [x, y] = fraction(e); dotNet.invokeMethodAsync("OnWheel", x, y, notch(e.deltaY), -notch(e.deltaX)); e.preventDefault(); },
    contextmenu: (e) => e.preventDefault(),
    keydown: (e) => { dotNet.invokeMethodAsync("OnKey", e.key, e.code, true, e.ctrlKey, e.shiftKey, e.altKey, e.metaKey); e.preventDefault(); },
    keyup: (e) => { dotNet.invokeMethodAsync("OnKey", e.key, e.code, false, e.ctrlKey, e.shiftKey, e.altKey, e.metaKey); e.preventDefault(); },
    blur: () => dotNet.invokeMethodAsync("OnResetKeys"),
  };

  for (const [name, handler] of Object.entries(handlers)) {
    canvas.addEventListener(name, handler);
  }

  view.input = { canvas, handlers };
}

export function fullScreen(element) {
  element?.requestFullscreen?.();
}

export function dispose(id) {
  const view = views.get(id);
  if (view?.input) {
    for (const [name, handler] of Object.entries(view.input.handlers)) {
      view.input.canvas.removeEventListener(name, handler);
    }
  }

  views.delete(id);
}
