// PiSharp.Web.Common (decision 0011), from PiSharp Durable Web's hub stream (PLAN.md Appendix B, hub mux): a tab's one Server-Sent Events
// stream. The server sends {sub, frame}; the first frame, sub "hello", names the stream, and the tab then PUTs the subscription keys it
// wants as JSON {subs}. EventSource reconnects by itself, and every new hello resubscribes; a PUT the server answers 404 (the stream is
// gone) reopens the stream.

export class MuxStream {
  /** `url` is the stream (default /api/stream); subscriptions go to `${url}/${streamId}`. */
  constructor({ url = '/api/stream' } = {}) {
    this.url = url;
    this.handlers = new Map();
    this.keys = [];
    this.streamId = null;
    /** Called with 'connected' or 'reconnecting'. */
    this.status = () => {};
  }

  start() {
    this.source = new EventSource(this.url);
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

  /** Replaces the subscription set (keys the server sends without asking, such as the hub's network, need none). */
  subscribe(keys) {
    this.keys = [...new Set(keys.filter(Boolean))];
    return this.push();
  }

  async push() {
    if (!this.streamId) return;
    const id = this.streamId;
    let response;
    try {
      response = await fetch(`${this.url}/${encodeURIComponent(id)}`, {
        method: 'PUT',
        headers: { Accept: 'application/json', 'Content-Type': 'application/json' },
        body: JSON.stringify({ subs: this.keys }),
      });
    } catch {
      return; // Offline: the EventSource reconnects, and its hello pushes again.
    }
    if (response.status === 404 && this.streamId === id) {
      this.streamId = null;
      this.source.close();
      this.start();
    }
  }
}
