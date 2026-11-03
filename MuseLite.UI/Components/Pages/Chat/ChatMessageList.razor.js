window.customElements.define('chat-messages', class ChatMessages extends HTMLElement {
    static _isFirstAutoScroll = true;

    connectedCallback() {
        this._observer = new MutationObserver(mutations => this._scheduleAutoScroll(mutations));
        this._observer.observe(this, { childList: true, attributes: true });

        // Messages already in the DOM when this element upgrades (e.g. a conversation hydrated from the
        // server at load) were not "added" after the observer attached, so no childList mutation fires to
        // trigger the auto-scroll. Scroll to the newest message once on connect so a restored conversation
        // opens at the bottom instead of the top. No-op when there are no messages yet.
        if (this.lastElementChild) {
            requestAnimationFrame(() => {
                const elem = this.lastElementChild;
                if (!elem) return;
                elem.scrollIntoView({ behavior: 'instant' });
                ChatMessages._isFirstAutoScroll = false;
            });
        }
    }

    disconnectedCallback() {
        this._observer.disconnect();
    }

    _scheduleAutoScroll(mutations) {
        cancelAnimationFrame(this._nextAutoScroll);
        this._nextAutoScroll = requestAnimationFrame(() => {
            const addedUserMessage = mutations.some(m =>
                Array.from(m.addedNodes).some(n =>
                    n.parentElement === this && n.classList?.contains('user-message')));
            const elem = this.lastElementChild;
            if (!elem) return;
            if (ChatMessages._isFirstAutoScroll || addedUserMessage || this._elemIsNearScrollBoundary(elem, 300)) {
                elem.scrollIntoView({ behavior: ChatMessages._isFirstAutoScroll ? 'instant' : 'smooth' });
                ChatMessages._isFirstAutoScroll = false;
            }
        });
    }

    _elemIsNearScrollBoundary(elem, threshold) {
        const maxScrollPos = document.body.scrollHeight - window.innerHeight;
        const remainingScrollDistance = maxScrollPos - window.scrollY;
        return remainingScrollDistance < elem.offsetHeight + threshold;
    }
});
