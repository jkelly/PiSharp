import assert from 'node:assert/strict';

// Notification callbacks stay synchronous; settlement distinguishes rejection from its value.
export function createNotificationPublication() {
  let pending = Promise.resolve(), failed = false, failure;
  const recordFailure = error => { if (!failed) { failed = true; failure = error; } };
  return {
    enqueue(deliver) {
      pending = pending.then(deliver);
      pending.catch(recordFailure);
      return undefined;
    },
    wait: () => pending,
    async join() {
      try { await pending; } catch (error) { recordFailure(error); }
      return { failed, ...(failed ? { failure } : {}) };
    }
  };
}

// Synchronous source callback, bounded queue, exactly one awaited native delivery at a time.
// Overflow is a recorded publication failure even if an extension catches the callback exception.
export function createToolProgress(signal, publish, observe) {
  let accepting = true, count = 0, bytes = 0, queued = 0, delivered = 0, failure, failed = false;
  const recordFailure = error => { if (!failed) { failed = true; failure = error; } };
  let pending = Promise.resolve(); const observations = [];
  const onUpdate = update => {
    try {
      assert(accepting, 'Tool progress admission closed'); signal.throwIfAborted();
      assert(count < 16 && queued < 16, 'Tool progress backpressure limit');
      // Observe before JSON encoding, rejecting cycles, excessive depth and source path budgets.
      let nodes = 0;
      const jsonOnly = (value, depth) => {
        assert(++nodes <= 65536 && depth <= 32, 'Tool progress JSON structure budget');
        assert(value === null || ['boolean', 'string', 'number', 'object'].includes(typeof value), 'Non-JSON tool progress value');
        if (typeof value === 'number') assert(Number.isFinite(value), 'Non-finite tool progress number');
        if (value && typeof value === 'object') {
          assert([null, Object.prototype, Array.prototype].includes(Object.getPrototypeOf(value)) && Object.getOwnPropertySymbols(value).length === 0,
            'Arbitrary tool progress object unsupported');
          if (Array.isArray(value)) for (let index = 0; index < value.length; index++) assert(Object.hasOwn(value, index), 'Sparse tool progress array unsupported');
          for (const [key, descriptor] of Object.entries(Object.getOwnPropertyDescriptors(value))) {
            assert(Object.hasOwn(descriptor, 'value'), 'Tool progress accessor unsupported');
            if (Array.isArray(value) && key === 'length') continue;
            assert(!Array.isArray(value) || /^(?:0|[1-9][0-9]*)$/u.test(key), 'Tool progress array metadata unsupported');
            jsonOnly(descriptor.value, depth + 1);
          }
        }
      };
      jsonOnly(update, 0); const observed = observe(update), raw = JSON.stringify(update);
      assert(typeof raw === 'string', 'Tool progress JSON value required');
      const length = Buffer.byteLength(raw);
      assert(length <= 65536 && bytes + length <= 262144, 'Tool progress byte budget');
      const sequence = ++count; bytes += length; queued++; observations.push(observed);
      pending = pending.then(async () => {
        signal.throwIfAborted();
        const receipt = await publish(sequence, raw);
        assert(receipt?.delivered === true && receipt.sequence === sequence, 'Actual matching native progress receipt required');
        signal.throwIfAborted(); delivered++;
      }).finally(() => { queued--; });
      pending.catch(recordFailure);
      return undefined;
    } catch (error) { recordFailure(error); throw error; }
  };
  const join = async () => {
    accepting = false;
    try { await pending; } catch (error) { recordFailure(error); }
    return { updateDeliveryJoined: true, updateCount: count, updateBytes: bytes, deliveredUpdates: delivered,
      updates: observations, ...(failed ? { updateFailure: { name: failure?.name, message: failure?.message ?? String(failure) } } : {}) };
  };
  return { onUpdate, join };
}
