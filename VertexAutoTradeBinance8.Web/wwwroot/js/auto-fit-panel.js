// Auto-fit panel sizing — measures a resizable panel's actual content
// height and sets the panel's own height to match (up to a cap), so
// the whole content is visible by default without an inner scrollbar,
// per direct request. Stops auto-fitting a specific panel once the
// user has manually dragged its native resize handle, so a later
// auto-fit call (e.g. after the position list refreshes with more
// rows) doesn't fight with or silently override their explicit choice.
window.autoFitPanel = {
    _manuallyResized: new Set(),

    // Call after content that affects a panel's natural height changes
    // (e.g. the position list re-rendering with a different row count).
    fit(panelId, maxHeight) {
        if (this._manuallyResized.has(panelId)) return;
        const el = document.getElementById(panelId);
        if (!el) return;

        if (!this._observed) this._observed = new Set();
        if (!this._observed.has(panelId)) {
            this._observed.add(panelId);
            // Native resize handle drags fire a 'resize' ResizeObserver
            // entry just like any other size change, so the only
            // reliable way to distinguish "user dragged the handle"
            // from "we set the height via JS" is comparing against the
            // height we last set ourselves.
            let lastSetHeight = null;
            const observer = new ResizeObserver(() => {
                if (lastSetHeight == null) return;
                const current = el.getBoundingClientRect().height;
                if (Math.abs(current - lastSetHeight) > 2) {
                    this._manuallyResized.add(panelId);
                    observer.disconnect();
                }
            });
            observer.observe(el);
            el._autoFitObserver = observer;
            el._autoFitSetHeight = (h) => { lastSetHeight = h; };
        }

        // Measure natural content height without first resetting the
        // explicit height (which would cause a visible flash/jump on
        // every call, even when the result ends up unchanged) - check
        // whether scrollHeight already differs meaningfully from the
        // current rendered height before touching anything.
        const currentHeight = el.getBoundingClientRect().height;
        const natural = el.scrollHeight;
        if (Math.abs(natural - currentHeight) < 4) return; // already a good fit, nothing to do

        const finalHeight = Math.min(natural, maxHeight || 2000);
        el.style.height = finalHeight + 'px';
        if (el._autoFitSetHeight) el._autoFitSetHeight(finalHeight);
    },
};
