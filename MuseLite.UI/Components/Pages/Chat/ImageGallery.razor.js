// Wraps a gallery image and reveals a declarative fallback card when the image fails to load — e.g. a
// dead link or a URL the model fabricated. Self-registering custom element, mirroring the <chat-messages>
// pattern; the define guard makes a repeat import() (one per gallery instance) a no-op.
if (!window.customElements.get('image-fallback')) {
    window.customElements.define('image-fallback', class ImageFallback extends HTMLElement {
        connectedCallback() {
            const img = this.querySelector('img');
            if (!img) {
                return;
            }

            // The image can finish loading — and fail — before this element upgrades (e.g. a hydrated
            // conversation). A completed img with zero natural width never decoded, so treat it as failed.
            if (img.complete && img.naturalWidth === 0) {
                this._showFallback();
                return;
            }

            this._onError = () => this._showFallback();
            img.addEventListener('error', this._onError);
        }

        disconnectedCallback() {
            if (this._onError) {
                this.querySelector('img')?.removeEventListener('error', this._onError);
            }
        }

        _showFallback() {
            this.classList.add('image-failed');
        }
    });
}
