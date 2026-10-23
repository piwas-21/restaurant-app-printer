# Cashier bill preferences and connected copies

In Settings → Cashier Printer Settings, **Include selected variations on cashier bills** controls
whether a line prints `Kofte Grill` or `Kofte Grill (Frites)`. New and existing configurations
default to the main product name. The setting applies recursively to bundle components. Cashier
bills include genuine item instructions, including nested component requests such as extra bread.
The ingredient/sauce preference controls structured selections independently. A note that duplicates
a variation is suppressed only when that variation is printed. Kitchen tickets retain all preparation details.
Prices, quantities, bill charges, refunds and credits keep their authoritative values.

Table order feeds print kitchen work per ordering round. The full cashier bill is queued when
the visit closes successfully. Cashier and Server bill actions offer browser printing or sending
to this app’s connected cashier printer. Each explicit copy is an independent server job and
prints only to the cashier destination. Automatic close bills honor auto-print and quiet hours;
manual copies bypass those restrictions. Version 1.0.39 or later is required for queued bills.

A server claim is consumed once across connected installations. A crash/lost response or unknown
output is held rather than automatically replayed. Check the printer before asking for another
copy. `Sent` is transport evidence; it does not prove physical paper. No output path changes
payment records or claims that staff acknowledgement means the printer produced a ticket.

Receipt labels follow the configured print language (or the order language in Auto mode). Order
channels and all known payment methods/statuses are localized; current `Takeaway` and legacy
`TakeAway` wire values both render as `À emporter` in French. `Client` is the French customer label.
