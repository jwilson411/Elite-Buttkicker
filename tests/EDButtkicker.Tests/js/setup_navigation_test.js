// Exercise the shipped markup, DOM helpers and event handlers with deliberately reordered GETs.
const { JSDOM } = require('jsdom');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const wwwroot = path.resolve(__dirname, '../../../src/EDButtkicker/wwwroot');
const response = (body, status = 200) => ({ ok: status < 400, status, json: async () => body });
const status = (current = 'journal') => ({
    current_step: current,
    steps: ['journal', 'audio-device', 'audio-test', 'finish'].map(id => ({ id, title: id })),
    incomplete_steps: ['finish']
});
const tick = () => new Promise(resolve => setImmediate(resolve));

async function fixture() {
    const page = new JSDOM(fs.readFileSync(path.join(wwwroot, 'index.html'), 'utf8'), {
        runScripts: 'outside-only', url: 'http://127.0.0.1:47811/'
    });
    const w = page.window;
    // Prevent automatic app construction; create a single instance after the parser event.
    await new Promise(resolve => w.document.addEventListener('DOMContentLoaded', resolve, { once: true }));
    const pending = [];
    let postStatus = 200;
    w.console.error = () => {};
    w.fetch = (url, options = {}) => {
        if (options.method === 'POST') {
            return Promise.resolve(response({ setup: status(), path: 'journal', device: { name: 'Test' }, played: true, reason: 'Played' }, postStatus));
        }
        if (url === '/api/setup/journal/candidates' || url === '/api/audio/devices') {
            return new Promise((resolve, reject) => pending.push({ url, resolve, reject }));
        }
        return Promise.resolve(response({}, 503));
    };
    for (const file of ['dom.js', 'app.js']) vm.runInContext(fs.readFileSync(path.join(wwwroot, 'js', file), 'utf8'), page.getInternalVMContext());
    w.document.dispatchEvent(new w.Event('DOMContentLoaded'));
    vm.runInContext('app.setup = ' + JSON.stringify(status()), page.getInternalVMContext());
    const click = selector => {
        const node = w.document.querySelector(selector);
        assert.ok(node, `missing control: ${selector}`);
        node.click();
    };
    const open = step => w.showSetupStep(step);
    const panel = () => w.document.getElementById('setupPanel').textContent;
    const active = () => w.document.querySelector('.setup-step.active .setup-step-title').textContent;
    return { page, w, pending, click, open, panel, active, failPosts: () => postStatus = 500 };
}

async function run() {
    let checks = 0;
    async function scenario(name, test) {
        const f = await fixture();
        try { await test(f); checks++; console.log('PASS:', name); }
        finally { f.page.window.close(); }
    }
    for (const [step, next, heading, action] of [
        ['journal', 'audio-device', '2. Choose', f => f.w.confirmJournalPath('journal')],
        ['audio-device', 'audio-test', '3. Run', f => f.w.selectSetupAudioDevice('test', 0)]
    ]) {
        await scenario(`${step} confirmation discards old section responses`, async f => {
            f.open(step);
            await action(f);
            // Resolve current section first and all previous section requests last.
            const old = f.pending.filter(p => p.url === (step === 'journal' ? '/api/setup/journal/candidates' : '/api/audio/devices'));
            const fresh = f.pending.filter(p => !old.includes(p));
            fresh.forEach(p => p.resolve(response({ devices: [] })));
            await tick();
            old.forEach(p => p.resolve(response({ candidates: [], devices: [] })));
            await tick();
            assert.equal(f.active(), next);
            assert.match(f.panel(), new RegExp(heading));
            assert.equal(old.length, 1, 'confirmation must not fetch the old section again');
        });
    }
    for (const fail of [false, true]) {
        await scenario(`journal → device → journal ignores obsolete ${fail ? 'error' : 'success'}`, async f => {
            f.open('journal');
            f.open('audio-device');
            f.open('journal');
            f.pending[2].resolve(response({ configured_path: 'latest', candidates: [] }));
            await tick();
            const before = f.panel();
            for (const p of f.pending.slice(0, 2)) {
                if (fail) p.reject(new Error('obsolete failure'));
                else p.resolve(response({ configured_path: 'obsolete', candidates: [], devices: [] }));
            }
            await tick();
            assert.equal(f.panel(), before);
            assert.equal(f.w.document.getElementById('setupJournalPath').value, 'latest');
        });
    }
    for (const escape of [false, true]) {
        await scenario(`${escape ? 'Escape' : 'close button'} discards pending work and reopen is fresh`, async f => {
            f.open('journal');
            if (escape) f.w.document.dispatchEvent(new f.w.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
            else f.click('#setupWizard [data-bind="closeSetupWizard"]');
            const before = f.panel();
            f.pending[0].resolve(response({ candidates: [] }));
            await tick();
            assert.equal(f.panel(), before);
            assert.equal(f.w.document.getElementById('setupWizard').hidden, true);
            f.open('journal');
            f.pending[1].resolve(response({ candidates: [] }));
            await tick();
            assert.match(f.panel(), /1\. Find/);
        });
    }
    await scenario('step controls navigate and failed confirmations stay on their section', async f => {
        f.open('journal');
        f.pending[0].resolve(response({ candidates: [] }));
        await tick();
        f.failPosts();
        await f.w.confirmJournalPath('journal');
        assert.equal(f.active(), 'journal');
        assert.match(f.panel(), /1\. Find/);
        f.click('#setupSteps li:nth-child(2)');
        f.pending.at(-1).resolve(response({ devices: [] }));
        await tick();
        await f.w.selectSetupAudioDevice('test', 0);
        assert.equal(f.active(), 'audio-device');
        assert.match(f.panel(), /2\. Choose/);
        assert.ok(f.w.document.querySelector('.toast.error'));
    });
    await scenario('successful audio test advances to finish', async f => {
        f.open('audio-test');
        f.click('#setupPanel button');
        await tick();
        assert.equal(f.active(), 'finish');
        assert.match(f.panel(), /4\. Finish/);
    });
    console.log(`${checks} setup navigation scenarios passed`);
}
run().catch(error => { console.error(error); process.exitCode = 1; });
