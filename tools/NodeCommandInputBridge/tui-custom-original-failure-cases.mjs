import assert from 'node:assert/strict';

// Synthetic admitted host effects, genuine supplied original Text/theme/keys/owner invocation.
// These controls author no worker implementation and have not been executed.
export function customOriginalFailureCases({ createTuiComponentBridge, bridgeOptions }) {
  function gate() { let resolve, reject; const original = new Promise((yes, no) => { resolve = yes; reject = no; }); return { original, resolve, reject }; }
  function graph(error) {
    const nodes = new Set(), pending = [error]; let edges = 0;
    while (pending.length) {
      const current = pending.pop(); if (nodes.has(current)) continue; nodes.add(current);
      assert.ok(nodes.size <= 1024);
      const children = current instanceof AggregateError ? [...current.errors] : current?.cause ? [current.cause] : [];
      edges += children.length; assert.ok(edges <= 4096); pending.push(...children);
    }
    return nodes;
  }
  return [false, true].flatMap(retireFault => [false, true].map(asyncFault => Object.freeze({
    name: 'custom.early-done-' + (asyncFault ? 'rejects' : 'throws') + '-held-open-joined' + (retireFault ? '-retirement-fault' : '-open-siblings'),
    async run() {
      const open = gate(), retireEntered = gate();
      const doneError = new Error('actual ' + (asyncFault ? 'asynchronous' : 'synchronous') + ' done signal fault');
      const openError = new Error('actual open fault'), sibling = new Error('actual open sibling');
      const retireError = new Error('actual retirement write fault');
      const errors = []; let custom, observed, caught, settled = false, bridge;
      try {
        bridge = createTuiComponentBridge({ ...bridgeOptions,
          hostCall(method) {
            if (method === 'ui.custom.open') return open.original;
            if (method === 'ui.custom.done') { if (asyncFault) return Promise.reject(doneError); throw doneError; }
            throw new Error('Unexpected synthetic host effect');
          },
          retireNativeOpen() {
            retireEntered.resolve();
            if (retireFault) throw retireError;
            return Promise.resolve(); // Signal acknowledgement only; it does not join held open.
          }
        });
        custom = bridge.custom((tui, theme, keys, done) => {
          done({ exactOriginalValue: true });
          return new bridgeOptions.originalTui.Text('original Text');
        });
        observed = custom.then(() => { settled = true; }, error => { settled = true; caught = error; });
        await Promise.race([retireEntered.original, observed]);
        assert.ok(!settled, 'custom must still join its held original open after the signal fault');
        assert.equal(bridge.originals.find(row => row.kind === 'native-open').original, open.original);
        open.reject(new AggregateError([openError, sibling], 'actual open siblings'));
        await observed;
        const nodes = graph(caught);
        assert.ok(nodes.has(doneError) && nodes.has(openError) && nodes.has(sibling));
        if (retireFault) assert.ok(nodes.has(retireError));
        assert.equal(bridge.originals.find(row => row.kind === 'native-open').status, 'faulted');
        assert.ok(bridge.originals.some(row => row.kind === 'native-open-retire'));
      } catch (error) { errors.push(error); }
      finally {
        open.reject(new AggregateError([openError, sibling], 'finally release actual open'));
        if (custom) {
          try { await custom; }
          catch (error) { if (error !== caught || !caught) errors.push(error); }
        } else {
          // No host-open admission occurred; settle and observe the synthetic original as well.
          try { await open.original; } catch (error) { if (!graph(error).has(openError)) errors.push(error); }
        }
        if (observed) await observed;
        if (bridge) try { await bridge.close(); } catch (error) { errors.push(error); }
      }
      if (errors.length) throw new AggregateError(errors, 'Original failure control/cleanup faults');
      return { heldOpenJoined: true, retainedSignalAndOpenOriginals: true, retirementFaultRetained: retireFault };
    }
  })));
}
