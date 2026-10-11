// One provider request's credits. Waiting never blocks the shared host protocol reader.
export class ProgressWindow {
  constructor(size, signal) {
    if (!Number.isInteger(size) || size < 1 || size > 1024) throw new Error('Invalid provider progress window');
    this.size = size;
    this.credits = size;
    this.signal = signal;
    this.waiter = undefined;
    this.abort = () => {
      const waiter = this.waiter; this.waiter = undefined;
      waiter?.reject(signal.reason ?? new Error('Provider stream cancelled'));
    };
    signal.addEventListener('abort', this.abort);
  }
  async take() {
    this.signal.throwIfAborted();
    if (this.credits > 0) { this.credits--; return; }
    await new Promise((resolve, reject) => { this.waiter = { resolve, reject }; });
    this.signal.throwIfAborted();
  }
  acknowledge() {
    if (this.waiter) {
      const waiter = this.waiter; this.waiter = undefined;
      waiter.resolve();
    } else this.credits = Math.min(this.size, this.credits + 1);
  }
  dispose() { this.signal.removeEventListener('abort', this.abort); }
}
