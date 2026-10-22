// The browser half of passkeys. WebAuthn exists only in JavaScript: the Blazor side fetches the server's options,
// hands them over as JSON, and gets back what the authenticator answered, also as JSON.

let pending;

export function isSupported() {
  return typeof window.PublicKeyCredential !== 'undefined'
    && typeof PublicKeyCredential.parseCreationOptionsFromJSON === 'function'
    && typeof PublicKeyCredential.parseRequestOptionsFromJSON === 'function';
}

// Whether the browser can offer passkeys in the email field's autofill.
export async function isAutofillSupported() {
  return isSupported() && (await PublicKeyCredential.isConditionalMediationAvailable?.()) === true;
}

export function create(optionsJson) {
  const options = PublicKeyCredential.parseCreationOptionsFromJSON(JSON.parse(optionsJson));
  return run(signal => navigator.credentials.create({ publicKey: options, signal }));
}

// Autofill waits, possibly for the whole visit, until the user picks a passkey from the email field's list.
export function get(optionsJson, autofill) {
  const options = PublicKeyCredential.parseRequestOptionsFromJSON(JSON.parse(optionsJson));
  return run(signal => navigator.credentials.get({ publicKey: options, mediation: autofill ? 'conditional' : undefined, signal }));
}

// Only one ceremony runs at a time: starting another, or leaving the page, cancels the one waiting.
export function abort() {
  pending?.abort();
  pending = undefined;
}

async function run(ceremony) {
  abort();
  const controller = new AbortController();
  pending = controller;
  try {
    const credential = await ceremony(controller.signal);
    return { credentialJson: JSON.stringify(credential) };
  } catch (error) {
    return { error: error.name };
  } finally {
    if (pending === controller) {
      pending = undefined;
    }
  }
}
