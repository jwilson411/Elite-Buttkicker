// The timeline editor was a mouse-only surface: points were created, moved and deleted by dragging
// on a canvas, and nothing on the page said where the selection was or what its numbers were.
//
// This drives the real js/timeline-editor.js in a DOM using only the keyboard - add a point, select
// it, type its time and intensity into number fields, nudge it with arrows, delete it, and manage
// layers - and checks that the pattern the editor would save (getHapticPattern) actually reflects
// those edits. A canvas cannot be inspected by assistive technology, so the inspector fields and the
// live region are the accessible interface being pinned here.
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

function close(actual, expected, description, tolerance = 1e-6) {
    check(Math.abs(actual - expected) <= tolerance,
        `${description} (expected ${expected}, got ${actual})`);
}

const virtualConsole = new VirtualConsole();
virtualConsole.on('jsdomError', (error) => {
    console.error('script error while loading the editor:', error.message);
    failures++;
});

const dom = new JSDOM('<!DOCTYPE html><html><body></body></html>', {
    runScripts: 'dangerously',
    url: 'http://localhost:8080/',
    virtualConsole
});
const { window } = dom;
const { document } = window;

// jsdom has no 2D canvas. Every drawing call the editor makes is accepted and discarded; the test is
// about the state behind the drawing, not the pixels.
function stubContext() {
    return {
        lineWidth: 1,
        strokeStyle: '',
        fillStyle: '',
        font: '',
        textAlign: '',
        globalAlpha: 1,
        fillRect() {},
        clearRect() {},
        strokeRect() {},
        beginPath() {},
        closePath() {},
        moveTo() {},
        lineTo() {},
        arc() {},
        fill() {},
        stroke() {},
        fillText() {},
        scale() {},
        save() {},
        restore() {},
        translate() {},
        setTransform() {},
        measureText() { return { width: 0 }; }
    };
}

window.HTMLCanvasElement.prototype.getContext = function () { return stubContext(); };

// Nothing is laid out in jsdom, so the canvas would measure zero and every coordinate would collapse
// onto the same point. Give it the size a real panel has.
window.Element.prototype.getBoundingClientRect = function () {
    return { width: 800, height: 300, left: 0, top: 0, right: 800, bottom: 300, x: 0, y: 0 };
};

let rafCalls = 0;
window.requestAnimationFrame = (fn) => { rafCalls++; fn(); return 0; };
window.cancelAnimationFrame = () => {};

function stubMatchMedia(matches) {
    window.matchMedia = () => ({ matches, media: '', addEventListener() {}, removeEventListener() {}, addListener() {}, removeListener() {} });
}

stubMatchMedia(false);

for (const file of ['js/dom.js', 'js/timeline-editor.js']) {
    const script = document.createElement('script');
    script.textContent = fs.readFileSync(path.join(wwwroot, file), 'utf8');
    document.head.appendChild(script);
}

check(typeof window.TimelineEditor === 'function', 'the timeline editor script defines TimelineEditor');
if (typeof window.TimelineEditor !== 'function') {
    process.exit(1);
}

const container = document.createElement('div');
document.body.appendChild(container);

const editor = new window.TimelineEditor();
editor.initialize(container);

const q = (selector) => container.querySelector(selector);

/// A keyboard event the editor will recognise: it switches on `code`, screen readers report `key`.
function key(target, code, keyName, init = {}) {
    const event = new window.KeyboardEvent('keydown', Object.assign({
        code,
        key: keyName,
        bubbles: true,
        cancelable: true
    }, init));
    target.dispatchEvent(event);
    return event;
}

function typeInto(input, value) {
    input.value = String(value);
    input.dispatchEvent(new window.Event('input', { bubbles: true }));
}

const canvas = q('#timelineCanvas');
const timeInput = q('#pointTimeInput');
const intensityInput = q('#pointIntensityInput');
const status = q('#pointSelectionStatus');

// ----- The canvas and its inspector are reachable and described -----

check(!!canvas, 'the timeline canvas exists');
equal(canvas.getAttribute('tabindex'), '0', 'the canvas is a keyboard focus stop');
check((canvas.getAttribute('aria-label') || '').length > 0, 'the canvas has an accessible name');
check(!!document.getElementById(canvas.getAttribute('aria-describedby'))
    || !!q('#' + canvas.getAttribute('aria-describedby')), 'the canvas points at its key help');
check((canvas.getAttribute('aria-keyshortcuts') || '').length > 0, 'the canvas advertises its shortcuts');

