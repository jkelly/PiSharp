// PiSharp Durable Web: the tab's one stream to the hub (PLAN.md Appendix B, hub mux). The hub sends {sub, frame}; the first frame names the
// stream, and the tab then puts the subscription keys it wants. EventSource reconnects by itself; every new hello resubscribes.
import { api } from './core.js';

export class HubStream {
  constructor() {
    this.handlers = new Map();
    this.keys = [];
    this.streamId = null;
    this.status = () => {};
  }

  start() {
    this.source = new EventSource('/api/stream');
    this.source.onmessage = (event) => {
      let message;
      try { message = JSON.parse(event.data); } catch { return; }
      this.dispatch(message.sub, message.frame);
    };
    this.source.onerror = () => {
      this.streamId = null;
      this.status('reconnecting');
    };
  }

  dispatch(sub, frame) {
    if (sub === 'hello') {
      this.streamId = frame.streamId;
      this.status('connected');
      this.push();
      return;
    }
    const handler = this.handlers.get(sub);
    if (handler) handler(frame);
  }

  on(sub, handler) {
    this.handlers.set(sub, handler);
  }

  off(sub) {
    this.handlers.delete(sub);
  }

  /** Replaces the subscription set (besides the network). */
  subscribe(keys) {
    this.keys = [...new Set(keys.filter(Boolean))];
    return this.push();
  }

  async push() {
    if (!this.streamId) return;
    const id = this.streamId;
    try {
      await api('PUT', `/api/stream/${id}`, { subs: this.keys });
    } catch (error) {
      if (error.status === 404 && this.streamId === id) {
        this.streamId = null;
        this.source.close();
        this.start();
      }
    }
  }
}
