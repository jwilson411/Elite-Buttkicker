// The configuration UI is driven as much by keyboard and screen reader as by mouse, and none of
// that is visible in a screenshot: the tabs have to be a tablist, the modals have to be dialogs
// that trap and restore focus, and status and toasts have to be announced.
//
// This loads the real index.html plus the real js/dom.js and js/app.js into a DOM and checks those
// semantics the way an assistive technology would read them - roles, names, states - and drives
// the keyboard interactions. It cannot substitute for a run with a real screen reader (no NVDA
// here); it pins the markup and behaviour that a screen reader depends on.
const { JSDOM, VirtualConsole } = require('jsdom');
const fs = require('fs');
const path = require('path');

const wwwroot = path.resolve(__dirname, '../../../src/EDButtkicker/wwwroot');

let failures = 0;

function check(condition, description) {
    if (!condition) {
        console.error('FAIL:', description);
        failures++;
    }
}

function equal(actual, expected, description) {
    check(actual === expected, `${description} (expected ${JSON.stringify(expected)}, got ${JSON.stringify(actual)})`);
}

// A script that throws while loading would leave every later assertion meaningless, so surface it.
const virtualConsole = new VirtualConsole();
virtualConsole.on('jsdomError', (error) => {
    console.error('script error while loading the page:', error.message);
    failures++;
});

const html = fs.readFileSync(path.join(wwwroot, 'index.html'), 'utf8');
const dom = new JSDOM(html, { runScripts: 'dangerously', url: 'http://localhost:8080/', virtualConsole });
const { window } = dom;
const { document } = window;

// There is no local service behind this DOM. Every call answers "unavailable" so the page takes its
// error paths instead of hanging, and no rejection escapes.
window.fetch = () => Promise.resolve({
    ok: false,
    status: 503,
    json: async () => ({}),
    text: async () => ''
});

for (const file of ['js/dom.js', 'js/app.js']) {
    const script = document.createElement('script');
    script.textContent = fs.readFileSync(path.join(wwwroot, file), 'utf8');
    document.head.appendChild(script);
}

// index.html is fully parsed by the time the scripts above are injected, so the page's own
// DOMContentLoaded wiring has to be kicked off by hand.
document.dispatchEvent(new window.Event('DOMContentLoaded'));

const key = (target, name, init = {}) => target.dispatchEvent(
    new window.KeyboardEvent('keydown', Object.assign({ key: name, bubbles: true, cancelable: true }, init)));

// ----- Tabs -----

const nav = document.querySelector('.main-nav');
equal(nav.getAttribute('role'), 'tablist', 'main nav is a tablist');
check(!!nav.getAttribute('aria-label'), 'tablist has an accessible name');

const tabs = Array.from(document.querySelectorAll('.nav-tab'));
check(tabs.length === 6, 'all six tabs are present');

for (const tab of tabs) {
    equal(tab.getAttribute('role'), 'tab', `${tab.dataset.tab} tab has role=tab`);

    const panel = document.getElementById(tab.getAttribute('aria-controls'));
    check(!!panel, `${tab.dataset.tab} tab controls an existing panel`);
    equal(panel.getAttribute('role'), 'tabpanel', `${panel.id} panel has role=tabpanel`);
    equal(panel.getAttribute('aria-labelledby'), tab.id, `${panel.id} panel is labelled by its tab`);
    check(tab.textContent.trim().length > 0, `${tab.dataset.tab} tab has a visible name`);
}

function assertSelection(expectedTab, description) {
    for (const tab of tabs) {
        const selected = tab.dataset.tab === expectedTab;
        equal(tab.getAttribute('aria-selected'), String(selected), `${description}: ${tab.dataset.tab} aria-selected`);
        equal(tab.tabIndex, selected ? 0 : -1, `${description}: ${tab.dataset.tab} roving tabindex`);
        equal(document.getElementById(tab.dataset.tab).hidden, !selected, `${description}: ${tab.dataset.tab} panel hidden`);
    }
}

assertSelection('dashboard', 'initial markup');

// Right arrow moves to the next tab and takes focus with it.
tabs[0].focus();
key(tabs[0], 'ArrowRight');
assertSelection('patterns', 'after ArrowRight');
equal(document.activeElement, tabs[1], 'ArrowRight moves focus to the newly selected tab');

key(tabs[1], 'ArrowLeft');
assertSelection('dashboard', 'after ArrowLeft');

key(tabs[0], 'End');
assertSelection('settings', 'after End');
equal(document.activeElement, tabs[tabs.length - 1], 'End focuses the last tab');

key(tabs[tabs.length - 1], 'Home');
assertSelection('dashboard', 'after Home');

