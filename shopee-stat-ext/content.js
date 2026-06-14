// Content script intentionally left as a no-op.
//
// The search flow is driven entirely from background.js via chrome.scripting
// (MAIN world) + CDP trusted input, and product data is collected by reading
// the rendered DOM. The previous fetch-interceptor here relayed responses via
// chrome.runtime.sendMessage({action:'intercepted'}), but background.js has no
// onMessage listener for it, so that channel was dead. Kept declared in
// manifest.json so the extension layout is unchanged.
