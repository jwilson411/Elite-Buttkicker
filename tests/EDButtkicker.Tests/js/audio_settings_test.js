// The Audio tab's two sliders are the page's view of settings that live in the service, and for a
// while they were only the markup's defaults: opening the tab showed 80% and 40Hz whatever was
// configured, and moving them changed nothing that outlived the navigation.
//
// This loads the real index.html plus the real js/dom.js and js/app.js into a DOM, answers the
// settings route with numbers that are not the defaults, and checks that opening the tab shows
// them - in the inputs and in the numbers printed beside them - and that the Save button sends
// them back, only when it is pressed.
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

const virtualConsole = new VirtualConsole();
virtualConsole.on('jsdomError', (error) => {
    console.error('script error while loading the page:', error.message);
    failures++;
});

const html = fs.readFileSync(path.join(wwwroot, 'index.html'), 'utf8');
const dom = new JSDOM(html, { runScripts: 'dangerously', url: 'http://localhost:47811/', virtualConsole });
const { window } = dom;
const { document } = window;

const SAVED = { maxIntensity: 75, defaultFrequency: 35 };

// Every request the page makes is recorded, so the test can also say what was *not* sent.
const requests = [];
let saveStatus = 200;

window.fetch = (url, options) => {
    requests.push({ url, options });

    const respond = (body, status = 200) => Promise.resolve({
        ok: status >= 200 && status < 300,
        status,
        json: async () => body,
        text: async () => JSON.stringify(body)
    });

    if (url === '/api/usersettings/current') {
        return respond({ audio: SAVED });
    }

    if (url === '/api/usersettings/save') {
        return respond({ message: 'saved' }, saveStatus);
    }

    // Nothing else is under test here; answer "unavailable" so those paths finish instead of
    // hanging, and no rejection escapes.
    return respond({}, 503);
};

for (const file of ['js/dom.js', 'js/app.js']) {
    const script = document.createElement('script');
    script.textContent = fs.readFileSync(path.join(wwwroot, file), 'utf8');
    document.head.appendChild(script);
}

// index.html is fully parsed by the time the scripts above are injected, so the page's own
// DOMContentLoaded wiring - which both constructs the app and binds the data-bind handlers - has
// to be kicked off by hand.
document.dispatchEvent(new window.Event('DOMContentLoaded'));

const click = (node) => node.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));

// The loads are asynchronous; let the fetch chains settle before reading the DOM.
const settle = () => new Promise(resolve => window.setTimeout(resolve, 0));

const maxIntensity = document.getElementById('maxIntensity');
const defaultFrequency = document.getElementById('defaultFrequency');
const intensityValue = document.querySelector('#maxIntensity + .range-value');
const frequencyValue = document.querySelector('#defaultFrequency + .range-value');

async function main() {
    check(!!maxIntensity && !!defaultFrequency, 'both sliders are in the markup');
    check(!!intensityValue && !!frequencyValue, 'each slider has a range-value beside it');

    // ----- Opening the tab loads the saved settings -----

    click(document.querySelector('.nav-tab[data-tab="audio"]'));
    await settle();

    check(requests.some(request => request.url === '/api/usersettings/current'),
        'opening the audio tab asks for the current settings');

    equal(maxIntensity.value, '75', 'the intensity slider shows the saved value');
    equal(defaultFrequency.value, '35', 'the frequency slider shows the saved value');
    equal(intensityValue.textContent, '75', 'the number beside the intensity slider matches it');
    equal(frequencyValue.textContent, '35', 'the number beside the frequency slider matches it');

    // ----- Dragging a slider updates its number, and saves nothing -----

    const before = requests.length;

    maxIntensity.value = '90';
    maxIntensity.dispatchEvent(new window.Event('input', { bubbles: true }));
    equal(intensityValue.textContent, '90', 'moving the slider updates its number live');

    await settle();
    equal(requests.length, before, 'moving a slider does not save anything by itself');

    // ----- The Save button sends what the sliders show -----

    const saveButton = document.querySelector('#audio [data-bind="saveAudioSettings"]');
    check(!!saveButton, 'the audio tab has a save button');

    click(saveButton);
    await settle();

    const save = requests.find(request => request.url === '/api/usersettings/save');
    check(!!save, 'pressing save posts to the settings route');

    if (save) {
        equal(save.options.method, 'POST', 'the save is a POST');
        const body = JSON.parse(save.options.body);
        equal(body.maxIntensity, 90, 'the saved intensity is the slider value, as a number');
        equal(body.defaultFrequency, 35, 'the saved frequency is the slider value, as a number');
    }

    const toasts = Array.from(document.querySelectorAll('#toastContainer .toast'));
    check(toasts.some(toast => toast.textContent.includes('Audio settings saved!')),
        'a successful save says so');

    // ----- A refused save says so rather than claiming success -----

    saveStatus = 400;
    click(saveButton);
    await settle();

    const errorToasts = Array.from(document.querySelectorAll('#toastContainer .toast'));
    check(errorToasts.some(toast => toast.textContent.includes('Error saving audio settings')),
        'a refused save is reported as an error');

    if (failures > 0) {
        console.error(`${failures} audio settings assertion(s) failed`);
        process.exit(failures);
    }

    console.log('ok');
    process.exit(0);
}

main().catch(error => {
    console.error('the test itself failed:', error);
    process.exit(1);
});
