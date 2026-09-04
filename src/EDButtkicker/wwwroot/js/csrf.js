// Anti-forgery token for state-changing requests.
//
// The server refuses any POST/PUT/DELETE that does not carry the token it handed this page, so a
// site the user happens to have open cannot drive the local API through their browser. Rather than
// thread a header through every call site, this wraps fetch once: mutations get the header, safe
// requests are untouched. Load it before any script that talks to the API.
(function () {
    const HEADER = 'X-CSRF-Token';
    const COOKIE = 'EDBK-CSRF-JS';
    const SAFE_METHODS = ['GET', 'HEAD', 'OPTIONS'];

    const nativeFetch = window.fetch.bind(window);
    let pending = null;

    function tokenFromCookie() {
        const match = document.cookie.match(new RegExp('(?:^|; )' + COOKIE + '=([^;]*)'));
        return match ? decodeURIComponent(match[1]) : null;
    }

    // The cookie is set on any GET of the UI; the endpoint is the fallback for a page that was
    // opened before the cookie existed. Only one request is ever in flight.
    function token() {
        const fromCookie = tokenFromCookie();
        if (fromCookie) {
            return Promise.resolve(fromCookie);
        }

        if (!pending) {
            pending = nativeFetch('/api/csrf', { credentials: 'same-origin' })
                .then(response => response.json())
                .then(body => body.token)
                .finally(() => { pending = null; });
        }

        return pending;
    }

    window.fetch = async function (input, init) {
        const options = init || {};
        const method = (options.method || (input && input.method) || 'GET').toUpperCase();

        if (SAFE_METHODS.includes(method)) {
            return nativeFetch(input, init);
        }

        const headers = new Headers(options.headers || (input && input.headers) || undefined);
        headers.set(HEADER, await token());

        return nativeFetch(input, { ...options, headers, credentials: 'same-origin' });
    };

    // Warm the token up front so the first mutation does not pay for a round trip.
    token();
})();
