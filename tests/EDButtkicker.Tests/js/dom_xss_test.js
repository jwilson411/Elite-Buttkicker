// Every string the UI shows - journal event names, pattern names, context strings, toast messages,
// audio device names - comes from data this application does not author. This runs the real
// js/dom.js in a DOM and pins that a markup payload in any of those places stays a visible string:
// no element is created from it, and the page still shows it verbatim.
const { JSDOM } = require('jsdom');
const fs = require('fs');
const path = require('path');

const wwwroot = path.resolve(__dirname, '../../../src/EDButtkicker/wwwroot/js');

const { window } = new JSDOM('<!doctype html><html><body></body></html>', { runScripts: 'dangerously' });
const script = window.document.createElement('script');
script.textContent = fs.readFileSync(path.join(wwwroot, 'dom.js'), 'utf8');
window.document.head.appendChild(script);

const payloads = ['<img src=x onerror=alert(1)>', '<script>window.__xss=1</script>', '"><svg/onload=alert(1)>'];

// The five kinds of untrusted text the renderers put on the page.
const kinds = ['event-type', 'pattern-name', 'context', 'toast-message', 'device-name'];

for (const payload of payloads) {
    for (const kind of kinds) {
        const root = window.document.createElement('div');
        window.document.body.appendChild(root);

        window.dom.replace(root, window.dom.el('div', { className: kind, text: payload }));

        if (root.querySelector('img, script, svg')) {
            console.error('payload was parsed as markup:', kind, payload);
            process.exit(1);
        }

        if (!root.textContent.includes(payload)) {
            console.error('payload did not survive as text:', kind, payload);
            process.exit(1);
        }

        root.remove();
    }
}

if (window.__xss !== undefined) {
    console.error('a payload executed');
    process.exit(1);
}

// The dashboard renders the same five kinds, so it must not have an innerHTML assignment left.
const appJs = fs.readFileSync(path.join(wwwroot, 'app.js'), 'utf8');
if (/innerHTML\s*=/.test(appJs)) {
    console.error('app.js still assigns innerHTML');
    process.exit(1);
}

console.log('ok');
process.exit(0);
