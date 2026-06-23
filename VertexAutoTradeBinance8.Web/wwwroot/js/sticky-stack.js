// Stacks multiple position:sticky elements correctly by measuring
// each one's REAL rendered height (not a hardcoded number — several
// of these elements, like the top header, don't have a fixed CSS
// height) and assigning each the correct cumulative "top" offset so
// they dock directly under one another with no gap, in document
// order.
//
// Why this exists: CSS alone can't do this for elements whose height
// isn't a known constant — position:sticky's "top" needs an exact
// pixel value to stack correctly. A ResizeObserver on every layer
// keeps this correct even if a layer's height changes later (content
// wrapping to a second line on a narrower window, a conditional block
// appearing/disappearing, etc).
(function () {
    'use strict';

    let observer = null;
    let targets = [];

    function recalc() {
        let offset = 0;
        for (const el of targets) {
            if (!el || !el.isConnected) continue;
            el.style.top = offset + 'px';
            // Only accumulate height for elements actually taking up
            // layout space right now (display:none elements report 0
            // height anyway, but this guards conditional Blazor blocks
            // that might be removed from the DOM entirely between calls).
            offset += el.getBoundingClientRect().height;
        }
    }

    window.stickyStack = {
        // selectors: array of CSS selectors, in the TOP-TO-BOTTOM
        // visual order they should stack in. Each must already have
        // position:sticky set in CSS — this only manages the "top"
        // value, not the sticky behavior itself.
        init(selectors) {
            this.dispose();

            targets = selectors
                .map(sel => document.querySelector(sel))
                .filter(el => el != null);

            if (targets.length === 0) return;

            observer = new ResizeObserver(() => recalc());
            for (const el of targets) observer.observe(el);

            recalc();
        },

        // Call after a layout change that might not trigger a
        // ResizeObserver callback on its own (e.g. a conditional block
        // newly appearing — its own size starts at non-zero immediately,
        // which IS observed, but calling this directly avoids waiting
        // a frame in cases where instant correctness matters).
        recalc() {
            recalc();
        },

        dispose() {
            if (observer) {
                try { observer.disconnect(); } catch (e) { }
                observer = null;
            }
            targets = [];
        },
    };
})();
