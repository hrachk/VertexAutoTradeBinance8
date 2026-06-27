// Signal card hover-expand: clones the hidden .sc-expand template content
// into a single shared, position:fixed floating panel, positioned via
// getBoundingClientRect anchored under the hovered card. Solves the CSS
// overflow-clipping problem (the cards live in a horizontally-scrolling
// container with overflow-x:auto, which forces overflow-y to behave as
// auto too — any CSS-only absolutely-positioned child would get clipped)
// by using position:fixed, which escapes the containing block entirely.
//
// Uses plain JS mouseenter/mouseleave via event delegation, not Blazor
// server-side events — a hover handler needs to be instant, not a
// network round-trip on every mouse movement.
(function () {
    'use strict';

    let overlay = null;
    let currentCardEl = null;
    let hideTimer = null;

    function ensureOverlay() {
        if (!overlay) overlay = document.getElementById('sigCardFloatOverlay');
        return overlay;
    }

    function showFor(cardEl) {
        const ov = ensureOverlay();
        if (!ov || !cardEl) return;

        const expandSrc = cardEl.querySelector('.sc-expand');
        if (!expandSrc) return;

        currentCardEl = cardEl;
        ov.innerHTML = expandSrc.innerHTML;

        const rect = cardEl.getBoundingClientRect();
        ov.style.left = rect.left + 'px';
        ov.style.width = rect.width + 'px';

        // Default: anchor below the card. If that would overflow past
        // the bottom of the viewport, anchor above it instead.
        const estimatedHeight = 160;
        const spaceBelow = window.innerHeight - rect.bottom;
        if (spaceBelow < estimatedHeight && rect.top > estimatedHeight) {
            ov.style.top = (rect.top - estimatedHeight - 4) + 'px';
        } else {
            ov.style.top = (rect.bottom + 4) + 'px';
        }

        ov.classList.add('sc-show');
    }

    function hide() {
        const ov = ensureOverlay();
        if (ov) ov.classList.remove('sc-show');
        currentCardEl = null;
    }

    function isSigCard(el) {
        const card = el && el.closest && el.closest('.sig-card');
        // Pinned cards render their expanded content inline via Blazor
        // state (no display:none on .sc-expand) — the floating overlay
        // mechanism is for unpinned cards only, since triggering it on
        // top of already-visible inline content would be redundant.
        if (card && card.classList.contains('sc-pinned')) return null;
        return card;
    }

    document.addEventListener('mouseover', (e) => {
        const card = isSigCard(e.target);
        if (!card) return;
        if (hideTimer) { clearTimeout(hideTimer); hideTimer = null; }
        if (card !== currentCardEl) showFor(card);
    });

    document.addEventListener('mouseout', (e) => {
        const card = isSigCard(e.target);
        if (!card) return;
        // Moving from the card to one of its own children still fires
        // mouseout — only actually hide if the related target isn't
        // still inside the same card.
        if (e.relatedTarget && card.contains(e.relatedTarget)) return;
        hideTimer = setTimeout(hide, 80);
    });

    // If the page re-renders and removes the hovered card from the DOM
    // (e.g. a signal expiring), make sure the stale overlay doesn't get
    // stuck open forever.
    document.addEventListener('scroll', () => {
        if (currentCardEl && !document.body.contains(currentCardEl)) hide();
    }, true);
})();