// Clicking still works, and leaves the same state the keyboard does.
tabs[3].dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
assertSelection('journal', 'after click');
tabs[0].dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
assertSelection('dashboard', 'back on the dashboard');

// ----- Icon-only controls -----

const named = {
    refreshHealth: 'Refresh system health',
    loadRecentEvents: 'Refresh recent events'
};

for (const [binding, label] of Object.entries(named)) {
    const button = document.querySelector(`[data-bind="${binding}"]`);
    equal(button.getAttribute('aria-label'), label, `${binding} button is named`);
}

for (const binding of ['hidePatternTester', 'closeSetupWizard', 'closePatternModal', 'refreshJournalFiles']) {
    const button = document.querySelector(`button[data-bind="${binding}"]`);
    const name = button.getAttribute('aria-label') || button.textContent.trim();
    check(name.length > 0 && name !== '×', `${binding} button has an accessible name, not just a glyph`);
}

// ----- Dialogs -----

for (const id of ['setupWizard', 'patternModal']) {
    const modal = document.getElementById(id);
    equal(modal.getAttribute('role'), 'dialog', `${id} has role=dialog`);
    equal(modal.getAttribute('aria-modal'), 'true', `${id} is modal`);

    const heading = document.getElementById(modal.getAttribute('aria-labelledby'));
    check(!!heading && heading.textContent.trim().length > 0, `${id} is labelled by a heading with text`);
    check(modal.contains(heading), `${id} label lives inside the dialog`);

    const close = modal.querySelector('.modal-close');
    check((close.getAttribute('aria-label') || '').length > 0, `${id} close button has an accessible name`);

    check(modal.hidden, `${id} starts hidden`);
}

const trigger = document.querySelector('[data-bind="openSetupWizard"]');
trigger.focus();
window.openDialog('setupWizard');

const wizard = document.getElementById('setupWizard');
check(!wizard.hidden && wizard.classList.contains('active'), 'the wizard opens');
check(wizard.contains(document.activeElement), 'focus moves into the wizard');

// Tab from the last control inside the dialog wraps back to the first instead of escaping it.
const focusables = Array.from(wizard.querySelectorAll('button:not([disabled]), a[href], input:not([disabled])'));
focusables[focusables.length - 1].focus();
key(document.activeElement, 'Tab');
equal(document.activeElement, focusables[0], 'Tab wraps to the first control in the dialog');

focusables[0].focus();
key(document.activeElement, 'Tab', { shiftKey: true });
equal(document.activeElement, focusables[focusables.length - 1], 'Shift+Tab wraps to the last control');

// Escape closes it and hands focus back to whatever opened it.
key(document.activeElement, 'Escape');
check(wizard.hidden && !wizard.classList.contains('active'), 'Escape closes the wizard');
equal(document.activeElement, trigger, 'closing restores focus to the opener');

// The close button does the same as Escape.
trigger.focus();
window.openDialog('setupWizard');
wizard.querySelector('.modal-close').dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
check(wizard.hidden, 'the close button closes the wizard');
equal(document.activeElement, trigger, 'the close button restores focus too');

const patternModal = document.getElementById('patternModal');
window.openDialog('patternModal');
check(!patternModal.hidden, 'the pattern dialog opens');
key(document.activeElement, 'Escape');
check(patternModal.hidden, 'Escape closes the pattern dialog');

// ----- Status and toasts -----

const status = document.getElementById('systemStatus');
const statusIsLive = status.getAttribute('role') === 'status' || status.getAttribute('aria-live') === 'polite';
check(statusIsLive, 'system status is a live region');
check(status.querySelector('.status-text').textContent.trim().length > 0,
    'system status says its state in words, not only in colour');

const toastContainer = document.getElementById('toastContainer');
equal(toastContainer.getAttribute('aria-live'), 'polite', 'the toast container is a polite live region');

window.eval('app.showToast("Saved the thing", "success")');
window.eval('app.showToast("Could not reach the service", "error")');

const toasts = Array.from(toastContainer.querySelectorAll('.toast'));
equal(toasts.length, 2, 'both toasts were added');
equal(toasts[0].getAttribute('role'), 'status', 'a normal toast is a status');
check(toasts[0].textContent.includes('Saved the thing'), 'the toast shows its message');
equal(toasts[1].getAttribute('role'), 'alert', 'an error toast is an alert');
check(toasts[1].textContent.includes('Could not reach the service'), 'the error toast shows its message');

if (failures > 0) {
    console.error(`${failures} accessibility assertion(s) failed`);
    process.exit(1);
}

console.log('ok');
process.exit(0);
