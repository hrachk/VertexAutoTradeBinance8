// ============================================================
// Drag-to-scroll for the horizontal symbol ticker bar on /market.
// Previously this lived inside the old canvas chart engine's init()
// function; when that engine was replaced with Lightweight Charts,
// this unrelated bit of UI behavior was lost along with it. Extracted
// here as its own small, focused script.
// ============================================================
window.tickerDragScroll = {
    bind() {
        const ticker = document.querySelector('.mk-ticker-bar');
        if (!ticker || ticker._dragBound) return;
        ticker._dragBound = true;

        let isDown = false, startX, scrollLeft;
        ticker.addEventListener('mousedown', e => {
            isDown = true;
            startX = e.pageX - ticker.offsetLeft;
            scrollLeft = ticker.scrollLeft;
            ticker.style.cursor = 'grabbing';
        });
        ticker.addEventListener('mouseleave', () => { isDown = false; ticker.style.cursor = 'grab'; });
        ticker.addEventListener('mouseup', () => { isDown = false; ticker.style.cursor = 'grab'; });
        ticker.addEventListener('mousemove', e => {
            if (!isDown) return;
            e.preventDefault();
            const x = e.pageX - ticker.offsetLeft;
            ticker.scrollLeft = scrollLeft - (x - startX);
        });
    }
};
