// Draws the remote screen. C# holds the relay connection and the protocol; this only turns an encoded region into
// pixels on a canvas, which is what the browser does well. One view per session id, so several could run at once.

const views = new Map();

export function init(id, canvas) {
  views.set(id, { canvas, context: canvas.getContext("2d", { alpha: false }) });
}

// Sizes the canvas and, if the video mode was set up, the video element too, to the shown display's pixels; CSS then
// scales either to fit the page. The video element also gets this from its own stream once decoded, but setting it
// up front avoids a moment at an unrelated size (or 0) while nothing has arrived yet.
export function resize(id, width, height) {
  const view = views.get(id);
  if (view) {
    view.canvas.width = width;
    view.canvas.height = height;
  }

  const video = videoViews.get(id);
  if (video) {
    video.videoElement.width = width;
    video.videoElement.height = height;
  }
}

// The video mode: a MediaSource fed chunks exactly as the encoder produced them, in order; the browser's own WebM
// demuxer reassembles them regardless of where they were cut. A chunk marked "init" starts a fresh MediaSource,
// since that's what a restarted encoder (a fresh key frame) also means: a new stream, not more of the old one.
const videoViews = new Map();

export function initVideo(id, videoElement) {
  videoViews.set(id, { videoElement, sourceBuffer: null, pending: [] });
}

function resetVideo(id, view) {
  const mediaSource = new MediaSource();
  view.sourceBuffer = null;
  view.pending = [];
  view.videoElement.src = URL.createObjectURL(mediaSource);
  mediaSource.addEventListener("sourceopen", () => {
    try {
      const sourceBuffer = mediaSource.addSourceBuffer('video/webm; codecs="vp9"');
      sourceBuffer.addEventListener("updateend", () => flushVideoPending(view));
      view.sourceBuffer = sourceBuffer;
      flushVideoPending(view);
    } catch (error) {
      console.debug("The video mode couldn't start; the browser may not support it.", error);
    }
  }, { once: true });
}

function flushVideoPending(view) {
  if (!view.sourceBuffer || view.sourceBuffer.updating) {
    return;
  }

  const next = view.pending.shift();
  if (!next) {
    return;
  }

  try {
    view.sourceBuffer.appendBuffer(next);
  } catch (error) {
    // The buffer was reset meanwhile (another init packet arrived); what's pending for it is gone too.
    console.debug("A video chunk couldn't be appended.", error);
    view.pending = [];
  }
}

export function appendVideoPacket(id, isInit, bytes) {
  const view = videoViews.get(id);
  if (!view) {
    return;
  }

  if (isInit) {
    resetVideo(id, view);
  }

  view.pending.push(bytes);
  flushVideoPending(view);
  view.videoElement.play().catch(() => {});
}

export function disposeVideo(id) {
  videoViews.delete(id);
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

// Sends the viewer's mouse and keyboard over whichever of the canvas (regions) or video (video mode) is shown, as
// fractions of the shown display (0..1) and the browser's own key names. Only the visible one receives DOM events,
// so both get the same listeners and the fraction is read off whichever element the event actually fired on.
// Only attached when the viewer may control; view-only never calls this.
export function attachInput(id, canvas, video, dotNet) {
  const view = views.get(id);
  if (!view) {
    return;
  }

  const elements = [canvas, video];
  for (const element of elements) {
    element.setAttribute("tabindex", "0");
  }

  const fraction = (event) => {
    const rect = event.currentTarget.getBoundingClientRect();
    const x = rect.width ? (event.clientX - rect.left) / rect.width : 0;
    const y = rect.height ? (event.clientY - rect.top) / rect.height : 0;
    return [Math.min(1, Math.max(0, x)), Math.min(1, Math.max(0, y))];
  };

  // One notch is 120; the device wants up and right positive, the opposite of the browser's vertical delta.
  const notch = (delta) => (delta > 0 ? -120 : delta < 0 ? 120 : 0);

  const handlers = {
    mousemove: (e) => { const [x, y] = fraction(e); dotNet.invokeMethodAsync("OnMove", x, y); },
    mousedown: (e) => { e.currentTarget.focus(); const [x, y] = fraction(e); dotNet.invokeMethodAsync("OnButton", e.button, true, x, y); e.preventDefault(); },
    mouseup: (e) => { const [x, y] = fraction(e); dotNet.invokeMethodAsync("OnButton", e.button, false, x, y); e.preventDefault(); },
    wheel: (e) => { const [x, y] = fraction(e); dotNet.invokeMethodAsync("OnWheel", x, y, notch(e.deltaY), -notch(e.deltaX)); e.preventDefault(); },
    contextmenu: (e) => e.preventDefault(),
    keydown: (e) => { dotNet.invokeMethodAsync("OnKey", e.key, e.code, true, e.ctrlKey, e.shiftKey, e.altKey, e.metaKey); e.preventDefault(); },
    keyup: (e) => { dotNet.invokeMethodAsync("OnKey", e.key, e.code, false, e.ctrlKey, e.shiftKey, e.altKey, e.metaKey); e.preventDefault(); },
    blur: () => dotNet.invokeMethodAsync("OnResetKeys"),
  };

  for (const element of elements) {
    for (const [name, handler] of Object.entries(handlers)) {
      element.addEventListener(name, handler);
    }
  }

  view.input = { elements, handlers };
}

export function fullScreen(element) {
  element?.requestFullscreen?.();
}

// The browser's own clipboard, which only the browser may touch. It may refuse without a user gesture or over plain
// HTTP, so both return whether they worked.
export async function readClipboard() {
  try {
    return await navigator.clipboard.readText();
  } catch {
    return null;
  }
}

export async function writeClipboard(text) {
  try {
    await navigator.clipboard.writeText(text);
    return true;
  } catch {
    return false;
  }
}

export function dispose(id) {
  const view = views.get(id);
  if (view?.input) {
    for (const element of view.input.elements) {
      for (const [name, handler] of Object.entries(view.input.handlers)) {
        element.removeEventListener(name, handler);
      }
    }
  }

  views.delete(id);
  disposeVideo(id);
}
