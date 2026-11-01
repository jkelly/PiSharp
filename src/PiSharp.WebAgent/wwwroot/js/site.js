// Decision 0011 (run W2, WU4): the module every page loads (_Layout). It starts the shell; page scripts (Scripts section) import from
// shell.js themselves.
import { initShell } from './shell.js';

initShell();
