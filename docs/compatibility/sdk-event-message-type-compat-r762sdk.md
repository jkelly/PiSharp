The existing dispatcher belongs to the experimental Events reducer API. Adding the
distinct registration message record in its parent namespace changed unqualified
type lookup in the dispatcher, producing genuine Runtime build CS1503 errors.
The first same-name file alias was insufficient: actual R766 compilation repeated
the same type errors. The successor removes that alias and globally qualifies the
builder's generic argument as global::PiSharp.Extensions.Events.ExtensionCustomMessage.
Both existing Events and new registration message contracts are unchanged. The failed
b561 and 70dad candidate products and receipts remain preserved.

The existing fourteen-group standalone fixture adds one vector inside its validation
group. It invokes the actual existing event dispatcher and checks that a callback's
exact Events message survives reduction with no diagnostics. The callback and dispatch
original Tasks are captured and directly joined through the existing audit helper.
This is a legacy experimental reducer regression control, not a claim of full original
event ABI parity. All controls remain unexecuted by the source author; root owns native
compilation and qualification. The failed b561/R737 products and receipts are preserved.
