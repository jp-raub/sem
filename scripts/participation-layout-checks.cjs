'use strict';

// Called only from the real, native-authenticated synthetic event fixture.
// Theme attributes are transient display probes, not preference-save tests.
module.exports = async ({ page, ok, answerId }) => {
    const viewport = page.viewportSize();
    const modeBefore = await page.evaluate(() => document.documentElement.getAttribute('data-appearance-mode'));
    const answer = page.locator(`[data-participation-answer="${answerId}"]`);
    const valueBefore = await answer.inputValue();
    const selected = () => page.locator('.participation-selection-chip').allTextContents();
    const selectedBefore = await selected();
    try {
        for (const width of [390, 768, 960, 961, 1200, 1500, 1501, 1920]) {
            for (const mode of ['light', 'dark']) {
                await page.setViewportSize({ width, height: 950 });
                await page.evaluate(mode => document.documentElement.dataset.appearanceMode = mode, mode);
                const state = await page.locator('.participation-entry-layout-embedded').evaluate(layout => {
                    const area = layout.querySelector('.participation-slot-area').getBoundingClientRect();
                    const panel = layout.querySelector('.participation-entry-panel');
                    const box = panel.getBoundingClientRect();
                    const scroll = layout.querySelector('.event-part-matrix-scroll');
                    const rightAligned = selector => Array.from(layout.querySelectorAll(selector))
                        .every(element => getComputedStyle(element).textAlign === 'right');
                    return {
                        stacked: box.top >= area.bottom - 1 && Math.abs(box.left - area.left) < 2,
                        beside: box.left >= area.right - 1,
                        position: getComputedStyle(panel).position,
                        pageFits: document.documentElement.scrollWidth <= innerWidth + 1,
                        horizontalScroll: scroll.scrollWidth > scroll.clientWidth,
                        artistCount: layout.querySelectorAll('.event-matrix-artist').length,
                        artistRight: rightAligned('.event-matrix-artist'),
                        durationRight: rightAligned('.event-matrix-duration'),
                        durationTabular: Array.from(layout.querySelectorAll('td.event-matrix-duration'))
                            .every(element => getComputedStyle(element).fontVariantNumeric.includes('tabular-nums')),
                    };
                });
                const expected = width <= 1500
                    ? state.stacked && state.position === 'static'
                    : state.beside && state.position === 'sticky';
                ok(expected && state.pageFits && (width > 600 || state.horizontalScroll)
                    && state.artistCount === 2 && state.artistRight && state.durationRight && state.durationTabular,
                `Real participation layout at ${width}px ${mode}: ${JSON.stringify(state)}`);
                ok(JSON.stringify(await selected()) === JSON.stringify(selectedBefore)
                    && await answer.inputValue() === valueBefore,
                `Viewport and display-mode changes preserve selected slots and unsent answer at ${width}px ${mode}`);
                const summaryState = await page.locator('.entry-summary').evaluate(element => {
                    const rect = element.getBoundingClientRect();
                    const meter = element.querySelector('[role="meter"]');
                    return {
                        fits: rect.left >= 0 && rect.right <= innerWidth + 1 && element.scrollWidth <= element.clientWidth + 1,
                        noControls: element.querySelectorAll('button,input,select,textarea,[tabindex]').length === 0,
                        now: meter?.getAttribute('aria-valuenow'),
                        max: meter?.getAttribute('aria-valuemax'),
                        number: element.querySelector('.entry-summary-number').textContent.replace(/\s+/g, ' ').trim(),
                    };
                });
                ok(summaryState.fits && summaryState.noControls && summaryState.now === '0'
                    && summaryState.max === '1' && summaryState.number === '0 / 1 曲',
                `Summary fits without focus stops and excludes unsent selection at ${width}px ${mode}`);
                if (process.env.SEM_E2E_RESULTS && [390, 1200, 1501].includes(width) && mode === 'light') {
                    const fs = require('node:fs');
                    const path = require('node:path');
                    fs.mkdirSync(process.env.SEM_E2E_RESULTS, { recursive: true });
                    await page.locator('.participation-entry-layout-embedded').screenshot({
                        path: path.join(process.env.SEM_E2E_RESULTS, `participation-layout-${width}.png`),
                    });
                }
            }
        }
    } finally {
        if (viewport) await page.setViewportSize(viewport);
        await page.evaluate(mode => {
            if (mode === null) document.documentElement.removeAttribute('data-appearance-mode');
            else document.documentElement.setAttribute('data-appearance-mode', mode);
        }, modeBefore);
    }
};