check(!!timeInput && !!intensityInput, 'the point inspector has time and intensity fields');
equal(timeInput.getAttribute('type'), 'number', 'time is a number field');
equal(intensityInput.getAttribute('type'), 'number', 'intensity is a number field');
check((timeInput.getAttribute('aria-label') || '').length > 0, 'the time field is named');
check((intensityInput.getAttribute('aria-label') || '').length > 0, 'the intensity field is named');

// With nothing selected the fields say so rather than offering numbers that edit nothing.
check(editor.selectedPoint === null || editor.selectedPoint === undefined, 'nothing is selected before any input');
check(timeInput.disabled, 'the time field starts disabled');
check(intensityInput.disabled, 'the intensity field starts disabled');
equal(status.textContent, 'No control point selected', 'the inspector says nothing is selected');
check(q('#removePointBtn').disabled, 'delete point is unavailable with no selection');

// ----- Adding points from the keyboard -----

const startingPoints = editor.controlPoints.length;

canvas.focus();
equal(document.activeElement, canvas, 'the canvas takes focus');

const addEvent = key(canvas, 'KeyA', 'a');
equal(editor.controlPoints.length, startingPoints + 1, 'A on the canvas adds a control point');
check(addEvent.defaultPrevented, 'A is consumed by the canvas');
check(!!editor.selectedPoint, 'the new point becomes the selection');
equal(editor.controlPoints.indexOf(editor.selectedPoint) >= 0, true, 'the selected point is one of the control points');

check(!timeInput.disabled, 'the time field is editable once a point is selected');
check(!intensityInput.disabled, 'the intensity field is editable once a point is selected');
check(/Point 1 of 1/.test(status.textContent), `the inspector names the selection (got "${status.textContent}")`);
check(q('.sr-only-announce').textContent.includes('Added point'), 'adding a point is announced');

const insertEvent = key(canvas, 'Insert', 'Insert');
equal(editor.controlPoints.length, startingPoints + 2, 'Insert adds a control point too');
check(insertEvent.defaultPrevented, 'Insert is consumed by the canvas');

// ----- Typing the numbers -----

const selected = editor.selectedPoint;

typeInto(timeInput, 900);
close(selected.time, 900, 'typing a time moves the selected point');

typeInto(intensityInput, 80);
close(selected.intensity, 80, 'typing an intensity changes the selected point');

// The saved pattern is what the numbers were typed into, not a separate mouse-only model.
let pattern = editor.getHapticPattern();
let saved = pattern.CustomCurvePoints.find(p => Math.abs(p.Time - 900 / editor.duration) < 1e-9);
check(!!saved, 'the typed time reaches the saved pattern');
if (saved) close(saved.Intensity, 0.8, 'the typed intensity reaches the saved pattern');

// The field being typed in keeps what was typed; the other one catches up.
typeInto(timeInput, 950);
equal(timeInput.value, '950', 'the time field is not rewritten under the caret');
equal(intensityInput.value, '80', 'the other field still shows the point');

// ----- Nudging with the arrows -----

const intensityBefore = selected.intensity;
const upEvent = key(canvas, 'ArrowUp', 'ArrowUp');
close(selected.intensity, intensityBefore + editor.pointIntensityStep, 'ArrowUp raises the intensity');
check(upEvent.defaultPrevented, 'ArrowUp is consumed while a point is selected');

const downEvent = key(canvas, 'ArrowDown', 'ArrowDown');
close(selected.intensity, intensityBefore, 'ArrowDown lowers it back');
check(downEvent.defaultPrevented, 'ArrowDown is consumed while a point is selected');

const timeBefore = selected.time;
key(canvas, 'ArrowRight', 'ArrowRight', { shiftKey: true });
close(selected.time, timeBefore + editor.pointTimeStep, 'Shift+ArrowRight moves the point later');

key(canvas, 'ArrowLeft', 'ArrowLeft', { shiftKey: true });
close(selected.time, timeBefore, 'Shift+ArrowLeft moves it back');

// ----- Traversing the points -----

key(canvas, 'Home', 'Home');
equal(editor.selectedPointIndex, 0, 'Home selects the first point');
key(canvas, 'End', 'End');
equal(editor.selectedPointIndex, editor.controlPoints.length - 1, 'End selects the last point');
key(canvas, 'PageUp', 'PageUp');
equal(editor.selectedPointIndex, editor.controlPoints.length - 2, 'Page Up steps back one point');
key(canvas, 'PageDown', 'PageDown');
equal(editor.selectedPointIndex, editor.controlPoints.length - 1, 'Page Down steps forward one point');

