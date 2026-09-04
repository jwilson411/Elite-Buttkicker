// Shared DOM construction helpers.
//
// Everything the local API hands back - journal event fields, pattern and pack metadata, audio
// device names, folder paths, context strings, error messages - is data this application does not
// author. None of it is concatenated into markup: it is written as text nodes and attribute values
// so that a value like `<img src=x onerror=alert(1)>` stays a visible string instead of becoming an
// element. Markup is only ever built from literals in this file's callers, via el()/append().
//
// The page's Content-Security-Policy allows no inline script, so handlers are attached with
// addEventListener - either directly by a renderer, or by bindActions() for the static markup.
(function (global) {
    'use strict';

    /// A child may be a node, or any value to be shown verbatim as text. null/undefined/false are
    /// skipped so callers can write `condition && node` inline.
    function append(parent, children) {
        if (children === null || children === undefined || children === false) return parent;

        const list = Array.isArray(children) ? children : [children];
        list.forEach(child => {
            if (child === null || child === undefined || child === false || child === '') return;
            parent.appendChild(child.nodeType ? child : document.createTextNode(String(child)));
        });

        return parent;
    }

    /// el('div', { className, id, text, title, value, disabled, attrs, dataset, style, on }, children)
    /// `text` and every child are set as text, never parsed; `attrs`/`dataset` go through
    /// setAttribute, so a payload cannot break out into a new attribute either.
    function el(tag, options, children) {
        const node = document.createElement(tag);
        const opts = options || {};

        if (opts.className) node.className = opts.className;
        if (opts.id) node.id = opts.id;
        if (opts.title !== undefined && opts.title !== null) node.title = String(opts.title);
        if (opts.text !== undefined && opts.text !== null) node.textContent = String(opts.text);
        if (opts.value !== undefined && opts.value !== null) node.value = String(opts.value);
        if (opts.disabled) node.disabled = true;

        if (opts.attrs) {
            Object.keys(opts.attrs).forEach(name => {
                const value = opts.attrs[name];
                if (value === null || value === undefined || value === false) return;
                node.setAttribute(name, String(value));
            });
        }

        if (opts.dataset) {
            Object.keys(opts.dataset).forEach(name => {
                const value = opts.dataset[name];
                if (value === null || value === undefined || value === false) return;
                node.setAttribute('data-' + name, String(value));
            });
        }

        if (opts.style) {
            Object.keys(opts.style).forEach(name => { node.style[name] = opts.style[name]; });
        }

        if (opts.on) {
            Object.keys(opts.on).forEach(name => node.addEventListener(name, opts.on[name]));
        }

        return append(node, children);
    }

    function clear(node) {
        while (node.firstChild) {
            node.removeChild(node.firstChild);
        }
        return node;
    }

    /// The one safe equivalent of `node.innerHTML = ...`: drop what is there, append new nodes.
    function replace(node, children) {
        return append(clear(node), children);
    }

    /// Decorative Font Awesome glyph. The class always comes from a literal at the call site.
    function icon(className) {
        return el('i', { className: 'fas ' + className, attrs: { 'aria-hidden': 'true' } });
    }

    /// Status words from the API also drive CSS class names. Reduce them to a harmless slug so an
    /// unexpected value cannot smuggle extra classes - or anything else - into the attribute.
    function slug(value) {
        return String(value === null || value === undefined ? '' : value)
            .toLowerCase()
            .replace(/[^a-z0-9_-]/g, '');
    }

    /// Numbers from the API are shown as numbers; a non-numeric payload becomes the fallback.
    function num(value, fallback) {
        const parsed = Number(value);
        return Number.isFinite(parsed) ? parsed : (fallback === undefined ? 0 : fallback);
    }

    /// Static markup declares `data-bind="someGlobalFunction"` (optionally `data-bind-args` as a
    /// JSON array and `data-bind-event`) instead of an inline `onclick`, which the CSP blocks. The
    /// name is looked up on `window` when the event fires, never evaluated as code.
    function bindActions(root) {
        (root || document).querySelectorAll('[data-bind]').forEach(node => {
            const eventName = node.getAttribute('data-bind-event') || 'click';
            node.addEventListener(eventName, () => invokeBinding(node));
        });
    }

    function invokeBinding(node) {
        const path = node.getAttribute('data-bind').split('.');
        let owner = global;
        let target = global;

        for (const part of path) {
            if (target === null || target === undefined) break;
            owner = target;
            target = target[part];
        }

        if (typeof target !== 'function') {
            console.error('No handler named', node.getAttribute('data-bind'));
            return;
        }

        const raw = node.getAttribute('data-bind-args');
        target.apply(owner, raw ? JSON.parse(raw) : []);
    }

    global.dom = { el, append, clear, replace, icon, slug, num, bindActions };

    document.addEventListener('DOMContentLoaded', () => bindActions(document));
})(window);
