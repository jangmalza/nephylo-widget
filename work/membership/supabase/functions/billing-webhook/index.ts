import { createRetiredBillingHandler } from '../_shared/free-distribution.mjs';
Deno.serve(createRetiredBillingHandler(Deno.env.toObject()));