check(Number(timeInput.value) === Math.round(editor.selectedPoint.time),
    'the inspector follows the keyboard selection');

// ----- Deleting -----

// A curve needs two points, so build up to three before asking for one back.
while (editor.controlPoints.length < 3) {
    key(canvas, 'KeyA', 'a');
}

const beforeDelete = editor.controlPoints.length;
const deleteEvent = key(canvas, 'Delete', 'Delete');
equal(editor.controlPoints.length, beforeDelete - 1, 'Delete removes the selected point');
check(deleteEvent.defaultPrevented, 'Delete is consumed when it removed something');

// Down to the last two, Delete stops removing and stops swallowing the key.
while (editor.controlPoints.length > 2) {
    key(canvas, 'Delete', 'Delete');
}
const refusedDelete = key(canvas, 'Backspace', 'Backspace');
equal(editor.controlPoints.length, 2, 'the last two points are kept');
check(!refusedDelete.defaultPrevented, 'Backspace is left alone when there is nothing to delete');
check(q('#removePointBtn').disabled, 'delete point is unavailable at two points');

// ----- Tab still leaves the canvas -----

const tabEvent = key(canvas, 'Tab', 'Tab');
equal(tabEvent.defaultPrevented, false, 'Tab is never trapped on the canvas');

// ----- The Prev/Next/Delete buttons do the same as the keys -----

const clickOn = (node) => node.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));

editor.selectControlPointByIndex(0);
clickOn(q('#nextPointBtn'));
equal(editor.selectedPointIndex, 1, 'the Next button selects the following point');
clickOn(q('#prevPointBtn'));
equal(editor.selectedPointIndex, 0, 'the Prev button selects the previous point');

// ----- Layers from the keyboard -----

const layersBefore = editor.layers.length;
const addLayerBtn = q('#addLayerBtn');
check(!!addLayerBtn, 'the add layer button exists');
check((addLayerBtn.getAttribute('aria-label') || addLayerBtn.textContent).trim().length > 0, 'the add layer button is named');
clickOn(addLayerBtn);
equal(editor.layers.length, layersBefore + 1, 'the add layer button adds a layer');

const layerItems = () => Array.from(container.querySelectorAll('.layer-item'));
equal(layerItems().length, editor.layers.length, 'every layer is listed');
for (const item of layerItems()) {
    equal(item.getAttribute('tabindex'), '0', 'a layer item is a keyboard focus stop');
    check((item.getAttribute('aria-label') || '').length > 0, 'a layer item has an accessible name');
}

const firstLayer = layerItems()[0];
firstLayer.focus();
equal(document.activeElement, firstLayer, 'a layer item takes focus');
const layerCountBefore = editor.layers.length;
key(firstLayer, 'Delete', 'Delete');
equal(editor.layers.length, layerCountBefore - 1, 'Delete on a focused layer removes it');

// The last layer stays: an empty pattern has nothing to edit.
while (editor.layers.length > 1) {
    key(layerItems()[0], 'Delete', 'Delete');
}
key(layerItems()[0], 'Delete', 'Delete');
equal(editor.layers.length, 1, 'the last layer is kept');

// ----- The pattern still round-trips after all of that -----

pattern = editor.getHapticPattern();
equal(pattern.CustomCurvePoints.length, editor.controlPoints.length, 'the saved pattern has every control point');
equal(pattern.Layers.length, editor.layers.length, 'the saved pattern has every layer');

// ----- Reduced motion -----

stubMatchMedia(true);

let reducedEditor = null;
try {
    const reducedContainer = document.createElement('div');
    document.body.appendChild(reducedContainer);
    reducedEditor = new window.TimelineEditor();
    reducedEditor.initialize(reducedContainer);
} catch (error) {
    check(false, `constructing an editor under reduced motion threw: ${error.message}`);
}

if (reducedEditor) {
    equal(reducedEditor.reducedMotion, true, 'the editor reads the reduced motion preference');

    const framesBefore = rafCalls;
    reducedEditor.scheduleRender();
    equal(rafCalls, framesBefore, 'reduced motion renders straight away instead of waiting for a frame');
}

if (failures > 0) {
    console.error(`${failures} timeline keyboard assertion(s) failed`);
    process.exit(1);
}

console.log('ok');
process.exit(0);
