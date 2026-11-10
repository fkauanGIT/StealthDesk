// Draws the remote screen. C# holds the relay connection and the protocol; this only turns an encoded region or video
// frame into pixels on a canvas, which is what the browser does well. One view per session id, so several could run
// at once.

const views = new Map();

export function init(id, canvas) {
  views.set(id, { canvas, context: canvas.getContext("2d", { alpha: false }) });
}

// Sizes both canvases to the shown display's pixels; CSS then scales either to fit the page. Also feeds the video
// canvas's size to its decoder, which needs it again on every reconfigure (see appendVideoPacket).
export function resize(id, width, height) {
  const view = views.get(id);
  if (view) {
    view.canvas.width = width;
    view.canvas.height = height;
  }

  const video = videoViews.get(id);
  if (video) {
    video.canvas.width = width;
    video.canvas.height = height;
  }
}

// The video mode: each packet is one already-encoded VP9 frame (see VideoPacket's doc comment), decoded with the
// browser's own WebCodecs VideoDecoder straight onto a canvas, with no muxed container (WebM/MediaSource) and the
// buffering that comes with one in between. A packet marked "init" is always a key frame and always means a fresh
// encode (the start of video mode, a requested key frame, or a display change), so the decoder is reconfigured then.
const videoViews = new Map();

// The server encodes 8-bit 4:2:0 VP9 at profile 0; this codec string names exactly that, and never changes.
const VIDEO_CODEC = "vp09.00.10.08";

export function initVideo(id, canvas) {
  videoViews.set(id, { canvas, context: canvas.getContext("2d", { alpha: false }), decoder: null, nextTimestamp: 0 });
}

function resetDecoder(view) {
  view.decoder?.close();
  view.nextTimestamp = 0;
  view.decoder = new VideoDecoder({
    output: (frame) => {
      view.context.drawImage(frame, 0, 0, view.canvas.width, view.canvas.height);
      frame.close();
    },
    error: (error) => console.debug("The video decoder failed; it restarts on the next key frame.", error),
  });
  view.decoder.configure({ codec: VIDEO_CODEC, codedWidth: view.canvas.width, codedHeight: view.canvas.height });
}

export function appendVideoPacket(id, isInit, bytes) {
  const view = videoViews.get(id);
  if (!view) {
    return;
  }

  if (isInit) {
    resetDecoder(view);
  }

  if (view.decoder.state !== "configured") {
    return;
  }

  try {
    view.decoder.decode(new EncodedVideoChunk({
      type: isInit ? "key" : "delta",
      timestamp: view.nextTimestamp++,
      data: bytes,
    }));
  } catch (error) {
    // The decoder closed itself after an error; it restarts on the next key frame.
    console.debug("A video frame couldn't be decoded.", error);
  }
}

export function disposeVideo(id) {
  const view = videoViews.get(id);
  view?.decoder?.close();
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

// Sends the viewer's mouse and keyboard over whichever of the two canvases (regions or video mode) is shown, as
// fractions of the shown display (0..1) and the browser's own key names. Only the visible one receives DOM events,
// so both get the same listeners and the fraction is read off whichever element the event actually fired on.
// Only attached when the viewer may control; view-only never calls this.
export function attachInput(id, canvas, videoCanvas, dotNet) {
  const view = views.get(id);
  if (!view) {
    return;
  }

  const elements = [canvas, videoCanvas];
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
